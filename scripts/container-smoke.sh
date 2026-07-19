#!/usr/bin/env bash
# Run the repository-owned runtime smoke contract against one image.
set -euo pipefail

kind="${1:-}"
image="${2:-}"
container_cli="${CONTAINER_CLI:-docker}"

if [[ -z "$kind" || -z "$image" ]]; then
    echo "usage: $0 backend|frontend IMAGE" >&2
    exit 2
fi

case "$kind" in
    backend|frontend) ;;
    *) echo "unsupported smoke-test kind: $kind" >&2; exit 2 ;;
esac

command -v "$container_cli" >/dev/null 2>&1 || {
    echo "container CLI not found: $container_cli" >&2
    exit 1
}
command -v curl >/dev/null 2>&1 || {
    echo "curl is required for container smoke tests" >&2
    exit 1
}

container_name="circus-smoke-${kind}-$(date +%s)-$$"
container_started=0
exit_status=0

cleanup() {
    exit_status=$?
    if [[ "$exit_status" -ne 0 && "$container_started" -eq 1 ]]; then
        echo "--- ${kind} container logs (failure) ---" >&2
        if "$container_cli" logs "$container_name" >&2; then
            :
        else
            echo "container logs were unavailable" >&2
        fi
        echo "--- end container logs ---" >&2
    fi

    if "$container_cli" inspect "$container_name" >/dev/null 2>&1; then
        "$container_cli" rm --force "$container_name" >/dev/null
    fi
    exit "$exit_status"
}
trap cleanup EXIT

if [[ -n "${SMOKE_HOST_PORT:-}" ]]; then
    host_port_request="$SMOKE_HOST_PORT"
elif [[ "$kind" == "backend" ]]; then
    host_port_request=18081
else
    host_port_request=18082
fi
run_args=(run --detach --name "$container_name" --publish "127.0.0.1:${host_port_request}:8080")
if [[ "$kind" == "backend" ]]; then
    # The API validates the shape of this setting when it starts, but /health/live
    # never opens a connection.  No PostgreSQL container is required for smoke.
    run_args+=(--env 'CIRCUS_DATABASE_URL=Host=127.0.0.1;Port=5432;Database=circus_smoke;Username=smoke;Password=smoke')
fi
run_args+=("$image")

"$container_cli" "${run_args[@]}" >/dev/null
container_started=1

host_port="$("$container_cli" port "$container_name" 8080/tcp | head -n 1 | awk -F: '{print $NF}')"
if [[ ! "$host_port" =~ ^[0-9]+$ || "$host_port" == "0" ]]; then
    echo "could not determine mapped host port for $container_name" >&2
    exit 1
fi
base_url="http://127.0.0.1:${host_port}"

ready=0
for _ in $(seq 1 60); do
    if [[ "$kind" == "backend" ]]; then
        probe_url="$base_url/health/live"
    else
        probe_url="$base_url/healthz"
    fi

    if curl --fail --silent --show-error "$probe_url" >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 1
done
if [[ "$ready" -ne 1 ]]; then
    echo "${kind} container did not become ready within 60 seconds" >&2
    exit 1
fi

running="$("$container_cli" inspect --format '{{.State.Running}}' "$container_name")"
[[ "$running" == "true" ]] || {
    echo "container is not running: $running" >&2
    exit 1
}
uid="$("$container_cli" exec "$container_name" id -u | tr -d '[:space:]')"
if [[ ! "$uid" =~ ^[0-9]+$ || "$uid" -eq 0 ]]; then
    echo "container runtime user is root or not numeric: '$uid'" >&2
    exit 1
fi
echo "${kind} container runtime UID: $uid"

if [[ "$kind" == "backend" ]]; then
    body_file="$(mktemp)"
    curl --fail --silent --show-error -o "$body_file" -w '%{http_code}' "$base_url/health/live" >"${body_file}.status"
    status="$(cat "${body_file}.status")"
    body="$(cat "$body_file")"
    rm -f "$body_file" "${body_file}.status"
    [[ "$status" == "200" ]] || { echo "backend /health/live returned $status" >&2; exit 1; }
    [[ "$body" == *'"status":"live"'* ]] || { echo "unexpected backend liveness response: $body" >&2; exit 1; }
    echo "GET /health/live -> 200"
else
    root_file="$(mktemp)"
    root_status="$(curl --fail --silent --show-error -o "$root_file" -w '%{http_code}' "$base_url/")"
    [[ "$root_status" == "200" ]] || { echo "frontend / returned $root_status" >&2; exit 1; }
    grep -F '<title>The Circus</title>' "$root_file" >/dev/null || {
        echo "frontend root did not contain the stable application marker" >&2
        exit 1
    }
    rm -f "$root_file"
    health_status="$(curl --fail --silent --show-error -o /dev/null -w '%{http_code}' "$base_url/healthz")"
    [[ "$health_status" == "200" ]] || { echo "frontend /healthz returned $health_status" >&2; exit 1; }
    echo "GET / -> 200 (The Circus marker present)"
    echo "GET /healthz -> 200"
fi

echo "${kind} container smoke test passed for ${image}"
