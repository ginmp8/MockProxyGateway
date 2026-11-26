#!/usr/bin/env bash
# Run the original pinned rule example against a synthetic loopback upstream.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
host_pid=""
upstream_pid=""
cleanup() {
  if [ -n "$host_pid" ]; then kill "$host_pid" 2>/dev/null || true; fi
  if [ -n "$upstream_pid" ]; then kill "$upstream_pid" 2>/dev/null || true; fi
  rm -rf "$work"
}
trap cleanup EXIT
trap 'exit 130' INT TERM
python3 - "$root/examples/mock-proxy-gateway.json" "$work/rules.json" <<'PY'
import json
import sys
from pathlib import Path
data = json.loads(Path(sys.argv[1]).read_text())
for name, destination in data["destinations"].items():
    destination["baseUrl"] = f"http://127.0.0.1:5181/{name}/"
Path(sys.argv[2]).write_text(json.dumps(data))
PY
cat > "$work/upstream.py" <<'PY'
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.respond()
    def do_POST(self):
        self.respond()
    def respond(self):
        length = int(self.headers.get("Content-Length", "0"))
        self.rfile.read(length)
        payload = json.dumps({"upstream": "synthetic", "path": self.path}).encode()
        self.send_response(201)
        self.send_header("Content-Type", "application/json")
        self.send_header("X-Upstream", "synthetic")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

ThreadingHTTPServer(("127.0.0.1", 5181), Handler).serve_forever()
PY
python3 "$work/upstream.py" >"$work/upstream.log" 2>&1 &
upstream_pid=$!
cd "$root/src/MockProxyGateway.Host"
ASPNETCORE_ENVIRONMENT=Development \
MockProxyGateway__Enabled=true \
MockProxyGateway__ConfigurationFile="$work/rules.json" \
MockProxyGateway__AllowLoopbackForTests=true \
MockProxyGateway__AllowedDestinationAuthorities__0=127.0.0.1:5181 \
dotnet bin/Release/net10.0/MockProxyGateway.Host.dll --urls http://127.0.0.1:5182 >"$work/host.log" 2>&1 &
host_pid=$!
ready=false
for i in $(seq 1 75); do
  if curl -fsS http://127.0.0.1:5182/health >/dev/null 2>&1 &&
     curl -fsS http://127.0.0.1:5181/ready >/dev/null 2>&1; then ready=true; break; fi
  if ! kill -0 "$host_pid" 2>/dev/null || ! kill -0 "$upstream_pid" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$ready" != true ]; then cat "$work/host.log" "$work/upstream.log"; exit 1; fi
if [ "$#" -gt 0 ] && [ "$1" = --verify ]; then
  url=http://127.0.0.1:5182/mock-proxy/compliance/analysis
  body='{"customer":{"riskScore":10}}'
  calls() {
    grep -Ec '"(GET|POST) /' "$work/upstream.log" || true
  }
  check() {
    expected="$1"; delta="$2"; fragment="$3"; shift 3
    before="$(calls)"
    actual="$(curl -sS -D "$work/response.headers" -o "$work/response.json" -w '%{http_code}' -H 'Content-Type: application/json' "$@")"
    if [ "$actual" != "$expected" ]; then cat "$work/response.json"; echo "Expected $expected, got $actual"; return 1; fi
    if [ -n "$fragment" ]; then grep -Fq -- "$fragment" "$work/response.json"; fi
    test "$(calls)" -eq "$((before + delta))"
  }
  check 200 0 Approved -H 'X-Test-Scenario: success' -d "$body" "$url"
  check 422 0 Rejected -d '{"customer":{"riskScore":90}}' "$url"
  check 503 0 'Service unavailable' -d "$body" "$url?simulate=unavailable"
  check 429 0 'Too many requests' -H 'X-Test-Scenario: rate-limited' -d "$body" "$url"
  grep -qi '^Retry-After: 30' "$work/response.headers"
  check 401 0 Unauthorized -H 'X-Test-Scenario: unauthorized' -d "$body" "$url"
  check 202 0 Pending -d '{"customer":{"document":"TEST-1001"}}' "$url"
  check 201 1 /compliance-real/analysis -d "$body" "$url"
  grep -qi '^X-Upstream: synthetic' "$work/response.headers"
  check 201 1 /compliance-v2/v2/analysis -H 'X-Test-Scenario: alternate' -d "$body" "$url"
  check 201 1 /compliance-real/analysis -H 'X-Test-Scenario: real' -d "$body" "$url?simulate=unavailable"
  check 200 0 composite -H 'X-Test-Session: session-a' -d "$body" "$url?simulate=success"
  check 200 0 composite -H 'X-Test-Session: session-a' -d '{"customer":{"type":"Organization"}}' "$url"
  check 201 1 /compliance-real/analysis -H 'X-Test-Session: session-a' -H 'X-Disable-Scenario: true' -d "$body" "$url?simulate=success"
  check 200 0 array -d '{"products":[{"type":"Other"},{"type":"TestProduct"}]}' "$url"
  check 200 0 cookie -H 'Cookie: test-session=session-b' -d "$body" "$url"
  check 503 0 'Service unavailable' -H 'X-Test-Scenario: success' -d "$body" "$url?simulate=unavailable"
  check 201 1 /compliance-real/analysis -H 'X-Test-Scenario: disabled' -d "$body" "$url"
  check 201 1 /compliance-real/analysis -H 'X-Test-Scenario: expired' -d "$body" "$url"
  check 200 0 Pending "$url/analysis-test-1001"
  check 200 0 Healthy http://127.0.0.1:5182/mock-proxy/compliance/health
  check 201 1 /compliance-v2/legacy/a/b http://127.0.0.1:5182/mock-proxy/compliance/legacy/a/b
  check 201 1 /compliance-real/other http://127.0.0.1:5182/mock-proxy/compliance/other
  check 201 1 /documents-real/anything http://127.0.0.1:5182/mock-proxy/documents/anything
  check 404 0 '' http://127.0.0.1:5182/mock-proxy/unknown/anything
  check 200 0 Healthy http://127.0.0.1:5182/health
  before="$(calls)"
  timeout_status=0
  curl -sS --max-time 1 -o "$work/timeout-response" -H 'Content-Type: application/json' \
    -H 'X-Test-Scenario: timeout' -d "$body" "$url" || timeout_status=$?
  test "$timeout_status" -eq 28
  test "$(calls)" -eq "$before"
  echo "Pinned example curl smoke passed."
else
  echo "Gateway: http://127.0.0.1:5182/mock-proxy"
  echo "Synthetic upstream: http://127.0.0.1:5181"
  echo "Temporary editable rule document: $work/rules.json"
  echo "Press Ctrl+C to stop both processes."
  wait "$host_pid"
fi
