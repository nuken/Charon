package main

import (
	"fmt"
	"log"
	"net/http"
	"os"
	"os/exec"
	"time"

	"charon-tuner/internal/providers"
	"github.com/playwright-community/playwright-go"
)

var (
	pw         *playwright.Playwright
	browser    playwright.Browser
	page       playwright.Page
	ffmpegProc *exec.Cmd
	provider   providers.Provider
)

func main() {
	log.Println("Initializing Tuner Environment...")

	// 1. Determine which provider to use
	providerName := os.Getenv("PROVIDER")
	switch providerName {
	case "directv":
		provider = providers.NewDirecTVProvider()
	case "yttv":
		provider = providers.NewYttvProvider()
	default:
		log.Printf("Unknown or missing PROVIDER env var: '%s'. Defaulting to YouTube TV.", providerName)
		provider = providers.NewYttvProvider()
	}

	log.Printf("Selected Provider Strategy: %s", provider.Name())

	// 2. Initialize Playwright
	err := initPlaywright()
	if err != nil {
		log.Fatalf("Failed to start Playwright: %v", err)
	}
	defer pw.Stop()

	// 3. Start the Anti-Timeout Routine
	go antiTimeoutMover()

	// 4. Setup Internal HTTP Routers
	mux8081 := http.NewServeMux()
	mux8081.HandleFunc("/tune", tuneHandler)

	mux8082 := http.NewServeMux()
	mux8082.HandleFunc("/stream", streamHandler)

	// Run command listener on 8081
	go func() {
		log.Println("Tuner command API listening on :8081")
		if err := http.ListenAndServe(":8081", mux8081); err != nil {
			log.Fatalf("API server crashed: %v", err)
		}
	}()

	// Run stream provider on 8082
	log.Println("Tuner stream provider listening on :8082")
	if err := http.ListenAndServe(":8082", mux8082); err != nil {
		log.Fatalf("Stream server crashed: %v", err)
	}
}

func initPlaywright() error {
	var err error
	pw, err = playwright.Run()
	if err != nil {
		return err
	}

	browser, err = pw.Chromium.Launch(playwright.BrowserTypeLaunchOptions{
		Headless: playwright.Bool(false),
		Args: []string{
			"--disable-dev-shm-usage",
			"--no-sandbox",
			"--enable-features=UseOzonePlatform",
			"--ozone-platform=wayland", 
		},
	})
	if err != nil {
		return err
	}

	page, err = browser.NewPage()
	if err != nil {
		return err
	}

	// Dynamic Warm-Up: If the provider has a Discover method, run it now to pre-warm the cache
	if discoverer, ok := provider.(interface{ Discover(playwright.Page) error }); ok {
		log.Println("Running provider initial discovery and warm-up...")
		if err := discoverer.Discover(page); err != nil {
			log.Printf("Notice: Provider discovery reported an error: %v", err)
		}
	} else {
		log.Println("Provider does not require initial discovery. Idling.")
	}

	return nil
}

// tuneHandler delegates entirely to the active Provider interface
func tuneHandler(w http.ResponseWriter, r *http.Request) {
	channelID := r.URL.Query().Get("channel")
	if channelID == "" {
		http.Error(w, "Missing channel", http.StatusBadRequest)
		return
	}

	log.Printf("Tuning command received for: %s", channelID)

	if err := provider.Tune(page, channelID); err != nil {
		http.Error(w, fmt.Sprintf("Provider failed to tune channel: %v", err), http.StatusInternalServerError)
		return
	}

	w.WriteHeader(http.StatusOK)
	w.Write([]byte("Tuned successfully"))
}

// streamHandler captures the Wayland/PipeWire output via FFmpeg
func streamHandler(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "video/mp2t")

	ffmpegProc = exec.Command("ffmpeg",
		"-hide_banner", "-loglevel", "error",
		"-f", "wayland", "-i", os.Getenv("XDG_RUNTIME_DIR")+"/wayland-0",
		"-f", "pulse", "-i", "default",
		"-vaapi_device", "/dev/dri/renderD128",
		"-c:v", "h264_vaapi", "-qp", "20",
		"-c:a", "aac", "-b:a", "192k",
		"-f", "mpegts", "pipe:1",
	)

	stdout, err := ffmpegProc.StdoutPipe()
	if err != nil {
		http.Error(w, "FFmpeg stdout error", http.StatusInternalServerError)
		return
	}

	if err := ffmpegProc.Start(); err != nil {
		http.Error(w, "Failed to start FFmpeg", http.StatusInternalServerError)
		return
	}

	defer func() {
		ffmpegProc.Process.Kill()
		ffmpegProc.Wait()
	}()

	buffer := make([]byte, 8192)
	for {
		n, err := stdout.Read(buffer)
		if n > 0 {
			w.Write(buffer[:n])
			w.(http.Flusher).Flush()
		}
		if err != nil {
			break
		}
	}
}

func antiTimeoutMover() {
	ticker := time.NewTicker(3*time.Hour + 45*time.Minute)
	defer ticker.Stop()

	for range ticker.C {
		log.Println("Executing synthetic mouse movement to prevent timeout...")
		if page != nil {
			_ = page.Mouse().Move(500, 500)
			time.Sleep(1 * time.Second)
			_ = page.Mouse().Move(510, 510)
		}
	}
}