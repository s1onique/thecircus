#!/usr/bin/env bash
# Trusted (publication) Buildx build for the Circus Harbor reusable workflow.
#
# This script is the source of truth for the publication path.  It runs the
# local smoke test result, then pushes the image, then updates the cache.  It
# does not perform the digest pull/smoke test (that is a separate step in the
# workflow so the step can be referenced individually).
#
# Required environment:
#   IMAGE_NAME        - circus-backend or circus-frontend
#   DOCKERFILE        - path to Dockerfile relative to repository root
#   BUILD_CONTEXT     - path to build context relative to repository root
#   PLATFORM          - OCI platform identifier
#   CACHE_REF         - Harbor cache reference
#   TAGS              - newline-separated list of full image:tag to push
#   BUILDER_NAME      - name of the pre-created Buildx builder
#   OCI_*             - OCI label values
#   RUNNER_TEMP       - temporary directory for the build log
#
# Optional environment:
#   CA_SECRET_PATH    - absolute path to a CA file to install only into the
#                       builder stage.
#
# Outputs (to stdout, one KEY=VALUE per line):
#   publish_status=success|failure
#   cache_status=exported|empty
set -euo pipefail

for required in \
    IMAGE_NAME DOCKERFILE BUILD_CONTEXT PLATFORM CACHE_REF TAGS BUILDER_NAME \
    OCI_TITLE OCI_DESCRIPTION OCI_SOURCE OCI_REVISION OCI_VERSION \
    OCI_CREATED RUNNER_TEMP; do
    if [[ -z "${!required:-}" ]]; then
        echo "publish_image: missing required environment variable: $required" >&2
        exit 2
    fi
done

if [[ -z "$CACHE_REF" ]]; then
    echo "publish_image: CACHE_REF is required" >&2
    exit 1
fi

# Defensive guard.  The workflow step that invokes this script is already
# guarded by `if: steps.metadata.outputs.publish == 'true'`, but the script
# itself must also refuse to push if it is ever called with a falsy
# PUBLISH value.  This protects against any future caller that bypasses
# the workflow condition.
if [[ "${PUBLISH:-false}" != "true" ]]; then
    echo "publish_image: refusing to push because PUBLISH is not 'true'" >&2
    exit 1
fi

publish_log="$RUNNER_TEMP/${IMAGE_NAME}-publish-build.log"
: > "$publish_log"

tag_args=()
while IFS= read -r tag; do
    [[ -n "$tag" ]] && tag_args+=(--tag "$tag")
done <<< "$TAGS"
if [[ "${#tag_args[@]}" -eq 0 ]]; then
    echo "publish_image: TAGS is empty" >&2
    exit 1
fi

build_args=(
    --build-arg "OCI_TITLE=$OCI_TITLE"
    --build-arg "OCI_DESCRIPTION=$OCI_DESCRIPTION"
    --build-arg "OCI_SOURCE=$OCI_SOURCE"
    --build-arg "OCI_REVISION=$OCI_REVISION"
    --build-arg "OCI_VERSION=$OCI_VERSION"
    --build-arg "OCI_CREATED=$OCI_CREATED"
)

secret_args=()
if [[ -n "${CA_SECRET_PATH:-}" && -s "${CA_SECRET_PATH}" ]]; then
    secret_args+=(--secret "id=spbnix-ca,src=$CA_SECRET_PATH")
fi

docker buildx build \
    --builder "$BUILDER_NAME" \
    --platform "$PLATFORM" \
    --file "$DOCKERFILE" \
    "${tag_args[@]}" \
    --cache-from "type=registry,ref=$CACHE_REF" \
    --cache-to "type=registry,ref=$CACHE_REF,mode=max,oci-mediatypes=true,image-manifest=true" \
    --provenance=mode=min \
    --sbom=true \
    --push \
    "${secret_args[@]}" \
    "${build_args[@]}" \
    "$BUILD_CONTEXT" 2>&1 | tee "$publish_log"

cache_status=empty
if grep -E 'exporting cache' "$publish_log" >/dev/null 2>&1; then
    cache_status=exported
fi

# Always emit to stdout so the executable shell test environment (which
# does not set $GITHUB_OUTPUT) keeps working.  When $GITHUB_OUTPUT is
# set (i.e., when the script runs inside GitHub Actions), APPEND the
# same lines to the GITHUB_OUTPUT file.  GitHub's documented contract
# is to append a single record per call.
emit_outputs() {
    printf 'publish_status=success\n'
    printf 'cache_status=%s\n' "$cache_status"
}
emit_outputs
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    emit_outputs >> "$GITHUB_OUTPUT"
fi
cat <<EOF
publish_status=success
cache_status=$cache_status
EOF
