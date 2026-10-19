#!/usr/bin/env bash
# Kullanım: ./build/publish.sh [rid ...]   (varsayılan: tüm platformlar)
# Çıktı: dist/<rid>/ içinde 'wa' (terminal) ve 'WebAutomater' (arayüz) birlikte.
set -euo pipefail
cd "$(dirname "$0")/.."
RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(win-x64 linux-x64 osx-arm64 osx-x64)
for rid in "${RIDS[@]}"; do
  out="dist/$rid"
  echo "==> $rid"
  for proj in src/WebAutomater.Cli src/WebAutomater.Gui; do
    dotnet publish "$proj" -c Release -r "$rid" --self-contained true -p:PublishSingleFile=false -o "$out" --nologo -v quiet
  done
done
echo "Bitti: dist/"
