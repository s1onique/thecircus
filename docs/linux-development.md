# Linux Development Environment Guide

## Overview

This guide covers setting up a Linux development environment for the Circus project, which consists of:
- **Backend**: F#/.NET 10 with ASP.NET Core
- **Frontend**: Elm 0.19.2 with Node.js 22.x
- **Database**: PostgreSQL (via Docker)
- **Containers**: Docker with Buildx

## Prerequisites

- Linux Mint 22.3, Ubuntu 24.04, or Debian 12 (x86_64)
- Bash 5.0+
- Git
- curl, sha256sum, tar, xz
- Docker with Buildx (for container development)
- Corporate CA certificate (for SSL-inspected networks)

## Quick Start

### Option 1: Automated Bootstrap

```bash
cd ~/Projects/thecircus
./scripts/bootstrap-linux-dev.sh
```

### Option 2: Manual Setup

Follow the sections below for manual installation.

## Toolchain Components

### .NET 10 SDK

The .NET SDK version is pinned in `global.json` and must match the repository version.

**Installation:**
```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- \
    --channel 10.0.202 \
    --install-dir ~/.local/share/circus-dev/dotnet
```

**Verification:**
```bash
~/.local/share/circus-dev/dotnet/dotnet --version
```

### Node.js 22.x

**Installation:**
```bash
curl -fsSL https://nodejs.org/dist/v22.17.0/node-v22.17.0-linux-x64.tar.xz | tar -xJ
mv node-v22.17.0-linux-x64 ~/.local/share/circus-dev/node/v22.17.0
```

**Verification:**
```bash
~/.local/share/circus-dev/node/v22.17.0/bin/node --version
```

### Elm 0.19.2

Elm is installed via npm:

```bash
~/.local/share/circus-dev/node/v22.17.0/bin/npm install -g elm@0.19.2
```

**Note:** If npm fails due to SSL certificate issues, see [Corporate SSL](#corporate-ssl) below.

**Verification:**
```bash
~/.local/share/circus-dev/node/v22.17.0/bin/elm --version
```

### Python 3.12 with Policy Virtualenv

**Installation:**
```bash
python3.12 -m venv ~/.local/share/circus-dev/venvs/policy
~/.local/share/circus-dev/venvs/policy/bin/pip install --upgrade pip pyyaml requests
```

**Verification:**
```bash
~/.local/share/circus-dev/venvs/policy/bin/python --version
```

### actionlint and ShellCheck

**actionlint:**
```bash
curl -fsSL https://github.com/rhysd/actionlint/releases/download/v1.7.4/actionlint_1.7.4_linux_amd64.tar.gz | tar -xz -C ~/.local/bin
```

**ShellCheck:**
```bash
curl -fsSL https://github.com/koalaman/shellcheck/releases/download/v0.11.0/shellcheck-v0.11.0.linux.x86_64.tar.xz | tar -xJ
cp shellcheck-v0.11.0/shellcheck ~/.local/bin/
```

**Verification:**
```bash
actionlint --version
shellcheck --version
```

## Environment Activation

### Source the Activation Script

```bash
source ~/.local/bin/circus-dev-activate
```

Or add to your `~/.bashrc` for automatic activation:

```bash
if [[ -f "$HOME/.local/bin/circus-dev-activate" ]]; then
    source "$HOME/.local/bin/circus-dev-activate"
fi
```

### PATH Components

The activation script sets:
- `~/.local/share/circus-dev/bin`
- `~/.local/share/circus-dev/node/v22.17.0/bin`
- `~/.local/share/circus-dev/venvs/policy/bin`
- `~/.local/share/circus-dev/dotnet`
- `~/.local/share/circus-dev/dotnet/tools`

## Building the Project

### Restore Dependencies

```bash
cd ~/Projects/thecircus
dotnet restore Circus.sln --locked-mode
```

### Build

```bash
dotnet build Circus.sln -c Release
```

### Run Tests

```bash
# Backend tests
dotnet test tests/Circus.Domain.Tests -c Release
dotnet test tests/Circus.Contracts.Tests -c Release
dotnet test tests/Circus.Application.Tests -c Release

# Elm tests
cd web
./node_modules/.bin/elm-test --compiler ./node_modules/.bin/elm
```

### Docker Containers

**Backend:**
```bash
make container-build-backend
make container-smoke
```

**Frontend:**
```bash
# Requires corporate CA certificate
docker build --secret id=spbnix-ca,src=/path/to/corporate-ca.pem \
    --platform linux/amd64 \
    --file Dockerfile.frontend \
    --tag circus-frontend:local .
```

## Corporate SSL

If your network uses SSL inspection, you may need to configure a corporate CA certificate for external package fetching.

### Option 1: Environment Variable

```bash
export SSL_CERT_FILE=/etc/ssl/certs/your-corporate-chain.pem
npm install elm
```

### Option 2: Docker Secret

For container builds with Elm:

```bash
docker build --secret id=spbnix-ca,src=/path/to/corporate-ca.pem ...
```

## Docker Configuration

### Docker Group Access

Users in the `docker` group have root-level privileges. See [Docker's documentation](https://docs.docker.com/engine/install/linux-postinstall/).

### Fresh Login Shell

To ensure Docker access after adding your user to the group:

1. Log out completely
2. Log back in
3. Verify: `docker info`

Or use `sg` for a temporary group activation:

```bash
sg docker -c 'docker info'
```

## Troubleshooting

### Elm Package Fetching Fails

**Symptom:**
```
InternalException (HandshakeFailed (Error_Protocol "certificate has unknown CA" UnknownCa))
```

**Solution:** Configure corporate CA certificate for npm/Elm.

### Docker Not Accessible

**Symptom:**
```
permission denied while trying to connect to the Docker daemon socket
```

**Solution:** Add your user to the `docker` group and log out/in, or use:
```bash
sg docker -c 'docker info'
```

### dotnet Restore Fails

**Symptom:**
```
Unable to load the service layer for source
```

**Solution:** Check NuGet source configuration in `~/.nuget/NuGet/NuGet.Config`.

### Testcontainers Fails

**Symptom:**
```
Docker is either not running or misconfigured
```

**Solution:** Ensure Docker daemon is running and accessible. See [Docker Configuration](#docker-configuration).

## Security Notes

1. **Docker Group**: Membership grants root privileges. Use cautiously.
2. **Corporate SSL Inspection**: Certificate chains should be from trusted sources only.
3. **PATH Integrity**: Ensure activation scripts correctly construct PATH.

## Scripts Reference

| Script | Purpose |
|--------|---------|
| `bootstrap-linux-dev.sh` | Automated toolchain setup |
| `dev-doctor.sh` | Environment verification |
| `activate-linux-dev.sh` | Environment activation helper |

## See Also

- [ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01](../acts/ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01.md)
- [Factory Documentation](../factory/README.md)
- [Architecture](../architecture.md)
