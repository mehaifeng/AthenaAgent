#!/usr/bin/env bash
# Registers this Athena build with the desktop: a .desktop entry pointing at the
# Athena.UI next to this script, plus the hicolor icon set that entry names.
#
# Why this exists — the two icons are not the same icon:
#   * The dock/taskbar paints the *window* icon. Avalonia writes Window.Icon
#     (Views/MainWindow.axaml) into _NET_WM_ICON, so the owl appears there even
#     with no launcher installed at all.
#   * The application list paints the *launcher* icon: GNOME reads the
#     .desktop file's Icon= key and looks that name up in the icon theme.
#     No file, or a file without Icon=, and it falls back to the generic
#     application-x-executable placeholder.
# StartupWMClass is the other half. The window's WM_CLASS is "Athena.UI", which
# does not match the desktop file id "athena", so without this key GNOME never
# links the running window to the launcher and treats it as an unknown app.
#
# Usage: ./install.sh            per-user install under $XDG_DATA_HOME
#        ./install.sh --uninstall
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

APP_ID="athena"
WM_CLASS="Athena.UI"
APP_BIN="$SCRIPT_DIR/Athena.UI"
ICON_SRC="$SCRIPT_DIR/icons/athena.png"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$DATA_HOME/applications"
ICON_ROOT="$DATA_HOME/icons/hicolor"

uninstall() {
  rm -f "$APP_DIR/$APP_ID.desktop"
  for size in 16 24 32 48 64 128 256 512; do
    rm -f "$ICON_ROOT/${size}x${size}/apps/$APP_ID.png"
  done
  refresh_caches
  echo "Removed the Athena launcher and icons."
}

refresh_caches() {
  command -v update-desktop-database >/dev/null 2>&1 &&
    update-desktop-database "$APP_DIR" >/dev/null 2>&1 || true
  command -v gtk-update-icon-cache >/dev/null 2>&1 &&
    gtk-update-icon-cache -f -t "$ICON_ROOT" >/dev/null 2>&1 || true
}

resize() {
  # $1 = source, $2 = size, $3 = destination
  if command -v magick >/dev/null 2>&1; then
    magick "$1" -background none -resize "${2}x${2}" -strip "$3"
  elif command -v convert >/dev/null 2>&1; then
    convert "$1" -background none -resize "${2}x${2}" -strip "$3"
  else
    return 1
  fi
}

install() {
  [ -x "$APP_BIN" ] || {
    echo "error: $APP_BIN is missing or not executable — run install.sh from the extracted Athena directory." >&2
    exit 1
  }

  if [ -f "$ICON_SRC" ]; then
    installed_icons=0
    for size in 16 24 32 48 64 128 256 512; do
      mkdir -p "$ICON_ROOT/${size}x${size}/apps"
      resize "$ICON_SRC" "$size" "$ICON_ROOT/${size}x${size}/apps/$APP_ID.png" && installed_icons=1
    done
    # No ImageMagick: one 256 PNG still satisfies the theme lookup at every
    # size (GTK scales down), it just looks softer below 48px.
    if [ "$installed_icons" -eq 0 ]; then
      mkdir -p "$ICON_ROOT/256x256/apps"
      cp "$ICON_SRC" "$ICON_ROOT/256x256/apps/$APP_ID.png"
    fi
  else
    echo "warning: no icon at $ICON_SRC — the launcher will use the generic placeholder." >&2
  fi

  mkdir -p "$APP_DIR"
  cat >"$APP_DIR/$APP_ID.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Athena
Comment=Athena AI Assistant
Exec=$APP_BIN
Icon=$APP_ID
Terminal=false
Categories=Utility;
StartupWMClass=$WM_CLASS
StartupNotify=true
EOF

  refresh_caches
  echo "Installed $APP_DIR/$APP_ID.desktop"
  echo "Exec: $APP_BIN"
}

case "${1:-}" in
  --uninstall) uninstall ;;
  "") install ;;
  *) echo "usage: $0 [--uninstall]" >&2; exit 2 ;;
esac
