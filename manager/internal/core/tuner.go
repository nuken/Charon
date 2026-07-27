package core

import (
	"context"
	"sync"
	"time"
)

type TunerState string

const (
	StateWarming   TunerState = "warming"
	StateWarm      TunerState = "warm"
	StateTuning    TunerState = "tuning"
	StateStreaming TunerState = "streaming"
	StateError     TunerState = "error"
)

type Tuner struct {
	ID          string
	ContainerID string
	State       TunerState
	IPAddress   string
	ChannelID   string
	StartedAt   time.Time

	// Context for lifecycle management
	Ctx    context.Context
	Cancel context.CancelFunc

	mu sync.RWMutex
}

func (t *Tuner) SetState(state TunerState) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.State = state
}