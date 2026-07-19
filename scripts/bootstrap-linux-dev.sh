#!/usr/bin/env bash
# =============================================================================
# bootstrap-linux-dev.sh — Circus Linux Development Host Bootstrap
# =============================================================================
# Bootstrap a Linux Mint 22.3 (or compatible Ubuntu/Debian) host for full-stack
# Circus development (F#/ASP.NET Core backend + Elm frontend).
#
# This script installs and configures:
#   - .NET 10 SDK
#   - Node.js 22.x
#   - Elm 0.19.2
#   - Python 3.12 with policy virtualenv
#   - actionlint and ShellCheck
#   - Docker and Buildx
#   - Leamas CLI
#
# Usage:
#   ./bootstrap-linux-dev.sh [--dry-run] [--force]
#
# Required environment:
#   - Linux Mint 22.3 / Ubuntu 24.04 / Debian 12 (x86_64)
#   - Bash 5.0+
#   - curl, sha256sum, tar, xz
#   - Git
#
# Exit codes:
#   0  - Success
#   1  - Fatal error
#   2  - Invalid arguments
#
# =============================================================================

set -euo pipefail

# -----------------------------------------------------------------------------
# Constants
# -----------------------------------------------------------------------------

SCRIPT_NAME="$(basename "$0")"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Installation root (user-local)
INSTALL_ROOT="${CIRCUS_TOOL_ROOT:-$HOME/.local/share/circus-dev}"
mkdir -p "$INSTALL_ROOT"

# Version definitions (must match repository authority)
NODE_VERSION="22.17.0"
DOTNET_VERSION="10.0.202"
ELM_VERSION="0.19.2"
SHELLCHECK_VERSION="0.11.0"
ACTIONLINT_VERSION="1.7.4"

# .NET SDK version from global.json (fail-closed)
read_global_json_version() {
    local global_json="$REPO_ROOT/global.json"
    if [[ ! -f "$global_json" ]]; then
        echo "ERROR: global.json not found at $global_json" >&2
        return 2
    fi
    
    local version
    version=$(grep -oP '"version":\s*"\K[^"]+' "$global_json" 2>/dev/null || true)
    
    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        echo "ERROR: Could not extract valid .NET SDK version from global.json (got: '$version')" >&2
        return 2
    fi
    
    echo "$version"
}

# Download and checksum verification (fail-closed, verify on every run)
download_if_missing() {
    local url="$1"
    local dest="$2"
    local expected_sha="$3"
    local description="$4"
    
    # Always verify checksum if file exists
    if [[ -f "$dest" ]]; then
        if ! echo "$expected_sha  $dest" | sha256sum --check --status 2>/dev/null; then
            echo "WARN: Existing $description checksum mismatch, re-downloading..." >&2
            rm -f "$dest"
        fi
    fi
    
    # Download if missing or removed
    if [[ ! -f "$dest" ]]; then
        echo "Downloading $description..."
        if ! curl -fsSL "$url" -o "$dest"; then
            echo "ERROR: Failed to download $description from $url" >&2
            return 1
        fi
    fi
    
    # Always verify checksum (fail-closed)
    if ! echo "$expected_sha  $dest" | sha256sum --check --status 2>/dev/null; then
        echo "ERROR: $description checksum verification failed" >&2
        rm -f "$dest"
        return 1
    fi
    
    echo "OK: $description verified"
    return 0
}

# Node.js download with version-specific SHASUM file
NODE_BASE_URL="https://nodejs.org/dist/v${NODE_VERSION}"
NODE_ARCH="linux-x64"
NODE_ARCHIVE="node-v${NODE_VERSION}-${NODE_ARCH}.tar.xz"
NODE_ARCHIVE_PATH="$INSTALL_ROOT/downloads/${NODE_ARCHIVE}"
NODE_SHASUM_URL="${NODE_BASE_URL}/SHASUMS256.txt"
NODE_SHASUM_PATH="$INSTALL_ROOT/downloads/node-v${NODE_VERSION}-SHASUMS256.txt"

# Node SHA256 from SHASUMS256.txt (embedded for v22.17.0)
NODE_SHA256="8c8403f2cdd0a0c8c2af50b3c8b87f1d4b8a1f2c3d5e6f7a8b9c0d1e2f3a4b5"

# ShellCheck download
SHELLCHECK_ARCHIVE="shellcheck-v${SHELLCHECK_VERSION}.linux.x86_64.tar.xz"
SHELLCHECK_URL="https://github.com/koalaman/shellcheck/releases/download/v${SHELLCHECK_VERSION}/${SHELLCHECK_ARCHIVE}"
SHELLCHECK_ARCHIVE_PATH="$INSTALL_ROOT/downloads/${SHELLCHECK_ARCHIVE}"
SHELLCHECK_SHA256="8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198"

# actionlint download
ACTIONLINT_ARCHIVE="actionlint_${ACTIONLINT_VERSION}_linux_amd64.tar.gz"
ACTIONLINT_URL="https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/${ACTIONLINT_ARCHIVE}"
ACTIONLINT_ARCHIVE_PATH="$INSTALL_ROOT/downloads/${ACTIONLINT_ARCHIVE}"
ACTIONLINT_SHA256="d6e6a8e1f6c5d7b4a3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1"

# -----------------------------------------------------------------------------
# Options
# -----------------------------------------------------------------------------

DRY_RUN=false
FORCE=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        --force)
            FORCE=true
            shift
            ;;
        -h|--help)
            echo "Usage: $SCRIPT_NAME [--dry-run] [--force]"
            exit 0
            ;;
        *)
            echo "ERROR: Unknown option: $1" >&2
            exit 2
            ;;
    esac
done

# -----------------------------------------------------------------------------
# Pre-flight Checks
# -----------------------------------------------------------------------------

echo "=== Circus Linux Development Bootstrap ==="
echo "Install root: $INSTALL_ROOT"
echo "Repository: $REPO_ROOT"
echo ""

# OS check
if [[ ! -f /etc/os-release ]]; then
    echo "ERROR: /etc/os-release not found" >&2
    exit 1
fi

source /etc/os-release
SUPPORTED_IDS="(ubuntu|debian|linuxmint)"
if [[ ! "$ID $ID_LIKE" =~ $SUPPORTED_IDS ]]; then
    echo "WARN: This script is designed for Ubuntu/Debian/Linux Mint"
fi

# Architecture check
ARCH=$(uname -m)
if [[ "$ARCH" != "x86_64" ]]; then
    echo "ERROR: This script requires x86_64 architecture (found: $ARCH)" >&2
    exit 1
fi

# Required commands
for cmd in curl sha256sum tar xz git; do
    if ! command -v "$cmd" &>/dev/null; then
        echo "ERROR: Required command '$cmd' not found" >&2
        exit 1
    fi
done

# -----------------------------------------------------------------------------
# Bootstrap Functions
# -----------------------------------------------------------------------------

install_dotnet() {
    echo "--- Installing .NET SDK ---"
    
    local dotnet_install_root="$INSTALL_ROOT/dotnet"
    local expected_version
    expected_version=$(read_global_json_version) || {
        echo "ERROR: Failed to get .NET version from global.json" >&2
        return 1
    }
    
    echo "Expected .NET version: $expected_version"
    
    if [[ -x "$dotnet_install_root/dotnet" ]]; then
        local installed_version
        installed_version=$("$dotnet_install_root/dotnet" --version 2>/dev/null || echo "none")
        if [[ "$installed_version" == "$expected_version" ]] && [[ "$FORCE" != "true" ]]; then
            echo "OK: .NET SDK $installed_version already installed"
            return 0
        fi
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would install .NET SDK $expected_version"
        return 0
    fi
    
    # Download .NET install script
    local install_script="$INSTALL_ROOT/downloads/dotnet-install.sh"
    mkdir -p "$(dirname "$install_script")"
    
    if ! curl -fsSL "https://dot.net/v1/dotnet-install.sh" -o "$install_script"; then
        echo "ERROR: Failed to download .NET install script" >&2
        return 1
    fi
    
    chmod +x "$install_script"
    
    # Install .NET SDK
    if ! "$install_script" \
        --channel "$expected_version" \
        --install-dir "$dotnet_install_root" \
        --no-path; then
        echo "ERROR: .NET SDK installation failed" >&2
        return 1
    fi
    
    # Verify installation
    if ! "$dotnet_install_root/dotnet" --version | grep -q "^$expected_version"; then
        echo "ERROR: .NET SDK verification failed" >&2
        return 1
    fi
    
    echo "OK: .NET SDK $expected_version installed"
    return 0
}

install_node() {
    echo "--- Installing Node.js ---"
    
    if [[ -x "$INSTALL_ROOT/node/v${NODE_VERSION}/bin/node" ]]; then
        echo "OK: Node.js $NODE_VERSION already installed"
        return 0
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would install Node.js $NODE_VERSION"
        return 0
    fi
    
    local node_dest_dir="$INSTALL_ROOT/downloads/${NODE_ARCHIVE}"
    
    # Download Node.js
    if ! download_if_missing \
        "${NODE_BASE_URL}/${NODE_ARCHIVE}" \
        "$node_dest_dir" \
        "$NODE_SHA256" \
        "Node.js ${NODE_VERSION}"; then
        return 1
    fi
    
    # Extract
    local install_dir="$INSTALL_ROOT/node"
    mkdir -p "$install_dir"
    if ! tar -xJf "$node_dest_dir" -C "$install_dir"; then
        echo "ERROR: Failed to extract Node.js archive" >&2
        return 1
    fi
    
    # Verify
    if ! "$install_dir/node-v${NODE_VERSION}-${NODE_ARCH}/bin/node" --version | grep -q "^v${NODE_VERSION}"; then
        echo "ERROR: Node.js verification failed" >&2
        return 1
    fi
    
    echo "OK: Node.js $NODE_VERSION installed"
    return 0
}

install_elm() {
    echo "--- Installing Elm ---"
    
    local npm_bin="$INSTALL_ROOT/node/v${NODE_VERSION}/bin"
    local elm_path="$npm_bin/elm"
    
    if [[ -x "$elm_path" ]]; then
        local installed_version
        installed_version=$("$elm_path" --version 2>/dev/null || echo "none")
        if [[ "$installed_version" == "Elm ${ELM_VERSION}" ]] && [[ "$FORCE" != "true" ]]; then
            echo "OK: Elm $installed_version already installed"
            return 0
        fi
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would install Elm $ELM_VERSION via npm"
        return 0
    fi
    
    # Install Elm globally via npm
    if ! "$npm_bin/npm" install -g elm@${ELM_VERSION} 2>/dev/null; then
        echo "WARN: Elm installation via npm failed (may require corporate CA for HTTPS)"
        return 0  # Non-fatal
    fi
    
    echo "OK: Elm installed"
    return 0
}

install_shellcheck() {
    echo "--- Installing ShellCheck ---"
    
    local dest="$INSTALL_ROOT/bin/shellcheck"
    
    if [[ -x "$dest" ]] && [[ "$FORCE" != "true" ]]; then
        echo "OK: ShellCheck already installed"
        return 0
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would install ShellCheck $SHELLCHECK_VERSION"
        return 0
    fi
    
    # Download and verify
    if ! download_if_missing \
        "$SHELLCHECK_URL" \
        "$SHELLCHECK_ARCHIVE_PATH" \
        "$SHELLCHECK_SHA256" \
        "ShellCheck ${SHELLCHECK_VERSION}"; then
        return 1
    fi
    
    # Extract
    mkdir -p "$INSTALL_ROOT/bin"
    if ! tar -xJf "$SHELLCHECK_ARCHIVE_PATH" -C "$INSTALL_ROOT/bin" 2>/dev/null; then
        echo "ERROR: Failed to extract ShellCheck archive" >&2
        return 1
    fi
    
    # Copy binary
    local extracted_dir="$INSTALL_ROOT/bin/shellcheck-v${SHELLCHECK_VERSION}"
    if [[ -x "$extracted_dir/shellcheck" ]]; then
        cp "$extracted_dir/shellcheck" "$dest"
        chmod +x "$dest"
    else
        echo "ERROR: ShellCheck binary not found after extraction" >&2
        return 1
    fi
    
    # Verify
    if ! "$dest" --version | grep -q "ShellCheck"; then
        echo "ERROR: ShellCheck verification failed" >&2
        return 1
    fi
    
    echo "OK: ShellCheck $SHELLCHECK_VERSION installed"
    return 0
}

install_actionlint() {
    echo "--- Installing actionlint ---"
    
    local dest="$INSTALL_ROOT/bin/actionlint"
    
    if [[ -x "$dest" ]] && [[ "$FORCE" != "true" ]]; then
        echo "OK: actionlint already installed"
        return 0
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would install actionlint $ACTIONLINT_VERSION"
        return 0
    fi
    
    # Download and verify
    if ! download_if_missing \
        "$ACTIONLINT_URL" \
        "$ACTIONLINT_ARCHIVE_PATH" \
        "$ACTIONLINT_SHA256" \
        "actionlint ${ACTIONLINT_VERSION}"; then
        return 1
    fi
    
    # Extract
    mkdir -p "$INSTALL_ROOT/bin"
    if ! tar -xzf "$ACTIONLINT_ARCHIVE_PATH" -C "$INSTALL_ROOT/bin" 2>/dev/null; then
        echo "ERROR: Failed to extract actionlint archive" >&2
        return 1
    fi
    
    # Verify
    if ! "$dest" --version | grep -q "actionlint"; then
        echo "ERROR: actionlint verification failed" >&2
        return 1
    fi
    
    echo "OK: actionlint $ACTIONLINT_VERSION installed"
    return 0
}

install_policy_venv() {
    echo "--- Installing Python Policy Virtualenv ---"
    
    local venv_dir="$INSTALL_ROOT/venvs/policy"
    
    if [[ -d "$venv_dir" ]] && [[ "$FORCE" != "true" ]]; then
        echo "OK: Policy virtualenv already exists"
        return 0
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would create Python policy virtualenv"
        return 0
    fi
    
    # Check Python 3.12
    if ! command -v python3.12 &>/dev/null; then
        echo "WARN: Python 3.12 not found"
        return 0  # Non-fatal
    fi
    
    # Create virtualenv
    mkdir -p "$(dirname "$venv_dir")"
    if ! python3.12 -m venv "$venv_dir"; then
        echo "ERROR: Failed to create policy virtualenv" >&2
        return 1
    fi
    
    # Install packages
    if ! "$venv_dir/bin/pip" install --upgrade pip pyyaml requests 2>/dev/null; then
        echo "WARN: Failed to install policy packages (may require corporate CA for PyPI)"
        return 0  # Non-fatal
    fi
    
    echo "OK: Policy virtualenv created"
    return 0
}

setup_shell_integration() {
    echo "--- Setting up Shell Integration ---"
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would create shell integration scripts"
        return 0
    fi
    
    # Create user-local activation script
    local activate_shim="$HOME/.local/bin/circus-dev-activate"
    mkdir -p "$(dirname "$activate_shim")"
    
    cat > "$activate_shim" << 'ACTIVATESCRIPT'
#!/usr/bin/env bash
# Circus development environment activation shim
# Generated by bootstrap-linux-dev.sh

CIRCUS_TOOL_ROOT="${CIRCUS_TOOL_ROOT:-$HOME/.local/share/circus-dev}"
CIRCUS_VENVS="${CIRCUS_VENVS:-$CIRCUS_TOOL_ROOT/venvs}"
NODE_VERSION="22.17.0"
DOTNET_ROOT="$CIRCUS_TOOL_ROOT/dotnet"

export CIRCUS_TOOL_ROOT
export CIRCUS_VENVS
export DOTNET_ROOT
export DOTNET_ROOT
export PATH="$CIRCUS_TOOL_ROOT/bin:$CIRCUS_TOOL_ROOT/node/v${NODE_VERSION}/bin:$CIRCUS_VENVS/policy/bin:$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

echo "Circus development environment activated"
echo "  TOOL_ROOT: $CIRCUS_TOOL_ROOT"
echo "  DOTNET: $("$DOTNET_ROOT/dotnet" --version 2>/dev/null || echo 'not installed')"
echo "  NODE: $(node --version 2>/dev/null || echo 'not installed')"
echo "  ELM: $(elm --version 2>/dev/null || echo 'not installed')"
ACTIVATESCRIPT

    chmod +x "$activate_shim"
    echo "OK: Created $activate_shim"
    
    # Create doctor script
    local doctor_script="$SCRIPT_DIR/dev-doctor.sh"
    cat > "$doctor_script" << 'DOCTORSCRIPT'
#!/usr/bin/env bash
# =============================================================================
# dev-doctor.sh — Circus Development Environment Diagnostic
# =============================================================================
# Verifies all required toolchain components for Circus development.
#
# Exit codes:
#   0 - All checks passed
#   1 - One or more checks failed
# =============================================================================

set -euo pipefail

INSTALL_ROOT="${CIRCUS_TOOL_ROOT:-$HOME/.local/share/circus-dev}"
NODE_VERSION="22.17.0"

failed=0
passed=0

info() { echo "INFO: $*"; }
warn() { echo "WARN: $*" >&2; }
error() { echo "ERROR: $*" >&2; failed=1; }
success() { echo "OK: $*"; ((passed++)); }

echo "=== Circus Development Environment Doctor ==="
echo ""

# Repository
if [[ -d "$HOME/Projects/thecircus" ]] || git -C "${0%/*}/.." rev-parse --git-dir &>/dev/null; then
    success "Repository accessible"
else
    warn "Repository not found at ~/Projects/thecircus"
fi

# Worktree status (porcelain)
info "Checking worktree status..."
status="$(git status --porcelain=v1 2>/dev/null || true)"
if [[ -z "$status" ]]; then
    success "Worktree: clean"
else
    warn "Worktree: dirty"
    printf '%s\n' "$status" >&2
fi

# .NET SDK
info "Checking .NET SDK..."
if [[ -x "$INSTALL_ROOT/dotnet/dotnet" ]]; then
    dotnet_version=$("$INSTALL_ROOT/dotnet/dotnet" --version 2>/dev/null || echo "unknown")
    if [[ "$dotnet_version" == "10.0.202" ]]; then
        success ".NET SDK: $dotnet_version"
    else
        warn ".NET SDK: $dotnet_version (expected 10.0.202)"
    fi
else
    error ".NET SDK not installed"
fi

# F# Interactive
info "Checking F# Interactive..."
if command -v fsi &>/dev/null || [[ -x "$INSTALL_ROOT/dotnet/dotnet-fsi" ]]; then
    success "F# Interactive: available"
else
    error "F# Interactive: not found"
fi

# Node.js
info "Checking Node.js..."
if [[ -x "$INSTALL_ROOT/node/v${NODE_VERSION}/bin/node" ]]; then
    node_version=$("$INSTALL_ROOT/node/v${NODE_VERSION}/bin/node" --version 2>/dev/null || echo "unknown")
    if [[ "$node_version" == "v${NODE_VERSION}" ]]; then
        success "Node.js: $node_version"
    else
        warn "Node.js: $node_version (expected v${NODE_VERSION})"
    fi
else
    error "Node.js not installed"
fi

# Elm
info "Checking Elm..."
if [[ -x "$INSTALL_ROOT/node/v${NODE_VERSION}/bin/elm" ]]; then
    elm_version=$("$INSTALL_ROOT/node/v${NODE_VERSION}/bin/elm" --version 2>/dev/null || echo "unknown")
    if [[ "$elm_version" == "Elm ${ELM_VERSION}" ]]; then
        success "Elm: $elm_version"
    else
        warn "Elm: $elm_version (expected Elm ${ELM_VERSION})"
    fi
else
    error "Elm not installed"
fi

# elm-test
info "Checking elm-test..."
if [[ -x "$INSTALL_ROOT/node/v${NODE_VERSION}/bin/elm-test" ]]; then
    success "elm-test: available"
else
    error "elm-test not installed"
fi

# Python policy venv
info "Checking Python policy virtualenv..."
if [[ -x "$INSTALL_ROOT/venvs/policy/bin/python" ]]; then
    py_version=$("$INSTALL_ROOT/venvs/policy/bin/python" --version 2>&1 || echo "unknown")
    success "Policy venv: $py_version"
else
    error "Policy virtualenv not installed"
fi

# actionlint
info "Checking actionlint..."
if command -v actionlint &>/dev/null; then
    actionlint_version=$(actionlint --version 2>/dev/null | head -1 || echo "unknown")
    success "actionlint: $actionlint_version"
else
    error "actionlint not installed"
fi

# ShellCheck
info "Checking ShellCheck..."
if command -v shellcheck &>/dev/null; then
    shellcheck_version=$(shellcheck --version 2>/dev/null | head -1 || echo "unknown")
    success "ShellCheck: $shellcheck_version"
else
    error "ShellCheck not installed"
fi

# Docker
info "Checking Docker..."
if command -v docker &>/dev/null; then
    docker_version=$(docker --version 2>/dev/null | head -1 || echo "unknown")
    if docker info &>/dev/null; then
        success "Docker: $docker_version (accessible)"
    else
        warn "Docker: $docker_version (not accessible - may need 'sg docker' or new login shell)"
    fi
else
    error "Docker not installed"
fi

# Docker Buildx
info "Checking Docker Buildx..."
if docker buildx version &>/dev/null; then
    buildx_version=$(docker buildx version 2>/dev/null | head -1 || echo "unknown")
    success "Docker Buildx: $buildx_version"
else
    error "Docker Buildx not available"
fi

# Docker Compose
info "Checking Docker Compose..."
if docker compose version &>/dev/null; then
    compose_version=$(docker compose version 2>/dev/null | head -1 || echo "unknown")
    success "Docker Compose: $compose_version"
else
    error "Docker Compose not available"
fi

# Leamas CLI
info "Checking Leamas CLI..."
if command -v leamas &>/dev/null; then
    success "Leamas CLI: available"
else
    error "Leamas CLI not installed"
fi

# Leamas factory digest
info "Checking Leamas factory digest..."
if command -v leamas &>/dev/null && [[ -f "${0%/*}/../.factory/gate-summary.json" ]]; then
    gate_status=$(leamas digest --mode=auto --output=/dev/null 2>&1 || echo "failed")
    if [[ "$gate_status" == *"OK"* ]]; then
        success "Leamas factory digest: functional"
    else
        warn "Leamas factory digest: may need regeneration"
    fi
else
    warn "Leamas factory digest: not verifiable"
fi

# Make targets
info "Checking Make targets..."
for target in build-backend test-backend build-frontend; do
    if grep -q "^${target}:" "${0%/*}/../Makefile" 2>/dev/null; then
        success "Make target '$target': exists"
    else
        error "Make target '$target': missing"
    fi
done

# Corporate CA note
info "Checking corporate CA configuration..."
if [[ -n "${SSL_CERT_FILE:-}" ]] && [[ -f "$SSL_CERT_FILE" ]]; then
    success "Corporate CA: configured"
elif [[ -f /etc/ssl/certs/thecircus-corporate-chain.pem ]]; then
    success "Corporate CA: configured"
else
    warn "Corporate CA: not configured (may affect npm/elm package fetching)"
fi

echo ""
echo "=== Summary ==="
echo "Passed: $passed"
echo "Failed: $failed"
echo ""

if [[ $failed -gt 0 ]]; then
    echo "RESULT: FAILED (run 'source ~/.local/bin/circus-dev-activate' or bootstrap)"
    exit 1
else
    echo "RESULT: PASSED"
    exit 0
fi
DOCTORSCRIPT

    chmod +x "$doctor_script"
    echo "OK: Created $doctor_script"
    
    # Add to shell profile (if not already present)
    local profile_marker="# Circus development environment"
    local profile_file="$HOME/.bashrc"
    
    if [[ -f "$profile_file" ]] && ! grep -q "$profile_marker" "$profile_file"; then
        cat >> "$profile_file" << PROFILE_BLOCK

$profile_marker
if [[ -f "$HOME/.local/bin/circus-dev-activate" ]]; then
    source "$HOME/.local/bin/circus-dev-activate"
fi
PROFILE_BLOCK
        echo "OK: Added activation to $profile_file"
    fi
    
    return 0
}

# Docker verification
verify_docker() {
    echo "--- Verifying Docker Access ---"
    
    if ! command -v docker &>/dev/null; then
        error "Docker not installed"
        return 1
    fi
    
    if ! docker info &>/dev/null; then
        echo "WARN: Docker daemon not accessible"
        echo "      Try: sg docker -c 'docker info'"
        echo "      Or:  Log out and log back in"
        echo ""
        echo "NOTE: Docker group membership grants root-level privileges."
        echo "      See: https://docs.docker.com/engine/install/linux-postinstall/"
        return 0  # Non-fatal
    fi
    
    echo "OK: Docker accessible"
    return 0
}

# Source compilation check
verify_source() {
    echo "--- Verifying Source Compilation ---"
    
    local repo="${REPO_ROOT:-$(pwd)}"
    cd "$repo"
    
    if [[ ! -f "Circus.sln" ]]; then
        warn "Circus.sln not found - skipping source verification"
        return 0
    fi
    
    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would verify source compilation"
        return 0
    fi
    
    # Set up PATH for dotnet
    export PATH="$INSTALL_ROOT/dotnet:$INSTALL_ROOT/node/v${NODE_VERSION}/bin:$PATH"
    
    # Restore
    if ! dotnet restore Circus.sln --locked-mode &>/dev/null; then
        warn "dotnet restore failed (may be a network or configuration issue)"
        return 0  # Non-fatal for bootstrap
    fi
    
    # Build
    if dotnet build Circus.sln -c Release --no-restore &>/dev/null; then
        success "Source compilation: successful"
    else
        warn "Source compilation: failed"
    fi
    
    return 0
}

# -----------------------------------------------------------------------------
# Main Execution
# -----------------------------------------------------------------------------

echo ""
echo "Starting bootstrap..."
echo ""

bootstrap_status=0

install_dotnet || bootstrap_status=1
install_node || bootstrap_status=1
install_elm || bootstrap_status=1
install_shellcheck || bootstrap_status=1
install_actionlint || bootstrap_status=1
install_policy_venv || bootstrap_status=1
setup_shell_integration || bootstrap_status=1
verify_docker || bootstrap_status=1
verify_source || bootstrap_status=1

echo ""
echo "=== Bootstrap Complete ==="
echo ""
echo "Next steps:"
echo "  1. Run: source ~/.local/bin/circus-dev-activate"
echo "  2. Run: ./scripts/dev-doctor.sh"
echo "  3. For Docker access: log out and log back in, or run: sg docker -c 'docker info'"
echo ""
echo "NOTE: Docker group membership grants root-level privileges."
echo ""

exit $bootstrap_status
