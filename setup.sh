#!/bin/bash
set -e

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

echo "Setup complete. You can now run 'dotnet build'."
