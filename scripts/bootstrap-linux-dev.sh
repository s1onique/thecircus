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
#   - Elm 0.19.2 (via locked repository restore)
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
# Logging helpers (fail-closed: all messages go to stderr)
# -----------------------------------------------------------------------------

log_info() {
    printf 'INFO: %s\n' "$*" >&2
}

log_warn() {
    printf 'WARN: %s\n' "$*" >&2
}

log_error() {
    printf 'ERROR: %s\n' "$*" >&2
}

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
SHELLCHECK_VERSION="0.11.0"
ACTIONLINT_VERSION="1.7.12"
PIP_VERSION="24.0"
PYYAML_VERSION="6.0.1"

# .NET SDK version from global.json (fail-closed)
read_global_json_version() {
    local global_json="$REPO_ROOT/global.json"
    if [[ ! -f "$global_json" ]]; then
        log_error "global.json not found at $global_json"
        return 2
    fi

    local version
    version=$(grep -oP '"version":\s*"\K[^"]+' "$global_json" 2>/dev/null || true)

    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        log_error "Could not extract valid .NET SDK version from global.json (got: '$version')"
        return 2
    fi

    echo "$version"
}

# Elm version from web/package.json (fail-closed)
read_elm_version() {
    local package_json="$REPO_ROOT/web/package.json"
    if [[ ! -f "$package_json" ]]; then
        log_error "web/package.json not found at $package_json"
        return 2
    fi

    local version
    version=$(grep -oP '"elm"\s*:\s*"\K[0-9+\.-]+' "$package_json" 2>/dev/null || true)

    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9-]+$ ]]; then
        log_error "Could not extract valid Elm version from web/package.json (got: '$version')"
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
            log_warn "Existing $description checksum mismatch, re-downloading..."
            rm -f "$dest"
        fi
    fi

    # Download if missing or removed
    if [[ ! -f "$dest" ]]; then
        log_info "Downloading $description..."
        if ! curl -fsSL "$url" -o "$dest"; then
            log_error "Failed to download $description from $url"
            return 1
        fi
    fi

    # Always verify checksum (fail-closed)
    if ! echo "$expected_sha  $dest" | sha256sum --check --status 2>/dev/null; then
        log_error "$description checksum verification failed"
        rm -f "$dest"
        return 1
    fi

    log_info "OK: $description verified"
    return 0
}

# Node.js download with version-specific SHASUM file
NODE_BASE_URL="https://nodejs.org/dist/v${NODE_VERSION}"
NODE_ARCH="linux-x64"
NODE_ARCHIVE="node-v${NODE_VERSION}-${NODE_ARCH}.tar.xz"
NODE_ARCHIVE_PATH="$INSTALL_ROOT/downloads/${NODE_ARCHIVE}"
NODE_SHASUM_URL="${NODE_BASE_URL}/SHASUMS256.txt"
NODE_SHASUM_PATH="$INSTALL_ROOT/downloads/node-v${NODE_VERSION}-SHASUMS256.txt"

# ShellCheck download
SHELLCHECK_ARCHIVE="shellcheck-v${SHELLCHECK_VERSION}.linux.x86_64.tar.xz"
SHELLCHECK_URL="https://github.com/koalaman/shellcheck/releases/download/v${SHELLCHECK_VERSION}/${SHELLCHECK_ARCHIVE}"
SHELLCHECK_ARCHIVE_PATH="$INSTALL_ROOT/downloads/${SHELLCHECK_ARCHIVE}"
SHELLCHECK_SHA256="8c3be12b05d5c177a04c29e3c78ce89ac86f1595681cab149b65b97c4e227198"

# actionlint download
ACTIONLINT_ARCHIVE="actionlint_${ACTIONLINT_VERSION}_linux_amd64.tar.gz"
ACTIONLINT_URL="https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/${ACTIONLINT_ARCHIVE}"
ACTIONLINT_ARCHIVE_PATH="$INSTALL_ROOT/downloads/${ACTIONLINT_ARCHIVE}"
ACTIONLINT_SHA256="8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8"

# -----------------------------------------------------------------------------
# Options
# -----------------------------------------------------------------------------

DRY_RUN=false
FORCE=false
CHECK_MODE=false

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
        --check)
            CHECK_MODE=true
            shift
            ;;
        -h|--help)
            echo "Usage: $SCRIPT_NAME [--dry-run] [--force] [--check]"
            echo ""
            echo "Options:"
            echo "  --dry-run  Show what would be installed without making changes"
            echo "  --force    Force reinstallation of existing components"
            echo "  --check    Verify prerequisites only (no installation)"
            echo "  -h, --help Show this help message"
            exit 0
            ;;
        *)
            log_error "Unknown option: $1"
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
    log_error "/etc/os-release not found"
    exit 1
fi

source /etc/os-release
SUPPORTED_IDS="(ubuntu|debian|linuxmint)"
if [[ ! "$ID $ID_LIKE" =~ $SUPPORTED_IDS ]]; then
    log_warn "This script is designed for Ubuntu/Debian/Linux Mint"
fi

# Architecture check (fail-closed)
ARCH=$(uname -m)
if [[ "$ARCH" != "x86_64" ]]; then
    log_error "This script requires x86_64 architecture (found: $ARCH)"
    exit 2
fi

# Required commands (fail-closed)
for cmd in curl sha256sum tar xz git; do
    if ! command -v "$cmd" &>/dev/null; then
        log_error "Required command '$cmd' not found"
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
        log_error "Failed to get .NET version from global.json"
        return 1
    }

    log_info "Expected .NET version: $expected_version"

    # Check if already installed with correct version
    if [[ -x "$dotnet_install_root/dotnet" ]]; then
        local installed_version
        installed_version=$("$dotnet_install_root/dotnet" --version 2>/dev/null || echo "none")
        if [[ "$installed_version" == "$expected_version" ]] && [[ "$FORCE" != "true" ]]; then
            log_info "OK: .NET SDK $installed_version already installed"
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
        log_error "Failed to download .NET install script"
        return 1
    fi

    chmod +x "$install_script"

    # Install .NET SDK using --version (exact version as per Microsoft docs)
    if ! "$install_script" \
        --version "$expected_version" \
        --install-dir "$dotnet_install_root" \
        --no-path; then
        log_error ".NET SDK installation failed"
        return 1
    fi

    # Verify installation (fail-closed)
    local verified_version
    verified_version=$("$dotnet_install_root/dotnet" --version 2>/dev/null || echo "none")
    if [[ "$verified_version" != "$expected_version" ]]; then
        log_error ".NET SDK verification failed: expected $expected_version, got $verified_version"
        return 1
    fi

    log_info "OK: .NET SDK $expected_version installed"
    return 0
}

install_node() {
    echo "--- Installing Node.js ---"

    local node_install_dir="$INSTALL_ROOT/node/v${NODE_VERSION}"

    # Check if already installed with correct version (fail-closed)
    if [[ -x "$node_install_dir/bin/node" ]]; then
        local installed_version
        installed_version=$("$node_install_dir/bin/node" --version 2>/dev/null || echo "none")
        installed_version="${installed_version#v}"  # Strip 'v' prefix for comparison

        if [[ "$installed_version" == "$NODE_VERSION" ]] && [[ "$FORCE" != "true" ]]; then
            log_info "OK: Node.js v$installed_version already installed"
            return 0
        fi
    fi

    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would install Node.js $NODE_VERSION"
        return 0
    fi

    local node_dest_dir="$INSTALL_ROOT/downloads/${NODE_ARCHIVE}"

    # Download SHASUMS256.txt first to get the official checksum from Node.js
    mkdir -p "$(dirname "$NODE_SHASUM_PATH")"
    if ! curl -fsSL "$NODE_SHASUM_URL" -o "$NODE_SHASUM_PATH" 2>/dev/null; then
        log_error "Failed to download Node.js SHASUMS256.txt"
        return 1
    fi

    # Extract the expected checksum for our archive from official manifest
    local expected_sha
    expected_sha=$(awk -v archive="$NODE_ARCHIVE" '$2 == archive { print $1; exit }' "$NODE_SHASUM_PATH" 2>/dev/null || true)

    if [[ ! "$expected_sha" =~ ^[0-9a-f]{64}$ ]]; then
        log_error "Checksum missing for $NODE_ARCHIVE in SHASUMS256.txt"
        return 1
    fi

    # Download Node.js archive with verification
    if ! download_if_missing \
        "${NODE_BASE_URL}/${NODE_ARCHIVE}" \
        "$node_dest_dir" \
        "$expected_sha" \
        "Node.js ${NODE_VERSION}"; then
        return 1
    fi

    # Extract with --strip-components=1 into the canonical directory
    rm -rf "$node_install_dir"
    mkdir -p "$node_install_dir"
    if ! tar -xJf "$node_dest_dir" -C "$node_install_dir" --strip-components=1; then
        log_error "Failed to extract Node.js archive"
        return 1
    fi

    # Verify installation (fail-closed)
    local verified_version
    verified_version=$("$node_install_dir/bin/node" --version 2>/dev/null || echo "none")
    if [[ "$verified_version" != "v${NODE_VERSION}" ]]; then
        log_error "Node.js verification failed: expected v${NODE_VERSION}, got $verified_version"
        return 1
    fi

    log_info "OK: Node.js $NODE_VERSION installed"
    return 0
}

install_elm() {
    echo "--- Installing Elm (via locked repository restore) ---"

    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would restore Elm via npm ci in web/"
        return 0
    fi

    local web_dir="$REPO_ROOT/web"
    if [[ ! -d "$web_dir" ]]; then
        log_error "web/ directory not found at $web_dir"
        return 1
    fi

    # Use locked repository restore instead of global installation
    cd "$web_dir"

    # Verify package-lock.json exists for locked restoration
    if [[ ! -f "package-lock.json" ]]; then
        log_error "package-lock.json not found - Elm installation requires locked dependencies"
        return 1
    fi

    # Use npm ci for locked restoration (not global npm install)
    if ! npm ci --ignore-scripts 2>/dev/null; then
        log_error "npm ci failed in web/ directory"
        cd "$REPO_ROOT"
        return 1
    fi

    # Explicitly invoke Elm installer (required for elm package)
    if [[ -x "./node_modules/.bin/elm" ]]; then
        if ! node ./node_modules/elm/install.js 2>/dev/null; then
            log_error "Elm platform binary installation failed"
            cd "$REPO_ROOT"
            return 1
        fi
    else
        log_error "Elm npm package not properly installed"
        cd "$REPO_ROOT"
        return 1
    fi

    # Verify Elm version matches repository authority (fail-closed)
    local expected_elm_version
    expected_elm_version=$(read_elm_version) || {
        log_error "Failed to get Elm version from web/package.json"
        cd "$REPO_ROOT"
        return 1
    }

    local actual_elm_version
    actual_elm_version=$("./node_modules/.bin/elm" --version 2>/dev/null || echo "ERROR")

    # Accept both "0.19.2" and "Elm 0.19.2" formats
    if [[ "$actual_elm_version" != "Elm ${expected_elm_version}" ]] && \
       [[ "$actual_elm_version" != "${expected_elm_version}" ]]; then
        log_error "Elm version mismatch: expected Elm $expected_elm_version, got $actual_elm_version"
        cd "$REPO_ROOT"
        return 1
    fi

    cd "$REPO_ROOT"
    log_info "OK: Elm $actual_elm_version installed via locked repository"
    return 0
}

install_shellcheck() {
    echo "--- Installing ShellCheck ---"

    local dest="$INSTALL_ROOT/bin/shellcheck"

    # Check if already installed with correct version (fail-closed)
    if [[ -x "$dest" ]]; then
        local installed_version
        installed_version=$("$dest" --version 2>/dev/null | head -1 || echo "none")
        if [[ "$installed_version" == *"v${SHELLCHECK_VERSION}"* ]] && [[ "$FORCE" != "true" ]]; then
            log_info "OK: ShellCheck $SHELLCHECK_VERSION already installed"
            return 0
        fi
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
        log_error "Failed to extract ShellCheck archive"
        return 1
    fi

    # Copy binary
    local extracted_dir="$INSTALL_ROOT/bin/shellcheck-v${SHELLCHECK_VERSION}"
    if [[ -x "$extracted_dir/shellcheck" ]]; then
        cp "$extracted_dir/shellcheck" "$dest"
        chmod +x "$dest"
    else
        log_error "ShellCheck binary not found after extraction"
        return 1
    fi

    # Verify installation (fail-closed)
    local verified_version
    verified_version=$("$dest" --version 2>/dev/null | head -1 || echo "none")
    if [[ "$verified_version" != *"ShellCheck"* ]]; then
        log_error "ShellCheck verification failed"
        return 1
    fi

    log_info "OK: ShellCheck $SHELLCHECK_VERSION installed"
    return 0
}

install_actionlint() {
    echo "--- Installing actionlint ---"

    local dest="$INSTALL_ROOT/bin/actionlint"

    # Check if already installed with correct version (fail-closed)
    if [[ -x "$dest" ]]; then
        local installed_version
        installed_version=$("$dest" --version 2>/dev/null | head -1 || echo "none")
        if [[ "$installed_version" == *"v${ACTIONLINT_VERSION}"* ]] && [[ "$FORCE" != "true" ]]; then
            log_info "OK: actionlint $ACTIONLINT_VERSION already installed"
            return 0
        fi
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
        log_error "Failed to extract actionlint archive"
        return 1
    fi

    # Verify installation (fail-closed)
    local verified_version
    verified_version=$("$dest" --version 2>/dev/null | head -1 || echo "none")
    if [[ "$verified_version" != *"actionlint"* ]]; then
        log_error "actionlint verification failed"
        return 1
    fi

    log_info "OK: actionlint $ACTIONLINT_VERSION installed"
    return 0
}

install_policy_venv() {
    echo "--- Installing Python Policy Virtualenv ---"

    local venv_dir="$INSTALL_ROOT/venvs/policy"

    # Check if Python 3.12 is available (fail-closed)
    if ! command -v python3.12 &>/dev/null; then
        log_error "Python 3.12 not found - policy virtualenv requires Python 3.12"
        return 1
    fi

    # Check if virtualenv already exists with correct packages (fail-closed)
    if [[ -d "$venv_dir" ]] && [[ "$FORCE" != "true" ]]; then
        local actual_pyyaml_version
        actual_pyyaml_version=$("$venv_dir/bin/python" -c "import yaml; print(yaml.__version__)" 2>/dev/null || echo "none")

        if [[ "$actual_pyyaml_version" == "$PYYAML_VERSION" ]]; then
            log_info "OK: Policy virtualenv already exists with correct PyYAML $actual_pyyaml_version"
            return 0
        fi
    fi

    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would create Python policy virtualenv with PyYAML $PYYAML_VERSION"
        return 0
    fi

    # Create virtualenv (fail-closed)
    mkdir -p "$(dirname "$venv_dir")"
    if ! python3.12 -m venv "$venv_dir"; then
        log_error "Failed to create policy virtualenv"
        return 1
    fi

    # Install pinned packages (fail-closed)
    if ! "$venv_dir/bin/pip" install "pip==${PIP_VERSION}" 2>/dev/null; then
        log_error "Failed to install pip ${PIP_VERSION}"
        return 1
    fi

    if ! "$venv_dir/bin/pip" install "PyYAML==${PYYAML_VERSION}" 2>/dev/null; then
        log_error "Failed to install PyYAML ${PYYAML_VERSION}"
        return 1
    fi

    # Verify PyYAML version (fail-closed)
    local actual_pyyaml_version
    actual_pyyaml_version=$("$venv_dir/bin/python" -c "import yaml; print(yaml.__version__)" 2>/dev/null || echo "none")

    if [[ "$actual_pyyaml_version" != "$PYYAML_VERSION" ]]; then
        log_error "PyYAML version mismatch: expected $PYYAML_VERSION, got $actual_pyyaml_version"
        return 1
    fi

    log_info "OK: Policy virtualenv created with PyYAML $PYYAML_VERSION"
    return 0
}

setup_shell_integration() {
    echo "--- Setting up Shell Integration ---"

    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would create shell integration scripts"
        return 0
    fi

    # Determine actual login shell for profile configuration
    local profile_file
    case "${SHELL:-}" in
        */zsh)  profile_file="$HOME/.zshrc" ;;
        */bash) profile_file="$HOME/.bashrc" ;;
        *)      profile_file="$HOME/.profile" ;;
    esac

    # Create user-local activation script (silent - no output on source)
    local activate_shim="$HOME/.local/bin/circus-dev-activate"
    mkdir -p "$(dirname "$activate_shim")"

    cat > "$activate_shim" << 'ACTIVATESCRIPT'
#!/usr/bin/env bash
# Circus development environment activation shim
# Generated by bootstrap-linux-dev.sh
# Silent activation - sets environment without output

CIRCUS_TOOL_ROOT="${CIRCUS_TOOL_ROOT:-$HOME/.local/share/circus-dev}"
CIRCUS_VENVS="${CIRCUS_VENVS:-$CIRCUS_TOOL_ROOT/venvs}"
NODE_VERSION="22.17.0"
DOTNET_ROOT="$CIRCUS_TOOL_ROOT/dotnet"

export CIRCUS_TOOL_ROOT
export CIRCUS_VENVS
export DOTNET_ROOT
export PATH="$CIRCUS_TOOL_ROOT/bin:$CIRCUS_TOOL_ROOT/node/v${NODE_VERSION}/bin:$CIRCUS_VENVS/policy/bin:$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
ACTIVATESCRIPT

    chmod +x "$activate_shim"
    log_info "OK: Created $activate_shim"

    # Create user-local doctor shim (executes the repository's dev-doctor.sh)
    local doctor_shim="$HOME/.local/bin/circus-dev-doctor"
    cat > "$doctor_shim" << DOCTORSHIM
#!/usr/bin/env bash
# Circus development doctor shim
# Executes the repository's dev-doctor.sh

exec "$REPO_ROOT/scripts/dev-doctor.sh" "\$@"
DOCTORSHIM

    chmod +x "$doctor_shim"
    log_info "OK: Created $doctor_shim"

    # Add to shell profile (idempotent - only adds if marker not present)
    local profile_marker="# Circus development environment"

    if [[ -f "$profile_file" ]] && ! grep -q "$profile_marker" "$profile_file"; then
        cat >> "$profile_file" << PROFILE_BLOCK

$profile_marker
if [[ -f "\$HOME/.local/bin/circus-dev-activate" ]]; then
    source "\$HOME/.local/bin/circus-dev-activate"
fi
PROFILE_BLOCK
        log_info "OK: Added activation to $profile_file"
    fi

    return 0
}

# Docker verification
verify_docker() {
    echo "--- Verifying Docker Access ---"

    if ! command -v docker &>/dev/null; then
        log_error "Docker not installed"
        return 1
    fi

    if ! docker info &>/dev/null; then
        log_warn "Docker daemon not accessible"
        echo "      Try: sg docker -c 'docker info'"
        echo "      Or:  Log out and log back in"
        echo ""
        echo "NOTE: Docker group membership grants root-level privileges."
        echo "      See: https://docs.docker.com/engine/install/linux-postinstall/"
        return 0  # Non-fatal for bootstrap
    fi

    log_info "OK: Docker accessible"
    return 0
}

# Source compilation check (fail-closed)
verify_source() {
    echo "--- Verifying Source Compilation ---"

    local repo="${REPO_ROOT:-$(pwd)}"
    cd "$repo"

    if [[ ! -f "Circus.sln" ]]; then
        log_error "Circus.sln not found - cannot verify source compilation"
        return 1
    fi

    if [[ "$DRY_RUN" == "true" ]]; then
        echo "DRY-RUN: Would verify source compilation"
        return 0
    fi

    # Set up PATH for dotnet
    export PATH="$INSTALL_ROOT/dotnet:$INSTALL_ROOT/node/v${NODE_VERSION}/bin:$PATH"

    # Restore (fail-closed)
    if ! dotnet restore Circus.sln --locked-mode >/dev/null 2>&1; then
        log_error "dotnet restore failed"
        return 1
    fi

    # Build (fail-closed)
    if ! dotnet build Circus.sln -c Release --no-restore >/dev/null 2>&1; then
        log_error "Source compilation failed"
        return 1
    fi

    log_info "OK: Source compilation successful"
    return 0
}

# -----------------------------------------------------------------------------
# Check Mode
# -----------------------------------------------------------------------------

run_check_mode() {
    echo "=== Circus Development Prerequisites Check ==="
    echo ""

    local check_failed=0

    # OS check
    if [[ -f /etc/os-release ]]; then
        source /etc/os-release
        if [[ "$ID" == "ubuntu" ]] || [[ "$ID" == "debian" ]] || [[ "$ID" == "linuxmint" ]]; then
            echo "OK: OS: $PRETTY_NAME ($ID)"
        else
            log_warn "OS: $PRETTY_NAME (designed for Ubuntu/Debian/Linux Mint)"
        fi
    else
        log_error "/etc/os-release not found"
        check_failed=1
    fi

    # Architecture check
    ARCH=$(uname -m)
    if [[ "$ARCH" == "x86_64" ]]; then
        echo "OK: Architecture: $ARCH"
    else
        log_error "Architecture: $ARCH (requires x86_64)"
        check_failed=1
    fi

    # Required commands
    for cmd in curl sha256sum tar xz git; do
        if command -v "$cmd" &>/dev/null; then
            echo "OK: Command '$cmd': available"
        else
            log_error "Command '$cmd': not found"
            check_failed=1
        fi
    done

    # global.json version
    local global_json="$REPO_ROOT/global.json"
    if [[ -f "$global_json" ]]; then
        local version
        version=$(grep -oP '"version":\s*"\K[^"]+' "$global_json" 2>/dev/null || true)
        if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            echo "OK: .NET SDK version from global.json: $version"
        else
            log_error "Invalid .NET SDK version in global.json: '$version'"
            check_failed=1
        fi
    else
        log_error "global.json not found"
        check_failed=1
    fi

    # Repository
    if [[ -d "$REPO_ROOT/.git" ]]; then
        echo "OK: Repository: $REPO_ROOT (git)"
    else
        log_error "Repository .git not found at $REPO_ROOT"
        check_failed=1
    fi

    echo ""
    if [[ $check_failed -eq 0 ]]; then
        echo "RESULT: All prerequisites passed"
        return 0
    else
        echo "RESULT: Prerequisites check failed"
        return 1
    fi
}

# -----------------------------------------------------------------------------
# Main Execution
# -----------------------------------------------------------------------------

# Run check mode if requested
if [[ "${CHECK_MODE:-false}" == "true" ]]; then
    run_check_mode
    exit $?
fi

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
