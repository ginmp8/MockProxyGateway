#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
upstream_pid=""
host_pid=""
cleanup() {
  if [ -n "$host_pid" ]; then kill "$host_pid" 2>/dev/null || true; fi
  if [ -n "$upstream_pid" ]; then kill "$upstream_pid" 2>/dev/null || true; fi
  rm -rf "$work"
}
trap cleanup EXIT
cd "$root"
dotnet new web --name EmbeddedHost --output "$work/consumer" --no-restore
cp samples/embedded-host/Program.cs samples/embedded-host/*.json "$work/consumer/"
# Exercise the library's actual default prefix, not an explicitly configured equivalent.
python3 - "$work/consumer/appsettings.json" <<'PY'
import json
import sys
from pathlib import Path
path = Path(sys.argv[1])
settings = json.loads(path.read_text())
settings["MockProxyGateway"].pop("Prefix", None)
path.write_text(json.dumps(settings))
PY
dotnet add "$work/consumer/EmbeddedHost.csproj" package MockProxyGateway --version 0.1.0-preview.1 --no-restore
dotnet restore "$work/consumer/EmbeddedHost.csproj" --packages "$work/packages" \
  --source "$root/artifacts/packages" --source https://api.nuget.org/v3/index.json
dotnet build "$work/consumer/EmbeddedHost.csproj" -c Release --no-restore
mkdir "$work/upstream"
printf 'local-upstream' > "$work/upstream/ping.txt"
python3 -m http.server 5181 --bind 127.0.0.1 --directory "$work/upstream" >"$work/upstream.log" 2>&1 &
upstream_pid=$!
cd "$work/consumer"
start_host() {
  env "$@" ASPNETCORE_ENVIRONMENT=Development \
    dotnet bin/Release/net10.0/EmbeddedHost.dll --urls http://127.0.0.1:5182 >"$work/host.log" 2>&1 &
  host_pid=$!
  for i in $(seq 1 50); do
    if curl -fsS http://127.0.0.1:5182/api/health >/dev/null 2>&1; then return; fi
    if ! kill -0 "$host_pid" 2>/dev/null; then break; fi
    sleep 0.2
  done
  cat "$work/host.log"
  return 1
}
start_host -u MockProxyGateway__Prefix
test "$(curl -fsS http://127.0.0.1:5182/mock-proxy/demo/health)" = mock-health
test "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5182/mock-proxy-other/demo/health)" = 404
test "$(curl -fsS http://127.0.0.1:5182/mock-proxy/demo/ping.txt)" = local-upstream
test "$(curl -fsS http://127.0.0.1:5182/api/health)" = normal
kill "$host_pid"
wait "$host_pid" || true
host_pid=""
# Rebuild only the temporary package consumer to exercise the explicit overload.
# Keep a different configured prefix to prove the method argument wins.
python3 - Program.cs <<'PY'
import sys
from pathlib import Path
path = Path(sys.argv[1])
source = path.read_text()
old = "app.UseMockProxyGateway();"
assert source.count(old) == 1
path.write_text(source.replace(old, 'app.UseMockProxyGateway("/integration-gateway");'))
PY
dotnet build EmbeddedHost.csproj -c Release --no-restore
start_host MockProxyGateway__Prefix=/configured-prefix
test "$(curl -fsS http://127.0.0.1:5182/integration-gateway/demo/health)" = mock-health
test "$(curl -fsS http://127.0.0.1:5182/integration-gateway/demo/ping.txt)" = local-upstream
test "$(curl -fsS http://127.0.0.1:5182/api/health)" = normal
test "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5182/mock-proxy/demo/health)" = 404
test "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5182/configured-prefix/demo/health)" = 404
test "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5182/integration-gateway-other/demo/health)" = 404
echo "Package-only embedded mock and proxy smoke passed."
