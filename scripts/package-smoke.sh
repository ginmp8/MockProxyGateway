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
# Only the freshly packed gateway may satisfy this package ID. Dependencies
# continue to restore from NuGet.org in the isolated consumer cache.
# https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping
python3 - "$work/NuGet.Config" "$root/artifacts/packages" <<'PY'
import sys
from xml.etree import ElementTree as ET
config = ET.Element("configuration")
sources = ET.SubElement(config, "packageSources")
ET.SubElement(sources, "clear")
ET.SubElement(sources, "add", key="gateway-local", value=sys.argv[2])
ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
mapping = ET.SubElement(config, "packageSourceMapping")
ET.SubElement(ET.SubElement(mapping, "packageSource", key="gateway-local"), "package", pattern="MockProxyGateway")
ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
ET.ElementTree(config).write(sys.argv[1], encoding="utf-8", xml_declaration=True)
PY
dotnet restore "$work/consumer/EmbeddedHost.csproj" --packages "$work/packages" \
  --configfile "$work/NuGet.Config"
# A successful restore is insufficient if it selected a different package.
# Compare the exact archives before building the independent application.
python3 - "$root/artifacts/packages" "$work/packages/mockproxygateway" <<'PY'
import hashlib
import sys
from pathlib import Path
packed = list(Path(sys.argv[1]).glob("*.nupkg"))
restored = list(Path(sys.argv[2]).glob("*/*.nupkg"))
assert len(packed) == len(restored) == 1, "Expected one built and one restored gateway archive"
assert hashlib.sha256(packed[0].read_bytes()).digest() == hashlib.sha256(restored[0].read_bytes()).digest(), "Consumer did not restore the exact locally built package"
print("Verified exact local gateway package SHA-256.")
PY
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
kill "$host_pid"
wait "$host_pid" || true
host_pid=""
# Only this temporary package consumer registers host-owned synthetic policies.
# No authentication scheme or request-controlled identity is introduced.
python3 - Program.cs <<'PY'
import sys
from pathlib import Path
path = Path(sys.argv[1])
source = path.read_text()
old = "builder.Services.AddMockProxyGateway();"
assert source.count(old) == 1
path.write_text(source.replace(old, """builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("smoke-allow", policy => policy.RequireAssertion(_ => true));
    options.AddPolicy("smoke-deny", policy => policy.RequireAssertion(_ => false));
});
builder.Services.AddMockProxyGateway();"""))
PY
dotnet build EmbeddedHost.csproj -c Release --no-restore
start_host MockProxyGateway__AuthorizationPolicy=smoke-deny
before="$(grep -c '"GET /ping.txt HTTP/' "$work/upstream.log" || true)"
denied="$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:5182/integration-gateway/demo/ping.txt)"
test "$denied" = 401 || test "$denied" = 403
test "$(curl -fsS http://127.0.0.1:5182/api/health)" = normal
test "$(grep -c '"GET /ping.txt HTTP/' "$work/upstream.log" || true)" = "$before"
kill "$host_pid"
wait "$host_pid" || true
host_pid=""
start_host MockProxyGateway__AuthorizationPolicy=smoke-allow
test "$(curl -fsS http://127.0.0.1:5182/integration-gateway/demo/ping.txt)" = local-upstream
test "$(curl -fsS http://127.0.0.1:5182/integration-gateway/demo/health)" = mock-health
test "$(curl -fsS http://127.0.0.1:5182/api/health)" = normal
echo "Package-only embedded mock, proxy and optional host policy smoke passed."
