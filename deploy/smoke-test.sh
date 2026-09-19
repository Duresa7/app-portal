#!/usr/bin/env bash
# Starts a built server image in fake Action1 mode and walks the paths an operator and a device use:
# health, catalog verify, device add, an authenticated catalog read, and an install request.
# CI runs this before any image is pushed. Run it by hand against a local build:
#   docker build -f deploy/Dockerfile -t app-portal-server:local . && deploy/smoke-test.sh app-portal-server:local
set -euo pipefail

image="${1:?usage: smoke-test.sh <image>}"
name="app-portal-smoke-$$"
port="${SMOKE_PORT:-18080}"
config="$(cd "$(dirname "$0")" && pwd)/config"

cleanup() {
    if [[ "${failed:-0}" != 0 ]]; then
        echo "--- container log"
        docker logs "$name" 2>&1 | tail -50
    fi
    docker rm -f "$name" >/dev/null 2>&1 || true
}
trap 'failed=$?; cleanup' EXIT

step() { echo; echo "==> $*"; }

step "Start $image in fake mode"
docker run -d --name "$name" -p "127.0.0.1:$port:8080" -e Action1__Mode=Fake -v "$config:/app/config:ro" "$image" >/dev/null

step "Wait for /healthz"
for _ in $(seq 1 30); do
    if curl -fsS "http://127.0.0.1:$port/healthz" >/dev/null 2>&1; then break; fi
    sleep 1
done
curl -fsS "http://127.0.0.1:$port/healthz" | grep -q '"status":"ok"'
echo "healthz answers ok"

step "The image's own HEALTHCHECK reports healthy"
for _ in $(seq 1 60); do
    status=$(docker inspect --format '{{.State.Health.Status}}' "$name")
    [[ "$status" == healthy ]] && break
    sleep 1
done
[[ "$status" == healthy ]] || { echo "HEALTHCHECK status is '$status'"; exit 1; }
echo "docker reports $status"

step "The checked-in catalog seeded the database"
docker exec "$name" dotnet AppPortal.Server.dll catalog export > /tmp/catalog-export.json
python3 -c 'import json,sys; apps=json.load(open("/tmp/catalog-export.json"))["apps"]; assert apps, "empty export"; print(len(apps), "apps exported")'

step "catalog import is idempotent"
docker exec "$name" dotnet AppPortal.Server.dll catalog import /app/config/catalog.json

step "catalog verify resolves every package"
docker exec "$name" dotnet AppPortal.Server.dll catalog verify

step "device add prints a token"
out=$(docker exec "$name" dotnet AppPortal.Server.dll device add --name SMOKE --endpoint-id smoke-endpoint-0001)
echo "$out"
token=$(echo "$out" | tail -n 1 | tr -d '[:space:]')
[[ ${#token} -ge 32 ]] || { echo "No token in device add output"; exit 1; }

step "A request without a token is refused"
code=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/api/v1/catalog")
[[ "$code" == 401 ]] || { echo "Expected 401, got $code"; exit 1; }
echo "401 as expected"

step "The device reads the catalog"
catalog=$(curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/catalog")
app_id=$(echo "$catalog" | python3 -c 'import json,sys; print(json.load(sys.stdin)[0]["id"])')
echo "$catalog" | grep -q '"packageId"' && { echo "Catalog leaks package identifiers"; exit 1; }
echo "first app: $app_id"

step "The device requests an install and gets 202"
code=$(curl -s -o /tmp/install.json -w '%{http_code}' -X POST \
    -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d "{\"appId\":\"$app_id\"}" "http://127.0.0.1:$port/api/v1/installs")
cat /tmp/install.json; echo
[[ "$code" == 202 ]] || { echo "Expected 202, got $code"; exit 1; }

step "The install shows up in the device's history"
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/installs" | grep -q "\"appId\":\"$app_id\""
echo "listed"

echo
echo "Smoke test passed."
