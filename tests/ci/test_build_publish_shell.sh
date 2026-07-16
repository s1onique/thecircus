#!/usr/bin/env bash
# Executable shell tests for the Circus Harbor build/publish shell scripts.
#
# These tests run without Docker: each script is invoked through a fake
# `docker` shim that records the arguments it received.  The tests verify the
# argument contract, the branch coverage of `PUBLISH=true`/`false`, the error
# paths, the GITHUB_OUTPUT contract, the BuildKit secret extension, and the
# workflow-to-script integration seams.
#
# These tests deliberately do not require Python; the policy test suite in
# scripts/verify_container_policy.py exercises the YAML structure and
# dependency pins.
#
# shellcheck shell=bash disable=SC1091,SC2034
set -euo pipefail


HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

FAKE_BIN="$(mktemp -d -t circus-docker-fake-XXXXXX)"
LOG="$FAKE_BIN/docker.log"
trap 'rm -rf "$FAKE_BIN"' EXIT

# Realistic fake BuildKit config.  This file is what the trusted workflow
# writes; the test copies it to the test environment so BUILDKITD_CONFIG is
# non-empty and passes the script's `[[ -s "$BUILDKITD_CONFIG" ]]` guard.
cat > "$FAKE_BIN/buildkitd.toml" <<'EOF'
debug = true

[registry."harbor-pve1.spbnix.local"]
  ca = ["/etc/ssl/certs/ca-certificates.crt"]
EOF

cat > "$FAKE_BIN/docker" <<'EOF'
#!/usr/bin/env bash
# Minimal Docker CLI stub that records every call.
set -euo pipefail
printf '%s\n' "$*" >> "${DOCKER_FAKE_LOG:?}"
case "$1" in
    buildx)
        case "$2" in
            create)
                # wire_buildx_builder.sh verifies inspect later; emit a fake builder.
                builder=""
                for arg in "$@"; do
                    if [[ "$arg" == --name ]]; then
                        builder="__next__"
                        continue
                    fi
                    if [[ "$builder" == "__next__" ]]; then
                        echo "$arg"
                        builder=""
                    fi
                done
                ;;
            inspect)
                # The real `docker buildx inspect --format '{{.Driver}}'`
                # prints ONLY the driver value.  Match the same shape so
                # the script's `driver="$(... --format ...)"` captures a
                # single-line value.
                printf 'docker-container'
                ;;
            build)
                # A fake build that always succeeds and reports optional cache events.
                if [[ -n "${FAKE_BUILD_LOG_FILE:-}" ]]; then
                    {
                        echo "#1 [internal] fake build step"
                        if [[ "${FAKE_BUILD_IMPORT_CACHE:-false}" == "true" ]]; then
                            echo "#1 importing cache manifest from harbor-pve1.spbnix.local/circus/cache/fake:buildcache"
                        fi
                        echo "#1 naming to harbor-pve1.spbnix.local/circus/fake:local"
                    } > "$FAKE_BUILD_LOG_FILE"
                fi
                if [[ -n "${FAKE_PUBLISH_LOG_FILE:-}" ]]; then
                    if [[ "${FAKE_BUILD_EXPORT_CACHE:-false}" == "true" ]]; then
                        echo "#1 exporting cache to harbor-pve1.spbnix.local/circus/cache/fake:buildcache" \
                            > "$FAKE_PUBLISH_LOG_FILE"
                    fi
                fi
                ;;
            imagetools)
                # buildx imagetools inspect emits Name: …, Digest: sha256:…,
                # MediaType: …, Digest: sha256:… (one per platform).  The verify
                # script uses awk to grab the first Digest: line.
                echo "Name: ${4:-unknown}"
                echo "Digest: sha256:0000000000000000000000000000000000000000000000000000000000000000"
                ;;
        esac
        ;;
    pull)
        printf '{"schemaVersion":1,"name":"%s"}\n' "${3:-fake/image}" >/dev/null
        ;;
    login)
        : # not used by these tests
        ;;
    image)
        : # not used by these tests
        ;;
esac
EOF
chmod +x "$FAKE_BIN/docker"

# create-stub `docker buildx rm` noop
cat > "$FAKE_BIN/docker-extra.sh" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod +x "$FAKE_BIN/docker-extra.sh"

# Pull smoke.sh/verify-published-image.sh apart to keep these tests
# independent of their full container runtime.  We source a shim for the
# scripts so build_image.sh/publish_image.sh/verify_build_image.sh can call
# them in the test env without spinning a container.
mkdir -p "$FAKE_BIN/scripts" "$FAKE_BIN/scripts/ci"
cat > "$FAKE_BIN/scripts/container-smoke.sh" <<'EOF'
#!/usr/bin/env bash
echo "fake smoke for $1 $2"
EOF
cat > "$FAKE_BIN/scripts/verify-published-image.sh" <<'EOF'
#!/usr/bin/env bash
echo "fake verify for $1 $2 $3"
EOF
chmod +x "$FAKE_BIN/scripts/container-smoke.sh" "$FAKE_BIN/scripts/verify-published-image.sh"

# Copy the real build/publish/verify scripts into the test env so the fake
# container-smoke and verify-published-image are picked up via relative path.
cp "$ROOT/scripts/ci/build_image.sh"      "$FAKE_BIN/scripts/ci/build_image.sh"
cp "$ROOT/scripts/ci/publish_image.sh"    "$FAKE_BIN/scripts/ci/publish_image.sh"
cp "$ROOT/scripts/ci/verify_build_image.sh" "$FAKE_BIN/scripts/ci/verify_build_image.sh"
cp "$ROOT/scripts/ci/wire_buildx_builder.sh" "$FAKE_BIN/scripts/ci/wire_buildx_builder.sh"

required_env() {
    local image_name="${1:-}"
    local dockerfile="${2:-}"
    local build_context="${3:-}"
    local platform="${4:-}"
    local cache_ref="${5:-}"
    local publish="${6:-}"
    local local_tag="${7:-}"
    local builder_name="${8:-}"
    local oci_title="${9:-The Circus production OCI image}"
    local local_build_log_file="$FAKE_BIN/${image_name}-local-build.log"
    local publish_build_log_file="$FAKE_BIN/${image_name}-publish-build.log"
    cat <<EOF
IMAGE_NAME=$image_name
DOCKERFILE=$dockerfile
BUILD_CONTEXT=$build_context
PLATFORM=$platform
CACHE_REF='$cache_ref'
PUBLISH=$publish
LOCAL_TAG=$local_tag
BUILDER_NAME=$builder_name
OCI_TITLE='$oci_title'
OCI_DESCRIPTION='The Circus production $image_name OCI image'
OCI_SOURCE=https://github.com/example/example
OCI_REVISION=0000000000000000000000000000000000000000
OCI_VERSION=act-local
OCI_CREATED=2026-07-16T12:00:00+00:00
RUNNER_TEMP=$FAKE_BIN
DOCKER_FAKE_LOG=$LOG
FAKE_BUILDER_NAME=$builder_name
LOCAL_BUILD_LOG_FILE=$local_build_log_file
PUBLISH_BUILD_LOG_FILE=$publish_build_log_file
FAKE_BUILD_LOG_FILE=$local_build_log_file
FAKE_PUBLISH_LOG_FILE=$publish_build_log_file
TAGS=
EXPECTED_REVISION='0000000000000000000000000000000000000000'
REVISION='0000000000000000000000000000000000000000'
IMAGE_REPOSITORY=harbor-pve1.spbnix.local/circus/$image_name
SMOKE_TEST_KIND=${image_name#circus-}
CONTAINER_CLI=podman
EOF
}

# Real scripts must fail when required variables are missing.  The script's
# required-variable check fires when the variable expands to empty or
# whitespace, so we set every required var to an empty string and expect
# the script to refuse with IMAGE_NAME missing.
( set +u
  set +e
  out="$(env -i PATH="$FAKE_BIN:$PATH" \
    IMAGE_NAME= \
    DOCKERFILE=Dockerfile.backend \
    BUILD_CONTEXT=. \
    PLATFORM=linux/amd64 \
    CACHE_REF= \
    PUBLISH=false \
    LOCAL_TAG=local \
    BUILDER_NAME=fake \
    OCI_TITLE= \
    OCI_DESCRIPTION= \
    OCI_SOURCE= \
    OCI_REVISION= \
    OCI_VERSION= \
    OCI_CREATED= \
    RUNNER_TEMP="$FAKE_BIN" \
    bash "$FAKE_BIN/scripts/ci/build_image.sh" 2>&1)"
  rc=$?
  if [[ "$rc" -ne 2 ]]; then
    echo "FAIL: build_image.sh must fail with exit 2 when required variables are missing" >&2
    echo "--- out ---" >&2
    echo "$out" >&2
    exit 1
  fi
  if ! grep -q "missing required environment variable: IMAGE_NAME" <<<"$out"; then
    echo "FAIL: build_image.sh must report missing IMAGE_NAME" >&2
    echo "--- out ---" >&2
    echo "$out" >&2
    exit 1
  fi
)

: > "$LOG"

assert_contains() {
    local label="$1"
    local needle="$2"
    if ! grep -F -- "$needle" "$LOG" >/dev/null; then
        echo "FAIL: expected to find '$needle' in docker shim log for $label" >&2
        echo "--- log ---" >&2
        cat "$LOG" >&2 || true
        exit 1
    fi
}

run_with_fake() {
    local script="$1"
    shift
    # cd to $FAKE_BIN so the relative paths inside verify_build_image.sh
    # (scripts/verify-published-image.sh, scripts/container-smoke.sh) resolve
    # to the fake shims under $FAKE_BIN/scripts/ rather than the real scripts
    # in the repository root.
    ( cd "$FAKE_BIN" \
        && PATH="$FAKE_BIN:$PATH" bash "$FAKE_BIN/scripts/ci/$script" "$@" )
}

# ---- PUBLISH=false: no cache, no secret, no BuildKit image override ----
required_env circus-backend Dockerfile.backend . linux/amd64 harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache false circus-backend:local-fake k9b-bb "The Circus backend" \
    > "$FAKE_BIN/env-build-false"
set -a; source "$FAKE_BIN/env-build-false"; set +a
out="$(run_with_fake build_image.sh 2>&1)"
echo "$out" | grep -q "build_status=success" || { echo "FAIL: build_status=success missing"; echo "$out"; exit 1; }
echo "$out" | grep -q "cache_status=disabled" || { echo "FAIL: cache_status=disabled missing"; echo "$out"; exit 1; }
grep -q '\-\-provenance=false' "$LOG" || { echo "FAIL: build did not set --provenance=false"; cat "$LOG"; exit 1; }
grep -q '\-\-sbom=false' "$LOG" || { echo "FAIL: build did not set --sbom=false"; cat "$LOG"; exit 1; }
grep -q -- '--cache-from' "$LOG" && { echo "FAIL: --cache-from must not appear when PUBLISH=false"; cat "$LOG"; exit 1; }
grep -q -- '--cache-to' "$LOG" && { echo "FAIL: --cache-to must not appear when PUBLISH=false"; cat "$LOG"; exit 1; }
grep -q -- '--secret' "$LOG" && { echo "FAIL: --secret must not appear without CA_SECRET_PATH"; cat "$LOG"; exit 1; }
grep -q -- '--push' "$LOG" && { echo "FAIL: local build must not push"; cat "$LOG"; exit 1; }

# ---- PUBLISH=true: cache-from appears, no cache-to (export happens in publish step) ----
: > "$LOG"
required_env circus-backend Dockerfile.backend . linux/amd64 harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache true circus-backend:local-fake k9b-cc "The Circus backend" \
    > "$FAKE_BIN/env-build-true"
set -a; source "$FAKE_BIN/env-build-true"; set +a
out="$(FAKE_BUILD_IMPORT_CACHE=true run_with_fake build_image.sh 2>&1)"
echo "$out" | grep -q "cache_status=imported" || { echo "FAIL: cache_status=imported expected"; echo "$out"; exit 1; }
assert_contains "build-publish-true" "--cache-from type=registry,ref=harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache"
grep -q -- '--cache-to' "$LOG" && { echo "FAIL: --cache-to must not appear in local build"; cat "$LOG"; exit 1; }
grep -q -- '--push' "$LOG" && { echo "FAIL: local build must not push even when PUBLISH=true"; cat "$LOG"; exit 1; }

# ---- Optional CA secret mount: BuildKit --secret id=spbnix-ca,src=... ----
: > "$LOG"
echo "-----BEGIN CERTIFICATE-----" > "$FAKE_BIN/ca.pem"
echo "MIIBfakeCA" >> "$FAKE_BIN/ca.pem"
echo "-----END CERTIFICATE-----" >> "$FAKE_BIN/ca.pem"
required_env circus-frontend Dockerfile.frontend . linux/amd64 harbor-pve1.spbnix.local/circus/cache/circus-frontend:buildcache true circus-frontend:local-fake k9b-ff "The Circus frontend" \
    > "$FAKE_BIN/env-build-frontend"
set -a; source "$FAKE_BIN/env-build-frontend"; set +a
out="$(CA_SECRET_PATH="$FAKE_BIN/ca.pem" run_with_fake build_image.sh 2>&1)"
assert_contains "frontend-ca-secret" "--secret id=spbnix-ca,src=$FAKE_BIN/ca.pem"

# ---- Publish step: cache-to + push + --provenance=mode=min + --sbom=true ----
: > "$LOG"
required_env circus-backend Dockerfile.backend . linux/amd64 harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache true circus-backend:local-fake k9b-pp "The Circus backend" \
    > "$FAKE_BIN/env-publish"
echo 'TAGS="harbor-pve1.spbnix.local/circus/circus-backend:0000000000000000000000000000000000000000
harbor-pve1.spbnix.local/circus/circus-backend:latest"' >> "$FAKE_BIN/env-publish"
set -a; source "$FAKE_BIN/env-publish"; set +a
out="$(FAKE_BUILD_EXPORT_CACHE=true FAKE_PUBLISH_LOG_FILE=$FAKE_BIN/circus-backend-publish-build.log run_with_fake publish_image.sh 2>&1)"
echo "$out" | grep -q "publish_status=success" || { echo "FAIL: publish_status=success expected"; echo "$out"; exit 1; }
echo "$out" | grep -q "cache_status=exported" || { echo "FAIL: cache_status=exported expected"; echo "$out"; exit 1; }
assert_contains "publish-step" "--cache-to type=registry,ref=harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache,mode=max,oci-mediatypes=true,image-manifest=true"
assert_contains "publish-step" "--push"
assert_contains "publish-step" "--provenance=mode=min"
assert_contains "publish-step" "--sbom=true"

# ---- Publish step refuses empty TAGS ----
set +e
required_env circus-backend Dockerfile.backend . linux/amd64 harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache true circus-backend:local-fake k9b-ppe "The Circus backend" \
    > "$FAKE_BIN/env-publish-empty"
# Use whitespace-only TAGS so the required-variable check passes (TAGS is
# non-empty) but the read loop produces no tags (TAGS is empty after parsing).
printf 'TAGS="\n\n\n"' >> "$FAKE_BIN/env-publish-empty"
set -a; source "$FAKE_BIN/env-publish-empty"; set +a
out="$(run_with_fake publish_image.sh 2>&1)"
rc=$?
set -e
if [[ "$rc" -eq 0 ]]; then
    echo "FAIL: empty TAGS must fail"; echo "$out"; exit 1
fi
echo "$out" | grep -q "TAGS is empty" || { echo "FAIL: TAGS is empty message expected"; echo "$out"; exit 1; }

# ---- Build step fails when PUBLISH=true but CACHE_REF is whitespace ----
# The script strips whitespace before checking, so a single space is rejected
# by the required-variable guard with the specific CACHE_REF message.
set +e
required_env circus-backend Dockerfile.backend . linux/amd64 " " true circus-backend:local-fake k9b-bad "The Circus backend" \
    > "$FAKE_BIN/env-build-bad"
set -a; source "$FAKE_BIN/env-build-bad"; set +a
out="$(run_with_fake build_image.sh 2>&1)"
rc=$?
set -e
if [[ "$rc" -eq 0 ]]; then
    echo "FAIL: build must fail when CACHE_REF is empty under PUBLISH=true"; echo "$out"; exit 1
fi
echo "$out" | grep -qE 'CACHE_REF is required when PUBLISH=true|missing required environment variable: CACHE_REF' || {
    echo "FAIL: CACHE_REF required message expected"; echo "$out"; exit 1
}

# ---- Build step refuses unsupported platform ----
set +e
required_env circus-backend Dockerfile.backend . linux/s390x harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache false circus-backend:local-fake k9b-platform "The Circus backend" \
    > "$FAKE_BIN/env-build-platform"
set -a; source "$FAKE_BIN/env-build-platform"; set +a
out="$(run_with_fake build_image.sh 2>&1)"
rc=$?
set -e
if [[ "$rc" -eq 0 ]]; then
    echo "FAIL: unsupported platform must fail"; echo "$out"; exit 1
fi
echo "$out" | grep -q "only linux/amd64 is supported" || {
    echo "FAIL: platform guard message expected"; echo "$out"; exit 1
}

# ---- Verify digest: emits sha256 digest and runs smoke ----
: > "$LOG"
required_env circus-backend Dockerfile.backend . linux/amd64 harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache true circus-backend:local-fake k9b-vd "The Circus backend" \
    > "$FAKE_BIN/env-verify"
set -a; source "$FAKE_BIN/env-verify"; set +a
out="$(run_with_fake verify_build_image.sh 2>&1)"
echo "$out" | grep -q "^digest=sha256:[0-9a-f]\{64\}$" || { echo "FAIL: digest output missing"; echo "$out"; exit 1; }
echo "$out" | grep -q "remote_smoke=passed" || { echo "FAIL: remote_smoke=passed expected"; echo "$out"; exit 1; }

# ---- wire_buildx_builder.sh: PUBLISH=true applies --buildkitd-config and writes to GITHUB_OUTPUT ----
# BUILDKITD_CONFIG points to a real (non-empty) buildkitd.toml file.
: > "$LOG"
# All required env vars (PUBLISH, BUILDER_NAME, BUILDKIT_IMAGE,
# BUILDKITD_CONFIG, GITHUB_RUN_ID, GITHUB_RUN_ATTEMPT, GITHUB_OUTPUT,
# IMAGE_NAME) are exported inside an explicit subshell so the outer
# variable assignments do not apply to the nested `bash` process that
# run_with_fake launches.  Without the subshell, the script refused
# with "wire_builder: missing required environment variable:
# GITHUB_RUN_ID" because the variables were assigned to the calling
# shell but never exported.
out="$(
    export PUBLISH=true
    export BUILDER_NAME=k9b-bb
    export BUILDKIT_IMAGE=harbor-pve1.spbnix.local/dockerhub-cache/moby/buildkit:buildx-stable-1
    export BUILDKITD_CONFIG="$FAKE_BIN/buildkitd.toml"
    export GITHUB_RUN_ID=42
    export GITHUB_RUN_ATTEMPT=1
    export GITHUB_OUTPUT="$FAKE_BIN/gho"
    export IMAGE_NAME=circus-backend

    run_with_fake wire_buildx_builder.sh
)"

rc=$?
if [[ "$rc" -ne 0 ]]; then
    echo "FAIL: wire_buildx_builder.sh must succeed when PUBLISH=true and BUILDKITD_CONFIG is set"
    echo "$out"
    exit 1
fi
assert_contains "wire-builder-publish" "--driver-opt image=harbor-pve1.spbnix.local/dockerhub-cache/moby/buildkit:buildx-stable-1"
assert_contains "wire-builder-publish" "buildkitd-config"
# The script appends the outputs to GITHUB_OUTPUT (the reviewer's R1c
# required this to be `>>` rather than overwriting).
if ! grep -q '^builder=k9b-bb$' "$FAKE_BIN/gho"; then
    echo "FAIL: wire_buildx_builder.sh did not write builder=<name> to GITHUB_OUTPUT"
    echo "--- gho ---"; cat "$FAKE_BIN/gho"
    exit 1
fi
if ! grep -q '^driver=docker-container$' "$FAKE_BIN/gho"; then
    echo "FAIL: wire_buildx_builder.sh did not write driver=docker-container to GITHUB_OUTPUT"
    echo "--- gho ---"; cat "$FAKE_BIN/gho"
    exit 1
fi
rm -f /tmp/wire-out "$FAKE_BIN/gho"

# ---- wire_buildx_builder.sh: PUBLISH=false omits BUILDKITD_CONFIG and BuildKit image ----
: > "$LOG"
# Same explicit-subshell pattern as the PUBLISH=true block above:
# the env var assignments must be exported inside the subshell so
# the inner `bash` launched by run_with_fake observes them.  An
# earlier revision used the env= form, which left the script
# refusing with "wire_builder: missing required environment
# variable: GITHUB_RUN_ID" because the variables were never
# exported into the child process.
out="$(
    export PUBLISH=false
    export BUILDER_NAME=k9b-cc
    export BUILDKIT_IMAGE=harbor-pve1.spbnix.local/dockerhub-cache/moby/buildkit:buildx-stable-1
    export BUILDKITD_CONFIG=""
    export GITHUB_RUN_ID=42
    export GITHUB_RUN_ATTEMPT=1
    export GITHUB_OUTPUT="$FAKE_BIN/gho"
    export IMAGE_NAME=circus-frontend
    export PATH="$FAKE_BIN:$PATH"

    run_with_fake wire_buildx_builder.sh
)"

rc=$?
if [[ "$rc" -ne 0 ]]; then
    echo "FAIL: wire_buildx_builder.sh must succeed when PUBLISH=false"
    echo "$out"
    exit 1
fi
if grep -q "buildkitd-config" "$LOG"; then
    echo "FAIL: wire_buildx_builder.sh must not pass --buildkitd-config when PUBLISH=false"
    cat "$LOG"
    exit 1
fi
if grep -q -- "--driver-opt image=" "$LOG"; then
    echo "FAIL: wire_buildx_builder.sh must not pass --driver-opt image= when PUBLISH=false"
    cat "$LOG"
    exit 1
fi
rm -f /tmp/wire-out "$FAKE_BIN/gho"

# ---- wire_buildx_builder.sh: missing required variables fails with exit 2 ----
set +e
out="$(env -i PATH="$FAKE_BIN:$PATH" bash "$FAKE_BIN/scripts/ci/wire_buildx_builder.sh" 2>&1)"
rc=$?
set -e
if [[ "$rc" -ne 2 ]]; then
    echo "FAIL: wire_buildx_builder.sh must exit 2 when required variables are missing"; echo "$out"; exit 1
fi
echo "$out" | grep -q "missing required environment variable" || { echo "FAIL: missing-var message expected"; echo "$out"; exit 1; }

# ---- Workflow-to-script seam: reusable workflow forces ubuntu-latest on PRs ----
# The reusable workflow forces ubuntu-latest on pull_request events so the
# trusted spbnix-k8s-docker runner is unreachable for untrusted builds.
# The expression actually lives in
# .github/workflows/harbor-build-image.yml (the reusable caller), not in
# the top-level harbor.yml orchestrator.  The previous revision of this
# test asserted the wrong workflow file; this is the R1.3 fix.
REUSABLE_WORKFLOW="$ROOT/.github/workflows/harbor-build-image.yml"
if ! grep -F \
    "github.event_name == 'pull_request' && 'ubuntu-latest' || inputs.runner" \
    "$REUSABLE_WORKFLOW"; then
    echo "FAIL: $REUSABLE_WORKFLOW must force 'ubuntu-latest' on pull_request events"
    exit 1
fi

# ---- Workflow-to-script seam: secret defaulting for untrusted events ----
# Both backend and frontend jobs in the top-level harbor.yml orchestrator
# must default the three Harbor secrets (HARBOR_USERNAME, HARBOR_PASSWORD,
# SPBNIX_CA_CERT_PEM) to '' for untrusted events.  The reviewer's R1a
# required this seam and the previous R1f defect had the test reading a
# temporary variable instead of the actual workflow file.  Inspect each
# job mapping independently so the assertion cannot pass by accident on a
# single repository-wide grep.
TOP_WORKFLOW="$ROOT/.github/workflows/harbor.yml"
top_workflow_text="$(cat "$TOP_WORKFLOW")"
for image in "backend" "frontend"; do
    # Isolate the block that calls the reusable workflow for this image
    # so the per-image defaulting assertions cannot bleed into each other.
    block="$(awk -v target="image_name: circus-${image}" '
        $0 ~ target { capture = 1; print; next }
        capture && /^  [a-zA-Z]/ { capture = 0; next }
        capture { print }
    ' "$TOP_WORKFLOW")"
    for secret in HARBOR_USERNAME HARBOR_PASSWORD SPBNIX_CA_CERT_PEM; do
        # `grep -F` does fixed-string matching, so the literal '.' in
        # `secrets.HARBOR_USERNAME` must appear as-is.  The previous
        # revision doubled-escaped it to `secrets\\.` which became
        # `secrets\.` literally and never matched the workflow text.
        pattern="secrets.${secret} || ''"
        if ! grep -F "$pattern" <<<"$block" >/dev/null; then
            echo "FAIL: harbor.yml $image job must default ${secret} to '' for untrusted events"
            echo "--- $image block ---"
            echo "$block"
            exit 1
        fi
    done
done

# ---- Workflow-to-script seam: GITHUB_OUTPUT assertions on the four scripts ----
# Outside of GitHub Actions, every script must still print the same
# KEY=VALUE lines to stdout so the executable shell test environment
# (which does not set $GITHUB_OUTPUT) keeps working.  This was the
# R1c review requirement: every script that the workflow reads as a
# step output must write to $GITHUB_OUTPUT.
for script in build_image.sh publish_image.sh verify_build_image.sh wire_buildx_builder.sh; do
    if ! grep -q 'GITHUB_OUTPUT' "$ROOT/scripts/ci/$script"; then
        echo "FAIL: $script does not write outputs to GITHUB_OUTPUT"
        exit 1
    fi
done

# ---- Verify digest: rejects malformed REVISION ----
set +e
REVISION=bad EXPECTED_REVISION=0000000000000000000000000000000000000000 \
    IMAGE_REPOSITORY=harbor-pve1.spbnix.local/circus/circus-backend \
    SMOKE_TEST_KIND=backend CONTAINER_CLI=podman \
    IMAGE_NAME=circus-backend DOCKERFILE=Dockerfile.backend BUILD_CONTEXT=. \
    PLATFORM=linux/amd64 CACHE_REF=harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache \
    LOCAL_TAG=harbor-pve1.spbnix.local/circus/circus-backend:local-fake \
    BUILDER_NAME=k9b-badvd OCI_TITLE=Test OCI_DESCRIPTION=test \
    OCI_SOURCE=local://test OCI_REVISION=0000000000000000000000000000000000000000 \
    OCI_VERSION=test OCI_CREATED=2026-07-16T12:00:00+00:00 \
    RUNNER_TEMP="$FAKE_BIN" \
    PATH="$FAKE_BIN:$PATH" \
    "$FAKE_BIN/scripts/ci/verify_build_image.sh"
rc=$?
set -e
if [[ "$rc" -eq 0 ]]; then
    echo "FAIL: malformed REVISION must fail"
    exit 1
fi

echo "all build/publish shell tests passed"
