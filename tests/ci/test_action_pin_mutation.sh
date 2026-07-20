#!/usr/bin/env bash
# Mutation test for the action-pin policy.
#
# The previous revision of scripts/verify_container_policy.py could be
# bypassed: replacing `actions/checkout@<full-SHA>` with
# `actions/checkout@v6` left `stripped` empty and silently skipped the
# entry.  This test mutates the workflow files in a sandbox copy and
# asserts the F# container-policy verifier (the F# port of the deleted
# Python script) rejects the un-pinned value with a non-zero exit code,
# proving the parser now rejects the un-pinned value.
#
# The test runs entirely in a temp directory so it never mutates the
# working tree.
#
# shellcheck shell=bash disable=SC1091
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

# Locate the dotnet host.  The shell PATH may not include it (the
# canonical linux dev-host bootstrap adds it via /etc/profile.d, which
# is not sourced by ``bash tests/...`` invocations under ``make`` or
# the circus-tooling gate runner).  Fall back to the well-known
# circus-dev location before giving up.
if ! command -v dotnet >/dev/null 2>&1; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export PATH="$HOME/.dotnet:$PATH"
    elif [[ -x /usr/share/dotnet/dotnet ]]; then
        export PATH="/usr/share/dotnet:$PATH"
    fi
fi

# The canonical F# tooling DLL.  The RID-neutral framework-dependent
# artefact is the single source of truth; we invoke it through dotnet.
TOOLING_DLL="$ROOT/tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll"

if [[ ! -f "$TOOLING_DLL" ]]; then
    echo "FAIL: tooling DLL not found at $TOOLING_DLL" >&2
    exit 1
fi

TOP_WORKFLOW="$ROOT/.github/workflows/harbor.yml"
REUSABLE_WORKFLOW="$ROOT/.github/workflows/harbor-build-image.yml"

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

# Mirror the repository layout the verifier reads from.  Only the files
# the verifier inspects need to be copied.
mkdir -p "$SANDBOX/.github/workflows" "$SANDBOX/scripts/ci" \
         "$SANDBOX/.github/scripts" "$SANDBOX/docker/frontend" \
         "$SANDBOX/docs" "$SANDBOX/scripts" "$SANDBOX/web" \
         "$SANDBOX/db/migrations" "$SANDBOX/src/Circus.Api" \
         "$SANDBOX/src/Circus.Application" "$SANDBOX/src/Circus.Contracts" \
         "$SANDBOX/src/Circus.Domain" "$SANDBOX/src/Circus.Persistence.Postgres" \
         "$SANDBOX/tests/ci" "$SANDBOX/.factory"

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

# Initialise the sandbox as a git repository so CP-29's
# `git ls-files -z` call works.
git -C "$SANDBOX" -c init.defaultBranch=main -c user.email=ci@local \
    -c user.name=ci init --quiet
git -C "$SANDBOX" add -A
git -C "$SANDBOX" -c user.email=ci@local -c user.name=ci \
    commit --quiet -m "sandbox" >/dev/null 2>&1 || true

# Mirror the executable bit on every shell script the verifier checks.
for script in scripts/ci/build_image.sh scripts/ci/publish_image.sh \
              scripts/ci/verify_build_image.sh scripts/ci/wire_buildx_builder.sh \
              tests/ci/test_build_publish_shell.sh tests/ci/test_action_pin_mutation.sh \
              tests/ci/test_gate_summary_acceptance.sh; do
    chmod +x "$SANDBOX/$script" 2>/dev/null || true
done

# Step 1: pristine tree must pass.  The verifier exits 0 when every
# check passes.
set +e
pristine_out="$(cd "$SANDBOX" && dotnet "$TOOLING_DLL" container-policy verify 2>&1)"
pristine_rc=$?
set -e
if [[ "$pristine_rc" -ne 0 ]]; then
    echo "FAIL: pristine policy check failed (rc=$pristine_rc)" >&2
    echo "--- pristine out ---" >&2
    echo "$pristine_out" >&2
    exit 1
fi
echo "OK: pristine policy check passes"

# Step 2: mutate the action pin in BOTH workflow files and assert
# failure.  The mutation rewrites every
# `actions/checkout@<full-SHA>` (optionally followed by a
# `# v6.x` comment) to the un-pinned `actions/checkout@v6`.
for workflow in "$SANDBOX/.github/workflows/harbor.yml" "$SANDBOX/.github/workflows/harbor-build-image.yml"; do
    sed -i.bak -E \
        's|actions/checkout@[0-9a-f]{40}([[:space:]]*#[^[:space:]].*)?|actions/checkout@v6|g' \
        "$workflow"
    rm -f "$workflow.bak"
done

# Sanity check: every checkout reference in the mutated tree must now
# be `@v6`.
if ! grep -E 'actions/checkout@v6' "$SANDBOX/.github/workflows/harbor.yml" "$SANDBOX/.github/workflows/harbor-build-image.yml" >/dev/null; then
    echo "FAIL: sandbox workflows do not contain the expected actions/checkout@v6 mutation" >&2
    exit 1
fi
if grep -E 'actions/checkout@[0-9a-f]{40}' "$SANDBOX/.github/workflows/harbor.yml" "$SANDBOX/.github/workflows/harbor-build-image.yml" >/dev/null; then
    echo "FAIL: a SHA-pinned actions/checkout survived the mutation" >&2
    exit 1
fi

# Step 3: the policy must now exit non-zero and surface a pin-related
# message.  Run with `set +e` so we can capture both the output and
# the exit code without aborting the test on the expected failure.
set +e
mutated_out="$(cd "$SANDBOX" && dotnet "$TOOLING_DLL" container-policy verify 2>&1)"
mutated_rc=$?
set -e
if [[ "$mutated_rc" -eq 0 ]]; then
    echo "FAIL: policy check accepted the @v6 mutation (rc=0)" >&2
    echo "--- mutated out ---" >&2
    echo "$mutated_out" >&2
    exit 1
fi
if ! grep -Eq 'pin|full SHA|unapproved external action|action_pin|action_allowlist|action_sha_pin' <<<"$mutated_out"; then
    echo "FAIL: policy failure message does not mention a pin problem" >&2
    echo "--- mutated out ---" >&2
    echo "$mutated_out" >&2
    exit 1
fi

echo "OK: mutated @v6 policy check failed as expected (rc=$mutated_rc)"

# Step 4: the original repository tree must still pass (defence in depth).
set +e
original_out="$(cd "$ROOT" && dotnet "$TOOLING_DLL" container-policy verify 2>&1)"
original_rc=$?
set -e
if [[ "$original_rc" -ne 0 ]]; then
    echo "FAIL: original policy check failed (rc=$original_rc)" >&2
    echo "--- original out ---" >&2
    echo "$original_out" >&2
    exit 1
fi
echo "OK: original policy check still passes after the sandbox mutation"

echo "action-pin mutation test passed"
