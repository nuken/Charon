package providers

import (
	"fmt"
	"log"
	"strings"

	"github.com/playwright-community/playwright-go"
)

// YttvProvider implements the Provider interface for YouTube TV.
type YttvProvider struct {
	channelCache map[string]string
}

func NewYttvProvider() *YttvProvider {
	return &YttvProvider{
		channelCache: make(map[string]string),
	}
}

func (y *YttvProvider) Name() string {
	return "YouTube TV"
}

// Discover populates the cache using Prismcast's exact DOM extraction strategy.
func (y *YttvProvider) Discover(page playwright.Page) error {
	log.Println("Discovering YouTube TV channels from Live Guide...")

	// 1. Navigate to the guide
	if _, err := page.Goto("https://tv.youtube.com/live"); err != nil {
		return err
	}

	// 2. Wait for the EPG grid to render
	if _, err := page.WaitForSelector("ytu-epg-row", playwright.PageWaitForSelectorOptions{
		Timeout: playwright.Float(10000),
	}); err != nil {
		return fmt.Errorf("guide grid did not load: %v", err)
	}

	// 3. Extract all channels and URLs in a single pass
	result, err := page.Evaluate(`() => {
		const channels = {};
		const thumbs = document.querySelectorAll("ytu-endpoint.tenx-thumb[aria-label]");
		
		for (const thumb of Array.from(thumbs)) {
			const label = thumb.getAttribute("aria-label") || "";
			if (!label.startsWith("watch ")) continue;
			
			const anchor = thumb.querySelector("a");
			const href = anchor ? anchor.getAttribute("href") : "";
			
			if (href.startsWith("watch/")) {
				const name = label.slice(6).toLowerCase();
				channels[name] = "https://tv.youtube.com/" + href;
			}
		}
		return channels;
	}`)

	if err != nil {
		return err
	}

	// 4. Update the internal Go cache
	discovered := result.(map[string]interface{})
	for name, url := range discovered {
		y.channelCache[name] = url.(string)
	}

	log.Printf("Successfully mapped %d channels via direct routing.", len(y.channelCache))
	return nil
}

// Tune executes the O(1) direct navigation.
func (y *YttvProvider) Tune(page playwright.Page, channelID string) error {
	target := strings.ToLower(channelID)

	// Note: We can implement Prismcast's "Prefix+Digit" affiliate mapping here later 
	// to handle local markets (e.g., matching "nbc 5" to "nbc").

	watchUrl, exists := y.channelCache[target]
	if !exists {
		// Fallback: If not in cache, try one more discovery pass
		_ = y.Discover(page)
		watchUrl, exists = y.channelCache[target]
		if !exists {
			return fmt.Errorf("channel '%s' not found in YouTube TV guide", channelID)
		}
	}

	log.Printf("Direct routing to: %s", watchUrl)
	if _, err := page.Goto(watchUrl); err != nil {
		return fmt.Errorf("failed to navigate to watch page: %v", err)
	}

	// Ensure the video player actually loads before handing off to FFmpeg
	if _, err := page.WaitForSelector("video", playwright.PageWaitForSelectorOptions{
		Timeout: playwright.Float(15000),
	}); err != nil {
		return fmt.Errorf("video player did not load")
	}

	return nil
}