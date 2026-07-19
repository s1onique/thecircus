#!/usr/bin/env bash
# =============================================================================
# dev-doctor.sh — Circus Development Environment Diagnostic
# =============================================================================
# Verifies all required toolchain components for Circus development.
#
# Exit codes:
#   0 - All checks passed
#   1 - One or more checks failed (missing/wrong capability)
#   2 - Invocation, repository identity, or unsupported-host error
#
# =============================================================================

set -euo pipefail

# ----------------------------------------------------------------------
# Constants
# ----------------------------------------------------------------------
readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
readonly CIRCUS_TOOL_ROOT="${CIRCUS_TOOL_ROOT:-$HOME/.local/share/circus-dev}"
readonly CIRCUS_BIN="$CIRCUS_TOOL_ROOT/bin"
readonly CIRCUS_VENVS="$CIRCUS_TOOL_ROOT/venvs"
readonly CIRCUS_NODE="${CIRCUS_NODE:-$CIRCUS_TOOL_ROOT/node}"
readonly DOTNET_ROOT="${DOTNET_ROOT:-$CIRCUS_TOOL_ROOT/dotnet}"

# ----------------------------------------------------------------------
# Global state
# ----------------------------------------------------------------------
failed=0

# ----------------------------------------------------------------------
# Version extraction helpers (fail-closed with guarded grep)
# ----------------------------------------------------------------------
extract_global_json_version() {
    local global_json="$1"
    local version=""

    # Guard against missing file before grep
    if [[ ! -f "$global_json" ]]; then
        return 2
    fi

    # Guard against empty/malformed file
    if [[ ! -s "$global_json" ]]; then
        return 2
    fi

    # Extract version with error capture
    version=$(grep -oP '"version":\s*"\K[0-9]+\.[0-9]+\.[0-9]+' "$global_json" 2>/dev/null | head -1 || true)

    # Validate extracted version format
    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        return 2
    fi

    echo "$version"
    return 0
}

extract_node_version_from_dockerfile() {
    local dockerfile="$1"
    local version=""

    if [[ ! -f "$dockerfile" ]]; then
        return 2
    fi

    if [[ ! -s "$dockerfile" ]]; then
        return 2
    fi

    version=$(grep -oP '^FROM node:\K[0-9]+\.[0-9]+\.[0-9]+' "$dockerfile" 2>/dev/null | head -1 || true)

    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        return 2
    fi

    echo "$version"
    return 0
}

extract_elm_version_from_package_json() {
    local package_json="$1"
    local version=""

    if [[ ! -f "$package_json" ]]; then
        return 2
    fi

    if [[ ! -s "$package_json" ]]; then
        return 2
    fi

    version=$(grep -oP '"elm"\s*:\s*"\K[0-9+\.-]+' "$package_json" 2>/dev/null | head -1 || true)

    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9-]+$ ]]; then
        return 2
    fi

    echo "$version"
    return 0
}

# ----------------------------------------------------------------------
# Tool versions from repository authority (fail-closed)
# ----------------------------------------------------------------------
DOTNET_VERSION=$(extract_global_json_version "$REPO_ROOT/global.json") || {
    echo "ERROR: Failed to extract .NET version from global.json" >&2
    exit 2
}

NODE_VERSION=$(extract_node_version_from_dockerfile "$REPO_ROOT/Dockerfile.frontend") || {
    echo "ERROR: Failed to extract Node version from Dockerfile.frontend" >&2
    exit 2
}

ELM_VERSION=$(extract_elm_version_from_package_json "$REPO_ROOT/web/package.json") || {
    echo "ERROR: Failed to extract Elm version from web/package.json" >&2
    exit 2
}

ACTIONLINT_VERSION="1.7.12"
SHELLCHECK_VERSION="0.11.0"

# ----------------------------------------------------------------------
# Utility functions
# ----------------------------------------------------------------------
info() { echo "INFO: $*"; }
warn() { echo "WARN: $*" >&2; }
error() { echo "ERROR: $*" >&2; }

# Check function that sets failed=1 on failure (fail-closed)
check() {
    local name="$1"
    local cmd="$2"
    shift 2

    if eval "$cmd" >/dev/null 2>&1; then
        info "OK   $name"
        return 0
    else
        error "MISSING $name"
        failed=1
        return 1
    fi
}

# ----------------------------------------------------------------------
# Supported architecture check (returns 2 for unsupported hosts)
# ----------------------------------------------------------------------
check_supported_architecture() {
    local arch
    arch=$(uname -m 2>/dev/null || echo "unknown")

    case "$arch" in
        x86_64)
            return 0
            ;;
        aarch64|arm64)
            # Unsupported by current bootstrap
            error "UNSUPPORTED ARCHITECTURE: $arch (only x86_64 is supported)"
            exit 2
            ;;
        *)
            error "UNKNOWN ARCHITECTURE: $arch"
            exit 2
            ;;
    esac
}

# ----------------------------------------------------------------------
# Supported OS check (returns 2 for unsupported hosts)
# ----------------------------------------------------------------------
check_supported_os() {
    if [[ ! -f /etc/os-release ]]; then
        error "UNSUPPORTED HOST: /etc/os-release not found"
        exit 2
    fi

    source /etc/os-release

    case "$ID" in
        ubuntu|debian|linuxmint)
            return 0
            ;;
        *)
            error "UNSUPPORTED OS: $ID (only ubuntu/debian/linuxmint are supported)"
            exit 2
            ;;
    esac
}

# ----------------------------------------------------------------------
# Phase 0: Supported host verification (exit 2 on failure)
# ----------------------------------------------------------------------
echo ""
echo "=== Host Verification ==="

# Check architecture first
check_supported_architecture
info "Architecture: $(uname -m)"

# Check OS
check_supported_os
info "Distribution: $PRETTY_NAME"

# ----------------------------------------------------------------------
# Phase 1: Host identity
# ----------------------------------------------------------------------
echo ""
echo "=== Host Identity ==="
info "Kernel: $(uname -r)"
info "User: $(whoami) (uid=$(id -u))"
info "Groups: $(id -nG)"
info "Shell: $SHELL"

# ----------------------------------------------------------------------
# Phase 2: Repository identity
# ----------------------------------------------------------------------
echo ""
echo "=== Repository Identity ==="

cd "$REPO_ROOT"

local_repo_root="$(git rev-parse --show-toplevel 2>/dev/null || true)"
if [[ "$local_repo_root" != "$REPO_ROOT" ]]; then
    error "REPOSITORY MISMATCH: expected $REPO_ROOT, got $local_repo_root"
    exit 2
fi
info "Root: $REPO_ROOT"

local_branch="$(git branch --show-current 2>/dev/null || true)"
info "Branch: $local_branch"

local_head="$(git rev-parse HEAD 2>/dev/null || true)"
info "HEAD: $local_head"

if ! git status --porcelain=v1 | grep -q .; then
    info "Status: clean"
else
    warn "Status: dirty (untracked or modified files present)"
    failed=1
fi

local_tree="$(git rev-parse HEAD^{tree} 2>/dev/null || true)"
info "Tree OID: $local_tree"

# ----------------------------------------------------------------------
# Phase 3: Tool authority
# ----------------------------------------------------------------------
echo ""
echo "=== Tool Authority ==="

info "Expected .NET SDK: $DOTNET_VERSION (from global.json)"
info "Expected Node: v$NODE_VERSION (from Dockerfile.frontend)"
info "Expected Elm: $ELM_VERSION (from web/package.json)"
info "Expected actionlint: v$ACTIONLINT_VERSION"
info "Expected ShellCheck: v$SHELLCHECK_VERSION"

# ----------------------------------------------------------------------
# Phase 4: .NET and F#
# ----------------------------------------------------------------------
echo ""
echo "=== .NET SDK and F# ==="

if [[ -x "$DOTNET_ROOT/dotnet" ]]; then
    actual_dotnet_version="$("$DOTNET_ROOT/dotnet" --version 2>/dev/null || echo "ERROR")"
    if [[ "$actual_dotnet_version" == "$DOTNET_VERSION" ]]; then
        info "OK   .NET SDK: $actual_dotnet_version at $DOTNET_ROOT"
    else
        error "WRONG .NET SDK: expected $DOTNET_VERSION, got $actual_dotnet_version"
        failed=1
    fi

    if printf 'printfn "F# toolchain OK"\n' | "$DOTNET_ROOT/dotnet" fsi --quiet >/dev/null 2>&1; then
        info "OK   F# Interactive works"
    else
        error "F# Interactive not working"
        failed=1
    fi
else
    error ".NET SDK not found at $DOTNET_ROOT/dotnet"
    failed=1
fi

# ----------------------------------------------------------------------
# Phase 5: Node and Elm
# ----------------------------------------------------------------------
echo ""
echo "=== Node.js and Elm ==="

if [[ -x "$CIRCUS_NODE/v${NODE_VERSION}/bin/node" ]]; then
    actual_node_version="$("$CIRCUS_NODE/v${NODE_VERSION}/bin/node" --version 2>/dev/null || echo "ERROR")"
    actual_node_version="${actual_node_version#v}"
    if [[ "$actual_node_version" == "$NODE_VERSION" ]]; then
        info "OK   Node.js: v$actual_node_version"
    else
        error "WRONG Node.js: expected $NODE_VERSION, got $actual_node_version"
        failed=1
    fi

    info "OK   npm: $($CIRCUS_NODE/v${NODE_VERSION}/bin/npm --version 2>/dev/null || echo "ERROR")"
else
    error "Node.js not found at $CIRCUS_NODE/v${NODE_VERSION}"
    failed=1
fi

# Elm - fail-closed for required capability
cd "$REPO_ROOT/web"
if [[ -x "./node_modules/.bin/elm" ]]; then
    actual_elm="$("./node_modules/.bin/elm" --version 2>/dev/null || echo "ERROR")"
    # Accept both "0.19.2" and "Elm 0.19.2" formats
    if [[ "$actual_elm" == "Elm ${ELM_VERSION}" ]] || [[ "$actual_elm" == "${ELM_VERSION}" ]]; then
        info "OK   Elm: $actual_elm"
    else
        error "WRONG Elm: expected Elm $ELM_VERSION, got $actual_elm"
        failed=1
    fi

    if [[ -x "./node_modules/.bin/elm-test" ]]; then
        info "OK   elm-test: available"
    else
        error "elm-test not installed"
        failed=1
    fi
else
    error "Elm not installed (run 'npm ci' in web/)"
    failed=1
fi
cd "$REPO_ROOT"

# ----------------------------------------------------------------------
# Phase 6: Python and policy tools
# ----------------------------------------------------------------------
echo ""
echo "=== Python Policy Environment ==="

if [[ -x "$CIRCUS_VENVS/policy/bin/python" ]]; then
    actual_pyyaml="$("$CIRCUS_VENVS/policy/bin/python" -c "import yaml; print(yaml.__version__)" 2>/dev/null || echo "ERROR")"
    if [[ "$actual_pyyaml" == "6.0.1" ]]; then
        info "OK   PyYAML: $actual_pyyaml"
    else
        error "WRONG PyYAML: expected 6.0.1, got $actual_pyyaml"
        failed=1
    fi
    info "OK   Python policy venv at $CIRCUS_VENVS/policy"
else
    error "Python policy venv not found"
    failed=1
fi

# ----------------------------------------------------------------------
# Phase 7: Linters
# ----------------------------------------------------------------------
echo ""
echo "=== Linters ==="

# actionlint - fail-closed
if [[ -x "$CIRCUS_BIN/actionlint" ]]; then
    actual_actionlint="$("$CIRCUS_BIN/actionlint" --version 2>/dev/null | head -1 || echo "ERROR")"
    if [[ "$actual_actionlint" == *"${ACTIONLINT_VERSION}"* ]]; then
        info "OK   actionlint: $actual_actionlint"
    else
        error "WRONG actionlint version: expected $ACTIONLINT_VERSION"
        failed=1
    fi
else
    error "actionlint not found at $CIRCUS_BIN/actionlint"
    failed=1
fi

# ShellCheck - fail-closed
if [[ -x "$CIRCUS_BIN/shellcheck" ]]; then
    actual_shellcheck="$("$CIRCUS_BIN/shellcheck" --version 2>/dev/null | head -1 || echo "ERROR")"
    if [[ "$actual_shellcheck" == *"${SHELLCHECK_VERSION}"* ]]; then
        info "OK   ShellCheck: $actual_shellcheck"
    else
        error "WRONG ShellCheck version: expected $SHELLCHECK_VERSION"
        failed=1
    fi
else
    error "ShellCheck not found at $CIRCUS_BIN/shellcheck"
    failed=1
fi

# ----------------------------------------------------------------------
# Phase 8: Docker
# ----------------------------------------------------------------------
echo ""
echo "=== Docker ==="

# Check docker binary exists
if ! command -v docker &>/dev/null; then
    error "Docker not installed"
    failed=1
else
    docker_version=$(docker --version 2>/dev/null | head -1 || echo "unknown")

    # Test direct access (NOT via sg docker) - this is the required capability
    if docker info &>/dev/null; then
        info "OK   Docker: $docker_version (direct access verified)"

        # Buildx check - fail-closed
        if docker buildx version &>/dev/null; then
            info "OK   Docker Buildx available"
        else
            error "Docker Buildx not working"
            failed=1
        fi

        # Compose check - fail-closed
        if docker compose version &>/dev/null; then
            info "OK   Docker Compose available"
        else
            error "Docker Compose not working"
            failed=1
        fi
    else
        error "Docker daemon not accessible (permission denied)"
        error "NOTE: Docker group membership grants root-equivalent access"
        error "Try: logout and login, or use 'sg docker' as workaround"
        failed=1
    fi

    # Check docker group membership (informational)
    if id -nG | grep -qw docker; then
        info "INFO   User in docker group (may require logout/login for activation)"
    else
        info "INFO   User not in docker group"
    fi
fi

# ----------------------------------------------------------------------
# Phase 9: Leamas
# ----------------------------------------------------------------------
echo ""
echo "=== Leamas CLI ==="

if command -v leamas >/dev/null 2>&1; then
    leamas_path="$(command -v leamas)"
    leamas_version="$(leamas version 2>/dev/null | head -1 || echo "ERROR")"
    leamas_commit="$(leamas version 2>/dev/null | grep 'commit:' | awk '{print $2}' || echo "UNKNOWN")"

    info "OK   Leamas: $leamas_version at $leamas_path"
    info "OK   Leamas commit: $leamas_commit"

    # Factory digest check - fail-closed
    if leamas factory digest --help >/dev/null 2>&1; then
        info "OK   Leamas factory digest: functional"
    else
        error "Leamas factory digest not working"
        failed=1
    fi
else
    error "Leamas not found in PATH"
    failed=1
fi

# ----------------------------------------------------------------------
# Phase 10: Make targets
# ----------------------------------------------------------------------
echo ""
echo "=== Make Targets ==="

# Required targets - fail-closed
for target in build-backend test-backend build-frontend; do
    if grep -q "^${target}:" "$REPO_ROOT/Makefile" 2>/dev/null; then
        info "OK   Make target: $target"
    else
        error "Missing Make target: $target"
        failed=1
    fi
done

# ----------------------------------------------------------------------
# Phase 11: System packages
# ----------------------------------------------------------------------
echo ""
echo "=== System Packages ==="

# Required system packages - fail-closed
missing_pkgs=0
for pkg in build-essential curl git jq make postgresql-client python3 python3-venv; do
    if command -v "$pkg" >/dev/null 2>&1 || dpkg -l "$pkg" >/dev/null 2>&1; then
        info "OK   $pkg"
    else
        error "MISSING $pkg"
        missing_pkgs=$((missing_pkgs + 1))
    fi
done

if [[ $missing_pkgs -gt 0 ]]; then
    failed=1
fi

# ----------------------------------------------------------------------
# Summary
# ----------------------------------------------------------------------
echo ""
echo "=== Doctor Summary ==="

if [[ ${failed:-0} -eq 0 ]]; then
    info "All checks passed (exit 0)"
    exit 0
else
    error "Some checks failed (exit 1)"
    exit 1
fi
