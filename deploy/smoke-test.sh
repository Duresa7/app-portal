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

step "admin add creates the first administrator"
docker exec -e APPPORTAL_ADMIN_PASSWORD=smoke-password-1234 "$name" \
    dotnet AppPortal.Server.dll admin add --username smokeadmin
docker exec "$name" dotnet AppPortal.Server.dll admin list | grep -q smokeadmin
echo "smokeadmin listed"

step "The admin area is closed without a session"
code=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/admin")
[[ "$code" == 302 ]] || { echo "Expected 302 to the sign-in page, got $code"; exit 1; }
echo "302 as expected"

step "POST /api/v1/admin/session issues a bearer token"
admin_token=$(curl -fsS -X POST -H 'Content-Type: application/json' \
    -d '{"username":"smokeadmin","password":"smoke-password-1234"}' \
    "http://127.0.0.1:$port/api/v1/admin/session" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
[[ "$admin_token" == apa_* ]] || { echo "Expected an apa_ token, got '$admin_token'"; exit 1; }
echo "token issued"

step "Signing in through the form opens /admin"
jar=$(mktemp)
# The sign-in form carries an antiforgery token that the post has to echo back.
verification=$(curl -fsS -c "$jar" "http://127.0.0.1:$port/admin/login" \
    | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' \
    | sed 's/.*value="\([^"]*\)".*/\1/')
[[ -n "$verification" ]] || { echo "No antiforgery token on the sign-in form"; exit 1; }
curl -fsS -b "$jar" -c "$jar" -o /dev/null \
    --data-urlencode "Username=smokeadmin" \
    --data-urlencode "Password=smoke-password-1234" \
    --data-urlencode "__RequestVerificationToken=$verification" \
    "http://127.0.0.1:$port/admin/login"
code=$(curl -s -b "$jar" -o /tmp/admin.html -w '%{http_code}' "http://127.0.0.1:$port/admin")
[[ "$code" == 200 ]] || { echo "Expected 200 for a signed-in /admin, got $code"; exit 1; }
grep -q "Dashboard" /tmp/admin.html || { echo "/admin did not render the shell"; exit 1; }
rm -f "$jar"
echo "signed in and the dashboard rendered"

step "DELETE /api/v1/admin/session revokes the token"
code=$(curl -s -o /dev/null -w '%{http_code}' -X DELETE \
    -H "Authorization: Bearer $admin_token" "http://127.0.0.1:$port/api/v1/admin/session")
[[ "$code" == 204 ]] || { echo "Expected 204, got $code"; exit 1; }
code=$(curl -s -o /dev/null -w '%{http_code}' -X DELETE \
    -H "Authorization: Bearer $admin_token" "http://127.0.0.1:$port/api/v1/admin/session")
[[ "$code" == 401 ]] || { echo "A revoked token still worked, got $code"; exit 1; }
echo "revoked, and refused the second time"

echo
echo "Smoke test passed."
