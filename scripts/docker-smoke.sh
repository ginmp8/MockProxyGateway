#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
work="$(mktemp -d)"
upstream_pid=""
container_id=""
cleanup() {
  if [ -n "$container_id" ]; then docker rm -f "$container_id" >/dev/null 2>&1 || true; fi
  if [ -n "$upstream_pid" ]; then kill "$upstream_pid" 2>/dev/null || true; fi
  rm -rf "$work"
}
trap cleanup EXIT
docker build -f src/MockProxyGateway.Host/Dockerfile -t mock-proxy-gateway:ci .
printf 'local-upstream' > "$work/ping.txt"
python3 -m http.server 5181 --bind 127.0.0.1 --directory "$work" >"$work/upstream.log" 2>&1 &
upstream_pid=$!
container_id="$(docker run -d --network host \
  -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_URLS=http://127.0.0.1:5182 \
  -e MockProxyGateway__Enabled=true -e MockProxyGateway__AllowLoopbackForTests=true \
  -e MockProxyGateway__AllowedDestinationAuthorities__0=127.0.0.1:5181 \
  -e MockProxyGateway__ConfigurationFile=/app/smoke-rules.json \
  -v "$root/samples/embedded-host/mock-proxy-gateway.json:/app/smoke-rules.json:ro" \
  mock-proxy-gateway:ci)"
for i in $(seq 1 50); do
  if curl -fsS http://127.0.0.1:5182/health >/dev/null 2>&1; then break; fi
  sleep 0.2
done
docker logs "$container_id"
test "$(curl -fsS http://127.0.0.1:5182/mock-proxy/demo/health)" = mock-health
test "$(curl -fsS http://127.0.0.1:5182/mock-proxy/demo/ping.txt)" = local-upstream
echo "Standalone Docker host mock and proxy smoke passed."
