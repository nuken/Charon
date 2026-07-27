package core

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/docker/docker/api/types/container"
	"github.com/docker/docker/api/types/network"
	"github.com/docker/docker/client"
)

var ErrMaxTunersReached = errors.New("maximum concurrent streams reached")

// Orchestrator manages the container lifecycle and the warm-standby queue.
type Orchestrator struct {
	DockerClient *client.Client
	Tuners       map[string]*Tuner
	WarmTuner    *Tuner
	MaxTuners    int
	NetworkName  string
	TunerImage   string

	mu sync.Mutex // Protects the pool and warm tuner rotation
}

// NewOrchestrator initializes the Docker SDK and the pool state.
func NewOrchestrator(maxTuners int, network, image string) (*Orchestrator, error) {
	cli, err := client.NewClientWithOpts(client.FromEnv, client.WithAPIVersionNegotiation())
	if err != nil {
		return nil, err
	}

	return &Orchestrator{
		DockerClient: cli,
		Tuners:       make(map[string]*Tuner),
		MaxTuners:    maxTuners,
		NetworkName:  network,
		TunerImage:   image,
	}, nil
}

// StartNewWarmTuner spins up a new container to idle on the provider's homepage.
func (o *Orchestrator) StartNewWarmTuner(ctx context.Context) error {
	o.mu.Lock()
	defer o.mu.Unlock()

	if len(o.Tuners) >= o.MaxTuners {
		log.Println("Pool at capacity. Cannot spin up a new warm tuner.")
		return ErrMaxTunersReached
	}

	tunerID := fmt.Sprintf("tuner-%d", time.Now().UnixNano())
	
	config := &container.Config{
		Image: o.TunerImage,
		Env: []string{
			"TUNER_ID=" + tunerID,
			"PROVIDER=" + os.Getenv("PROVIDER"),
		},
	}

	hostConfig := &container.HostConfig{
		ShmSize: 2 * 1024 * 1024 * 1024,
	}

	networkConfig := &network.NetworkingConfig{
		EndpointsConfig: map[string]*network.EndpointSettings{
			o.NetworkName: {},
		},
	}

	resp, err := o.DockerClient.ContainerCreate(ctx, config, hostConfig, networkConfig, nil, tunerID)
	if err != nil {
		return fmt.Errorf("failed to create tuner container: %w", err)
	}

	if err := o.DockerClient.ContainerStart(ctx, resp.ID, container.StartOptions{}); err != nil {
		return fmt.Errorf("failed to start tuner container: %w", err)
	}

	inspect, err := o.DockerClient.ContainerInspect(ctx, resp.ID)
	if err != nil {
		return fmt.Errorf("failed to inspect tuner: %w", err)
	}

	ipAddress := inspect.NetworkSettings.Networks[o.NetworkName].IPAddress

	tunerCtx, cancel := context.WithCancel(context.Background())
	newTuner := &Tuner{
		ID:          tunerID,
		ContainerID: resp.ID,
		State:       StateWarming,
		IPAddress:   ipAddress,
		StartedAt:   time.Now(),
		Ctx:         tunerCtx,
		Cancel:      cancel,
	}

	o.Tuners[tunerID] = newTuner
	o.WarmTuner = newTuner

	log.Printf("Warm tuner [%s] started at %s", tunerID, ipAddress)
	newTuner.SetState(StateWarm)

	return nil
}

// StreamHandler processes requests from Channels DVR (e.g., /stream/cnn)
func (o *Orchestrator) StreamHandler(w http.ResponseWriter, r *http.Request) {
	channelID := strings.TrimPrefix(r.URL.Path, "/stream/")
	if channelID == "" {
		http.Error(w, "Missing channel ID", http.StatusBadRequest)
		return
	}

	o.mu.Lock()
	activeTuner := o.WarmTuner

	if activeTuner == nil {
		o.mu.Unlock()
		http.Error(w, "Service Unavailable: Max tuners reached", http.StatusServiceUnavailable)
		return
	}

	// Claim the warm tuner and remove it from standby
	o.WarmTuner = nil
	activeTuner.SetState(StateTuning)
	activeTuner.ChannelID = channelID
	tunerIP := activeTuner.IPAddress
	o.mu.Unlock()

	// Immediately start replenishing the pool in the background
	go func() {
		if err := o.StartNewWarmTuner(context.Background()); err != nil {
			log.Printf("Notice: Could not start replacement warm tuner: %v", err)
		}
	}()

	// 1. Tell the Tuner to execute Playwright accessibility locators to switch channels
	tuneURL := fmt.Sprintf("http://%s:8081/tune?channel=%s", tunerIP, channelID)
	tuneResp, err := http.Post(tuneURL, "application/json", nil)
	if err != nil || tuneResp.StatusCode != http.StatusOK {
		activeTuner.SetState(StateError)
		http.Error(w, "Failed to tune channel", http.StatusInternalServerError)
		return
	}

	// 2. Proxy the FFmpeg stream from the Tuner back to Channels DVR
	activeTuner.SetState(StateStreaming)
	streamURL := fmt.Sprintf("http://%s:8082/stream", tunerIP)
	streamResp, err := http.Get(streamURL)
	if err != nil {
		activeTuner.SetState(StateError)
		http.Error(w, "Failed to connect to video stream", http.StatusBadGateway)
		return
	}
	defer streamResp.Body.Close()

	for k, v := range streamResp.Header {
		w.Header()[k] = v
	}
	w.WriteHeader(streamResp.StatusCode)

	_, _ = io.Copy(w, streamResp.Body)

	log.Printf("Stream ended for channel %s. Terminating tuner [%s].", channelID, activeTuner.ID)
	o.RemoveTuner(activeTuner.ID)
}

// RemoveTuner kills and removes a specific container, cleaning up the pool.
func (o *Orchestrator) RemoveTuner(id string) {
	o.mu.Lock()
	defer o.mu.Unlock()

	tuner, exists := o.Tuners[id]
	if !exists {
		return
	}

	tuner.Cancel()
	
	_ = o.DockerClient.ContainerRemove(context.Background(), tuner.ContainerID, container.RemoveOptions{
		Force: true,
	})
	
	delete(o.Tuners, id)
}

// Cleanup forcefully removes all Tuner containers managed by this instance.
func (o *Orchestrator) Cleanup(ctx context.Context) {
	o.mu.Lock()
	defer o.mu.Unlock()

	for id, tuner := range o.Tuners {
		_ = o.DockerClient.ContainerRemove(context.Background(), tuner.ContainerID, container.RemoveOptions{
			Force: true,
		})
		delete(o.Tuners, id)
	}
}