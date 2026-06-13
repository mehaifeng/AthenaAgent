#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./release.sh <version-or-tag> [options]

Examples:
  ./release.sh 1.2.3
  ./release.sh v1.2.3 --platforms osx-arm64,win-x64,linux-x64
  ./release.sh 1.2.3 --upload --generate-notes
  ./release.sh 1.2.3 --dmg --upload --generate-notes

Options:
  --platforms <list>   Comma-separated RIDs. Default: osx-arm64,win-x64,linux-x64
  --repo <owner/name>  GitHub repo slug. Default: mehaifeng/AthenaAgent
  --output-dir <dir>   Output directory. Default: artifacts/releases/<tag>
  --upload             Create and upload a GitHub Release with gh
  --draft              Create the GitHub Release as a draft
  --prerelease         Mark the GitHub Release as a prerelease
  --notes-file <file>  Use a release notes file for gh release create
  --generate-notes     Let GitHub auto-generate release notes
  --dmg                Create DMG for macOS platforms (macOS only, requires hdiutil)
  -h, --help           Show this help text
EOF
}

die() {
  echo "Error: $*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "Missing required command: $1"
}

sha256_file() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
    return
  fi

  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
    return
  fi

  if command -v openssl >/dev/null 2>&1; then
    openssl dgst -sha256 "$1" | awk '{print $NF}'
    return
  fi

  die "Could not find shasum, sha256sum, or openssl to compute SHA-256."
}

validate_rid() {
  case "$1" in
    osx-x64|osx-arm64|win-x64|win-arm64|linux-x64|linux-arm64)
      ;;
    *)
      die "Unsupported RID: $1"
      ;;
  esac
}

archive_type_for_rid() {
  case "$1" in
    win-*)
      echo "zip"
      ;;
    osx-*|linux-*)
      echo "tar.gz"
      ;;
    *)
      die "Unsupported RID: $1"
      ;;
  esac
}

entry_executable_for_rid() {
  case "$1" in
    win-*)
      echo "Athena.UI.exe"
      ;;
    osx-*|linux-*)
      echo "Athena.UI"
      ;;
    *)
      die "Unsupported RID: $1"
      ;;
  esac
}

updater_executable_for_rid() {
  case "$1" in
    win-*)
      echo "Athena.Updater.exe"
      ;;
    osx-*|linux-*)
      echo "Athena.Updater"
      ;;
    *)
      die "Unsupported RID: $1"
      ;;
  esac
}

publish_project() {
  local project="$1"
  local rid="$2"
  local output_dir="$3"

  dotnet publish "$project" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:Version="$VERSION" \
    -p:InformationalVersion="$VERSION" \
    -p:FileVersion="$VERSION" \
    -p:AssemblyVersion="$VERSION" \
    -p:PublishSingleFile=false \
    -p:UseAppHost=true \
    -o "$output_dir"
}

archive_package() {
  local rid="$1"
  local package_dir="$2"
  local archive_path="$3"
  local archive_type

  archive_type="$(archive_type_for_rid "$rid")"
  rm -f "$archive_path"

  if [ "$archive_type" = "zip" ]; then
    require_command zip
    (
      cd "$package_dir"
      zip -qr "$archive_path" .
    )
    return
  fi

  require_command tar
  (
    cd "$package_dir"
    tar -czf "$archive_path" .
  )
}

generate_icns() {
  # Generate Athena.icns from Athena.ico (or a square PNG) when a pre-built
  # .icns is not available. The result is cached in Assets/ so subsequent
  # runs skip the conversion.
  local icns_path="$REPO_ROOT/Assets/Athena.icns"
  [ -f "$icns_path" ] && return 0

  local src=""
  if [ -f "$REPO_ROOT/Assets/Athena.ico" ]; then
    src="$REPO_ROOT/Assets/Athena.ico"
  elif [ -f "$REPO_ROOT/Assets/Athena.png" ]; then
    src="$REPO_ROOT/Assets/Athena.png"
  else
    echo "Warning: No Athena.ico or Athena.png found — app will have no icon"
    return 1
  fi

  require_command sips
  require_command iconutil

  local iconset
  iconset="$(mktemp -d)/Athena.iconset"
  mkdir -p "$iconset"

  echo "Generating Athena.icns from $(basename "$src")"

  local size
  for size in 16 32 128 256 512; do
    sips -s format png -z "$size" "$size" "$src" --out "$iconset/icon_${size}x${size}.png" >/dev/null 2>&1
    local double=$((size * 2))
    sips -s format png -z "$double" "$double" "$src" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null 2>&1
  done

  iconutil -c icns "$iconset" -o "$icns_path"
  rm -rf "$(dirname "$iconset")"

  echo "Created Assets/Athena.icns"
}

create_app_bundle() {
  local package_dir="$1"
  local app_bundle="$2"
  local version="$3"

  mkdir -p "$app_bundle/Contents/MacOS"
  mkdir -p "$app_bundle/Contents/Resources"

  # Copy all publish files into the MacOS directory
  cp -R "$package_dir"/* "$app_bundle/Contents/MacOS/"

  # Generate Info.plist from csproj CFBundle properties
  cat > "$app_bundle/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleExecutable</key>
	<string>Athena.UI</string>
	<key>CFBundleIdentifier</key>
	<string>com.athena.ai</string>
	<key>CFBundleName</key>
	<string>Athena</string>
	<key>CFBundleDisplayName</key>
	<string>Athena</string>
	<key>CFBundleIconFile</key>
	<string>Athena</string>
	<key>CFBundleVersion</key>
	<string>${version}</string>
	<key>CFBundleShortVersionString</key>
	<string>${version}</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleSignature</key>
	<string>????</string>
	<key>NSHighResolutionCapable</key>
	<true/>
</dict>
</plist>
PLIST

  # Copy icon — generate from .ico on the fly if .icns is missing
  if [ -f "$REPO_ROOT/Assets/Athena.icns" ]; then
    cp "$REPO_ROOT/Assets/Athena.icns" "$app_bundle/Contents/Resources/"
  else
    generate_icns && cp "$REPO_ROOT/Assets/Athena.icns" "$app_bundle/Contents/Resources/" 2>/dev/null || true
  fi
}
create_dmg() {
  local rid="$1"
  local package_dir="$2"
  local dmg_path="$3"
  local version="$4"

  require_command hdiutil

  local dmg_staging="${package_dir}.dmg_staging"
  rm -rf "$dmg_staging"
  mkdir -p "$dmg_staging"

  # Wrap flat publish output into a proper .app bundle
  create_app_bundle "$package_dir" "$dmg_staging/Athena.app" "$version"

  # Symlink to /Applications for drag-to-install UX
  ln -s /Applications "$dmg_staging/Applications"

  rm -f "$dmg_path"
  hdiutil create -volname "Athena" \
    -srcfolder "$dmg_staging" \
    -ov -format UDZO \
    "$dmg_path"

  rm -rf "$dmg_staging"
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$SCRIPT_DIR"
DEFAULT_REPO="mehaifeng/AthenaAgent"
DEFAULT_PLATFORMS="osx-arm64,win-x64,linux-x64"

REPO="$DEFAULT_REPO"
PLATFORMS_CSV="$DEFAULT_PLATFORMS"
OUTPUT_DIR=""
UPLOAD=0
DRAFT=0
PRERELEASE=0
GENERATE_NOTES=0
NOTES_FILE=""
DMG=0
VERSION_INPUT=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    -h|--help)
      usage
      exit 0
      ;;
    --platforms)
      [ "$#" -ge 2 ] || die "--platforms requires a value"
      PLATFORMS_CSV="$2"
      shift 2
      ;;
    --repo)
      [ "$#" -ge 2 ] || die "--repo requires a value"
      REPO="$2"
      shift 2
      ;;
    --output-dir)
      [ "$#" -ge 2 ] || die "--output-dir requires a value"
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --upload)
      UPLOAD=1
      shift
      ;;
    --draft)
      DRAFT=1
      shift
      ;;
    --prerelease)
      PRERELEASE=1
      shift
      ;;
    --notes-file)
      [ "$#" -ge 2 ] || die "--notes-file requires a value"
      NOTES_FILE="$2"
      shift 2
      ;;
    --generate-notes)
      GENERATE_NOTES=1
      shift
      ;;
    --dmg)
      DMG=1
      shift
      ;;
    -*)
      die "Unknown option: $1"
      ;;
    *)
      if [ -n "$VERSION_INPUT" ]; then
        die "Unexpected extra argument: $1"
      fi
      VERSION_INPUT="$1"
      shift
      ;;
  esac
done

[ -n "$VERSION_INPUT" ] || {
  usage
  exit 1
}

require_command dotnet

RAW_TAG="$VERSION_INPUT"
VERSION="${VERSION_INPUT#v}"
[ -n "$VERSION" ] || die "Version cannot be empty"
TAG="$RAW_TAG"

if [ -z "$OUTPUT_DIR" ]; then
  OUTPUT_DIR="$REPO_ROOT/artifacts/releases/$TAG"
fi

if [ -n "$NOTES_FILE" ] && [ "$GENERATE_NOTES" -eq 1 ]; then
  die "--notes-file and --generate-notes cannot be used together"
fi

if [ -n "$NOTES_FILE" ] && [ ! -f "$NOTES_FILE" ]; then
  die "Notes file does not exist: $NOTES_FILE"
fi

if [ "$UPLOAD" -eq 1 ]; then
  require_command gh
fi

if [ "$DMG" -eq 1 ]; then
  case "$(uname -s)" in
    Darwin) ;;
    *)
      die "--dmg requires macOS (hdiutil is only available on Darwin)"
      ;;
  esac
fi

ORIGINAL_IFS="$IFS"
IFS=','
read -r -a PLATFORMS <<<"$PLATFORMS_CSV"
IFS="$ORIGINAL_IFS"

[ "${#PLATFORMS[@]}" -gt 0 ] || die "No platforms specified"

for rid in "${PLATFORMS[@]}"; do
  [ -n "$rid" ] || die "Empty RID in --platforms"
  validate_rid "$rid"
done

mkdir -p "$OUTPUT_DIR"

MANIFEST_PACKAGES=""
MANIFEST_SEPARATOR=""
ASSET_PATHS=()

echo "Releasing Athena $TAG"
echo "Repo: $REPO"
echo "Platforms: ${PLATFORMS[*]}"
echo "Output: $OUTPUT_DIR"
if [ "$DMG" -eq 1 ]; then
  echo "DMG: yes (macOS only)"
fi

for rid in "${PLATFORMS[@]}"; do
  platform_root="$OUTPUT_DIR/$rid"
  app_dir="$platform_root/app"
  updater_dir="$platform_root/updater"
  package_dir="$platform_root/package"
  archive_type="$(archive_type_for_rid "$rid")"
  archive_name="Athena-$rid.$archive_type"
  archive_path="$OUTPUT_DIR/$archive_name"
  entry_executable="$(entry_executable_for_rid "$rid")"
  updater_executable="$(updater_executable_for_rid "$rid")"

  rm -rf "$platform_root"
  mkdir -p "$app_dir" "$updater_dir" "$package_dir"

  echo
  echo "Publishing $rid"
  publish_project "$REPO_ROOT/Athena.UI.csproj" "$rid" "$app_dir"
  publish_project "$REPO_ROOT/Athena.Updater/Athena.Updater.csproj" "$rid" "$updater_dir"

  cp -R "$app_dir"/. "$package_dir"/
  mkdir -p "$package_dir/updater"
  cp -R "$updater_dir"/. "$package_dir/updater"/

  [ -f "$package_dir/$entry_executable" ] || die "Missing entry executable for $rid: $entry_executable"
  [ -f "$package_dir/updater/$updater_executable" ] || die "Missing updater executable for $rid: $updater_executable"

  case "$rid" in
    win-*)
      ;;
    *)
      chmod +x "$package_dir/$entry_executable" "$package_dir/updater/$updater_executable"
      ;;
  esac

  # --- tar.gz (always, for updater) ---
  archive_package "$rid" "$package_dir" "$archive_path"
  sha256="$(sha256_file "$archive_path")"
  asset_url="https://github.com/$REPO/releases/download/$TAG/$archive_name"

  ASSET_PATHS+=("$archive_path")
  MANIFEST_PACKAGES="${MANIFEST_PACKAGES}${MANIFEST_SEPARATOR}
    \"${rid}\": {
      \"url\": \"${asset_url}\",
      \"sha256\": \"${sha256}\",
      \"archiveType\": \"${archive_type}\",
      \"entryExecutable\": \"${entry_executable}\"
    }"
  MANIFEST_SEPARATOR=","

  echo "Created $archive_name"
  echo "SHA256  $sha256"

  # --- DMG (optional, macOS only, for first-time install) ---
  if [ "$DMG" -eq 1 ]; then
    case "$rid" in
      osx-*)
        dmg_name="Athena-$VERSION-$rid.dmg"
        dmg_path="$OUTPUT_DIR/$dmg_name"
        create_dmg "$rid" "$package_dir" "$dmg_path" "$VERSION"
        ASSET_PATHS+=("$dmg_path")
        echo "Created $dmg_name"
        ;;
      *)
        echo "Skipping DMG for $rid (not macOS)"
        ;;
    esac
  fi
done

MANIFEST_PATH="$OUTPUT_DIR/update-manifest.json"
cat >"$MANIFEST_PATH" <<EOF
{
  "version": "$VERSION",
  "releaseNotesUrl": "https://github.com/$REPO/releases/tag/$TAG",
  "preservePaths": [
    "AthenaData"
  ],
  "packages": {
$MANIFEST_PACKAGES
  }
}
EOF

echo
echo "Generated manifest: $MANIFEST_PATH"

if [ "$UPLOAD" -eq 1 ]; then
  GH_ARGS=(release create "$TAG" --repo "$REPO" --title "$TAG")

  if [ -n "$NOTES_FILE" ]; then
    GH_ARGS+=(--notes-file "$NOTES_FILE")
  elif [ "$GENERATE_NOTES" -eq 1 ]; then
    GH_ARGS+=(--generate-notes)
  else
    GH_ARGS+=(--notes "Release $TAG")
  fi

  if [ "$DRAFT" -eq 1 ]; then
    GH_ARGS+=(--draft)
  fi

  if [ "$PRERELEASE" -eq 1 ]; then
    GH_ARGS+=(--prerelease)
  fi

  GH_ARGS+=("${ASSET_PATHS[@]}" "$MANIFEST_PATH")

  echo
  echo "Creating GitHub Release $TAG"
  gh "${GH_ARGS[@]}"
else
  echo
  echo "Assets are ready for upload:"
  for asset in "${ASSET_PATHS[@]}"; do
    echo "  $asset"
  done
  echo "  $MANIFEST_PATH"
  echo
  echo "Next step:"
  echo "  Upload the archives above and update-manifest.json to GitHub Release $TAG"
fi
