#!/usr/bin/env bash
# Local (pre-publish) Buildx build for the Circus Harbor reusable workflow.
#
# This script is the source of truth for the local build path.  The reusable
# workflow invokes it; the shell unit tests exercise it directly.
#
# Required environment:
#   IMAGE_NAME        - circus-backend or circus-frontend
#   DOCKERFILE        - path to Dockerfile relative to repository root
#   BUILD_CONTEXT     - path to build context relative to repository root
#   PLATFORM          - OCI platform identifier, e.g. linux/amd64
#   CACHE_REF         - Harbor cache reference (may be empty when publish=false)
#   PUBLISH           - "true" or "false"
#   LOCAL_TAG         - local image tag for the loaded image
#   BUILDER_NAME      - name of the pre-created Buildx builder
#   OCI_TITLE         - org.opencontainers.image.title label
#   OCI_DESCRIPTION   - org.opencontainers.image.description label
#   OCI_SOURCE        - org.opencontainers.image.source label
#   OCI_REVISION      - org.opencontainers.image.revision label
#   OCI_VERSION       - org.opencontainers.image.version label
#   OCI_CREATED       - org.opencontainers.image.created label
#   RUNNER_TEMP       - temporary directory for the build log
#
# Optional environment:
#   CA_SECRET_PATH    - absolute path to a CA file to install only into the
#                       frontend builder (the CA is not copied into the final
#                       image stage).
#
# Outputs (to stdout, one KEY=VALUE per line):
#   local_tag=<image:tag>
#   build_status=success|failure
#   cache_status=imported|empty|disabled
set -euo pipefail

for required in \
    IMAGE_NAME DOCKERFILE BUILD_CONTEXT PLATFORM CACHE_REF PUBLISH LOCAL_TAG \
    BUILDER_NAME OCI_TITLE OCI_DESCRIPTION OCI_SOURCE OCI_REVISION \
    OCI_VERSION OCI_CREATED RUNNER_TEMP; do
    # Use the substring expansion so an empty string OR a whitespace-only
    # value both fail the required-variable check.
    if [[ -z "${!required// /}" ]]; then
        echo "build_image: missing required environment variable: $required" >&2
        exit 2
    fi
done

case "$PLATFORM" in
    linux/amd64) ;;
    *) echo "build_image: only linux/amd64 is supported by this ACT, got '$PLATFORM'" >&2; exit 1 ;;
esac

build_log="$RUNNER_TEMP/${IMAGE_NAME}-local-build.log"
: > "$build_log"

build_args=(
    --build-arg "OCI_TITLE=$OCI_TITLE"
    --build-arg "OCI_DESCRIPTION=$OCI_DESCRIPTION"
    --build-arg "OCI_SOURCE=$OCI_SOURCE"
    --build-arg "OCI_REVISION=$OCI_REVISION"
    --build-arg "OCI_VERSION=$OCI_VERSION"
    --build-arg "OCI_CREATED=$OCI_CREATED"
)

cache_args=()
cache_status=disabled
if [[ "$PUBLISH" == "true" ]]; then
    # Reject empty, whitespace-only, or otherwise non-substantive values so
    # the same guard protects both the test environment and the live runner.
    if [[ -z "${CACHE_REF// /}" ]]; then
        echo "build_image: CACHE_REF is required when PUBLISH=true" >&2
        exit 1
    fi
    cache_args+=(--cache-from "type=registry,ref=$CACHE_REF")
    cache_status=empty
fi

secret_args=()
if [[ -n "${CA_SECRET_PATH:-}" && -s "${CA_SECRET_PATH}" ]]; then
    secret_args+=(--secret "id=spbnix-ca,src=$CA_SECRET_PATH")
fi

# The local build is loaded into the local daemon for the smoke test.  It does
# not push.  SBOM and provenance are produced only by the publication build.
docker buildx build \
    --builder "$BUILDER_NAME" \
    --platform "$PLATFORM" \
    --file "$DOCKERFILE" \
    --tag "$LOCAL_TAG" \
    --load \
    --provenance=false \
    --sbom=false \
    "${cache_args[@]}" \
    "${secret_args[@]}" \
    "${build_args[@]}" \
    "$BUILD_CONTEXT" 2>&1 | tee "$build_log"

if [[ "$PUBLISH" == "true" ]] && grep -E 'importing cache manifest' "$build_log" >/dev/null 2>&1; then
    cache_status=imported
fi

# Always emit to stdout so the executable shell test environment (which
# does not set $GITHUB_OUTPUT) keeps working.  When $GITHUB_OUTPUT is
# set (i.e., when the script runs inside GitHub Actions), APPEND the
# same lines to the GITHUB_OUTPUT file.  GitHub's documented contract
# is to append a single record per call.
emit_outputs() {
    printf 'local_tag=%s\n' "$LOCAL_TAG"
    printf 'build_status=success\n'
    printf 'cache_status=%s\n' "$cache_status"
}
emit_outputs
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    emit_outputs >> "$GITHUB_OUTPUT"
fi
cat <<EOF
local_tag=$LOCAL_TAG
build_status=success
cache_status=$cache_status
EOF
