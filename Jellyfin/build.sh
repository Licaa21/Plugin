#!/usr/bin/env bash
# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Works on Linux, macOS, and Windows (Git Bash/WSL)
# Usage: CHANGELOG="What changed." [PUBLISH=1] ./build.sh [version] [target-abi] [source-url]
# The version is read from the csproj (MoonfinVersion.MoonfinForkRevision) unless given.
# PUBLISH=1 also creates the GitHub release fork-v<version> on Licaa21/Plugin with the zip, at the
# commit being built, which has to be on origin/main.
# Needs jq and zip, and curl and unzip for the web app. build.ps1 is the same script for a PC without them.

set -e

# Get repo root (where this script lives)
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
csproj_value() { sed -n "s:.*<$1>\([^<]*\)</$1>.*:\1:p" "$ROOT_DIR/backend/Moonfin.Server.csproj" | head -1; }

VERSION="${1:-$(csproj_value MoonfinVersion).$(csproj_value MoonfinForkRevision)}"
TARGET_ABI="${2:-10.10.0}"
SOURCE_URL="${3:-}"
CHANGELOG="${CHANGELOG:-}"
BUILD_TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')
RELEASE_TAG="fork-v${VERSION}"

if ! [[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then
    echo "Error: '$VERSION' is not a four-number version. Check MoonfinVersion and MoonfinForkRevision in Moonfin.Server.csproj." >&2
    exit 1
fi
if [ "${PUBLISH:-0}" = "1" ] && [ "${SKIP_MANIFEST_UPDATE:-0}" = "1" ]; then
    echo "Error: PUBLISH=1 needs the manifest entry, drop SKIP_MANIFEST_UPDATE." >&2
    exit 1
fi
if [ "${SKIP_MANIFEST_UPDATE:-0}" != "1" ] && [ -z "$CHANGELOG" ]; then
    echo "Error: give the release a CHANGELOG, or build without a manifest entry (SKIP_MANIFEST_UPDATE=1)." >&2
    exit 1
fi
if [ "${PUBLISH:-0}" = "1" ]; then
    if [ -n "$SOURCE_URL" ]; then
        echo "Error: PUBLISH=1: a source-url would make the manifest point somewhere the release is not." >&2
        exit 1
    fi
    if [ -n "$(git -C "$ROOT_DIR" status --porcelain)" ]; then
        echo "Error: PUBLISH=1: commit or remove the changes first, the zip is built from the working tree and the release is cut at the commit." >&2
        exit 1
    fi
    git -C "$ROOT_DIR" fetch --quiet origin main
    if ! git -C "$ROOT_DIR" merge-base --is-ancestor HEAD origin/main; then
        echo "Error: PUBLISH=1: this commit is not on origin/main. Releases are cut from main, merge and push it there first." >&2
        exit 1
    fi
    if ! command -v gh &> /dev/null; then
        echo "Error: PUBLISH=1: gh is not installed." >&2
        exit 1
    fi
    if ! gh auth status &> /dev/null; then
        echo "Error: PUBLISH=1: gh is not signed in (gh auth login)." >&2
        exit 1
    fi
    if gh release view "$RELEASE_TAG" --repo Licaa21/Plugin &> /dev/null; then
        echo "Error: PUBLISH=1: the release $RELEASE_TAG already exists. Releases are kept and never replaced, so raise MoonfinForkRevision." >&2
        exit 1
    fi
fi

BACKEND_DIR="$ROOT_DIR/backend"
FRONTEND_DIR="$ROOT_DIR/frontend"
VERIFY_PROJECT="$ROOT_DIR/tools/verify-plugin/VerifyPlugin.csproj"
VERIFY_DIR="$ROOT_DIR/tools/verify-plugin"

echo "Building Moonfin v${VERSION} for Jellyfin ${TARGET_ABI}..."
echo "Build Time: ${BUILD_TIMESTAMP}"

# Resolve dotnet binary (PATH first, then local user install)
DOTNET_BIN="dotnet"
if ! command -v "$DOTNET_BIN" &> /dev/null; then
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        DOTNET_BIN="$HOME/.dotnet/dotnet"
    else
        echo ""
        echo "Error: dotnet command not found."
        echo "Install .NET 8 SDK, or install locally with:"
        echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh"
        echo "  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir \"\$HOME/.dotnet\""
        exit 127
    fi
fi

# The Flutter web app (the Moonfin button in Jellyfin web, served at /Moonfin/Web/) is not in git. Upstream builds it
# from Moonfin-Core and adds it to its zip by hand, so this build takes it from upstream's published release: a
# pinned version, checked against its checksum (SHA-256; the file's MD5 is also upstream's own, 6B3E789A1BF33870CC938B41C0FCF324),
# unchanged. Its source is https://github.com/Moonfin-Client/Moonfin-Core. Needs curl and unzip.
WEB_BUNDLE_URL="https://github.com/Moonfin-Client/Plugin/releases/download/2.5.0/Moonfin.Server-2.5.0.0.zip"
WEB_BUNDLE_SHA256="37D3CBB9082331510D7CD21EAA0E879CF1E9AFD0BF658127A4F918141CBD0797"
sha256_upper() {
    if command -v sha256sum &> /dev/null; then sha256sum < "$1" | awk '{print toupper($1)}'
    else shasum -a 256 < "$1" | awk '{print toupper($1)}'; fi
}
web_app_ready() {
    [ -f "$FRONTEND_DIR/index.html" ] && [ -f "$FRONTEND_DIR/main.dart.js" ] && [ -f "$FRONTEND_DIR/version.json" ] \
        && [ -f "$FRONTEND_DIR/assets/AssetManifest.bin" ] && [ -f "$FRONTEND_DIR/canvaskit/canvaskit.wasm" ]
}
web_app_fail() {
    # A build that is not a release (SKIP_MANIFEST_UPDATE=1) can go on without the web app, a release cannot.
    if [ "${SKIP_MANIFEST_UPDATE:-0}" = "1" ]; then
        echo "Warning: $1"
    else
        echo "Error: $1" >&2
        exit 1
    fi
}
if ! web_app_ready; then
    echo ""
    echo "The web app is missing or incomplete in frontend/, taking it from upstream's 2.5.0.0 release (about 66 MB)..."
    if ! command -v curl &> /dev/null || ! command -v unzip &> /dev/null; then
        web_app_fail "curl and unzip are needed to fetch the web app."
    else
        # A private folder, so nobody else can swap the file between the checksum and the unpacking.
        WEB_CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/moonfin-build"
        mkdir -p "$WEB_CACHE" && chmod 700 "$WEB_CACHE"
        WEB_ZIP="$WEB_CACHE/moonfin-upstream-2.5.0.0.zip"
        WEB_ZIP_OK=0
        if [ -f "$WEB_ZIP" ] && [ "$(sha256_upper "$WEB_ZIP")" = "$WEB_BUNDLE_SHA256" ]; then
            WEB_ZIP_OK=1
        elif curl -fL "$WEB_BUNDLE_URL" -o "$WEB_ZIP"; then
            if [ "$(sha256_upper "$WEB_ZIP")" = "$WEB_BUNDLE_SHA256" ]; then
                WEB_ZIP_OK=1
            else
                rm -f "$WEB_ZIP"
                web_app_fail "the downloaded upstream zip does not match its known checksum, not using it."
            fi
        else
            rm -f "$WEB_ZIP"
            web_app_fail "could not download the upstream zip."
        fi
        if [ "$WEB_ZIP_OK" = "1" ]; then
            # Unpacked into a staging folder first, so a failure leaves frontend/ as it was. Everything but the plugin's own
            # three files is taken, because Info-ZIP's 'frontend/*' pattern misses what is inside the subfolders.
            WEB_STAGING="$(mktemp -d)"
            # unzip exits 1 for warnings that still unpacked everything, so only a higher status is a failure.
            if { unzip -q -o "$WEB_ZIP" -x 'Moonfin.Server.dll' 'SharpCompress.dll' 'meta.json' -d "$WEB_STAGING" || [ $? -eq 1 ]; } && [ -f "$WEB_STAGING/frontend/index.html" ]; then
                # Leftovers of an earlier or other web app would ship next to these files, so the folder starts clean
                # (theme and editor are tracked here and stay).
                find "$FRONTEND_DIR" -mindepth 1 -maxdepth 1 ! -name theme ! -name editor ! -name .gitkeep -exec rm -rf {} +
                find "$WEB_STAGING/frontend" -mindepth 1 -maxdepth 1 ! -name theme ! -name editor -exec cp -R {} "$FRONTEND_DIR/" \;
                rm -rf "$WEB_STAGING"
            else
                rm -rf "$WEB_STAGING"
                web_app_fail "could not unpack the web app."
            fi
        fi
    fi
fi
if ! web_app_ready; then
    web_app_fail "the web app (frontend/index.html, main.dart.js, version.json) is missing, so the zip would have no web app and the Moonfin button would be dead. Not building a release without it."
fi

# Optional push relay key, read from a gitignored .env at the repo root. Without
# one the build still succeeds and the plugin ships with push disabled.
RELAY_APP_KEY=""
ENV_FILE="$ROOT_DIR/../.env"
if [ -f "$ENV_FILE" ]; then
    RELAY_APP_KEY="$(grep -E '^[[:space:]]*MOONFIN_RELAY_APP_KEY[[:space:]]*=' "$ENV_FILE" \
        | tail -1 | cut -d= -f2- | tr -d '"'"'"' \r')"
fi
if [ -n "$RELAY_APP_KEY" ] && [ "${PUBLISH:-0}" = "1" ]; then
    echo "Error: PUBLISH=1: a push relay key is in .env and would be stamped into the public release. Move .env away for the release." >&2
    exit 1
fi
if [ -n "$RELAY_APP_KEY" ]; then
    echo "Push relay key found, stamping it into this build."
else
    echo "No push relay key, building with push disabled."
fi

# Build the .NET plugin from clean Release state so the package cannot reuse an
# assembly produced from an older project or checkout path.
echo ""
echo "--- Building server plugin ---"
PUBLISH_DIR="$BACKEND_DIR/bin/Release/net8.0/publish"
rm -rf "$BACKEND_DIR/bin/Release" "$BACKEND_DIR/obj/Release"
rm -rf "$VERIFY_DIR/bin/Release" "$VERIFY_DIR/obj/Release"
"$DOTNET_BIN" publish "$BACKEND_DIR/Moonfin.Server.csproj" \
    -c Release \
    -o "$PUBLISH_DIR" \
    -p:AssemblyVersion="$VERSION" \
    -p:FileVersion="$VERSION" \
    -p:Version="$VERSION" \
    -p:MoonfinRelayAppKey="$RELAY_APP_KEY"

echo ""
echo "--- Verifying server plugin ---"
"$DOTNET_BIN" run --project "$VERIFY_PROJECT" -c Release -- "$PUBLISH_DIR/Moonfin.Server.dll" "$VERSION"

# Create release directory
RELEASE_DIR="$ROOT_DIR/release"
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# Copy the plugin DLL plus its bundled dependencies
cp "$PUBLISH_DIR/Moonfin.Server.dll" "$RELEASE_DIR/"
cp "$PUBLISH_DIR/SharpCompress.dll" "$RELEASE_DIR/"

# Bundle Flutter web files next to plugin DLL for local/sideload installs
if web_app_ready; then
    mkdir -p "$RELEASE_DIR/frontend"
    cp -R "$FRONTEND_DIR/." "$RELEASE_DIR/frontend/"
    rm -rf "$RELEASE_DIR/frontend/node_modules"
    rm -f "$RELEASE_DIR/frontend/package.json" "$RELEASE_DIR/frontend/package-lock.json"
fi

# Generate meta.json for plugin discovery
PLUGIN_GUID="8c5d0e91-4f2a-4b6d-9e3f-1a7c8d9e0f2b"
TIMESTAMP_ISO=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
cat > "$RELEASE_DIR/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "Moonbase is the Moonfin server plugin for Jellyfin, powering settings sync, the Moonfin Web app at /Moonfin/Web/, a built-in theme editor with custom theme APIs, media bar and ratings integrations, and Seerr proxy/SSO with admin defaults and broadcasts across Moonfin clients.",
  "guid": "${PLUGIN_GUID}",
  "name": "Moonbase",
  "overview": "Moonfin server plugin for Jellyfin with web app hosting, settings sync, theming, and Seerr integrations",
  "owner": "Licaa21",
  "targetAbi": "${TARGET_ABI}.0",
  "timestamp": "${TIMESTAMP_ISO}",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": ["Moonfin.Server.dll", "SharpCompress.dll"]
}
EOF

# Create the ZIP file
ZIP_NAME="Moonfin.Server-${VERSION}.zip"
rm -f "$ROOT_DIR/$ZIP_NAME"
cd "$RELEASE_DIR"
zip -r "$ROOT_DIR/$ZIP_NAME" .
cd "$ROOT_DIR"

# What was just written is checked, since a wrong zip only shows after it is installed.
if unzip -Z1 "$ROOT_DIR/$ZIP_NAME" | grep -q '\\'; then
    echo "Error: the zip has backslashes in its entry names." >&2
    exit 1
fi
if [ "${SKIP_MANIFEST_UPDATE:-0}" != "1" ]; then
    for required in index.html main.dart.js version.json assets/AssetManifest.bin canvaskit/canvaskit.wasm; do
        if ! unzip -Z1 "$ROOT_DIR/$ZIP_NAME" | grep -qx "frontend/$required"; then
            echo "Error: the zip has no frontend/$required, so its web app is incomplete." >&2
            exit 1
        fi
    done
fi

# Calculate MD5 checksum (cross-platform)
if command -v md5sum &> /dev/null; then
    CHECKSUM=$(md5sum "$ZIP_NAME" | awk '{print toupper($1)}')
elif command -v md5 &> /dev/null; then
    CHECKSUM=$(md5 -q "$ZIP_NAME" | tr '[:lower:]' '[:upper:]')
elif command -v certutil &> /dev/null; then
    # Windows fallback (Git Bash)
    CHECKSUM=$(certutil -hashfile "$ZIP_NAME" MD5 2>/dev/null | sed -n '2p' | tr -d ' ' | tr '[:lower:]' '[:upper:]')
else
    CHECKSUM="UNABLE_TO_CALCULATE"
fi

# Update manifest.json
MANIFEST_FILE="$ROOT_DIR/manifest.json"
MANIFEST_UPDATED="no"
if [ "${SKIP_MANIFEST_UPDATE:-0}" != "1" ] && [ -f "$MANIFEST_FILE" ]; then
    # Create timestamp in ISO 8601 format
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S")

    if command -v jq &> /dev/null; then
        # Prepend rather than assign into versions[0], so cutting a release can't overwrite
        # the previous one's entry.
        EXISTING=$(jq -r --arg ver "$VERSION" \
            '[.[0].versions[] | select(.version == $ver)] | length' "$MANIFEST_FILE")
        if [ "$EXISTING" != "0" ]; then
            echo "Error: manifest.json already lists version $VERSION." >&2
            echo "Bump the version, or remove that entry first if you are deliberately re-cutting it." >&2
            exit 1
        fi

        jq --arg ver "$VERSION" \
           --arg abi "${TARGET_ABI}.0" \
           --arg sum "$CHECKSUM" \
           --arg time "$TIMESTAMP" \
           --arg source "$SOURCE_URL" \
           --arg zip "$ZIP_NAME" \
           --arg tag "$RELEASE_TAG" \
           --arg log "$CHANGELOG" \
           '.[0].versions |= ([{
                version: $ver,
                changelog: $log,
                targetAbi: $abi,
                sourceUrl: (if $source != "" then $source
                            else "https://github.com/Licaa21/Plugin/releases/download/" + $tag + "/" + $zip
                            end),
                checksum: $sum,
                timestamp: $time,
                dependencies: []
            }] + .)' \
           "$MANIFEST_FILE" > "${MANIFEST_FILE}.tmp" && mv "${MANIFEST_FILE}.tmp" "$MANIFEST_FILE"
        echo "Prepended version $VERSION to manifest.json"

        # The catalog URL people add points at the manifest in the repo root, not this one.
        REPO_ROOT_MANIFEST="$(cd "$ROOT_DIR/.." && pwd)/manifest.json"
        if [ -f "$REPO_ROOT_MANIFEST" ]; then
            cp "$MANIFEST_FILE" "$REPO_ROOT_MANIFEST"
            echo "Synced the repo root manifest.json"
        fi
        MANIFEST_UPDATED="yes"
    else
        echo "Error: jq not found. manifest.json was NOT updated." >&2
        echo "Install jq, or update versions[0] manually:" >&2
        echo "  version=$VERSION  targetAbi=${TARGET_ABI}.0  checksum=$CHECKSUM  timestamp=$TIMESTAMP  sourceUrl=$SOURCE_URL" >&2
        exit 1
    fi
fi

# Last, so a failure above leaves no release behind. The manifest entry is only local until it is
# committed and pushed, which is when installs can see it.
if [ "${PUBLISH:-0}" = "1" ]; then
    gh release create "$RELEASE_TAG" "$ROOT_DIR/$ZIP_NAME" --repo Licaa21/Plugin \
        --target "$(git -C "$ROOT_DIR" rev-parse HEAD)" --title "Moonbase $VERSION (fork)" --notes "$CHANGELOG" || {
        # The tree was clean when PUBLISH=1 started, so the manifests can be put back as they were.
        git -C "$ROOT_DIR" checkout -- ':/manifest.json' ':/Jellyfin/manifest.json'
        echo "Error: gh release create failed. The manifest entries were undone, nothing was published." >&2
        exit 1
    }
    echo "Published release $RELEASE_TAG. Commit and push the manifest.json files, then merge main into implement."
fi

# Cleanup
rm -rf "$RELEASE_DIR"

echo ""
echo "========================================="
echo "Build complete!"
echo "Build Time: ${BUILD_TIMESTAMP}"
echo "========================================="
echo "ZIP file: $ZIP_NAME"
echo "MD5 Checksum: $CHECKSUM"
echo "Manifest updated: $MANIFEST_UPDATED"
echo "========================================="
echo ""
echo "Done!"
