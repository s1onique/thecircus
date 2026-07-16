#!/usr/bin/env bash
# Produce the closed image tag and OCI metadata contract for the reusable workflow.
set -euo pipefail

: "${IMAGE_REPOSITORY:?IMAGE_REPOSITORY is required}"
: "${IMAGE_NAME:?IMAGE_NAME is required}"
: "${PUBLISH_REQUESTED:?PUBLISH_REQUESTED is required}"
: "${GITHUB_EVENT_NAME:?GITHUB_EVENT_NAME is required}"
: "${GITHUB_REF:?GITHUB_REF is required}"
: "${GITHUB_SHA:?GITHUB_SHA is required}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
: "${GITHUB_SERVER_URL:?GITHUB_SERVER_URL is required}"
: "${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"

sha="$GITHUB_SHA"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || { echo "GITHUB_SHA must be a full 40-character SHA" >&2; exit 1; }

# Publication is fail-closed.  A pull request, a feature-branch dispatch, or a
# manually selected non-release ref can never turn this metadata step into a
# Harbor publication even if a caller supplies push=true.
publish=false
if [[ "$PUBLISH_REQUESTED" == "true" && "$GITHUB_EVENT_NAME" != "pull_request" ]]; then
    case "$GITHUB_REF" in
        refs/heads/main|refs/tags/v[0-9]*.[0-9]*.[0-9]*) publish=true ;;
    esac
fi

created="$(git show -s --format=%cI "$sha")"
[[ -n "$created" ]] || { echo "could not determine commit timestamp" >&2; exit 1; }
source="${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}"
local_tag="${IMAGE_REPOSITORY}:local-${sha}"
version="$sha"
tags=""

if [[ "$publish" == "true" ]]; then
    case "$GITHUB_REF" in
        refs/heads/main)
            version="main"
            tags="${IMAGE_REPOSITORY}:${sha}
${IMAGE_REPOSITORY}:latest
${IMAGE_REPOSITORY}:main"
            ;;
        refs/tags/v*)
            release="${GITHUB_REF#refs/tags/v}"
            if [[ ! "$release" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
                echo "release tags must be vMAJOR.MINOR.PATCH: $GITHUB_REF" >&2
                exit 1
            fi
            major="${release%%.*}"
            remainder="${release#*.}"
            minor="${remainder%%.*}"
            version="$release"
            tags="${IMAGE_REPOSITORY}:v${release}
${IMAGE_REPOSITORY}:${release}
${IMAGE_REPOSITORY}:${major}.${minor}
${IMAGE_REPOSITORY}:${major}
${IMAGE_REPOSITORY}:${sha}"
            ;;
        *)
            echo "unsupported publication ref: $GITHUB_REF" >&2
            exit 1
            ;;
    esac
fi

cat >> "$GITHUB_OUTPUT" <<EOF
publish=${publish}
local_tag=${local_tag}
image_repository=${IMAGE_REPOSITORY}
source=${source}
revision=${sha}
version=${version}
created=${created}
tags<<CIRCUS_TAGS
${tags}
CIRCUS_TAGS
EOF

echo "publication=${publish} image=${IMAGE_REPOSITORY} revision=${sha} version=${version}"
if [[ -n "$tags" ]]; then
    echo "published tag set:"
printf '%s\n' "$tags" | sed 's/^/  /'
else
    echo "no Harbor tags: local smoke-only execution"
fi
