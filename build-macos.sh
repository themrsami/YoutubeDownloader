#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
RID="${1:-}"
DOTNET_BIN="${DOTNET_BIN:-dotnet}"
EXECUTABLE_NAME="PremiumYoutubeDownloader"
APP_NAME="Premium YouTube Downloader"

if [[ -z "$RID" ]]; then
  case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *)
      echo "Unsupported macOS architecture: $(uname -m)" >&2
      exit 1
      ;;
  esac
fi

case "$RID" in
  osx-arm64|osx-x64) ;;
  *)
    echo "RID must be osx-arm64 or osx-x64." >&2
    exit 1
    ;;
esac

VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PROJECT_DIR/version.json" | head -n 1)"
if [[ -z "$VERSION" ]]; then
  echo "Could not read the version from version.json." >&2
  exit 1
fi

APP_DIR="$PROJECT_DIR/artifacts/macos/$RID/$APP_NAME.app"
MACOS_DIR="$APP_DIR/Contents/MacOS"
RESOURCES_DIR="$APP_DIR/Contents/Resources"
ZIP_PATH="$PROJECT_DIR/artifacts/macos/PremiumYoutubeDownloader-$VERSION-$RID.zip"

rm -rf "$APP_DIR" "$ZIP_PATH"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

"$DOTNET_BIN" publish "$PROJECT_DIR/PremiumYoutubeDownloader.csproj" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:PublishReadyToRun=true \
  -o "$MACOS_DIR"

cp "$PROJECT_DIR/Platforms/macOS/Info.plist" "$APP_DIR/Contents/Info.plist"
cp "$PROJECT_DIR/Platforms/macOS/AppIcon.icns" "$RESOURCES_DIR/AppIcon.icns"

if command -v plutil >/dev/null 2>&1; then
  plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP_DIR/Contents/Info.plist"
  plutil -replace CFBundleVersion -string "$VERSION" "$APP_DIR/Contents/Info.plist"
fi

chmod +x "$MACOS_DIR/$EXECUTABLE_NAME"
xattr -dr com.apple.quarantine "$APP_DIR" 2>/dev/null || true

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$APP_DIR" >/dev/null
fi

# A zip made with ditto keeps the bundle's symlinks, permissions and signature intact,
# which a plain "zip -r" does not.
if command -v ditto >/dev/null 2>&1; then
  ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$ZIP_PATH"
  echo "$ZIP_PATH"
fi

echo "$APP_DIR"
