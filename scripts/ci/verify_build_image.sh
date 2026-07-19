#!/usr/bin/env bash
# Post-publish registry digest capture and digest-qualified smoke test for the
# Circus Harbor reusable workflow.
#
# Required environment:
#   IMAGE_REPOSITORY   - harbor-pve1.spbnix.local/circus/<image_name>
#   REVISION          - the full 40-character GitHub SHA
#   SMOKE_TEST_KIND   - backend | frontend
#   CONTAINER_CLI     - docker or podman (must accept login-less pull)
#   EXPECTED_REVISION - full SHA to compare against the OCI revision label
#
# Outputs (to stdout, one KEY=VALUE per line):
#   digest=sha256:<64 hex>
#   remote_smoke=passed|failed
set -euo pipefail

for required in \
    IMAGE_REPOSITORY REVISION SMOKE_TEST_KIND CONTAINER_CLI EXPECTED_REVISION; do
    if [[ -z "${!required:-}" ]]; then
        echo "verify_digest: missing required environment variable: $required" >&2
        exit 2
    fi
done

case "$SMOKE_TEST_KIND" in
    backend|frontend) ;;
    *) echo "verify_digest: unsupported smoke-test kind: $SMOKE_TEST_KIND" >&2; exit 1 ;;
esac

if [[ ! "$REVISION" =~ ^[0-9a-f]{40}$ ]]; then
    echo "verify_digest: REVISION is not a full 40-character SHA" >&2
    exit 1
fi

if [[ ! "$EXPECTED_REVISION" =~ ^[0-9a-f]{40}$ ]]; then
    echo "verify_digest: EXPECTED_REVISION is not a full 40-character SHA" >&2
    exit 1
fi

digest="$(docker buildx imagetools inspect "${IMAGE_REPOSITORY}:${REVISION}" | awk '$1 == "Digest:" { print $2; exit }')"
if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "verify_digest: registry digest was not captured" >&2
    exit 1
fi

docker pull "${IMAGE_REPOSITORY}@${digest}"
docker image inspect "${IMAGE_REPOSITORY}@${digest}" >/dev/null

CONTAINER_CLI="$CONTAINER_CLI" EXPECTED_REVISION="$EXPECTED_REVISION" \
    scripts/verify-published-image.sh "$SMOKE_TEST_KIND" "$IMAGE_REPOSITORY" "$digest"

CONTAINER_CLI="$CONTAINER_CLI" \
    scripts/container-smoke.sh "$SMOKE_TEST_KIND" "${IMAGE_REPOSITORY}@${digest}"

# Always emit to stdout so the executable shell test environment (which
# does not set $GITHUB_OUTPUT) keeps working.  When $GITHUB_OUTPUT is
# set (i.e., when the script runs inside GitHub Actions), APPEND the
# same lines to the GITHUB_OUTPUT file.  GitHub's documented contract
# is to append a single record per call.
emit_outputs() {
    printf 'digest=%s\n' "$digest"
    printf 'remote_smoke=passed\n'
}
emit_outputs
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    emit_outputs >> "$GITHUB_OUTPUT"
fi
cat <<EOF
digest=$digest
remote_smoke=passed
EOF
