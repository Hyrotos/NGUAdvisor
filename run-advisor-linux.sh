#!/usr/bin/env bash
# Launch NGU Advisor into an already-running NGU Idle instance under the game's Proton prefix.
# When included in a release package, this script lives beside "Advisor Launcher.exe".
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
LAUNCHER="$SCRIPT_DIR/Advisor Launcher.exe"
STEAM_ROOT="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$HOME/.local/share/Steam}"
STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT"
STEAM_COMPAT_DATA_PATH="${STEAM_COMPAT_DATA_PATH:-$STEAM_ROOT/steamapps/compatdata/1147690}"
PROTON="${PROTON:-/usr/share/steam/compatibilitytools.d/proton-ge-custom/proton}"
GAME_EXE="${NGU_GAME_EXE:-$STEAM_ROOT/steamapps/common/NGU IDLE/NGUIdle.exe}"

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

[[ -f "$LAUNCHER" ]] || fail "Advisor Launcher.exe not found beside this script: $LAUNCHER"
[[ -x "$PROTON" ]] || fail "Proton executable not found or not executable: $PROTON (override with PROTON=/path/to/proton)"
[[ -d "$STEAM_COMPAT_DATA_PATH/pfx" ]] || fail "Proton prefix not found: $STEAM_COMPAT_DATA_PATH/pfx"
[[ -f "$GAME_EXE" ]] || fail "NGU Idle executable not found: $GAME_EXE (override with NGU_GAME_EXE=/path/to/NGUIdle.exe)"

# Require the game to be running; this script attaches the advisor and never starts/restarts NGU Idle.
if ! pgrep -af '[N]GUIdle.exe' >/dev/null; then
    fail 'NGU Idle does not appear to be running. Start it through Steam first, then run this script again.'
fi

export STEAM_COMPAT_CLIENT_INSTALL_PATH STEAM_COMPAT_DATA_PATH
printf 'Launching NGU Advisor for the already-running NGU Idle session.\n'
printf 'Proton prefix: %s\n' "$STEAM_COMPAT_DATA_PATH"
exec "$PROTON" run "$LAUNCHER"
