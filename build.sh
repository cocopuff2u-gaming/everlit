#!/usr/bin/env bash
# Build Everlit and package it for a server.
#
#   ./build.sh [--managed <path to valheim_server_Data/Managed>] [--deploy <BepInEx/plugins dir>]
#
# Needs the .NET SDK (8+) and the Valheim dedicated server assemblies.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

MANAGED="" DEPLOY="" BEPINEX="${BEPINEX_RELEASE:-5.4.23.2}"
while [ $# -gt 0 ]; do
  case "$1" in
    --managed) MANAGED="$2"; shift 2 ;;
    --deploy) DEPLOY="$2"; shift 2 ;;
    *) echo "unknown option $1"; exit 1 ;;
  esac
done

export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# --- game assemblies
for c in "$MANAGED" /opt/steam/valheim/valheim_server_Data/Managed \
         "$HOME/.steam/steam/steamapps/common/Valheim dedicated server/valheim_server_Data/Managed" \
         "$HOME/.local/share/Steam/steamapps/common/Valheim dedicated server/valheim_server_Data/Managed"; do
  if [ -n "$c" ] && [ -f "$c/assembly_valheim.dll" ]; then MANAGED="$c"; break; fi
done
[ -n "$MANAGED" ] || { echo "Valheim assemblies not found; pass --managed <.../valheim_server_Data/Managed>"; exit 1; }
echo "game assemblies: $MANAGED"
mkdir -p libs/valheim
for f in assembly_valheim.dll assembly_utils.dll UnityEngine.dll UnityEngine.CoreModule.dll; do
  cp -f "$MANAGED/$f" libs/valheim/
done

# --- BepInEx
if [ ! -f libs/BepInEx/core/BepInEx.dll ]; then
  tmp=$(mktemp -d)
  curl -sSL -o "$tmp/bepinex.zip" "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX/BepInEx_win_x64_$BEPINEX.zip"
  (cd "$tmp" && unzip -q bepinex.zip 'BepInEx/*')
  mkdir -p libs && cp -r "$tmp/BepInEx" libs/BepInEx
fi

# --- build
dotnet build src/Everlit.csproj -c Release -v minimal

# --- package
V=$(python3 -c "import json;print(json.load(open('package/manifest.json'))['version_number'])")
rm -rf dist/pkg "dist/Everlit-$V.zip"
PKG=dist/pkg/plugins/Everlit
mkdir -p "$PKG"
cp src/bin/Release/Everlit.dll "$PKG/"
cp package/manifest.json README.md CHANGELOG.md LICENSE dist/pkg/
[ -f package/icon.png ] && cp package/icon.png dist/pkg/
(cd dist/pkg && zip -qr "../Everlit-$V.zip" . -x '.*')
echo "packaged dist/Everlit-$V.zip"

if [ -n "$DEPLOY" ]; then
  mkdir -p "$DEPLOY/Everlit"
  cp -r "$PKG/." "$DEPLOY/Everlit/"
  echo "deployed to $DEPLOY/Everlit"
fi
