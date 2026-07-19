# Linux Development Environment Guide

## Overview

This guide covers setting up a Linux development environment for the Circus project, which consists of:
- **Backend**: F#/.NET 10 with ASP.NET Core
- **Frontend**: Elm 0.19.2 with Node.js 22.x
- **Database**: PostgreSQL (via Docker)
- **Containers**: Docker with Buildx

## Security and Reproducibility Policy

This project follows strict supply-chain security practices:
- **Download before execution**: All archives are downloaded to disk first, then verified
- **Checksum verification**: All downloads are verified against official checksums (parsed from version-specific checksum files)
- **Repository authority**: Tool versions are derived from `global.json`, `Dockerfile.frontend`, `web/elm.json`, and `eng/devhost-toolchain.json`
- **User-local installation**: Tools are installed under `~/.local/share/circus-dev/` to avoid system-wide side effects
- **Pinned versions**: All tool versions are explicitly pinned and documented

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
make dev-bootstrap-linux
```

### Option 2: Manual Setup

Follow the sections below for manual installation.

## Toolchain Components

### .NET 10 SDK

The .NET SDK version is pinned in `global.json` and must match the repository version.

**Installation:**

```bash
# Download install script
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh

# Install (using --version for exact version)
/tmp/dotnet-install.sh \
    --version "$(grep -oP '"version":\s*"\K[0-9]+\.[0-9]+\.[0-9]+' global.json)" \
    --install-dir ~/.local/share/circus-dev/dotnet

# Clean up
rm /tmp/dotnet-install.sh
```

**Verification:**

```bash
~/.local/share/circus-dev/dotnet/dotnet --version
```

### Node.js 22.x

Node.js versions are verified against the official SHASUMS256.txt file from nodejs.org.

**Installation:**

```bash
NODE_VERSION="22.17.0"
NODE_ARCHIVE="node-v${NODE_VERSION}-linux-x64.tar.xz"
INSTALL_DIR="$HOME/.local/share/circus-dev/node/v${NODE_VERSION}"

# Download official SHASUMS256.txt
curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/SHASUMS256.txt" \
    -o /tmp/SHASUMS256.txt

# Extract checksum for our archive
EXPECTED_SHA=$(awk -v archive="$NODE_ARCHIVE" '$2 == archive { print $1 }' /tmp/SHASUMS256.txt)

# Download archive
curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/${NODE_ARCHIVE}" \
    -o /tmp/"$NODE_ARCHIVE"

# Verify checksum
echo "$EXPECTED_SHA  /tmp/$NODE_ARCHIVE" | sha256sum -c

# Extract
mkdir -p "$INSTALL_DIR"
tar -xJf /tmp/"$NODE_ARCHIVE" -C "$INSTALL_DIR" --strip-components=1

# Clean up
rm -f /tmp/SHASUMS256.txt /tmp/"$NODE_ARCHIVE"
```

**Verification:**

```bash
~/.local/share/circus-dev/node/v22.17.0/bin/node --version
```

### Elm 0.19.2

Elm is installed via npm using the version from package.json:

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

**actionlint (v1.7.12):**

```bash
# Download official release
curl -fsSL https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz \
    -o /tmp/actionlint.tar.gz

# Verify checksum (from GitHub release page)
echo "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  /tmp/actionlint.tar.gz" \
    | sha256sum -c

# Extract to user bin
tar -xzf /tmp/actionlint.tar.gz -C ~/.local/bin
rm /tmp/actionlint.tar.gz
```

**ShellCheck (v0.11.0):**

```bash
curl -fsSL https://github.com/koalaman/shellcheck/releases/download/v0.11.0/shellcheck-v0.11.0.linux.x86_64.tar.xz \
    -o /tmp/shellcheck.tar.xz

# Verify checksum
echo "8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198  /tmp/shellcheck.tar.xz" \
    | sha256sum -c

tar -xJf /tmp/shellcheck.tar.xz -C /tmp
cp /tmp/shellcheck-v0.11.0/shellcheck ~/.local/bin/
rm -rf /tmp/shellcheck*
```

**Verification:**

```bash
actionlint --version
shellcheck --version
```

## Environment Activation

### Render or Install the Managed Environment

Activate the environment in the current Bash or Zsh session:

```bash
eval "$(./scripts/circus-dev env)"
```

To reconcile the managed block in the detected shell profile:

```bash
./scripts/circus-dev install-shell-hook
```

Use `--shell bash` or `--shell zsh` when explicit selection is required.

### PATH Components

The generated environment sets:
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
3. Verify: `docker info` (NOT `sg docker -c 'docker info'`)

**Note:** The development doctor (`make dev-doctor`) tests direct Docker access, not the `sg docker` workaround. A clean login is required for the doctor to pass.

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

**Solution:** Add your user to the `docker` group and log out/in. The `sg docker` workaround will not satisfy the doctor check because it masks the underlying permission issue.

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
4. **Checksum Verification**: Always verify downloaded archives against official checksums.

## DevHost Command Reference

| Command | Purpose |
|---------|---------|
| `make dev-bootstrap-linux` | Reconcile the user-local development toolchain |
| `make dev-bootstrap-check-linux` | Validate repository authority files without installation |
| `make dev-doctor` | Run fail-closed host and toolchain diagnostics |
| `./scripts/circus-dev env` | Render activation exports for the detected shell |
| `./scripts/circus-dev install-shell-hook` | Reconcile the managed Bash/Zsh profile block |

## See Also

- [ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01](./acts/ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01.md)
- [Factory Documentation](../factory/README.md)
- [Architecture](../architecture.md)
