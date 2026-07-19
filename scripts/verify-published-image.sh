#!/usr/bin/env bash
# Verify immutable registry image metadata before its digest smoke test.
set -euo pipefail

kind="${1:-}"
image="${2:-}"
digest="${3:-}"
container_cli="${CONTAINER_CLI:-docker}"
expected_revision="${EXPECTED_REVISION:-${GITHUB_SHA:-}}"

case "$kind" in
    backend|frontend) ;;
    *) echo "usage: $0 backend|frontend IMAGE sha256:DIGEST" >&2; exit 2 ;;
esac
[[ "$image" != *'@'* ]] || { echo "image must be a repository/tag without @" >&2; exit 2; }
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "invalid digest: $digest" >&2; exit 2; }
[[ "$expected_revision" =~ ^[0-9a-f]{40}$ ]] || { echo "GITHUB_SHA is required and must be a full SHA" >&2; exit 2; }
ref="${image}@${digest}"

os="$("$container_cli" image inspect --format '{{.Os}}' "$ref")"
arch="$("$container_cli" image inspect --format '{{.Architecture}}' "$ref")"
user="$("$container_cli" image inspect --format '{{.Config.User}}' "$ref")"
ports="$("$container_cli" image inspect --format '{{json .Config.ExposedPorts}}' "$ref")"
revision="$("$container_cli" image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$ref")"
source="$("$container_cli" image inspect --format '{{index .Config.Labels "org.opencontainers.image.source"}}' "$ref")"

[[ "$os" == "linux" ]] || { echo "unexpected image OS: $os" >&2; exit 1; }
[[ "$arch" == "amd64" ]] || { echo "unexpected image architecture: $arch" >&2; exit 1; }
uid="${user%%:*}"
[[ "$uid" =~ ^[0-9]+$ && "$uid" -ne 0 ]] || { echo "image runtime user is root/non-numeric: '$user'" >&2; exit 1; }
[[ "$ports" == *'8080/tcp'* ]] || { echo "image does not expose 8080/tcp: $ports" >&2; exit 1; }
[[ "$revision" == "$expected_revision" ]] || {
    echo "revision label mismatch: expected $expected_revision, got $revision" >&2
    exit 1
}
[[ "$source" == https://github.com/*/* ]] || { echo "invalid OCI source label: $source" >&2; exit 1; }

for label in title description source revision version created; do
    value="$("$container_cli" image inspect --format "{{index .Config.Labels \"org.opencontainers.image.${label}\"}}" "$ref")"
    [[ -n "$value" && "$value" != '<no value>' ]] || {
        echo "missing OCI label: org.opencontainers.image.${label}" >&2
        exit 1
    }
done

echo "verified ${kind} ${ref}: os=${os} architecture=${arch} uid=${uid} port=8080 revision=${revision}"
