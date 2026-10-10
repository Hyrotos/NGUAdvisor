#!/usr/bin/env bash
#
# Linux/Proton release packager. Produces the same Windows-ready release ZIP as
# package-release.sh, but builds with .NET SDK 9 on Linux and uses 7-Zip for packaging.
# The packaged .exe/.dll files are intended to run with NGU Idle inside the same Proton prefix.
#
# Usage:
#   ./package-release-linux.sh
#   ./package-release-linux.sh 2.4.1
#   ./package-release-linux.sh --no-hot-reload [version]
#
# By default the package carries NGUAdvisorBootstrap.dll, and the launcher injects that instead of
# the advisor itself. The bootstrap byte-loads NGUAdvisor.dll and can load a newer one into the same
# game session, so after a rebuild "Hot-reload advisor" (F5) picks it up without restarting NGU Idle.
# --no-hot-reload leaves it out and gives the plain direct-inject layout of a public release.
#   NGU_RUNTIME=/path/to/NGU NGU_TOOLS=/path/to/NGU/injector ./package-release-linux.sh

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="Glowey-Glow/NGUAdvisor"
RUNTIME="${NGU_RUNTIME:-$ROOT/../NGU}"
TOOLS="${NGU_TOOLS:-$RUNTIME/injector}"
PROFILES="${NGU_PROFILES:-$ROOT/NGUAdvisor/SampleProfiles}"

# Pin builds to the installed .NET 9 SDK; do not silently use the default .NET 10 SDK.
DOTNET_BIN="$(command -v dotnet || true)"
[ -n "$DOTNET_BIN" ] || { echo "ERROR: dotnet was not found" >&2; exit 1; }
DOTNET_ROOT="$(dirname "$(readlink -f "$DOTNET_BIN")")"
SDK9_VERSION="$(dotnet --list-sdks | awk '$1 ~ /^9\./ { version = $1 } END { print version }')"
[ -n "$SDK9_VERSION" ] || { echo "ERROR: .NET SDK 9 is not installed" >&2; exit 1; }
SDK9_DLL="$DOTNET_ROOT/sdk/$SDK9_VERSION/dotnet.dll"
[ -f "$SDK9_DLL" ] || { echo "ERROR: .NET SDK entry point not found: $SDK9_DLL" >&2; exit 1; }
dotnet9() { "$DOTNET_BIN" "$SDK9_DLL" "$@"; }

command -v 7z >/dev/null || { echo "ERROR: 7z is required to create the release ZIP (install p7zip)" >&2; exit 1; }

HOT_RELOAD=1
if [ "${1:-}" = "--no-hot-reload" ]; then HOT_RELOAD=0; shift; fi

VERSION="${1:-$(grep -oE 'Version = "[^"]+"' "$ROOT/NGUAdvisor/Main.cs" | head -1 | sed -E 's/.*"([^"]+)".*/\1/')}"
[ -n "$VERSION" ] || { echo "ERROR: could not determine version" >&2; exit 1; }
OUT="$ROOT/dist"
STAGE="$OUT/NGUAdvisor-$VERSION"
ZIP="$OUT/NGUAdvisor_$VERSION.zip"

# Keep the classic Mono-readable WinForms resource contract: don't attempt to regenerate .resources
# from Linux PowerShell 7. That conversion requires Windows PowerShell 5.1/.NET Framework.
RESX="$ROOT/NGUAdvisor/SettingsForm.resx"
CLASSIC_RES="$ROOT/NGUAdvisor/SettingsForm.resources"
if [ -f "$RESX" ] && { [ ! -f "$CLASSIC_RES" ] || [ "$RESX" -nt "$CLASSIC_RES" ]; }; then
  echo "ERROR: SettingsForm.resources is missing or stale." >&2
  echo "Regenerate it on Windows with Windows PowerShell 5.1 (see BUILD.md), then retry." >&2
  exit 1
fi

for f in "$TOOLS/smi.exe" "$TOOLS/SharpMonoInjector.dll"; do
  [ -f "$f" ] || { echo "ERROR: missing injector tool: $f (set NGU_TOOLS)" >&2; exit 1; }
done
[ -d "$PROFILES" ] || { echo "ERROR: missing sample profiles: $PROFILES (set NGU_PROFILES)" >&2; exit 1; }

printf '==> NGU Advisor Linux release packager\n    version : %s\n    runtime : %s\n    SDK     : %s\n' "$VERSION" "$RUNTIME" "$SDK9_VERSION"

echo '==> Building NGUAdvisor (Release)...'
dotnet9 build "$ROOT/NGUAdvisor/NGUAdvisor.csproj" -c Release -v quiet
DLL="$(find "$ROOT/NGUAdvisor/bin/Release/net48" -maxdepth 1 -type f -name 'NGUAdvisor.r*.dll' -printf '%T@ %p\n' | sort -nr | awk 'NR == 1 { $1=""; sub(/^ /, ""); print }')"
[ -f "$DLL" ] || { echo 'ERROR: build produced no NGUAdvisor.r*.dll' >&2; exit 1; }
echo "    built: $(basename "$DLL")"

echo '==> Building Advisor Launcher (Release)...'
dotnet9 build "$ROOT/NGUAdvisorLauncher/NGUAdvisorLauncher.csproj" -c Release -v quiet
LAUNCHER="$ROOT/NGUAdvisorLauncher/bin/Release/net48/Advisor Launcher.exe"
[ -f "$LAUNCHER" ] || { echo "ERROR: launcher build produced no 'Advisor Launcher.exe'" >&2; exit 1; }

if [ "$HOT_RELOAD" = 1 ]; then
  echo '==> Building hot-reload bootstrap (Release)...'
  dotnet9 build "$ROOT/NGUAdvisorBootstrap/NGUAdvisorBootstrap.csproj" -c Release -v quiet
  BOOTSTRAP="$ROOT/NGUAdvisorBootstrap/bin/Release/net48/NGUAdvisorBootstrap.dll"
  [ -f "$BOOTSTRAP" ] || { echo 'ERROR: bootstrap build produced no NGUAdvisorBootstrap.dll' >&2; exit 1; }
fi

echo '==> Publishing Companion (Release, self-contained win-x64)...'
COMPANION_PUB="$ROOT/NGUAdvisorCompanion/bin/Release/net8.0-windows/win-x64/publish"
rm -rf "$COMPANION_PUB"
dotnet9 publish "$ROOT/NGUAdvisorCompanion/NGUAdvisorCompanion.csproj" \
  -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -v quiet
[ -f "$COMPANION_PUB/NGUAdvisorCompanion.exe" ] || { echo 'ERROR: companion publish produced no NGUAdvisorCompanion.exe' >&2; exit 1; }

# Stage the same runtime layout as the Windows packager.
echo "==> Staging $STAGE ..."
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE/injector"

# CRLF batch fallback. This and the launcher both write the path consumed by the companion host.
{
cat <<'BAT'
@setlocal enableextensions
pushd "%~dp0"

if not exist "%USERPROFILE%\AppData\LocalLow\NGUAdvisor" mkdir "%USERPROFILE%\AppData\LocalLow\NGUAdvisor"
<nul set /p="%~dp0injector" > "%USERPROFILE%\AppData\LocalLow\NGUAdvisor\injector-path.txt"

rem With the hot-reload bootstrap in the package, inject that: it loads NGUAdvisor.dll itself and
rem can load a newer build into the same game session (Hot-reload advisor / F5).
if exist ".\injector\NGUAdvisorBootstrap.dll" (
  .\injector\smi.exe inject -p NGUIdle -a .\injector\NGUAdvisorBootstrap.dll -n NGUAdvisorBootstrap -c Boot -m Init
) else (
  .\injector\smi.exe inject -p NGUIdle -a .\injector\NGUAdvisor.dll -n NGUAdvisor -c Loader -m Init
)

popd
BAT
} | sed 's/$/\r/' > "$STAGE/Run NGU Advisor.bat"

cp "$DLL" "$STAGE/injector/NGUAdvisor.dll"
if [ "$HOT_RELOAD" = 1 ]; then cp "$BOOTSTRAP" "$STAGE/injector/NGUAdvisorBootstrap.dll"; fi
cp "$TOOLS/SharpMonoInjector.dll" "$TOOLS/smi.exe" "$STAGE/injector/"
cp -r "$PROFILES" "$STAGE/sampleprofiles"
cp "$LAUNCHER" "$STAGE/Advisor Launcher.exe"
cp "$ROOT/run-advisor-linux.sh" "$STAGE/Run NGU Advisor Linux.sh"
chmod +x "$STAGE/Run NGU Advisor Linux.sh"
mkdir -p "$STAGE/injector/companion"
cp -r "$COMPANION_PUB/." "$STAGE/injector/companion/"
find "$STAGE/injector/companion" -type f \( -iname '*.pdb' -o -iname '*.xml' \) -delete

# Consume the full input so grep's early exit cannot make tr fail with SIGPIPE under pipefail.
grep -q 'injector-path.txt' "$STAGE/Run NGU Advisor.bat" \
  || { echo 'ERROR: staged .bat does not write injector-path.txt' >&2; exit 1; }
if ! tr -d '\000' < "$STAGE/Advisor Launcher.exe" | grep -a 'injector-path.txt' >/dev/null; then
  echo "ERROR: 'Advisor Launcher.exe' does not write injector-path.txt" >&2
  exit 1
fi

# The bootstrap is forbidden exactly when this run was asked to leave it out.
FORBIDDEN="$(find "$STAGE" \( -iname 'Assembly-CSharp.dll' -o -iname '*.bak*' -o -iname '*.orig' \) -print)"
if [ "$HOT_RELOAD" = 0 ]; then FORBIDDEN="$FORBIDDEN$(find "$STAGE" -iname '*Bootstrap*' -print)"; fi
if [ -n "$FORBIDDEN" ]; then
  echo 'ERROR: forbidden file staged:' >&2
  printf '%s\n' "$FORBIDDEN" >&2
  exit 1
fi

echo '==> Creating ZIP...'
mkdir -p "$OUT"
(
  cd "$OUT"
  7z a -tzip -mx=9 "$(basename "$ZIP")" "$(basename "$STAGE")" >/dev/null
)

printf '\n==> Done: %s (%s)\n' "$ZIP" "$(du -h "$ZIP" | cut -f1)"
printf '\nPublish a NEW release:\n  gh release create v%s "%s" --repo %s --title "NGU Advisor v%s" --notes-file <notes.md>\n' "$VERSION" "$ZIP" "$REPO" "$VERSION"
printf '\nOr refresh an EXISTING release asset:\n  gh release upload v%s "%s" --repo %s --clobber\n' "$VERSION" "$ZIP" "$REPO"
