# The runtime image for the Bendover Agent Sandbox
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Install dotnet-script globally
RUN dotnet tool install -g dotnet-script

# Ensure tools are in PATH
ENV PATH="$PATH:/root/.dotnet/tools"

# Set working directory
WORKDIR /app
