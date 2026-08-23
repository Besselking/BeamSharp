#!/usr/bin/env bash
# The same interop suite, over an encrypted transport.
#
# Starts an Elixir node with -proto_dist inet_tls and a C# node with TLS enabled, both using
# certificates from a throwaway CA, and runs the inbound checks across it. Proves the transport is
# genuinely interoperable rather than merely encrypted between two of our own processes.
#
#   test/run_tls_integration.sh
set -euo pipefail

cd "$(dirname "$0")/.."

COOKIE=${COOKIE:-testcookie}
HOST=$(hostname -s)
CS_NODE="csharp@${HOST}"
CERTS="$(pwd)/test/certs"

command -v elixir >/dev/null || { echo "elixir is not on PATH"; exit 1; }
command -v openssl >/dev/null || { echo "openssl is not on PATH"; exit 1; }
epmd -daemon 2>/dev/null || true

test/gen_certs.sh "$CERTS" >/dev/null

cleanup() { [[ -n "${CS_PID:-}" ]] && kill "$CS_PID" 2>/dev/null || true; }
trap cleanup EXIT

dotnet build --nologo -v q

dotnet run --no-build --project samples/BeamSharp.Server -- \
  "$CS_NODE" "$COOKIE" --tls "$CERTS" >/tmp/beamsharp-tls-node.log 2>&1 &
CS_PID=$!

for _ in $(seq 1 40); do
  grep -q "listening on port" /tmp/beamsharp-tls-node.log 2>/dev/null && break
  sleep 0.5
done

grep -q "listening on port" /tmp/beamsharp-tls-node.log || {
  echo "the C# node did not start:"; cat /tmp/beamsharp-tls-node.log; exit 1
}

echo "=== inbound over TLS: Elixir -> C# ==="
status=0
elixir --sname tlstester --cookie "$COOKIE" \
  --erl "-proto_dist inet_tls -ssl_dist_optfile $CERTS/ssl_dist.conf" \
  -r test/elixir_structs.exs test/elixir_client.exs || status=1

echo
echo "=== a plaintext peer must not get in ==="
# Encryption that a peer can simply decline is not encryption. A node speaking plain distribution
# should fail to connect, not fall back.
if elixir --sname plaintester --cookie "$COOKIE" -e "
    case Node.connect(:\"$CS_NODE\") do
      true -> IO.puts(\"FAIL: a plaintext node connected to a TLS node\"); System.halt(1)
      _    -> IO.puts(\"PASS: plaintext connection refused\"); System.halt(0)
    end" 2>/dev/null; then
  :
else
  echo "FAIL: plaintext peer was not refused cleanly"
  status=1
fi

echo
echo "=== transport actually negotiated ==="
grep -E "TLS established" /tmp/beamsharp-tls-node.log | head -2 || {
  echo "FAIL: no TLS handshake was logged, so the connection was not encrypted"
  status=1
}

exit $status
