#!/usr/bin/env bash
# Smoke test for the assembled application.
#
# Runs in a single `set -euo pipefail` shell transaction with trap-based
# cleanup. Uses a temporary directory owned by this script rather than
# fixed paths under /tmp.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# Use a private temporary directory for response bodies; removed on exit.
TMP_DIR="$(mktemp -d -t circus-smoke.XXXXXX)"
SERVER_PID=""

SMOKE_PORT="${SMOKE_PORT:-18080}"
HEALTH_URL="http://127.0.0.1:${SMOKE_PORT}/health/live"
ABOUT_URL="http://127.0.0.1:${SMOKE_PORT}/api/v1/about"
STYLES_URL="http://127.0.0.1:${SMOKE_PORT}/styles.css"
ROOT_URL="http://127.0.0.1:${SMOKE_PORT}/"
APP_JS_URL="http://127.0.0.1:${SMOKE_PORT}/app.js"

cleanup() {
    if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

assert_equal() {
    local expected="$1"
    local actual="$2"
    local description="$3"
    if [[ "$expected" != "$actual" ]]; then
        echo "FAIL: $description"
        echo "  expected: $expected"
        echo "  actual:   $actual"
        exit 1
    fi
}

assert_contains() {
    local needle="$1"
    local haystack="$2"
    local description="$3"
    if [[ "$haystack" != *"$needle"* ]]; then
        echo "FAIL: $description"
        echo "  needle:   $needle"
        echo "  haystack: $haystack"
        exit 1
    fi
}

echo "Starting API server..."
ASPNETCORE_HTTP_PORTS="$SMOKE_PORT" CIRCUS_DATABASE_URL='Host=127.0.0.1;Port=5432;Database=circus_smoke;Username=smoke;Password=smoke' \
    dotnet run --project src/Circus.Api -c Release --no-build --no-restore \
     > "$TMP_DIR/server.log" 2>&1 &
SERVER_PID=$!

echo "Waiting for $HEALTH_URL ..."
ready=0
for _ in $(seq 1 50); do
    if curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 0.2
done
if [[ "$ready" -ne 1 ]]; then
    echo "FAIL: server did not become ready in time"
    cat "$TMP_DIR/server.log"
    exit 1
fi

echo "Checking /health/live ..."
live_code="$(curl -sS -o "$TMP_DIR/live.json" -w '%{http_code}' "$HEALTH_URL")"
assert_equal "200" "$live_code" "GET /health/live status"
live_body="$(cat "$TMP_DIR/live.json")"
assert_equal '{"status":"live"}' "$live_body" "GET /health/live body"

echo "Checking /api/v1/about ..."
about_code="$(curl -sS -o "$TMP_DIR/about.json" -w '%{http_code}' "$ABOUT_URL")"
assert_equal "200" "$about_code" "GET /api/v1/about status"
about_body="$(cat "$TMP_DIR/about.json")"
assert_contains '"name":"The Circus"' "$about_body" "/api/v1/about includes name"
assert_contains '"tagline":"Team-scale Leamas"' "$about_body" "/api/v1/about includes tagline"
assert_contains '"description":"The team-scale coordination, evidence, and governance platform for Leamas."' \
    "$about_body" "/api/v1/about includes description"

echo "Checking /styles.css ..."
styles_code="$(curl -sS -o "$TMP_DIR/styles.css" -w '%{http_code}' "$STYLES_URL")"
assert_equal "200" "$styles_code" "GET /styles.css status"
styles_content_type="$(curl -sS -o /dev/null -D - "$STYLES_URL" | tr -d '\r' | awk -F': ' 'tolower($1) == "content-type" { print $2 }')"
case "$styles_content_type" in
    text/css*) ;;
    *) echo "FAIL: /styles.css content-type was '$styles_content_type', expected text/css"; exit 1 ;;
esac
styles_size="$(wc -c < "$TMP_DIR/styles.css" | tr -d ' ')"
if [[ "$styles_size" -lt 100 ]]; then
    echo "FAIL: /styles.css is empty (size=$styles_size)"
    exit 1
fi

echo "Checking / (HTML) ..."
root_code="$(curl -sS -o "$TMP_DIR/root.html" -w '%{http_code}' "$ROOT_URL")"
assert_equal "200" "$root_code" "GET / status"

echo "Checking /app.js ..."
js_code="$(curl -sS -o "$TMP_DIR/app.js" -w '%{http_code}' "$APP_JS_URL")"
assert_equal "200" "$js_code" "GET /app.js status"
js_size="$(wc -c < "$TMP_DIR/app.js" | tr -d ' ')"
if [[ "$js_size" -lt 100 ]]; then
    echo "FAIL: /app.js is empty (size=$js_size)"
    exit 1
fi

echo "Smoke test passed."