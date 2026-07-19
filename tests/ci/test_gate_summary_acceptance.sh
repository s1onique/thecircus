#!/usr/bin/env bash
# Acceptance test for the canonical Leamas v1 gate-summary contract.
#
# This test closes the R1 / R2 reviewers' findings on
# ACT-CIRCUS-CONTAINER-HARBOR-PUBLISH01-CORRECTION07.  It exercises the
# full chain that the targeted digest consumer relies on:
#
#   1. Regenerate .factory/gate-summary.json from the three canonical
#      local gates.
#   2. Assert the artefact uses the canonical Leamas v1 vocabulary:
#        overall_status: pass | fail | unavailable
#        check status:   pass | fail | skip | unavailable
#   3. Assert the tested_tree_oid binds to the committed tree
#      (HEAD^{tree}), proving the evidence matches the closure commit.
#   4. Run the actual targeted-digest command (`leamas factory digest`)
#      and parse its GATE_SUMMARY section.  Assert that the consumer
#      reports:
#        overall_status=pass
#        checks_passed=3
#        checks_failed=0
#        checks_unavailable=0
#
# The previous revision validated the raw JSON producer only; the
# targeted digest kept reporting every check as `unavailable` because
# the JSON used non-canonical `green` / `passed` values.  This test
# exercises the real consumer so the same regression cannot recur.
#
# shellcheck shell=bash disable=SC1091,SC2016
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
SUMMARY_PATH="$ROOT/.factory/gate-summary.json"
REGEN="$ROOT/.factory/regenerate_gate_summary.py"

PYTHON_BIN="$(command -v python3)"
if [[ -z "$PYTHON_BIN" ]]; then
    echo "FAIL: python3 is required for the gate-summary acceptance test" >&2
    exit 1
fi

if [[ ! -x "$PYTHON_BIN" ]]; then
    echo "FAIL: python3 at $PYTHON_BIN is not executable" >&2
    exit 1
fi

if ! command -v leamas >/dev/null 2>&1; then
    echo "FAIL: leamas CLI is required for the targeted-digest assertion" >&2
    exit 1
fi

if [[ ! -f "$REGEN" ]]; then
    echo "FAIL: regenerate script missing: $REGEN" >&2
    exit 1
fi

if [[ ! -f "$ROOT/.gitignore" ]]; then
    echo "FAIL: .gitignore missing; the gate-summary detachment contract requires it" >&2
    exit 1
fi

# Detachment contract: the artefact must be ignored.  Tracking the file
# would create a self-reference (the file would carry the hash of the
# tree that contains itself).  The .gitignore check protects future
# edits from silently re-introducing the tracked-file binding.
if ! grep -Eq '^[[:space:]]*\.factory/gate-summary\.json[[:space:]]*$' "$ROOT/.gitignore"; then
    echo "FAIL: .gitignore does not exclude .factory/gate-summary.json (R1 self-reference)" >&2
    exit 1
fi

# --- Step 1: regenerate the artefact from the three canonical gates ---
echo "----- regenerating .factory/gate-summary.json -----"
regen_out="$("$PYTHON_BIN" "$REGEN" 2>&1)"
regen_rc=$?
if [[ "$regen_rc" -ne 0 ]]; then
    echo "FAIL: regenerate_gate_summary.py exited with $regen_rc" >&2
    echo "--- output ---" >&2
    echo "$regen_out" >&2
    exit 1
fi
echo "$regen_out"

if [[ ! -f "$SUMMARY_PATH" ]]; then
    echo "FAIL: regenerate did not produce $SUMMARY_PATH" >&2
    exit 1
fi

# --- Step 2: canonical Leamas v1 vocabulary ---
echo "----- validating canonical vocabulary -----"
validation="$("$PYTHON_BIN" - "$SUMMARY_PATH" <<'PY'
import json
import re
import sys

VALID_OVERALL = {"pass", "fail", "unavailable"}
VALID_CHECK = {"pass", "fail", "skip", "unavailable"}

path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    summary = json.load(handle)

errors: list[str] = []

if not isinstance(summary, dict):
    errors.append("gate-summary.json root must be a JSON object")
else:
    if summary.get("schema_version") != 1:
        errors.append(
            f"schema_version must be 1, got {summary.get('schema_version')!r}"
        )
    overall = summary.get("overall_status")
    if overall not in VALID_OVERALL:
        errors.append(
            f"overall_status must be one of {sorted(VALID_OVERALL)}, got {overall!r}"
        )
    checks = summary.get("checks")
    if not isinstance(checks, list) or not checks:
        errors.append("checks must be a non-empty list")
    for index, check in enumerate(checks or []):
        if not isinstance(check, dict):
            errors.append(f"checks[{index}] must be a JSON object")
            continue
        name = check.get("name")
        status = check.get("status")
        if not isinstance(name, str) or not name:
            errors.append(f"checks[{index}].name must be a non-empty string")
        if status not in VALID_CHECK:
            errors.append(
                f"checks[{index}].status must be one of {sorted(VALID_CHECK)}, "
                f"got {status!r} (this is the R1 vocabulary regression)"
            )

    recorded_tree = summary.get("tested_tree_oid")
    if not isinstance(recorded_tree, str) or not re.fullmatch(r"[0-9a-f]{40}", recorded_tree):
        errors.append(
            f"tested_tree_oid must be a SHA-1 string, got {recorded_tree!r}"
        )

if errors:
    print("FAIL")
    for error in errors:
        print(f"  - {error}")
    sys.exit(1)

print("OK")
PY
)"
if [[ "$validation" != "OK" ]]; then
    echo "$validation"
    exit 1
fi

# --- Step 3: tree-OID binding (detached evidence) ---
echo "----- validating tree-OID binding -----"
binding_check="$("$PYTHON_BIN" - "$SUMMARY_PATH" "$ROOT" <<'PY'
import json
import subprocess
import sys

summary_path, root = sys.argv[1], sys.argv[2]
with open(summary_path, encoding="utf-8") as handle:
    summary = json.load(handle)

recorded = summary.get("tested_tree_oid")
completed = subprocess.run(
    ["git", "rev-parse", "HEAD^{tree}"],
    cwd=root,
    check=True,
    capture_output=True,
    text=True,
)
expected = completed.stdout.strip()

if recorded != expected:
    print(
        f"FAIL: tested_tree_oid {recorded!r} != HEAD^{{tree}} ({expected!r})"
    )
    sys.exit(1)

print(f"OK: tested_tree_oid {recorded[:12]}... matches HEAD^{{tree}}")
PY
)"
echo "$binding_check"
if [[ "$binding_check" != OK* ]]; then
    exit 1
fi

# --- Step 4: targeted-digest integration assertion ---
# This is the canonical check the previous revision missed: the raw JSON
# can be valid while the actual consumer reports every check as
# `unavailable`.  Running `leamas factory digest` and asserting its
# GATE_SUMMARY section proves the closed loop.
echo "----- running leamas factory digest -----"
DIGEST_PATH="$(mktemp -t circus-gate-summary-digest-XXXXXX.txt)"
trap 'rm -f "$DIGEST_PATH" "$SUMMARY_PATH"' EXIT
leamas_out="$(leamas factory digest --output "$DIGEST_PATH" 2>&1)"
echo "$leamas_out"
if [[ ! -f "$DIGEST_PATH" ]]; then
    echo "FAIL: leamas factory digest did not produce $DIGEST_PATH" >&2
    exit 1
fi

# The digest file uses a YAML-like "## GATE_SUMMARY" section.  Pull the
# block out so the assertion only inspects the GATE_SUMMARY data, not
# other sections that mention overall_status (e.g. CHANGESET_MANIFEST).
gate_summary_block="$(
    awk '
        /^## GATE_SUMMARY/ { capture = 1; next }
        capture && /^## / { capture = 0 }
        capture { print }
    ' "$DIGEST_PATH"
)"

if [[ -z "$gate_summary_block" ]]; then
    echo "FAIL: leamas factory digest output has no GATE_SUMMARY section" >&2
    cat "$DIGEST_PATH" >&2
    exit 1
fi

assert_kv() {
    local label="$1"
    local key="$2"
    local expected="$3"
    local actual
    actual="$(awk -v want="^${key}=" '
        $0 ~ want {
            sub(want, "", $0)
            sub(/^[[:space:]]+/, "", $0)
            print
            exit
        }
    ' <<<"$gate_summary_block")"
    if [[ "$actual" != "$expected" ]]; then
        echo "FAIL: leamas digest GATE_SUMMARY.${key} = ${actual:-<missing>}, expected ${expected}" >&2
        echo "--- GATE_SUMMARY ---" >&2
        echo "$gate_summary_block" >&2
        exit 1
    fi
    echo "OK: leamas digest GATE_SUMMARY.${key} = ${actual}"
}

assert_kv "overall_status" "overall_status" "pass"
assert_kv "checks_total"   "checks_total"   "3"
assert_kv "checks_passed"  "checks_passed"  "3"
assert_kv "checks_failed"  "checks_failed"  "0"
assert_kv "checks_unavailable" "checks_unavailable" "0"

# --- Step 5: per-check assertion ---
# The previous revision left the per-check statuses at `unavailable`
# even when the raw JSON used `passed`.  Assert every named check is
# recognised as `pass` by the digest consumer.
expected_checks=(
    "container-publication-policy"
    "executable-shell-tests"
    "action-pin-mutation-test"
)
for name in "${expected_checks[@]}"; do
    # The digest line looks like:
    #   - name=<NAME> status=<STATUS> evidence=<EVIDENCE>
    # so we capture only the STATUS value (terminated by the next space).
    actual="$(awk -v want="^  - name=${name} status=" '
        $0 ~ want {
            value = $0
            sub(want, "", value)
            sub(/[[:space:]].*$/, "", value)
            print value
            exit
        }
    ' <<<"$gate_summary_block")"
    if [[ "$actual" != "pass" ]]; then
        echo "FAIL: leamas digest GATE_SUMMARY check ${name} = ${actual:-<missing>}, expected pass" >&2
        echo "--- GATE_SUMMARY ---" >&2
        echo "$gate_summary_block" >&2
        exit 1
    fi
    echo "OK: leamas digest GATE_SUMMARY check ${name} = pass"
done

echo "gate-summary acceptance test passed"
