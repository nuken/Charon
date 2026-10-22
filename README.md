<p align="center">
  <img src="docs/assets/header.png" width="100%" alt="Nucleus HTPC Banner" />
</p>

# Charon

Charon is a C# desktop application designed to manage, tune, and stream content from web-based media providers.

## Features

* Built with C# and WPF for the Windows desktop interface.
* Includes an automated Chrome tuner implementation for handling browser-based streams.
* Features a built-in server component for managing connections.
* Supports modular providers such as YouTube TV with custom channel listings and styling.
* Process audio tapping capabilities to capture and handle stream audio.


### Step 1: Download and Install Charon

1. Go to the official releases page: **[https://github.com/nuken/Charon/releases](https://github.com/nuken/Charon/releases)**.
2. Download the latest release installer.
3. Run the installer. Make sure you also have Google Chrome installed on the machine, as Charon relies on it for browser session handling.

---

### Step 2: Choosing Your Provider (YouTube TV or Spectrum TV)

Charon supports 2 streaming providers out of the box.

1. Launch **Charon**.
2. Look at the provider selection dropdown near the top of the dashboard interface.
3. Select your streaming provider of choice: **YouTube TV** or **Spectrum TV**.

---

### Step 3: Logging In to Your Provider

Before scanning channels, Charon needs an active authentication session from your account:

1. Select your target provider from the dropdown menu.
2. Click the **"1. Login to Provider"** button.
3. A visible Chrome window will open pointing to the provider's sign-in page (e.g., `tv.youtube.com` or `watch.spectrum.net`).
4. Sign in with your account credentials and verify that live video plays correctly.
5. **Close that Chrome login window completely** once you are signed in so that Charon can safely access the profile lock files.

---

### Step 4: Scanning and Loading `channels.json`

Once authenticated, you can pull your live channel lineups directly into Charon:

1. Click the **"2. Scan & Sync Lineup"** button.
2. **Behavior by Provider:**
* **For Spectrum TV:** Charon will instantly scan your entitled channel lineup. Spectrum provides native Gracenote station IDs (`tmsid`) directly in its streams, so it skips external server prompts and saves the configuration immediately.
* **For YouTube TV:** Charon will prompt you to enter your **Channels DVR Server IP and Port** (default port is `8089`). It then scrapes your live YouTube TV guide grid via Chrome DevTools Protocol and automatically queries your Channels DVR server to match and resolve your local Gracenote `StationId` values.


3. When the scan finishes, a completion prompt will show how many channels and Gracenote IDs were successfully mapped and saved to `channels.json`.

---

### Step 5: Fixing Missing or Incorrect Guide Data (YouTube TV)

Because YouTube TV uses local marketing names (e.g., "FOX 46" or "WSOC 9") while broadcast databases use strict FCC callsigns, automated matching handles about 95% of channels, but some will require manual adjustment:

1. Load your Charon M3U source into your **Channels DVR** server.
2. Navigate to your Channels DVR web interface, open your Charon source, and click **Manage Lineup**.
3. Locate any channels with missing or incorrect guide information.
4. Click the **+** (search) icon next to the channel, type in the correct call sign or station name, and select the proper match from the dropdown. Channels DVR will lock in and remember that custom mapping permanently.
