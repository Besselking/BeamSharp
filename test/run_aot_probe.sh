#!/usr/bin/env bash
# Publishes the serialization probe with NativeAOT and runs it.
#
# This is the evidence that the generated path is genuinely AOT-safe: the reflection fallback would
# fail here with "missing native code or metadata", so a green run cannot be faked by the reflection
# path quietly taking over. The publish is also required to be warning-free.
#
#   test/run_aot_probe.sh [runtime-identifier]
set -euo pipefail

cd "$(dirname "$0")/.."

PROJECT=test/BeamSharp.Aot.Probe/BeamSharp.Aot.Probe.csproj
OUT=$(mktemp -d)
LOG=$(mktemp)
trap 'rm -rf "$OUT" "$LOG"' EXIT

RID_ARG=()
[[ $# -gt 0 ]] && RID_ARG=(-r "$1")

echo "publishing with PublishAot=true..."
dotnet publish "$PROJECT" -c Release "${RID_ARG[@]}" -o "$OUT" --nologo > "$LOG" 2>&1 || {
  cat "$LOG"
  exit 1
}

WARNINGS=$(grep -c 'IL[0-9]\{4\}' "$LOG" || true)
if [[ "$WARNINGS" -ne 0 ]]; then
  echo "FAIL: $WARNINGS trim/AOT warnings during publish"
  grep 'IL[0-9]\{4\}' "$LOG" | cut -c1-200
  exit 1
fi
echo "publish clean: 0 trim/AOT warnings"

echo
"$OUT/BeamSharp.Aot.Probe"
