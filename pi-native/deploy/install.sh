#!/usr/bin/env bash
# Flex Control Companion — native Raspberry Pi / Linux build (Python + Qt + C++ core)
#
# One-line install (downloads the source and builds it on this machine):
#   curl -fsSL https://raw.githubusercontent.com/Craig-M-M9XDT/FlexCompanion/main/pi-native/deploy/install.sh | sudo bash -s -- --touch
#
# From a checkout:
#   sudo pi-native/deploy/install.sh --touch --autostart
#
# Works on 64-bit and 32-bit Raspberry Pi OS (Bookworm / Trixie) and Debian / Ubuntu desktops.
set -euo pipefail

REPO="Craig-M-M9XDT/FlexCompanion"
REF="main"
PREFIX="/opt/flexcompanion-native"
LAUNCHER="/usr/local/bin/flexcompanion-native"
DESKTOP_FILE="/usr/share/applications/flexcompanion-native.desktop"

SRC="" TOUCH=0 KIOSK=0 AUTOSTART=0 UNINSTALL=0 DEPS=1

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWARNING:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }
usage() {
  cat <<'HELP'
Usage: sudo install.sh [options]

  --touch          start with large touch controls
  --kiosk          start full-screen
  --autostart      start automatically when the desktop logs in
  --ref <branch>   GitHub branch or tag to install (default: main)
  --from <dir>     install from a local pi-native source folder
  --no-deps        skip apt packages
  --uninstall      remove the native build (settings are kept)
  -h, --help       show this help
HELP
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --touch) TOUCH=1 ;;
    --kiosk) KIOSK=1 ;;
    --autostart) AUTOSTART=1 ;;
    --ref) REF="${2:?--ref needs a branch or tag}"; shift ;;
    --from) SRC="${2:?--from needs a folder}"; shift ;;
    --no-deps) DEPS=0 ;;
    --uninstall) UNINSTALL=1 ;;
    -h|--help) usage ;;
    *) die "Unknown option: $1 (try --help)" ;;
  esac
  shift
done

[[ $EUID -eq 0 ]] || die "Please run with sudo."

TARGET_USER="${SUDO_USER:-}"
[[ -z "$TARGET_USER" || "$TARGET_USER" == "root" ]] && TARGET_USER="$(logname 2>/dev/null || true)"
TARGET_HOME="" TARGET_GROUP=""
if [[ -n "$TARGET_USER" && "$TARGET_USER" != "root" ]]; then
  TARGET_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
  TARGET_GROUP="$(id -gn "$TARGET_USER")"
fi

if [[ $UNINSTALL -eq 1 ]]; then
  say "Removing the native Flex Control Companion"
  rm -rf "$PREFIX" "$LAUNCHER" "$DESKTOP_FILE" /usr/share/pixmaps/flexcompanion-native.png
  [[ -n "$TARGET_HOME" ]] && rm -f "$TARGET_HOME/.config/autostart/flexcompanion-native.desktop"
  say "Done. Settings were kept in ~/.config/FlexCompanion/settings.json"
  exit 0
fi

ARCH="$(dpkg --print-architecture 2>/dev/null || uname -m)"
say "Architecture: $ARCH"

# ---------------------------------------------------------------- source
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
if [[ -z "$SRC" ]]; then
  here=""
  if d="$(dirname "${BASH_SOURCE[0]:-$0}")" && [[ -d "$d" ]]; then here="$(cd "$d" && pwd)"; fi
  if [[ -n "$here" && -f "$here/../pyproject.toml" && -d "$here/../flexcompanion" ]]; then
    SRC="$(cd "$here/.." && pwd)"
  fi
fi
if [[ -z "$SRC" ]]; then
  command -v curl >/dev/null || { apt-get update -qq && apt-get install -y -qq curl ca-certificates >/dev/null; }
  say "Downloading $REPO ($REF)"
  curl -fsSL "https://codeload.github.com/$REPO/tar.gz/$REF" | tar -xz -C "$WORK" \
    || die "Download failed. Check the branch name (--ref) and your internet connection."
  SRC="$(find "$WORK" -maxdepth 3 -type d -name pi-native | head -n1)"
  [[ -n "$SRC" ]] || die "Branch '$REF' does not contain pi-native/. Try: --ref pi-native-python"
fi
[[ -f "$SRC/pyproject.toml" ]] || die "Not a pi-native source folder: $SRC"
say "Source: $SRC"

# ---------------------------------------------------------------- packages
have_pkg() { apt-cache show "$1" >/dev/null 2>&1; }
SYSTEM_QT=0
if [[ $DEPS -eq 1 ]] && command -v apt-get >/dev/null; then
  say "Installing system packages (apt)"
  apt-get update -qq || warn "apt-get update failed; using cached package lists"
  pkgs=(python3 python3-venv python3-dev python3-pip python3-numpy build-essential cmake
        ca-certificates curl fonts-dejavu-core
        libglib2.0-0 libglib2.0-0t64 libfreetype6 libx11-6 libx11-xcb1 libxcb1 libxext6 libxrender1
        libxi6 libsm6 libice6
        libgl1 libegl1 libfontconfig1 libdbus-1-3 libxkbcommon0 libxkbcommon-x11-0
        libxcb-cursor0 libxcb-icccm4 libxcb-image0 libxcb-keysyms1 libxcb-randr0 libxcb-render-util0
        libxcb-shape0 libxcb-xinerama0 libxcb-xinput0 libxcb-xfixes0
        libwayland-client0 libwayland-cursor0 libwayland-egl1)
  # Trixie and newer package PySide6; Bookworm does not (pip wheels are used there instead).
  if have_pkg python3-pyside6.qtwidgets; then
    pkgs+=(python3-pyside6.qtcore python3-pyside6.qtgui python3-pyside6.qtwidgets)
    SYSTEM_QT=1
  fi
  avail=()
  for p in "${pkgs[@]}"; do have_pkg "$p" && avail+=("$p"); done
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${avail[@]}" >/dev/null \
    || die "apt-get install failed"
fi

PY="$(command -v python3)" || die "python3 not found"
"$PY" - <<'EOF' || die "Python 3.9 or newer is required"
import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)
EOF

# ---------------------------------------------------------------- venv
say "Creating Python environment in $PREFIX"
rm -rf "$PREFIX"
install -d "$PREFIX"
"$PY" -m venv --system-site-packages "$PREFIX/venv"
VPIP="$PREFIX/venv/bin/pip"
VPY="$PREFIX/venv/bin/python"
"$VPIP" install -q --upgrade pip >/dev/null 2>&1 || true

if [[ $SYSTEM_QT -eq 1 ]] && "$VPY" -c "import PySide6.QtWidgets" 2>/dev/null; then
  say "Using the distribution's PySide6"
else
  # PySide6 >= 6.8.1 ARM64 wheels need glibc 2.39; Bookworm has 2.36, so pick a release that runs here.
  glibc="$(ldd --version 2>/dev/null | head -n1 | grep -oE '[0-9]+\.[0-9]+$' || echo 0)"
  spec="PySide6-Essentials>=6.5"
  if [[ "$ARCH" == "arm64" || "$ARCH" == "aarch64" ]] && "$PY" -c "import sys; sys.exit(0 if tuple(map(int,'$glibc'.split('.'))) < (2,39) else 1)" 2>/dev/null; then
    spec="PySide6-Essentials>=6.5,<6.8.1"
  fi
  say "Installing $spec from PyPI (a ~60 MB download)"
  "$VPIP" install -q "$spec" \
    || die "Couldn't install PySide6 for $ARCH. On 32-bit Raspberry Pi OS use Trixie (which packages PySide6) or the 64-bit OS."
fi
"$VPY" -c "import numpy" 2>/dev/null || "$VPIP" install -q "numpy>=1.21"

say "Building the C++ core"
if "$VPIP" install -q scikit-build-core pybind11 >/dev/null 2>&1 \
   && "$VPIP" install -q --no-build-isolation "$SRC" 2>"$WORK/build.log"; then
  say "C++ core built"
else
  warn "The C++ core didn't build (see below); installing the pure-Python version, which is slower but complete."
  tail -n 15 "$WORK/build.log" >&2 || true
  site="$("$VPY" -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')"
  cp -r "$SRC/flexcompanion" "$site/"
fi
"$VPY" -m flexcompanion --version || die "Installed, but the app does not start; see the messages above."
if ! "$VPY" -c "import PySide6.QtWidgets" 2>"$WORK/qt.log"; then
  cat "$WORK/qt.log" >&2
  qtdir="$("$VPY" -c 'import PySide6, os; print(os.path.dirname(PySide6.__file__))' 2>/dev/null || true)"
  missing="$(find "$qtdir" -name 'libQt6*.so*' -exec ldd {} + 2>/dev/null | awk '/not found/{print $1}' | sort -u | tr '\n' ' ')"
  die "Qt can't load${missing:+ — missing system libraries: $missing}. Install them with apt and re-run."
fi
chmod -R a+rX "$PREFIX"

# ---------------------------------------------------------------- launcher
ARGS=""
[[ $TOUCH -eq 1 ]] && ARGS+=" --touch"
[[ $KIOSK -eq 1 ]] && ARGS+=" --kiosk"

cat >"$LAUNCHER" <<'LAUNCH'
#!/usr/bin/env bash
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/flexcompanion-native"
mkdir -p "$CACHE"
# Started over SSH: attach to the Pi's own desktop if one is running.
if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
  uid="$(id -u)"
  if [[ -S "/run/user/$uid/wayland-0" ]]; then
    export XDG_RUNTIME_DIR="/run/user/$uid" WAYLAND_DISPLAY=wayland-0
  elif [[ -S /tmp/.X11-unix/X0 ]]; then
    export DISPLAY=:0
  else
    echo "flexcompanion-native: no desktop session found. Log in to the Pi desktop first." >&2
    exit 1
  fi
fi
if [[ ! -t 1 ]]; then
  exec >"$CACHE/last-run.log" 2>&1
  echo "$(date) starting flexcompanion-native $*"
fi
cd "$HOME" || cd /
exec /opt/flexcompanion-native/venv/bin/python -m flexcompanion "$@"
LAUNCH
chmod 755 "$LAUNCHER"

ICON="/usr/share/pixmaps/flexcompanion-native.png"
install -D -m 644 "$SRC/flexcompanion/ui/assets/FlexCompanionIcon.png" "$ICON"
cat >"$DESKTOP_FILE" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Flex Control Companion (native)
Comment=FLEX radio companion — Python/Qt build
Exec=$LAUNCHER$ARGS
Icon=$ICON
Terminal=false
Categories=Utility;HamRadio;
StartupWMClass=flexcompanion-native
DESKTOP
chmod 644 "$DESKTOP_FILE"
if command -v update-desktop-database >/dev/null; then
  update-desktop-database -q /usr/share/applications || true
fi

if [[ $AUTOSTART -eq 1 ]]; then
  if [[ -n "$TARGET_HOME" ]]; then
    install -d -o "$TARGET_USER" -g "$TARGET_GROUP" "$TARGET_HOME/.config" "$TARGET_HOME/.config/autostart"
    cp "$DESKTOP_FILE" "$TARGET_HOME/.config/autostart/flexcompanion-native.desktop"
    chown "$TARGET_USER:$TARGET_GROUP" "$TARGET_HOME/.config/autostart/flexcompanion-native.desktop"
    say "Autostart enabled for $TARGET_USER"
  else
    warn "Couldn't work out the desktop user; autostart not configured."
  fi
fi

echo
say "Installed. Start it from the menu, or from a desktop terminal:  flexcompanion-native$ARGS"
echo "    No radio handy? Try:  flexcompanion-native --demo"
echo "    Logs when started from the menu:  ~/.cache/flexcompanion-native/last-run.log"
echo "    Uninstall:  sudo bash install.sh --uninstall"
