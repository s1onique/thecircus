#!/usr/bin/env bash
#
# dev-doctor.sh — Development environment diagnostic for The Circus
#
# Exit codes:
#   0 — all required capabilities available
#   1 — one or more required capabilities absent or wrong
#   2 — invocation, repository identity, or unsupported-host error
#
set -euo pipefail

# ----------------------------------------------------------------------
# Constants
# ----------------------------------------------------------------------
readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
readonly CIRCUS_TOOL_ROOT="${CIRCUS_TOOL_ROOT:-$HOME/.local/share/circus-dev}"
readonly CIRCUS_BIN="$CIRCUS_TOOL_ROOT/bin"
readonly CIRCUS_VENVS="$CIRCUS_TOOL_ROOT/venvs"
readonly DOTNET_ROOT="$HOME/.dotnet"

# ----------------------------------------------------------------------
# Utility functions
# ----------------------------------------------------------------------
error() { echo "ERROR: $*" >&2; }
warn() { echo "WARN: $*" >&2; }
info() { echo "INFO: $*"; }

# Check function that returns 1 on failure
check() {
  local name="$1"
  local cmd="$2"
  shift 2
  local args=("$@")
  
  if eval "$cmd" >/dev/null 2>&1; then
    info "OK   $name"
    return 0
  else
    error "MISSING $name"
    return 1
  fi
}

check_version() {
  local name="$1"
  local expected="$2"
  local actual="$3"
  
  if [[ "$expected" == "$actual" ]]; then
    info "OK   $name: $actual"
    return 0
  else
    error "WRONG $name: expected $expected, got $actual"
    return 1
  fi
}

# ----------------------------------------------------------------------
# Phase 1: Host identity
# ----------------------------------------------------------------------
echo ""
echo "=== Host Identity ==="
info "Distribution: $(grep '^PRETTY_NAME=' /etc/os-release | cut -d= -f2 | tr -d '"')"
info "Architecture: $(uname -m)"
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

if ! git status --short | grep -q '^??'; then
  info "Status: clean"
else
  warn "Status: dirty (untracked files present)"
fi

local_tree="$(git rev-parse HEAD^{tree} 2>/dev/null || true)"
info "Tree OID: $local_tree"

# ----------------------------------------------------------------------
# Phase 3: Tool authority
# ----------------------------------------------------------------------
echo ""
echo "=== Tool Authority ==="

DOTNET_VERSION="$(grep -oP '"version"\s*:\s*"\K[0-9]+\.[0-9]+\.[0-9]+' "$REPO_ROOT/global.json" | head -1)"
NODE_VERSION="$(grep -oP '^FROM node:\K[0-9]+\.[0-9]+\.[0-9]+' "$REPO_ROOT/Dockerfile.frontend" | head -1)"
ELM_VERSION="$(grep -oP '"elm"\s*:\s*"\K[0-9+\.-]+' "$REPO_ROOT/web/package.json" | head -1)"

info "Expected .NET SDK: $DOTNET_VERSION (from global.json)"
info "Expected Node: v$NODE_VERSION (from Dockerfile.frontend)"
info "Expected Elm: $ELM_VERSION (from web/package.json)"

# ----------------------------------------------------------------------
# Phase 4: .NET and F#
# ----------------------------------------------------------------------
echo ""
echo "=== .NET SDK and F# ==="

failed=0

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

# Elm
cd "$REPO_ROOT/web"
if [[ -x "./node_modules/.bin/elm" ]]; then
  actual_elm="$("./node_modules/.bin/elm" --version 2>/dev/null || echo "ERROR")"
  info "OK   Elm: $actual_elm"
  
  if [[ -x "./node_modules/.bin/elm-test" ]]; then
    actual_elm_test="$("./node_modules/.bin/elm-test" --version 2>/dev/null || echo "ERROR")"
    info "OK   elm-test: $actual_elm_test"
  else
    warn "elm-test not found"
  fi
else
  warn "Elm not installed (run npm ci in web/)"
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
  warn "Python policy venv not found"
fi

# ----------------------------------------------------------------------
# Phase 7: Linters
# ----------------------------------------------------------------------
echo ""
echo "=== Linters ==="

if [[ -x "$CIRCUS_BIN/actionlint" ]]; then
  actual_actionlint="$("$CIRCUS_BIN/actionlint" --version 2>/dev/null | head -1 || echo "ERROR")"
  info "OK   actionlint: $actual_actionlint"
else
  warn "actionlint not found at $CIRCUS_BIN/actionlint"
fi

if [[ -x "$CIRCUS_BIN/shellcheck" ]]; then
  actual_shellcheck="$("$CIRCUS_BIN/shellcheck" --version 2>/dev/null | head -1 || echo "ERROR")"
  info "OK   ShellCheck: $actual_shellcheck"
else
  warn "ShellCheck not found at $CIRCUS_BIN/shellcheck"
fi

# ----------------------------------------------------------------------
# Phase 8: Docker
# ----------------------------------------------------------------------
echo ""
echo "=== Docker ==="

# Check docker group membership
if id -nG | grep -qw docker; then
  if sg docker -c 'docker info' >/dev/null 2>&1; then
    info "OK   Docker: accessible (docker group membership active)"
    
    docker_arch="$(sg docker -c 'docker run --rm busybox:latest uname -m' 2>/dev/null || echo "ERROR")"
    info "OK   Docker architecture: $docker_arch"
    
    if sg docker -c 'docker buildx version' >/dev/null 2>&1; then
      info "OK   Docker Buildx available"
    else
      warn "Docker Buildx not working"
    fi
    
    if sg docker -c 'docker compose version' >/dev/null 2>&1; then
      info "OK   Docker Compose available"
    else
      warn "Docker Compose not working"
    fi
  else
    error "Docker daemon not accessible (permission denied)"
    error "NOTE: Docker group membership grants root-equivalent access"
    failed=1
  fi
else
  warn "User not in docker group (Docker commands require sudo)"
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
  
  if leamas factory digest --help >/dev/null 2>&1; then
    info "OK   Leamas: $leamas_version at $leamas_path"
    info "OK   Leamas commit: $leamas_commit"
  else
    warn "Leamas installed but factory digest not working"
  fi
else
  warn "Leamas not found in PATH"
fi

# ----------------------------------------------------------------------
# Phase 10: Make targets
# ----------------------------------------------------------------------
echo ""
echo "=== Make Targets ==="

for target in dev-bootstrap-linux dev-doctor dev-restore dev-test-linux dev-container-smoke dev-gate-linux; do
  if grep -q "^${target}:" "$REPO_ROOT/Makefile" 2>/dev/null; then
    info "OK   Make target: $target"
  else
    warn "Missing Make target: $target"
  fi
done

# ----------------------------------------------------------------------
# Phase 11: System packages
# ----------------------------------------------------------------------
echo ""
echo "=== System Packages ==="

for pkg in build-essential curl git jq make postgresql-client python3 python3-venv; do
  if command -v "$pkg" >/dev/null 2>&1 || dpkg -l "$pkg" >/dev/null 2>&1; then
    info "OK   $pkg"
  else
    warn "MISSING $pkg"
  fi
done

# ----------------------------------------------------------------------
# Summary
# ----------------------------------------------------------------------
echo ""
echo "=== Doctor Summary ==="

if [[ $failed -eq 0 ]]; then
  info "All checks passed (exit 0)"
  exit 0
else
  error "Some checks failed (exit 1)"
  exit 1
fi
