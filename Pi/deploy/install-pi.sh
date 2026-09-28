#!/usr/bin/env bash
set -euo pipefail

# Run this ON the Raspberry Pi from the published pi-arm64 folder, e.g.:
#   sudo ./install-pi.sh /path/to/publish/pi-arm64 --autostart --touch
SRC="${1:-.}"
shift || true
AUTOSTART=0
TOUCH=0
KIOSK=0
for arg in "$@"; do
  case "$arg" in
    --autostart) AUTOSTART=1 ;;
    --touch) TOUCH=1 ;;
    --kiosk) KIOSK=1 ;;
  esac
done

if [[ ! -f "$SRC/FlexCompanion" ]]; then
  echo "FlexCompanion executable not found in: $SRC" >&2
  exit 1
fi

APPDIR=/opt/flexcompanion
install -d "$APPDIR"
cp -a "$SRC"/. "$APPDIR"/
chmod +x "$APPDIR/FlexCompanion"

ARGS=""
[[ $TOUCH -eq 1 ]] && ARGS="$ARGS --touch"
[[ $KIOSK -eq 1 ]] && ARGS="$ARGS --kiosk"

cat >/usr/share/applications/flexcompanion.desktop <<DESKTOP
[Desktop Entry]
Type=Application
Name=Flex Companion
Comment=FLEX radio companion controller
Exec=$APPDIR/FlexCompanion$ARGS
Icon=$APPDIR/Assets/FlexCompanionIcon.png
Terminal=false
Categories=HamRadio;Utility;
DESKTOP

if [[ $AUTOSTART -eq 1 ]]; then
  USER_HOME="${SUDO_USER:+$(getent passwd "$SUDO_USER" | cut -d: -f6)}"
  USER_HOME="${USER_HOME:-$HOME}"
  install -d "$USER_HOME/.config/autostart"
  cp /usr/share/applications/flexcompanion.desktop "$USER_HOME/.config/autostart/flexcompanion.desktop"
  if [[ -n "${SUDO_USER:-}" ]]; then
    chown -R "$SUDO_USER":"$SUDO_USER" "$USER_HOME/.config/autostart"
  fi
fi

echo "Installed Flex Companion to $APPDIR"
echo "Desktop entry: /usr/share/applications/flexcompanion.desktop"
[[ $AUTOSTART -eq 1 ]] && echo "Autostart enabled."
