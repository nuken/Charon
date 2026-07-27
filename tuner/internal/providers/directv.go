package providers

import (
	"fmt"
	"log"
	"strings"

	"github.com/playwright-community/playwright-go"
)

type DirecTVProvider struct {}

func NewDirecTVProvider() *DirecTVProvider {
	return &DirecTVProvider{}
}

func (d *DirecTVProvider) Name() string {
	return "DirecTV Stream"
}

func (d *DirecTVProvider) Tune(page playwright.Page, channelName string) error {
	log.Printf("Executing DirecTV tuning sequence for: %s", channelName)

	// 1. Wait for the logo column to render in the DOM
	_, err := page.WaitForSelector(`[aria-label^="view "]`, playwright.PageWaitForSelectorOptions{
		Timeout: playwright.Float(15000),
		State:   playwright.WaitForSelectorStateVisible,
	})
	if err != nil {
		return fmt.Errorf("guide grid did not load: %v", err)
	}

	// 2. Locate the specific channel logo using case-insensitive semantic matching
	// e.g., aria-label="view CNN"
	targetLabel := fmt.Sprintf("view %s", strings.ToLower(channelName))
	
	// We evaluate the click directly in the browser context. 
	// This bypasses invisible coordinate-blocking overlays that break page.Mouse().Click()
	clicked, err := page.Evaluate(`(targetLabel) => {
		const logos = Array.from(document.querySelectorAll('[aria-label^="view "]'));
		for (const logo of logos) {
			const label = (logo.getAttribute("aria-label") || "").toLowerCase();
			if (label === targetLabel || label.startsWith(targetLabel + "-")) {
				logo.scrollIntoView({ behavior: "instant", block: "center" });
				logo.click();
				return true;
			}
		}
		return false;
	}`, targetLabel)

	if err != nil || clicked == false {
		return fmt.Errorf("could not find or click channel logo for: %s", channelName)
	}

	// 3. Wait for the "On Now" mini-guide overlay to appear
	_, err = page.WaitForSelector(`[aria-label^="on now,"]`, playwright.PageWaitForSelectorOptions{
		Timeout: playwright.Float(10000),
		State:   playwright.WaitForSelectorStateVisible,
	})
	if err != nil {
		return fmt.Errorf("mini-guide play button did not appear")
	}

	// 4. Click the inner focusable element to start playback
	started, err := page.Evaluate(`() => {
		const onNow = document.querySelector('[aria-label^="on now,"]');
		if (!onNow) return false;
		
		// The outer container has the aria-label, but the clickable Pressable has tabindex="0"
		const pressable = onNow.querySelector('[tabindex="0"]') || onNow;
		pressable.click();
		return true;
	}`)

	if err != nil || started == false {
		return fmt.Errorf("failed to click the final play button")
	}

	log.Println("DirecTV tuning sequence completed successfully.")
	return nil
}