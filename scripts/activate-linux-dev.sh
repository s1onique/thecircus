#!/usr/bin/env bash
#
# activate-linux-dev.sh — Activate The Circus Linux development environment
#
# Usage:
#   source scripts/activate-linux-dev.sh
#
# This script is meant to be sourced, not executed.
# When executed directly, it exits with an error.
#

# Check if being sourced
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  echo "ERROR: This script must be sourced, not executed." >&2
  echo "Usage: source $0" >&2
  exit 1
fi

# ----------------------------------------------------------------------
# Environment configuration
# ----------------------------------------------------------------------
export CIRCUS_TOOL_ROOT="${CIRCUS_TOOL_ROOT:-$HOME/.local/share/circus-dev}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

# Prepend paths
export PATH="$CIRCUS_TOOL_ROOT/bin:$CIRCUS_TOOL_ROOT/node/v22.17.0/bin:$CIRCUS_VENVS/policy/bin:$PATH"

# .NET configuration
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Optional: Docker group (uncomment if not using sg)
# sg docker -c ':' 2>/dev/null || true
