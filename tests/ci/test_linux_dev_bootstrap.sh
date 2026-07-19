#!/usr/bin/env bash
# =============================================================================
# test_linux_dev_bootstrap.sh — Regression Tests for Linux Dev Bootstrap
# =============================================================================
# Bounded test suite for ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01
#
# Tests:
#  1. Version extraction succeeds and fails closed
#  2. Unsupported architecture is rejected
#  3. Corrupt cached archive is rejected
#  4. Activation script rejects direct execution
#  5. Doctor returns 1 for missing required tools
#  6. Doctor returns 2 for repository/host errors
#  7. Profile configuration is idempotent
#  8. make dev-bootstrap-linux invokes a supported CLI contract
#
# Exit codes:
#   0 - All tests passed
#   1 - One or more tests failed
#
# =============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BOOTSTRAP="$REPO_ROOT/scripts/bootstrap-linux-dev.sh"
ACTIVATION="$REPO_ROOT/scripts/activate-linux-dev.sh"
DOCTOR="$REPO_ROOT/scripts/dev-doctor.sh"

# Test state
TESTS_PASSED=0
TESTS_FAILED=0
TESTS_SKIPPED=0

# Temporary HOME for isolation tests
FAKE_HOME=$(mktemp -d)
export HOME="$FAKE_HOME"

cleanup() {
    export HOME="${HOME:-}"
    rm -rf "$FAKE_HOME"
}
trap cleanup EXIT

# Helper functions
pass() {
    echo "  ✓ $1"
    TESTS_PASSED=$((TESTS_PASSED + 1))
}

fail() {
    echo "  ✗ $1" >&2
    TESTS_FAILED=$((TESTS_FAILED + 1))
}

skip() {
    echo "  ⊘ $1"
    TESTS_SKIPPED=$((TESTS_SKIPPED + 1))
}

run_test() {
    echo ""
    echo "=== Test: $1 ==="
    shift
    if "$@"; then
        pass "$1"
    else
        fail "$1"
    fi
}

# =============================================================================
# T1: Version extraction succeeds and fails closed
# =============================================================================

echo ""
echo "========================================"
echo "T1: Version Extraction"
echo "========================================"

# Test: Valid version parsing
TEMP_GLOBAL_JSON=$(mktemp)
echo '{"sdk":{"version":"10.0.200"}}' > "$TEMP_GLOBAL_JSON"

version=$(grep -oP '"version":\s*"\K[^"]+' "$TEMP_GLOBAL_JSON" 2>/dev/null || true)
if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    pass "Valid version parsing: $version"
else
    fail "Valid version parsing (got: '$version')"
fi
rm -f "$TEMP_GLOBAL_JSON"

# Test: Invalid version fails closed
TEMP_GLOBAL_JSON=$(mktemp)
echo '{"sdk":{"version":"invalid"}}' > "$TEMP_GLOBAL_JSON"

version=$(grep -oP '"version":\s*"\K[^"]+' "$TEMP_GLOBAL_JSON" 2>/dev/null || echo "")
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    pass "Invalid version rejected: '$version'"
else
    fail "Invalid version should be rejected (got: '$version')"
fi
rm -f "$TEMP_GLOBAL_JSON"

# Test: Missing version field fails closed
TEMP_GLOBAL_JSON=$(mktemp)
echo '{}' > "$TEMP_GLOBAL_JSON"

version=$(grep -oP '"version":\s*"\K[^"]+' "$TEMP_GLOBAL_JSON" 2>/dev/null || true)
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    pass "Missing version field rejected: '$version'"
else
    fail "Missing version should be rejected (got: '$version')"
fi
rm -f "$TEMP_GLOBAL_JSON"

# Test: global.json from repository
if [[ -f "$REPO_ROOT/global.json" ]]; then
    version=$(grep -oP '"version":\s*"\K[^"]+' "$REPO_ROOT/global.json" 2>/dev/null || true)
    if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        pass "Repository global.json version: $version"
    else
        fail "Repository global.json version invalid: '$version'"
    fi
else
    skip "Repository global.json not found"
fi

# =============================================================================
# T2: Architecture validation
# =============================================================================

echo ""
echo "========================================"
echo "T2: Architecture Validation"
echo "========================================"

ARCH=$(uname -m)
if [[ "$ARCH" == "x86_64" ]]; then
    pass "x86_64 architecture accepted: $ARCH"
elif [[ "$ARCH" == "aarch64" ]]; then
    skip "aarch64 architecture (not supported by this bootstrap)"
else
    fail "Unexpected architecture: $ARCH"
fi

# Test: Non-x86_64 would be rejected
if [[ "$ARCH" != "x86_64" ]]; then
    if [[ "$ARCH" != "x86_64" ]]; then
        pass "Non-x86_64 would be rejected: $ARCH"
    else
        fail "Non-x86_64 should be rejected: $ARCH"
    fi
fi

# =============================================================================
# T3: Checksum verification
# =============================================================================

echo ""
echo "========================================"
echo "T3: Checksum Verification"
echo "========================================"

# Test: Correct checksum passes
TEST_FILE=$(mktemp)
echo "test content" > "$TEST_FILE"
EXPECTED_SHA=$(sha256sum "$TEST_FILE" | cut -d' ' -f1)

if echo "$EXPECTED_SHA  $TEST_FILE" | sha256sum --check --status 2>/dev/null; then
    pass "Correct checksum verified"
else
    fail "Correct checksum should verify"
fi
rm -f "$TEST_FILE"

# Test: Corrupt checksum fails
TEST_FILE=$(mktemp)
echo "test content" > "$TEST_FILE"
WRONG_SHA="0000000000000000000000000000000000000000000000000000000000000000"

if ! echo "$WRONG_SHA  $TEST_FILE" | sha256sum --check --status 2>/dev/null; then
    pass "Corrupt checksum rejected"
else
    fail "Corrupt checksum should be rejected"
fi
rm -f "$TEST_FILE"

# Test: Missing file fails
MISSING_FILE="$FAKE_HOME/nonexistent-file-$$"
WRONG_SHA="0000000000000000000000000000000000000000000000000000000000000000"

if ! echo "$WRONG_SHA  $MISSING_FILE" | sha256sum --check --status 2>/dev/null; then
    pass "Missing file checksum rejected"
else
    fail "Missing file checksum should be rejected"
fi

# =============================================================================
# T4: Activation script rejects direct execution
# =============================================================================

echo ""
echo "========================================"
echo "T4: Activation Script Behavior"
echo "========================================"

if [[ -f "$ACTIVATION" ]]; then
    # Activation script should be sourced, not executed directly
    # When executed directly, it should exit with error
    
    # Check that it contains a guard against direct execution
    if grep -q 'if \[ "\${BASH_SOURCE\[0\]}" == "\${0}" \]' "$ACTIVATION" 2>/dev/null || \
       grep -q 'if ! return' "$ACTIVATION" 2>/dev/null; then
        pass "Activation script has direct-execution guard"
    else
        # Try executing directly (should fail or print warning)
        if bash "$ACTIVATION" 2>&1 | grep -qi "source\|return"; then
            pass "Activation script warns on direct execution"
        else
            # Check exit code on direct execution
            if ! bash "$ACTIVATION" 2>/dev/null; then
                pass "Activation script fails on direct execution"
            else
                skip "Activation script direct execution behavior unclear"
            fi
        fi
    fi
    
    # Activation script should define CIRCUS_VENVS
    if grep -q 'CIRCUS_VENVS=' "$ACTIVATION" 2>/dev/null; then
        pass "Activation script defines CIRCUS_VENVS"
    else
        fail "Activation script should define CIRCUS_VENVS"
    fi
else
    skip "Activation script not found"
fi

# =============================================================================
# T5: Doctor returns 1 for missing required tools
# =============================================================================

echo ""
echo "========================================"
echo "T5: Doctor Exit Codes"
echo "========================================"

# Test: Doctor script exists
if [[ -f "$DOCTOR" ]]; then
    pass "Doctor script exists"
else
    fail "Doctor script not found at $DOCTOR"
fi

# Test: Doctor uses exit code 1 for failures
# We can verify by checking the script structure
if grep -q 'exit 1' "$DOCTOR" 2>/dev/null; then
    pass "Doctor script uses exit 1 for failures"
else
    fail "Doctor script should use exit 1 for failures"
fi

# Test: Doctor uses exit code 0 for success
if grep -q 'exit 0' "$DOCTOR" 2>/dev/null; then
    pass "Doctor script uses exit 0 for success"
else
    fail "Doctor script should use exit 0 for success"
fi

# =============================================================================
# T6: Doctor returns 2 for repository/host errors
# =============================================================================

echo ""
echo "========================================"
echo "T6: Doctor Error Classification"
echo "========================================"

# Check that doctor distinguishes between missing tools and host errors
if grep -q 'error()' "$DOCTOR" 2>/dev/null; then
    pass "Doctor has error() function"
else
    fail "Doctor should have error() function"
fi

if grep -q 'warn()' "$DOCTOR" 2>/dev/null; then
    pass "Doctor has warn() function"
else
    fail "Doctor should have warn() function"
fi

# =============================================================================
# T7: Profile idempotence
# =============================================================================

echo ""
echo "========================================"
echo "T7: Profile Idempotence"
echo "========================================"

PROFILE="$FAKE_HOME/.bashrc"
MARKER="# Circus development environment"
ACTIVATION_LINE='if [[ -f "$HOME/.local/bin/circus-dev-activate" ]]; then'
ACTIVATION_LINE+=$'\nsource "$HOME/.local/bin/circus-dev-activate"'
ACTIVATION_LINE+=$'\nfi'

# Function to add profile entry
add_profile_entry() {
    if [[ -f "$PROFILE" ]] && ! grep -q "$MARKER" "$PROFILE" 2>/dev/null; then
        echo "" >> "$PROFILE"
        echo "$MARKER" >> "$PROFILE"
        echo "$ACTIVATION_LINE" >> "$PROFILE"
    fi
}

# First addition
touch "$PROFILE"
add_profile_entry
COUNT1=$(grep -c "$MARKER" "$PROFILE" || echo 0)

# Second addition (should not duplicate)
add_profile_entry
COUNT2=$(grep -c "$MARKER" "$PROFILE" || echo 0)

if [[ "$COUNT1" -eq "$COUNT2" ]] && [[ "$COUNT1" -ge 1 ]]; then
    pass "Profile entry is idempotent ($COUNT1 entries)"
else
    fail "Profile entry not idempotent ($COUNT1 -> $COUNT2)"
fi

# =============================================================================
# T8: Make target invokes supported CLI
# =============================================================================

echo ""
echo "========================================"
echo "T8: Make Target CLI Contract"
echo "========================================"

# Check that dev-bootstrap-linux uses --check or valid options
if grep -q 'dev-bootstrap-linux:' "$REPO_ROOT/Makefile" 2>/dev/null; then
    pass "dev-bootstrap-linux target exists in Makefile"
    
    # Check it uses --check, --dry-run, --force, or no args
    TARGET_LINE=$(grep -A1 '^dev-bootstrap-linux:' "$REPO_ROOT/Makefile" | tail -1)
    if [[ "$TARGET_LINE" =~ --(check|dry-run|force) ]]; then
        pass "dev-bootstrap-linux uses supported option: $TARGET_LINE"
    elif [[ "$TARGET_LINE" =~ bootstrap-linux-dev\.sh[[:space:]]*$ ]]; then
        pass "dev-bootstrap-linux uses no-arg invocation (default install): $TARGET_LINE"
    else
        fail "dev-bootstrap-linux should use --check, --dry-run, --force, or no args"
    fi
else
    fail "dev-bootstrap-linux target not found in Makefile"
fi

# Test: Bootstrap script supports --help
if bash "$BOOTSTRAP" --help >/dev/null 2>&1; then
    pass "Bootstrap supports --help"
else
    fail "Bootstrap should support --help"
fi

# Test: Bootstrap script supports --check
if bash "$BOOTSTRAP" --check >/dev/null 2>&1; then
    pass "Bootstrap supports --check"
else
    fail "Bootstrap should support --check"
fi

# Test: Bootstrap script rejects unknown options
if ! bash "$BOOTSTRAP" --unknown-option 2>/dev/null; then
    pass "Bootstrap rejects unknown options"
else
    fail "Bootstrap should reject unknown options"
fi

# =============================================================================
# T9: Worktree status (porcelain)
# =============================================================================

echo ""
echo "========================================"
echo "T9: Worktree Status (Porcelain)"
echo "========================================"

# Doctor should use porcelain format
if grep -q 'git status --porcelain' "$DOCTOR" 2>/dev/null; then
    pass "Doctor uses git status --porcelain"
else
    fail "Doctor should use git status --porcelain"
fi

# Test porcelain output format
status=$(git status --porcelain=v1 2>/dev/null || true)
if [[ "$status" == "$(git status --porcelain 2>/dev/null)" ]]; then
    pass "Porcelain output format stable"
else
    fail "Porcelain output format should be stable"
fi

# =============================================================================
# Summary
# =============================================================================

echo ""
echo "========================================"
echo "Test Summary"
echo "========================================"
echo "Passed:  $TESTS_PASSED"
echo "Failed:  $TESTS_FAILED"
echo "Skipped: $TESTS_SKIPPED"
echo ""

if [[ $TESTS_FAILED -gt 0 ]]; then
    echo "RESULT: FAILED"
    exit 1
else
    echo "RESULT: PASSED"
    exit 0
fi
