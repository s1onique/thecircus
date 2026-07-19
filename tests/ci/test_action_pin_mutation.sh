#!/usr/bin/env bash
# Mutation test for the action-pin policy.
#
# The previous revision of scripts/verify_container_policy.py could be
# bypassed: replacing `actions/checkout@<full-SHA>` with
# `actions/checkout@v6` left `stripped` empty and silently skipped the
# entry.  This test mutates the workflow files in a sandbox copy and
# asserts the policy fails with a non-zero exit code, proving the
# parser now rejects the un-pinned value.
#
# The test runs entirely in a temp directory so it never mutates the
# working tree.
#
# shellcheck shell=bash disable=SC1091
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
POLICY="$ROOT/scripts/verify_container_policy.py"
TOP_WORKFLOW="$ROOT/.github/workflows/harbor.yml"
REUSABLE_WORKFLOW="$ROOT/.github/workflows/harbor-build-image.yml"

if [[ ! -f "$POLICY" ]]; then
    echo "FAIL: policy script not found at $POLICY" >&2
    exit 1
fi
if [[ ! -f "$TOP_WORKFLOW" ]]; then
    echo "FAIL: top-level workflow not found at $TOP_WORKFLOW" >&2
    exit 1
fi
if [[ ! -f "$REUSABLE_WORKFLOW" ]]; then
    echo "FAIL: reusable workflow not found at $REUSABLE_WORKFLOW" >&2
    exit 1
fi

SANDBOX="$(mktemp -d -t circus-pin-mutation-XXXXXX)"
trap 'rm -rf "$SANDBOX"' EXIT

# Mirror the repository layout the policy reads from.  Only the files
# the policy inspects need to be copied; everything else stays under
# the original $ROOT because the policy never reads from $SANDBOX
# unless we set ROOT explicitly.  We instead exercise the policy by
# running it from the sandbox with PYTHONPATH overrides that swap the
# ROOT variable.
mkdir -p "$SANDBOX/.github/workflows" "$SANDBOX/scripts/ci" \
         "$SANDBOX/.github/scripts" "$SANDBOX/docker/frontend" \
         "$SANDBOX/docs" "$SANDBOX/scripts" "$SANDBOX/web" \
         "$SANDBOX/db/migrations" "$SANDBOX/src/Circus.Api" \
         "$SANDBOX/src/Circus.Application" "$SANDBOX/src/Circus.Contracts" \
         "$SANDBOX/src/Circus.Domain" "$SANDBOX/src/Circus.Persistence.Postgres" \
         "$SANDBOX/tests/ci" "$SANDBOX/.factory"
cp "$POLICY"               "$SANDBOX/scripts/verify_container_policy.py"
cp "$ROOT/Dockerfile.backend"     "$SANDBOX/Dockerfile.backend"
cp "$ROOT/Dockerfile.frontend"    "$SANDBOX/Dockerfile.frontend"
cp "$ROOT/.dockerignore"          "$SANDBOX/.dockerignore"
cp "$ROOT/docker/frontend/nginx.conf" "$SANDBOX/docker/frontend/nginx.conf"
cp "$ROOT/scripts/container-smoke.sh" "$SANDBOX/scripts/container-smoke.sh"
cp "$ROOT/scripts/verify-published-image.sh" "$SANDBOX/scripts/verify-published-image.sh"
cp "$ROOT/scripts/ci/build_image.sh" "$SANDBOX/scripts/ci/build_image.sh"
cp "$ROOT/scripts/ci/publish_image.sh" "$SANDBOX/scripts/ci/publish_image.sh"
cp "$ROOT/scripts/ci/verify_build_image.sh" "$SANDBOX/scripts/ci/verify_build_image.sh"
cp "$ROOT/scripts/ci/wire_buildx_builder.sh" "$SANDBOX/scripts/ci/wire_buildx_builder.sh"
cp "$ROOT/tests/ci/test_build_publish_shell.sh" "$SANDBOX/tests/ci/test_build_publish_shell.sh"
cp "$ROOT/tests/ci/test_action_pin_mutation.sh" "$SANDBOX/tests/ci/test_action_pin_mutation.sh"
cp "$ROOT/tests/ci/test_gate_summary_acceptance.sh" "$SANDBOX/tests/ci/test_gate_summary_acceptance.sh"
cp "$ROOT/.github/scripts/harbor-metadata.sh" "$SANDBOX/.github/scripts/harbor-metadata.sh"
cp "$ROOT/.github/scripts/install-spbnix-harbor-ca.sh" "$SANDBOX/.github/scripts/install-spbnix-harbor-ca.sh"
cp "$ROOT/docs/harbor-publishing.md" "$SANDBOX/docs/harbor-publishing.md"
cp "$ROOT/Circus.sln" "$SANDBOX/Circus.sln" 2>/dev/null || true
cp "$TOP_WORKFLOW" "$SANDBOX/.github/workflows/harbor.yml"
cp "$REUSABLE_WORKFLOW" "$SANDBOX/.github/workflows/harbor-build-image.yml"

# Initialise the sandbox as a git repository so the policy's
# `git ls-files --cached --others --exclude-standard` call works.
# The configuration uses a dummy identity because the script only
# inspects the file listing.
git -C "$SANDBOX" -c init.defaultBranch=main -c user.email=ci@local \
    -c user.name=ci init --quiet
git -C "$SANDBOX" add -A
git -C "$SANDBOX" -c user.email=ci@local -c user.name=ci \
    commit --quiet -m "sandbox" >/dev/null 2>&1 || true


# The .factory/gate-summary.json file is now *detached* evidence
# (untracked, see .gitignore).  The action-pin mutation test no longer
# needs to copy the artefact into the sandbox or re-stamp its
# tested_tree_oid: the policy script does not read the artefact, and
# the regenerate script records HEAD^{tree} against the committed
# sandbox tree at generation time.

if [[ -f "$ROOT/.factory/regenerate_gate_summary.py" ]]; then
    cp "$ROOT/.factory/regenerate_gate_summary.py" \
        "$SANDBOX/.factory/regenerate_gate_summary.py"
fi

# Step 1: pristine tree must pass.
python3 "$SANDBOX/scripts/verify_container_policy.py" >/dev/null
echo "OK: pristine policy check passes"

# Step 2: mutate the action pin in BOTH workflow files and assert failure.
# The workflow files were copied during the initial mirror step; the
# `cp` here ensures the mutation starts from a clean state regardless
# of any prior in-place edits.
cp "$TOP_WORKFLOW" "$SANDBOX/.github/workflows/harbor.yml"
cp "$REUSABLE_WORKFLOW" "$SANDBOX/.github/workflows/harbor-build-image.yml"

mutated=0
for workflow in "$SANDBOX/.github/workflows/harbor.yml" "$SANDBOX/.github/workflows/harbor-build-image.yml"; do
    # Rewrite every `actions/checkout@<full-SHA>` (optionally followed
    # by a `# v6.x` comment) to the un-pinned `actions/checkout@v6`.
    if sed -i.bak -E \
        's|actions/checkout@[0-9a-f]{40}([[:space:]]*#[^[:space:]].*)?|actions/checkout@v6|g' \
        "$workflow"; then
        mutated=1
    fi
    rm -f "$workflow.bak"
done
if [[ "$mutated" -ne 1 ]]; then
    echo "FAIL: sed did not mutate any workflow file" >&2
    exit 1
fi

# Sanity check: every checkout reference in the mutated tree must now
# be `@v6`.
if ! grep -E 'actions/checkout@v6' "$SANDBOX/.github/workflows/harbor.yml" "$SANDBOX/.github/workflows/harbor-build-image.yml" >/dev/null; then
    echo "FAIL: sandbox workflows do not contain the expected actions/checkout@v6 mutation" >&2
    exit 1
fi
if grep -E 'actions/checkout@[0-9a-f]{40}' "$SANDBOX/.github/workflows/harbor.yml" "$SANDBOX/.github/workflows/harbor-build-image.yml" >/dev/null; then
    echo "FAIL: a SHA-pinned actions/checkout survived the mutation" >&2
    grep -E 'actions/checkout@[0-9a-f]{40}' "$SANDBOX/.github/workflows/harbor.yml" "$SANDBOX/.github/workflows/harbor-build-image.yml" >&2 || true
    exit 1
fi

# Step 3: the policy must now exit non-zero and surface a pin-related
# message.  Run with `set +e` so we can capture both the output and
# the exit code without aborting the test on the expected failure.
set +e
mutated_out="$(python3 "$SANDBOX/scripts/verify_container_policy.py" 2>&1)"
mutated_rc=$?
set -e
if [[ "$mutated_rc" -eq 0 ]]; then
    echo "FAIL: policy check accepted the @v6 mutation (rc=0)" >&2
    echo "--- mutated out ---" >&2
    echo "$mutated_out" >&2
    exit 1
fi
if ! grep -Eq 'pin|full SHA|unapproved external action' <<<"$mutated_out"; then
    echo "FAIL: policy failure message does not mention a pin problem" >&2
    echo "--- mutated out ---" >&2
    echo "$mutated_out" >&2
    exit 1
fi

echo "OK: mutated @v6 policy check failed as expected (rc=$mutated_rc)"

# Step 4: the original repository tree must still pass (defence in depth).
python3 "$POLICY" >/dev/null
echo "OK: original policy check still passes after the sandbox mutation"

echo "action-pin mutation test passed"
