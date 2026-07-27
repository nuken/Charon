#!/bin/bash
set -e

echo "[Tuner Init] Booting Linux Subsystems..."

# 1. Establish the XDG runtime directory (Required for Wayland and PipeWire)
export XDG_RUNTIME_DIR=/tmp/wayland-runtime
mkdir -p $XDG_RUNTIME_DIR
chmod 0700 $XDG_RUNTIME_DIR

# 2. Start the system D-Bus (Required for component communication)
mkdir -p /run/dbus
dbus-daemon --system &
eval $(dbus-launch --sh-syntax)

# 3. Start PipeWire in the background (Our virtual audio cable)
export PIPEWIRE_RUNTIME_DIR=$XDG_RUNTIME_DIR
pipewire &
sleep 1 # Give the daemon a moment to allocate the virtual sinks

# 4. Launch the Go Tuner app inside the Cage Wayland compositor
# Cage creates a headless virtual display, and our Go app (running Playwright)
# will fill that display natively, allowing FFmpeg to capture it.
echo "[Tuner Init] Launching Cage Compositor and Go Application..."
exec cage -- /usr/local/bin/tuner-app