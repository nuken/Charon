# Stage 1: Build the Manager Go binary
FROM golang:1.26 AS builder

WORKDIR /app

# Copy module files and download dependencies
COPY go.mod go.sum ./
RUN go mod download

# Copy the rest of the manager source code
COPY . .

# Compile the Go application statically
RUN CGO_ENABLED=0 GOOS=linux go build -o manager-app ./cmd/main.go

# Stage 2: Create the lightweight runtime image
FROM alpine:latest

WORKDIR /app

# Copy the compiled binary from the builder stage
COPY --from=builder /app/manager-app .

# Copy the channels lineup file so the Manager can read it
COPY --from=builder /app/channels.json .

EXPOSE 8080

CMD ["./manager-app"]