package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"charon/internal/core"
)

func main() {
	// Trap SIGINT and SIGTERM for graceful shutdown
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	maxTuners, _ := strconv.Atoi(os.Getenv("MAX_TUNERS"))
	image := os.Getenv("TUNER_IMAGE")
	network := os.Getenv("NETWORK_NAME")

	orchestrator, err := core.NewOrchestrator(maxTuners, network, image)
	if err != nil {
		log.Fatalf("Failed to initialize orchestrator: %v", err)
	}

	// Ensure all containers are destroyed when the program exits
	defer orchestrator.Cleanup(context.Background())

	// Generate the initial warm tuner before starting the web server
	log.Println("Initializing the first warm tuner...")
	if err := orchestrator.StartNewWarmTuner(context.Background()); err != nil {
		log.Printf("Warning: Failed to start initial tuner: %v", err)
	}

	mux := http.NewServeMux()
	
	// Channels DVR Endpoints
	mux.HandleFunc("/playlist.m3u", orchestrator.PlaylistHandler)
	mux.HandleFunc("/stream/", orchestrator.StreamHandler)

	srv := &http.Server{
		Addr:    ":8080",
		Handler: mux,
	}

	// Run the server in a goroutine so it doesn't block
	go func() {
		log.Println("Charon Manager listening on :8080")
		if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("HTTP server crashed: %v", err)
		}
	}()

	// Wait for the termination signal
	<-ctx.Done()
	log.Println("\nShutdown signal received. Cleaning up Tuners...")

	// Give the HTTP server 5 seconds to finish active requests
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		log.Printf("HTTP server shutdown error: %v", err)
	}
	
	log.Println("Shutdown complete.")
}