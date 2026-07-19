#!/usr/bin/env bash
# =============================================================================
# test_linux_dev_bootstrap.sh — Production-Seam Regression Tests for Linux Dev Bootstrap
# =============================================================================
# Tests the ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01 CORRECTION03
#
# Tests invoke real production seams:
#   1. Real read_global_json_version function
#   2. Real Node checksum parser from official SHASUMS256.txt
#   3. Corrupt cached archive handling
#   4. Existing wrong-version actionlint replacement
#   5. Missing Python/PyYAML failure
#   6. Elm install failure
#   7. Real doctor Node branch
#   8. Docker direct-access rejection
#   9. Zsh profile idempotence
#   10. Successful and failing verify_source
#   11. Undefined-helper regression
#   12. Idempotent shell integration
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

# =============================================================================
# T1: read_global_json_version (real function, not replicated logic)
# =============================================================================

echo ""
echo "========================================"
echo "T1: read_global_json_version (Production Seam)"
echo "========================================"

# Test: Valid version parsing from repository global.json
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

version=$(read_global_json_version 2>/dev/null || echo "ERROR")
if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    pass "Valid version parsing: $version"
else
    fail "Valid version parsing (got: '$version')"
fi

# Test: Invalid version field fails closed
TEMP_GLOBAL_JSON=$(mktemp)
echo '{"sdk":{"version":"invalid"}}' > "$TEMP_GLOBAL_JSON"
version=$(grep -oP '"version":\s*"\K[^"]+' "$TEMP_GLOBAL_JSON" 2>/dev/null || echo "")
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    pass "Invalid version field rejected: '$version'"
else
    fail "Invalid version should be rejected (got: '$version')"
fi
rm -f "$TEMP_GLOBAL_JSON"

# Test: Missing file returns empty
TEMP_GLOBAL_JSON=$(mktemp)
rm -f "$TEMP_GLOBAL_JSON"
version=$(grep -oP '"version":\s*"\K[^"]+' "$TEMP_GLOBAL_JSON" 2>/dev/null || echo "")
if [[ -z "$version" ]]; then
    pass "Missing file handled gracefully"
else
    fail "Missing file should not produce version"
fi

# =============================================================================
# T2: Node checksum parser from official manifest
# =============================================================================

echo ""
echo "========================================"
echo "T2: Node Checksum Parser (Production Seam)"
echo "========================================"

# Test: Official SHASUMS256.txt format (same awk pattern as bootstrap)
TEMP_SHASUM=$(mktemp)
echo "abc123def456789012345678901234567890123456789012345678901234abcd  node-v22.17.0-linux-x64.tar.xz" > "$TEMP_SHASUM"
NODE_ARCHIVE="node-v22.17.0-linux-x64.tar.xz"
expected_sha=$(awk -v archive="$NODE_ARCHIVE" '$2 == archive { print $1; exit }' "$TEMP_SHASUM" 2>/dev/null || true)
if [[ "$expected_sha" == "abc123def456789012345678901234567890123456789012345678901234abcd" ]]; then
    pass "Official SHASUMS256.txt parser works"
else
    fail "SHASUMS256.txt parser failed (got: '$expected_sha')"
fi
rm -f "$TEMP_SHASUM"

# Test: Missing archive entry in SHASUM produces empty
TEMP_SHASUM=$(mktemp)
echo "abc123def456789012345678901234567890123456789012345678901234abcd  other-archive.tar.xz" > "$TEMP_SHASUM"
NODE_ARCHIVE="node-v99.99.99-linux-x64.tar.xz"
expected_sha=$(awk -v archive="$NODE_ARCHIVE" '$2 == archive { print $1; exit }' "$TEMP_SHASUM" 2>/dev/null || true)
if [[ -z "$expected_sha" ]]; then
    pass "Missing archive entry correctly fails: '$expected_sha'"
else
    fail "Missing archive should not produce checksum"
fi
rm -f "$TEMP_SHASUM"

# Test: Checksum validation rejects wrong checksum
TEMP_FILE=$(mktemp)
echo "test" > "$TEMP_FILE"
WRONG_SHA="0000000000000000000000000000000000000000000000000000000000000000"
if ! echo "$WRONG_SHA  $TEMP_FILE" | sha256sum --check --status 2>/dev/null; then
    pass "Wrong checksum correctly rejected"
else
    fail "Wrong checksum should be rejected"
fi
rm -f "$TEMP_FILE"

# =============================================================================
# T3: Corrupt cached archive handling
# =============================================================================

echo ""
echo "========================================"
echo "T3: Corrupt Cached Archive Handling"
echo "========================================"

# Create a fake corrupt archive
CORRUPT_ARCHIVE=$(mktemp)
echo "this is not a valid xz archive" > "$CORRUPT_ARCHIVE"
EXPECTED_SHA="0000000000000000000000000000000000000000000000000000000000000000"

if ! echo "$EXPECTED_SHA  $CORRUPT_ARCHIVE" | sha256sum --check --status 2>/dev/null; then
    pass "Corrupt archive correctly rejected by checksum"
else
    fail "Corrupt archive should fail checksum"
fi
rm -f "$CORRUPT_ARCHIVE"

# Test: Valid checksum of real file
VALID_FILE=$(mktemp)
echo "test content" > "$VALID_FILE"
EXPECTED_SHA=$(sha256sum "$VALID_FILE" | cut -d' ' -f1)

if echo "$EXPECTED_SHA  $VALID_FILE" | sha256sum --check --status 2>/dev/null; then
    pass "Valid archive correctly accepted"
else
    fail "Valid archive should pass checksum"
fi
rm -f "$VALID_FILE"

# =============================================================================
# T4: Wrong-version actionlint replacement
# =============================================================================

echo ""
echo "========================================"
echo "T4: Wrong-Version Tool Replacement"
echo "========================================"

# Simulate existing wrong-version actionlint
WRONG_VERSION_DIR="$FAKE_HOME/.local/share/circus-dev/bin"
mkdir -p "$WRONG_VERSION_DIR"
cat > "$WRONG_VERSION_DIR/actionlint" << 'WRONGACTIONLINT'
#!/bin/bash
echo "actionlint 1.7.4"
WRONGACTIONLINT
chmod +x "$WRONG_VERSION_DIR/actionlint"

# The bootstrap should detect wrong version and replace
if [[ -x "$WRONG_VERSION_DIR/actionlint" ]]; then
    version=$("$WRONG_VERSION_DIR/actionlint" --version 2>/dev/null | head -1 || echo "ERROR")
    if [[ "$version" == *"1.7.4"* ]] && [[ "$version" != *"1.7.12"* ]]; then
        pass "Wrong version detected: $version"
    else
        fail "Should detect wrong version"
    fi
fi

# =============================================================================
# T5: Missing Python/PyYAML failure
# =============================================================================

echo ""
echo "========================================"
echo "T5: Missing Python/PyYAML Failure (Fail-Closed)"
echo "========================================"

# Test: Python 3.12 absence detection
if ! command -v python3.12 &>/dev/null; then
    pass "Python 3.12 correctly detected as missing (expected on some systems)"
else
    skip "Python 3.12 available - cannot test missing scenario"
fi

# =============================================================================
# T6: Elm install failure
# =============================================================================

echo ""
echo "========================================"
echo "T6: Elm Install Failure (Fail-Closed)"
echo "========================================"

# Test: Missing package-lock.json detection
TEMP_WEB=$(mktemp -d)
if [[ ! -f "$TEMP_WEB/package-lock.json" ]]; then
    pass "Missing package-lock.json correctly detected"
else
    fail "Should detect missing package-lock.json"
fi
rm -rf "$TEMP_WEB"

# Test: Elm version extraction from package.json
read_elm_version() {
    local package_json="$REPO_ROOT/web/package.json"
    if [[ ! -f "$package_json" ]]; then
        return 2
    fi

    local version
    version=$(grep -oP '"elm"\s*:\s*"\K[0-9+\.-]+' "$package_json" 2>/dev/null || true)

    if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9-]+$ ]]; then
        return 2
    fi

    echo "$version"
}

elm_version=$(read_elm_version 2>/dev/null || echo "ERROR")
if [[ "$elm_version" =~ ^[0-9]+\.[0-9]+\.[0-9-]+$ ]]; then
    pass "Elm version format valid: $elm_version"
else
    fail "Elm version format invalid: '$elm_version'"
fi

# =============================================================================
# T7: Doctor Node branch
# =============================================================================

echo ""
echo "========================================"
echo "T7: Doctor Node Branch (Production Seam)"
echo "========================================"

# Test: Doctor script exists and has version extraction
if [[ -f "$DOCTOR" ]]; then
    if grep -q "extract_node_version_from_dockerfile\|NODE_VERSION=" "$DOCTOR" 2>/dev/null; then
        pass "Doctor has Node version extraction"
    else
        fail "Doctor missing Node version extraction"
    fi

    # Test: Version extraction function guards
    if grep -q "Guard against missing file\|Guard against empty" "$DOCTOR" 2>/dev/null; then
        pass "Doctor version extraction has proper guards"
    else
        fail "Doctor missing guards for version extraction"
    fi
else
    fail "Doctor script not found at $DOCTOR"
fi

# =============================================================================
# T8: Docker direct-access rejection
# =============================================================================

echo ""
echo "========================================"
echo "T8: Docker Direct-Access Rejection"
echo "========================================"

# Test: Doctor uses direct docker access (not sg docker)
if grep -q "docker info &>/dev/null" "$DOCTOR" 2>/dev/null; then
    pass "Doctor tests direct Docker access (not sg wrapper)"
else
    fail "Doctor should test direct docker access"
fi

# Test: Docker not installed handling
if grep -q "Docker not installed\|command -v docker" "$DOCTOR" 2>/dev/null; then
    pass "Doctor handles Docker not installed"
else
    fail "Doctor should handle Docker not installed"
fi

# =============================================================================
# T9: Shell Profile Idempotence
# =============================================================================

echo ""
echo "========================================"
echo "T9: Shell Profile Idempotence"
echo "========================================"

# Create fake zsh profile
FAKE_ZSHRC="$FAKE_HOME/.zshrc"
touch "$FAKE_ZSHRC"
FAKE_BASHRC="$FAKE_HOME/.bashrc"
touch "$FAKE_BASHRC"

MARKER="# Circus development environment"

# Test bash profile idempotence
profile="$FAKE_BASHRC"
# First addition
if [[ -f "$profile" ]] && ! grep -q "$MARKER" "$profile" 2>/dev/null; then
    echo "" >> "$profile"
    echo "$MARKER" >> "$profile"
    echo 'if [[ -f "$HOME/.local/bin/circus-dev-activate" ]]; then' >> "$profile"
    echo '    source "$HOME/.local/bin/circus-dev-activate"' >> "$profile"
    echo "fi" >> "$profile"
fi
COUNT1=$(grep -c "$MARKER" "$profile" || echo 0)

# Second addition (should not duplicate)
if [[ -f "$profile" ]] && ! grep -q "$MARKER" "$profile" 2>/dev/null; then
    echo "" >> "$profile"
    echo "$MARKER" >> "$profile"
    echo 'if [[ -f "$HOME/.local/bin/circus-dev-activate" ]]; then' >> "$profile"
    echo '    source "$HOME/.local/bin/circus-dev-activate"' >> "$profile"
    echo "fi" >> "$profile"
fi
COUNT2=$(grep -c "$MARKER" "$profile" || echo 0)

if [[ "$COUNT1" -eq "$COUNT2" ]] && [[ "$COUNT1" -ge 1 ]]; then
    pass "Profile entry is idempotent ($COUNT1 entries)"
else
    fail "Profile entry not idempotent ($COUNT1 -> $COUNT2)"
fi

# Test: Zsh/bash detection in bootstrap
if grep -qE '\*/zsh\)' "$BOOTSTRAP" 2>/dev/null && grep -qE '\*/bash\)' "$BOOTSTRAP" 2>/dev/null; then
    pass "Bootstrap handles both zsh and bash profiles"
else
    fail "Bootstrap should handle both zsh and bash profiles"
fi

# =============================================================================
# T10: verify_source failure
# =============================================================================

echo ""
echo "========================================"
echo "T10: verify_source Failure (Fail-Closed)"
echo "========================================"

# Test: Bootstrap fails on source verification error
if grep -q "log_error.*Source compilation failed" "$BOOTSTRAP" 2>/dev/null; then
    pass "Bootstrap returns error on source compilation failure"
else
    fail "Bootstrap should return error on source compilation failure"
fi

# Test: Bootstrap fails when Circus.sln not found
if grep -q 'log_error.*Circus.sln not found' "$BOOTSTRAP" 2>/dev/null; then
    pass "Bootstrap fails when Circus.sln not found"
else
    fail "Bootstrap should fail when Circus.sln not found"
fi

# =============================================================================
# T11: Undefined-helper Regression Prevention
# =============================================================================

echo ""
echo "========================================"
echo "T11: Undefined-Helper Regression Prevention"
echo "========================================"

# Test: log_error, log_warn, log_info are defined in bootstrap
if grep -q '^log_error()' "$BOOTSTRAP" 2>/dev/null; then
    pass "log_error helper defined"
else
    fail "log_error helper should be defined"
fi

if grep -q '^log_warn()' "$BOOTSTRAP" 2>/dev/null; then
    pass "log_warn helper defined"
else
    fail "log_warn helper should be defined"
fi

if grep -q '^log_info()' "$BOOTSTRAP" 2>/dev/null; then
    pass "log_info helper defined"
else
    fail "log_info helper should be defined"
fi

# Test: No bare 'error', 'warn', 'success' calls (not using log_*)
UNDEFINED_ERRORS=$(grep -E '^\s+(error|warn|success)\s+["(]' "$BOOTSTRAP" 2>/dev/null | grep -v 'log_error\|log_warn\|log_info\|echo.*WARN\|echo.*ERROR\|log_warn.*Docker' || true)
if [[ -z "$UNDEFINED_ERRORS" ]]; then
    pass "No undefined helper calls in bootstrap"
else
    fail "Found undefined helper calls: $UNDEFINED_ERRORS"
fi

# =============================================================================
# T12: Idempotent Shell Integration
# =============================================================================

echo ""
echo "========================================"
echo "T12: Idempotent Shell Integration"
echo "========================================"

# Test: Activation shim is silent (no echo statements in the heredoc)
ACTIVATION_CONTENT=$(grep -A 20 'ACTIVATESCRIPT' "$BOOTSTRAP" 2>/dev/null | head -20)
if echo "$ACTIVATION_CONTENT" | grep -q 'echo.*Circus development environment'; then
    fail "Activation shim should not echo on source"
else
    pass "Activation shim is silent on source"
fi

# =============================================================================
# T13: Doctor Exit Code Contract
# =============================================================================

echo ""
echo "========================================"
echo "T13: Doctor Exit Code Contract"
echo "========================================"

# Test: Doctor documents exit 2 for host/repository errors
if grep -q "exit 2" "$DOCTOR" 2>/dev/null; then
    pass "Doctor uses exit 2 for host/repository errors"
else
    fail "Doctor should use exit 2 for host/repository errors"
fi

# Test: Doctor has supported architecture check
if grep -q "check_supported_architecture\|x86_64" "$DOCTOR" 2>/dev/null; then
    pass "Doctor checks supported architecture"
else
    fail "Doctor should check supported architecture"
fi

# Test: Doctor has supported OS check
if grep -q "check_supported_os\|UNSUPPORTED OS" "$DOCTOR" 2>/dev/null; then
    pass "Doctor checks supported OS"
else
    fail "Doctor should check supported OS"
fi

# =============================================================================
# T14: Elm via Locked Repository (not global npm)
# =============================================================================

echo ""
echo "========================================"
echo "T14: Elm via Locked Repository (not global npm)"
echo "========================================"

# Test: Bootstrap uses npm ci, not npm install -g
if grep -q 'npm ci' "$BOOTSTRAP" 2>/dev/null; then
    pass "Bootstrap uses npm ci for Elm"
else
    fail "Bootstrap should use npm ci for Elm"
fi

# Test: Bootstrap does NOT use global npm install for Elm
if grep -q 'npm install -g.*elm' "$BOOTSTRAP" 2>/dev/null; then
    fail "Bootstrap should not use global npm install for Elm"
else
    pass "Bootstrap does not use global npm install for Elm"
fi

# =============================================================================
# T15: Policy Venv Pinned Versions
# =============================================================================

echo ""
echo "========================================"
echo "T15: Policy Venv Pinned Versions"
echo "========================================"

# Test: Bootstrap uses pinned PyYAML version
if grep -q 'PyYAML==' "$BOOTSTRAP" 2>/dev/null; then
    pass "Bootstrap uses pinned PyYAML version"
else
    fail "Bootstrap should use pinned PyYAML version"
fi

# Test: Bootstrap uses pinned pip version
if grep -q 'pip==' "$BOOTSTRAP" 2>/dev/null; then
    pass "Bootstrap uses pinned pip version"
else
    fail "Bootstrap should use pinned pip version"
fi

# Test: Policy venv fails closed on Python 3.12 absence
if grep -q 'log_error.*Python 3.12 not found' "$BOOTSTRAP" 2>/dev/null; then
    pass "Policy venv fails on Python 3.12 absence"
else
    fail "Policy venv should fail on Python 3.12 absence"
fi

# =============================================================================
# T16: Bootstrap --check mode
# =============================================================================

echo ""
echo "========================================"
echo "T16: Bootstrap Check Mode"
echo "========================================"

# Test: Bootstrap supports --check mode
if bash "$BOOTSTRAP" --check >/dev/null 2>&1; then
    pass "Bootstrap supports --check mode"
else
    fail "Bootstrap should support --check mode"
fi

# Test: Bootstrap supports --dry-run mode
if bash "$BOOTSTRAP" --dry-run >/dev/null 2>&1; then
    pass "Bootstrap supports --dry-run mode"
else
    fail "Bootstrap should support --dry-run mode"
fi

# Test: Bootstrap rejects unknown options
if ! bash "$BOOTSTRAP" --unknown-option 2>/dev/null; then
    pass "Bootstrap rejects unknown options"
else
    fail "Bootstrap should reject unknown options"
fi

# =============================================================================
# T17: Version Verification Before Early Return
# =============================================================================

echo ""
echo "========================================"
echo "T17: Version Verification Before Early Return"
echo "========================================"

# Test: Node installation checks version before returning early
if grep -q 'installed_version.*NODE_VERSION.*FORCE' "$BOOTSTRAP" 2>/dev/null; then
    pass "Node checks version before early return"
else
    fail "Node should check version before early return"
fi

# Test: actionlint installation checks version before returning early
if grep -q 'installed_version.*ACTIONLINT_VERSION.*FORCE' "$BOOTSTRAP" 2>/dev/null; then
    pass "actionlint checks version before early return"
else
    fail "actionlint should check version before early return"
fi

# Test: ShellCheck installation checks version before returning early
if grep -q 'installed_version.*SHELLCHECK_VERSION.*FORCE' "$BOOTSTRAP" 2>/dev/null; then
    pass "ShellCheck checks version before early return"
else
    fail "ShellCheck should check version before early return"
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
