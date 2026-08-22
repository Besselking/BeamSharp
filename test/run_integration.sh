#!/usr/bin/env bash
# End-to-end check against a real Erlang runtime: starts the C# node and an Elixir node,
# then runs the test suites in both directions.
#
#   test/run_integration.sh
set -euo pipefail

cd "$(dirname "$0")/.."

COOKIE=${COOKIE:-testcookie}
HOST=$(hostname -s)
CS_NODE="csharp@${HOST}"
EX_NODE="exserver@${HOST}"

command -v elixir >/dev/null || { echo "elixir is not on PATH"; exit 1; }
epmd -daemon 2>/dev/null || true

cleanup() {
  [[ -n "${CS_PID:-}" ]] && kill "$CS_PID" 2>/dev/null || true
  [[ -n "${EX_PID:-}" ]] && kill "$EX_PID" 2>/dev/null || true
}
trap cleanup EXIT

dotnet build --nologo -v q

dotnet run --no-build --project samples/BeamSharp.Server -- "$CS_NODE" "$COOKIE" >/tmp/beamsharp-csnode.log 2>&1 &
CS_PID=$!

elixir --sname exserver --cookie "$COOKIE" test/elixir_server.exs >/tmp/beamsharp-exserver.log 2>&1 &
EX_PID=$!

# Wait for both to announce themselves.
for _ in $(seq 1 40); do
  grep -q "listening on port" /tmp/beamsharp-csnode.log 2>/dev/null &&
    grep -q "^ready:" /tmp/beamsharp-exserver.log 2>/dev/null && break
  sleep 0.5
done

status=0

echo "=== inbound: Elixir -> C# ==="
elixir --sname tester --cookie "$COOKIE" test/elixir_client.exs || status=1

echo
echo "=== outbound: C# -> Elixir ==="
dotnet run --no-build --project samples/BeamSharp.Client -- "$EX_NODE" "$COOKIE" || status=1

exit $status
