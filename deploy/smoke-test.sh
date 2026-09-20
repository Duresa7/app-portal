#!/usr/bin/env bash
# Starts a built server image in fake Action1 mode and walks the paths an operator and a device use:
# health, catalog edits, devices, installs, admin sessions, and software request decisions.
# CI runs this before any image is pushed. Run it by hand against a local build:
#   docker build -f deploy/Dockerfile -t app-portal-server:local . && deploy/smoke-test.sh app-portal-server:local
set -euo pipefail

image="${1:?usage: smoke-test.sh <image>}"
name="app-portal-smoke-$$"
port="${SMOKE_PORT:-18080}"
config="$(cd "$(dirname "$0")" && pwd)/config"
scratch=$(mktemp -d)
jar="$scratch/cookies"
mkdir "$scratch/data"

cleanup() {
    if [[ "${failed:-0}" != 0 ]]; then
        echo "--- container log"
        docker logs "$name" 2>&1 | tail -50
    fi
    docker rm -f "$name" >/dev/null 2>&1 || true
    rm -rf "$scratch"
}
trap 'failed=$?; cleanup' EXIT

step() { echo; echo "==> $*"; }

step "Start $image in fake mode"
docker run -d --user "$(id -u):$(id -g)" --name "$name" -p "127.0.0.1:$port:8080" -e Action1__Mode=Fake -v "$config:/app/config:ro" -v "$scratch/data:/app/data" "$image" >/dev/null

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
docker exec "$name" dotnet AppPortal.Server.dll catalog export > "$scratch/catalog-export.json"
python3 -c 'import json,sys; apps=json.load(open(sys.argv[1]))["apps"]; assert apps, "empty export"; print(len(apps), "apps exported")' "$scratch/catalog-export.json"

step "catalog import is idempotent"
docker exec "$name" dotnet AppPortal.Server.dll catalog import /app/config/catalog.json

step "catalog verify resolves every package"
docker exec "$name" dotnet AppPortal.Server.dll catalog verify

step "device add prints a token"
out=$(docker exec "$name" dotnet AppPortal.Server.dll device add --name SMOKE --endpoint-id smoke-endpoint-0001)
token=$(echo "$out" | tail -n 1 | tr -d '[:space:]')
[[ ${#token} -ge 32 ]] || { echo "No token in device add output"; exit 1; }

step "key create prints an enrollment key"
out=$(docker exec "$name" dotnet AppPortal.Server.dll key create --name smoke-rollout --engine agent)
enroll_key=$(echo "$out" | tail -n 1 | tr -d '[:space:]')
[[ "$enroll_key" == ape_* ]] || { echo "No enrollment key in key create output"; exit 1; }

step "The enrollment check accepts the key and names its engine"
# The setup wizard reads the engine to decide whether it has to ask for an Action1 endpoint id before
# it installs anything, so the name in this body is a contract and not a label.
code=$(curl -s -o "$scratch/check.json" -w '%{http_code}' \
    -H "X-Enrollment-Key: $enroll_key" "http://127.0.0.1:$port/api/v1/enroll/check")
[[ "$code" == 200 ]] || { echo "Expected 200 from the enrollment check, got $code"; exit 1; }
python3 -c 'import json,sys; r=json.load(open(sys.argv[1])); assert r["engine"]=="agent", r' "$scratch/check.json"
echo "200 naming the agent engine, as expected"

step "A PC trades the key for a device token"
# No bearer token on this call: the key is what authenticates it, which is the whole point of the route.
code=$(curl -s -o "$scratch/enroll.json" -w '%{http_code}' -X POST \
    -H 'Content-Type: application/json' \
    -d "{\"key\":\"$enroll_key\",\"deviceName\":\"SMOKE-ENROLLED\",\"machineId\":\"smoke-machine-0001\",\"agentVersion\":\"0.4.0\"}" \
    "http://127.0.0.1:$port/api/v1/enroll")
[[ "$code" == 201 ]] || { cat "$scratch/enroll.json"; echo "Expected 201 from enroll, got $code"; exit 1; }
enrolled_token=$(python3 -c 'import json,sys; r=json.load(open(sys.argv[1])); assert r["engines"]==["agent"], r["engines"]; print(r["deviceToken"])' "$scratch/enroll.json")

step "The enrolled device reads the catalog with the token it was handed"
curl -fsS -H "Authorization: Bearer $enrolled_token" "http://127.0.0.1:$port/api/v1/catalog" | grep -q '"id"'
echo "enrolled device authenticated"

step "Re-enrolling the same machine rotates the token and stops the old one"
code=$(curl -s -o "$scratch/enroll2.json" -w '%{http_code}' -X POST \
    -H 'Content-Type: application/json' \
    -d "{\"key\":\"$enroll_key\",\"deviceName\":\"SMOKE-ENROLLED\",\"machineId\":\"smoke-machine-0001\"}" \
    "http://127.0.0.1:$port/api/v1/enroll")
[[ "$code" == 201 ]] || { cat "$scratch/enroll2.json"; echo "Expected 201 from re-enrolment, got $code"; exit 1; }
second_token=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["deviceToken"])' "$scratch/enroll2.json")
[[ "$second_token" != "$enrolled_token" ]] || { echo "Re-enrolment handed back the same token"; exit 1; }
code=$(curl -s -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer $enrolled_token" "http://127.0.0.1:$port/api/v1/catalog")
[[ "$code" == 401 ]] || { echo "The token from before the re-enrolment still works, got $code"; exit 1; }
echo "the old token is dead and the new one is live"

step "Neither the enrollment key nor a device token is in the server log"
docker logs "$name" 2>&1 | grep -q "$enroll_key" && { echo "The enrollment key leaked into the log"; exit 1; }
docker logs "$name" 2>&1 | grep -q "$second_token" && { echo "A device token leaked into the log"; exit 1; }
echo "no secrets in the log"

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
code=$(curl -s -o "$scratch/install.json" -w '%{http_code}' -X POST \
    -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -H 'X-AppPortal-User: SMOKE\operator' \
    -d "{\"appId\":\"$app_id\"}" "http://127.0.0.1:$port/api/v1/installs")
cat "$scratch/install.json"; echo
[[ "$code" == 202 ]] || { echo "Expected 202, got $code"; exit 1; }

step "The install shows up in the device's history"
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/installs" | grep -q "\"appId\":\"$app_id\""
echo "listed"

step "An agent-only app becomes a job and reports progress on the same install"
# M3-01 owns catalog authoring; seed its package row directly until that UI is available here.
python3 - "$scratch/data/app-portal.db" <<'PYSQL'
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as db:
    db.execute("INSERT INTO catalog_apps (id, name, created_at, updated_at) VALUES ('agent-smoke', 'Agent Smoke', '', '')")
    db.execute("INSERT INTO catalog_packages VALUES ('agent-smoke', 'agent', ?)",
               ('{"kind":"winget","id":"Smoke.Package","scope":"machine"}',))
PYSQL
curl -fsS -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d '{"agentVersion":"0.5.0","clientVersion":null,"osVersion":"smoke"}' \
    "http://127.0.0.1:$port/api/v1/agent/heartbeat" >/dev/null
curl -fsS -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d '{"appId":"agent-smoke"}' "http://127.0.0.1:$port/api/v1/installs" > "$scratch/agent-install.json"
agent_install=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"])' "$scratch/agent-install.json")
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/agent/jobs?wait=0" > "$scratch/job.json"
job_id=$(python3 -c 'import json,sys; j=json.load(open(sys.argv[1])); assert j["installId"]==sys.argv[2]; print(j["id"])' "$scratch/job.json" "$agent_install")
curl -fsS -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d '{"state":"downloading","percent":43,"detail":"Downloading 43%"}' \
    "http://127.0.0.1:$port/api/v1/agent/jobs/$job_id/progress" >/dev/null
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/installs/$agent_install" \
    | python3 -c 'import json,sys; i=json.load(sys.stdin); assert i["percentComplete"]==43; assert i["detail"]=="Downloading 43%"'
curl -fsS -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d '{"ok":false,"detail":"no executor","exitCode":null}' \
    "http://127.0.0.1:$port/api/v1/agent/jobs/$job_id/complete" >/dev/null
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/installs/$agent_install" \
    | python3 -c 'import json,sys; i=json.load(sys.stdin); assert i["state"]=="Failed"; assert i["detail"]=="no executor"'
echo "agent progress and completion reached the original install"

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
code=$(curl -s -b "$jar" -o "$scratch/admin.html" -w '%{http_code}' "http://127.0.0.1:$port/admin")
[[ "$code" == 200 ]] || { echo "Expected 200 for a signed-in /admin, got $code"; exit 1; }
grep -q "Dashboard" "$scratch/admin.html" || { echo "/admin did not render the shell"; exit 1; }
echo "signed in and the dashboard rendered"

step "Hiding an app in the browser takes it off the device catalog"
verification=$(curl -fsS -b "$jar" -c "$jar" "http://127.0.0.1:$port/admin/catalog" \
    | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' \
    | head -n 1 | sed 's/.*value="\([^"]*\)".*/\1/')
[[ -n "$verification" ]] || { echo "No antiforgery token on the catalog page"; exit 1; }
curl -fsS -b "$jar" -c "$jar" -o /dev/null -X POST \
    --data-urlencode "id=$app_id" \
    --data-urlencode "hidden=true" \
    --data-urlencode "__RequestVerificationToken=$verification" \
    "http://127.0.0.1:$port/admin/catalog?handler=Hide"
# No restart in between: the store reads through to the database on every request.
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/catalog" \
    | grep -q "\"id\":\"$app_id\"" && { echo "A hidden app is still served to devices"; exit 1; }
echo "$app_id is hidden from devices"

step "Showing it again puts it back"
curl -fsS -b "$jar" -c "$jar" -o /dev/null -X POST \
    --data-urlencode "id=$app_id" \
    --data-urlencode "hidden=false" \
    --data-urlencode "__RequestVerificationToken=$verification" \
    "http://127.0.0.1:$port/admin/catalog?handler=Hide"
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/catalog" \
    | grep -q "\"id\":\"$app_id\"" || { echo "The app did not come back"; exit 1; }
echo "$app_id is served again"

step "Editing an app changes what the device reads"
# Copy the seeded app's fields so this checks an edit without changing its package mapping.
python3 - "$scratch/catalog-export.json" "$app_id" > "$scratch/catalog-form" <<'PY'
import json, sys, urllib.parse
app = next(a for a in json.load(open(sys.argv[1]))['apps'] if a['id'] == sys.argv[2])
match = app.get('match') or {}
print(urllib.parse.urlencode({
    'Id': app['id'], 'Name': 'Smoke edited app', 'Publisher': app['publisher'],
    'Description': app['description'], 'Category': app['category'],
    'IconUrl': app.get('iconUrl') or '', 'Featured': str(app.get('featured', False)).lower(),
    'Hidden': 'false', 'MatchNameContains': match.get('nameContains') or '',
    'MatchNameEquals': match.get('nameEquals') or '',
    'PackageId': app['action1']['packageId'], 'Version': app['action1']['version'],
}), end='')
PY
code=$(curl -sS -b "$jar" -o "$scratch/catalog-save.html" -w '%{http_code}' \
    --data-binary "@$scratch/catalog-form" \
    --data-urlencode "__RequestVerificationToken=$verification" \
    "http://127.0.0.1:$port/admin/catalog/$app_id")
[[ "$code" == 302 ]] || { echo "Expected catalog save redirect, got $code"; exit 1; }
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/catalog" \
    | python3 -c 'import json,sys; app=next(a for a in json.load(sys.stdin) if a["id"]==sys.argv[1]); assert app["name"]=="Smoke edited app"' "$app_id"
echo "edited name is visible without a restart"

step "A device submits a software request"
code=$(curl -sS -o "$scratch/request.json" -w '%{http_code}' \
    -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -H 'X-AppPortal-User: SMOKE\operator' \
    -d '{"text":"Please add a PDF editor for the smoke test."}' \
    "http://127.0.0.1:$port/api/v1/requests")
[[ "$code" == 201 ]] || { echo "Expected 201 for a new request, got $code"; exit 1; }
request_id=$(python3 -c 'import json,sys; r=json.load(open(sys.argv[1])); assert r["status"]=="Pending"; assert r["requestedBy"]==r"SMOKE\operator"; print(r["id"])' "$scratch/request.json")

step "The administrator approves it and the device sees the decision"
curl -fsS -b "$jar" "http://127.0.0.1:$port/admin/requests" > "$scratch/requests.html"
grep -q "$request_id" "$scratch/requests.html" || { echo "Request missing from admin page"; exit 1; }
curl -fsS -b "$jar" -o /dev/null \
    --data-urlencode "id=$request_id" \
    --data-urlencode "reason=Approved for the smoke test" \
    --data-urlencode "__RequestVerificationToken=$verification" \
    "http://127.0.0.1:$port/admin/requests?handler=Approve"
curl -fsS -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/requests" \
    | python3 -c 'import json,sys; r=next(r for r in json.load(sys.stdin) if r["id"]==sys.argv[1]); assert r["status"]=="Approved"; assert r["reason"]=="Approved for the smoke test"' "$request_id"
echo "approval and reason reached the device"

step "The administrator can read fleet install history"
code=$(curl -sS -b "$jar" -o "$scratch/installs.html" -w '%{http_code}' "http://127.0.0.1:$port/admin/installs")
[[ "$code" == 200 ]] || { echo "Expected 200 for install history, got $code"; exit 1; }
install_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"])' "$scratch/install.json")
grep -q "$install_id" "$scratch/installs.html" || { echo "Install missing from admin history"; exit 1; }
grep -Fq 'SMOKE\operator' "$scratch/installs.html" || { echo "Install requester missing from admin history"; exit 1; }
echo "install and requester are visible to the administrator"

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
