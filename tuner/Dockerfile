# Stage 1: Build the Tuner Go binary
FROM golang:1.26 AS builder

WORKDIR /app
# We will create these Go files in the next step
COPY . .
RUN go mod init charon-tuner && \
    go get github.com/playwright-community/playwright-go@v0.4400.1 && \
    go mod tidy

RUN go build -o /tuner-app ./cmd/main.go

# Stage 2: Build the runtime environment
# Using the official Playwright image guarantees browser compatibility
FROM mcr.microsoft.com/playwright:v1.44.0-jammy

# Install Wayland, PipeWire, Cage, FFmpeg, and HW Transcoding drivers
RUN apt-get update && apt-get install -y \
    cage \
    pipewire \
    wireplumber \
    ffmpeg \
    intel-media-va-driver-non-free \
    mesa-va-drivers \
    dbus-x11 \
    libvulkan1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy the compiled Go binary from the builder stage
COPY --from=builder /tuner-app /usr/local/bin/tuner-app

# Copy our custom initialization script
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/entrypoint.sh && \
    chmod +x /usr/local/bin/entrypoint.sh

# Install the Playwright browsers (Chromium only to save space)
RUN npx playwright install chromium

EXPOSE 8081 8082

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]