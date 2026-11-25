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
  check() {
    expected="$1"; shift
    actual="$(curl -sS -o "$work/response.json" -w '%{http_code}' -H 'Content-Type: application/json' "$@")"
    if [ "$actual" != "$expected" ]; then cat "$work/response.json"; echo "Expected $expected, got $actual"; return 1; fi
  }
  check 200 -H 'X-Test-Scenario: success' -d "$body" "$url"
  grep -q Approved "$work/response.json"
  check 422 -d '{"customer":{"riskScore":90}}' "$url"
  grep -q Rejected "$work/response.json"
  check 503 -d "$body" "$url?simulate=unavailable"
  check 201 -d "$body" "$url"
  grep -q /compliance-real/analysis "$work/response.json"
  check 201 -H 'X-Test-Scenario: alternate' -d "$body" "$url"
  grep -q /compliance-v2/v2/analysis "$work/response.json"
  check 201 -H 'X-Test-Scenario: real' -d "$body" "$url?simulate=unavailable"
  echo "Pinned example curl smoke passed."
else
  echo "Gateway: http://127.0.0.1:5182/mock-proxy"
  echo "Synthetic upstream: http://127.0.0.1:5181"
  echo "Temporary editable rule document: $work/rules.json"
  echo "Press Ctrl+C to stop both processes."
  wait "$host_pid"
fi
