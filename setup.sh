#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "Seting up Bendover Environment..."

# 1. Install .NET 10 SDK (Force update)
echo "Installing/Updating to .NET 10..."
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0


# 2. Docker check is now handled by the application at startup.
echo "Skipping shell-based Docker check."

# 3. Ensure bendover wrapper exists and is executable.
if [ ! -f "$SCRIPT_DIR/bendover" ]; then
  echo "Error: expected wrapper script at $SCRIPT_DIR/bendover but it was not found."
  exit 1
fi
chmod +x "$SCRIPT_DIR/bendover"
echo "Configured wrapper command: ./bendover"

echo "Setup complete. You can now run './bendover \"Add a unit test for run scoring\"'."
