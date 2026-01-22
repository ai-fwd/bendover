# Bendover

A self-evolving, sandboxed coding agent system.

## Prerequisites

- **.NET 8.0 SDK** (or later)
- **Docker Desktop** (Windows) or **Docker Engine** (Linux/WSL)
  - Ensure Docker is running and accessible.

## Setup & Run

### 1. Build the Solution

```bash
dotnet build
```

### 2. Run Integration Tests

The tests require Docker to be running.

```bash
dotnet test
```

### 3. Run the Server

```bash
dotnet run --project src/Bendover.Presentation.Server
```

### 4. Run the CLI Client

```bash
dotnet run --project src/Bendover.Presentation.CLI
```

---

## WSL Setup Guide

To quickly bootstrap your environment (install .NET 8, check Docker), run the setup script:

```bash
chmod +x setup.sh
./setup.sh
```

### Manual Install (if script fails)

```bash
# Remove previous versions (if any)
sudo apt-get remove -y dotnet* aspnetcore* netstandard*

# Add Microsoft package repository
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Install .NET SDK
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0
```

### Troubleshooting Docker in WSL

If you get access denied errors connecting to Docker:

1. **Enable WSL Integration** in Docker Desktop settings (if using Docker Desktop on Windows).
2. Or, if running native Docker in WSL, ensure your user is in the docker group:
   ```bash
   sudo usermod -aG docker $USER
   newgrp docker
   ```
3. Verify connectivity:
   ```bash
   docker run --rm hello-world
   ```
