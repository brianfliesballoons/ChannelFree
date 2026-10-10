#!/usr/bin/env bash
# Build Channel Free from source with the official .NET SDK container and copy it into Jellyfin's plugins folder.
# Most people should install from the plugin repository instead (see README). This is for testing your own changes.
#
#   JELLYFIN_PLUGINS=/path/to/jellyfin/config/plugins JELLYFIN_CONTAINER=jellyfin bash scripts/build-and-install.sh
#
# JELLYFIN_PLUGINS   Jellyfin's plugins folder (required)
# JELLYFIN_CONTAINER Docker container to restart afterwards (optional; otherwise restart Jellyfin yourself)
set -euo pipefail

SRC="$(cd "$(dirname "$0")/.." && pwd)"
PLUGINS="${JELLYFIN_PLUGINS:?Set JELLYFIN_PLUGINS to the Jellyfin plugins folder, e.g. /var/lib/jellyfin/plugins}"
BUILD="$(mktemp -d)"
VERSION="$(python3 -c "import json;print(json.load(open('$SRC/meta.json'))['version'])")"
DEST="$PLUGINS/Channel Free_$VERSION"

echo "==> Building $VERSION (first run downloads the .NET SDK image, ~1 GB)"
cp -r "$SRC"/. "$BUILD"/
rm -rf "$BUILD/bin" "$BUILD/obj" "$BUILD/out"
docker run --rm -v "$BUILD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet build -c Release -o /src/out

echo "==> Installing to $DEST"
sudo rm -rf "$PLUGINS"/"Channel Free_"* "$PLUGINS"/ChannelFree_* "$PLUGINS"/CablePackage_* "$PLUGINS"/QuasiTV_*
sudo mkdir -p "$DEST"
sudo cp "$BUILD/out/Jellyfin.Plugin.QuasiTV.dll" "$SRC/meta.json" "$SRC/logo.png" "$DEST/"
rm -rf "$BUILD"

if [ -n "${JELLYFIN_CONTAINER:-}" ]; then
  echo "==> Restarting $JELLYFIN_CONTAINER"
  docker restart "$JELLYFIN_CONTAINER" >/dev/null
else
  echo "==> Done. Restart Jellyfin to load it."
fi
