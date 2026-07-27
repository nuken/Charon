package core

import (
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"os"
)

// Channel represents a single network mapping for the M3U playlist.
type Channel struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	GracenoteID string `json:"gracenote_id"`
	Category    string `json:"category"`
}

// PlaylistHandler builds the M3U8 playlist dynamically for Channels DVR.
func (o *Orchestrator) PlaylistHandler(w http.ResponseWriter, r *http.Request) {
	// Read the external JSON file
	file, err := os.ReadFile("channels.json")
	if err != nil {
		log.Printf("Error reading channels.json: %v", err)
		http.Error(w, "Failed to load channel lineup", http.StatusInternalServerError)
		return
	}

	var channels []Channel
	if err := json.Unmarshal(file, &channels); err != nil {
		log.Printf("Error parsing channels.json: %v", err)
		http.Error(w, "Failed to parse channel lineup", http.StatusInternalServerError)
		return
	}

	// Channels DVR requires this exact Content-Type to recognize the playlist
	w.Header().Set("Content-Type", "application/x-mpegurl")
	
	fmt.Fprintln(w, "#EXTM3U")

	host := r.Host 
	if host == "" {
		host = "localhost:8080"
	}

	for _, ch := range channels {
		extinf := fmt.Sprintf(`#EXTINF:-1 channel-id="%s" tms-id="%s" tvc-guide-stationid="%s" group-title="%s", %s`,
			ch.ID, ch.GracenoteID, ch.GracenoteID, ch.Category, ch.Name)
		
		fmt.Fprintln(w, extinf)
		fmt.Fprintf(w, "http://%s/stream/%s\n", host, ch.ID)
	}
}