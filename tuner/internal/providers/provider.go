package providers

import "github.com/playwright-community/playwright-go"

// Provider defines the standard contract for all streaming services.
type Provider interface {
	// Name returns the display name of the provider.
	Name() string
	
	// Tune executes the provider-specific DOM navigation to start a live stream.
	Tune(page playwright.Page, channelName string) error
}