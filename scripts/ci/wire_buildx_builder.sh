#!/usr/bin/env bash
# Create the Circus Harbor Buildx builder.
#
# Required environment for every invocation:
#   IMAGE_NAME        - circus-backend or circus-frontend
#   BUILDER_NAME      - requested builder name (the script appends a run id)
#   PUBLISH           - "true" or "false"
#   GITHUB_RUN_ID     - GitHub run id (used for the builder suffix)
#   GITHUB_RUN_ATTEMPT - GitHub run attempt (used for the builder suffix)
#
# Required only when PUBLISH=true (the trusted-publication path):
#   BUILDKIT_IMAGE    - Harbor-proxied BuildKit image
#   BUILDKITD_CONFIG  - buildkitd config path; must be a non-empty file
#
# Outputs:
#   - On stdout (always): the same KEY=VALUE lines so the executable shell
#     test environment can still consume them.
#   - On $GITHUB_OUTPUT when set: the same lines, APPENDED so they
#     satisfy the GitHub Actions workflow-command contract.
#     The reusable workflow reads steps.builder.outputs.{builder,driver}.
set -euo pipefail

for required in IMAGE_NAME BUILDER_NAME PUBLISH GITHUB_RUN_ID GITHUB_RUN_ATTEMPT; do
    if [[ -z "${!required:-}" ]]; then
        echo "wire_builder: missing required environment variable: $required" >&2
        exit 2
    fi
done

if [[ "$PUBLISH" == "true" ]]; then
    for required in BUILDKIT_IMAGE BUILDKITD_CONFIG; do
        if [[ -z "${!required:-}" ]]; then
            echo "wire_builder: missing required environment variable: $required (PUBLISH=true)" >&2
            exit 2
        fi
    done
    if [[ ! -s "$BUILDKITD_CONFIG" ]]; then
        echo "wire_builder: BUILDKITD_CONFIG is missing or empty: $BUILDKITD_CONFIG" >&2
        exit 1
    fi
    docker buildx create \
        --name "$BUILDER_NAME" \
        --driver docker-container \
        --driver-opt "image=$BUILDKIT_IMAGE" \
        --buildkitd-config "$BUILDKITD_CONFIG" \
        --use \
        --bootstrap
else
    # Untrusted builds still need a builder so the local image can be
    # loaded for the smoke test.  BUILDKITD_CONFIG and BUILDKIT_IMAGE are
    # intentionally not required for this path.
    docker buildx create \
        --name "$BUILDER_NAME" \
        --driver docker-container \
        --use \
        --bootstrap
fi

docker buildx inspect "$BUILDER_NAME" >/dev/null
driver="$(docker buildx inspect "$BUILDER_NAME" --format '{{.Driver}}')"

emit_outputs() {
    printf 'builder=%s\n' "$BUILDER_NAME"
    printf 'driver=%s\n' "$driver"
}

# Always emit to stdout so the executable shell test environment (which
# does not set $GITHUB_OUTPUT) keeps working.  When $GITHUB_OUTPUT is set
# (i.e., when the script runs inside GitHub Actions), APPEND the same
# lines to the GITHUB_OUTPUT file.  The reviewer's R1c required this to
# be `>>` rather than overwriting, and the GITHUB_OUTPUT contract is
# "append a single record per call".
emit_outputs
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    emit_outputs >> "$GITHUB_OUTPUT"
fi
