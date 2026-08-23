#!/usr/bin/env bash
# Generates a throwaway CA and two node certificates for the TLS interop test.
#
# Certificates are generated rather than committed: a repository is no place for private keys, even
# test ones, and generating them keeps the SANs correct for whatever host this runs on.
#
#   test/gen_certs.sh [output-dir]
set -euo pipefail

OUT=${1:-"$(cd "$(dirname "$0")" && pwd)/certs"}
HOST=$(hostname -s)

mkdir -p "$OUT"
cd "$OUT"

# Node certificates carry the short host name, localhost and the loopback address, so a peer that
# does check the name is satisfied however it reached us.
cat > san.cnf <<CNF
[req]
distinguished_name = dn
[dn]
[ext]
basicConstraints = CA:FALSE
keyUsage = digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth, clientAuth
subjectAltName = DNS:${HOST}, DNS:localhost, IP:127.0.0.1
CNF

if [[ ! -f ca.crt ]]; then
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -keyout ca.key -out ca.crt -subj "/CN=BeamSharp Test CA" 2>/dev/null
fi

issue() {
  local name=$1
  [[ -f "$name.crt" ]] && return 0
  openssl req -newkey rsa:2048 -nodes -keyout "$name.key" -out "$name.csr" \
    -subj "/CN=$name.$HOST" 2>/dev/null
  openssl x509 -req -in "$name.csr" -CA ca.crt -CAkey ca.key -CAcreateserial \
    -out "$name.crt" -days 3650 -extfile san.cnf -extensions ext 2>/dev/null
  rm -f "$name.csr"
}

issue elixir
issue csharp

# The option file an Erlang node reads for -proto_dist inet_tls.
cat > ssl_dist.conf <<CONF
[{server, [{certfile, "$OUT/elixir.crt"},
           {keyfile,  "$OUT/elixir.key"},
           {cacertfile, "$OUT/ca.crt"},
           {verify, verify_peer},
           {fail_if_no_peer_cert, true}]},
 {client, [{certfile, "$OUT/elixir.crt"},
           {keyfile,  "$OUT/elixir.key"},
           {cacertfile, "$OUT/ca.crt"},
           {verify, verify_peer}]}].
CONF

chmod 600 ./*.key
echo "certificates in $OUT (host $HOST)"
