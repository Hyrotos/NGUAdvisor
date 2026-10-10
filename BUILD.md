# Building NGU Advisor

## Prerequisites
- **.NET SDK** (9.x is fine) — installed via `winget install Microsoft.DotNet.SDK.9`.
  No Visual Studio needed; net48 reference assemblies come from the
  `Microsoft.NETFramework.ReferenceAssemblies` NuGet package.
- **NGU Idle installed** (Unity 2019.4 / Mono). The `.csproj` references the assemblies in
  `<steam library>/steamapps/common/NGU IDLE/NGUIdle_Data/Managed/` and finds that folder on its
  own in the usual Steam locations on Linux (`~/.local/share/Steam`, `~/.steam/steam`, Flatpak) and
  Windows (`Program Files (x86)\Steam`, `SteamLibrary` on `C:`–`F:`).
  If your library is somewhere else, point the build at the `Managed` folder with the
  `NGU_MANAGED_DIR` environment variable or `-p:NguManagedDir=<path>`.

## Why net48 (do not "upgrade")
The DLL is injected into NGU Idle's Unity 2019.4 **Mono (.NET 4.x)** runtime and must be a
.NET Framework 4.x assembly. A modern .NET build cannot be loaded by that runtime. See `PLAN.md`.

## Build
```
dotnet build "NGUAdvisor/NGUAdvisor.csproj" -c Release
```
Output: `NGUAdvisor/bin/Release/net48/NGUAdvisor.r<timestamp>.dll` — a single self-contained DLL.
The build stamps a unique `.r<timestamp>` name each time; deploy renames it to `NGUAdvisor.dll`.

## WinForms resources — important
The `.resx` are **not** compiled by the SDK at build time. The SDK's resource generator emits the
"preserialized" format, which needs `System.Resources.Extensions.dll` at runtime — that assembly
does not exist in the game's Mono domain, so the settings form would crash on open.

Instead we pre-generate **classic** `.resources` (the format Mono reads natively) and embed those:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/convert-resx.ps1
```

This must run under **Windows PowerShell 5.1** (`powershell.exe`), not `pwsh`/PowerShell 7,
because it relies on .NET Framework's classic `ResXResourceReader`/`ResourceWriter`.

Run it whenever you change `SettingsForm.resx` (edit the form in a designer, then regenerate),
then rebuild. The generated `SettingsForm.resources` is what actually gets embedded.

`SettingsForm.dje.resx` is a dead leftover (no code loads it) and is intentionally not embedded.

## Deploy
Copy the built `NGUAdvisor.r<timestamp>.dll` over `injector/NGUAdvisor.dll` in your runnable
folder, keeping the existing `smi.exe` and `SharpMonoInjector.dll`. Then run `Run NGU Advisor.bat`
with NGU Idle open — it injects `NGUAdvisor.dll` directly (`NGUAdvisor.Loader.Init`).

## Hot reload (iterating without restarting the game)
`package-release-linux.sh` ships `injector/NGUAdvisorBootstrap.dll` by default, and the launcher
injects that instead of the advisor when it is present. The bootstrap loads `NGUAdvisor.dll` from
bytes, so a newer build can be loaded into the same game session:

1. Start NGU Idle and launch the advisor once (this is the only start that needs the game restarted,
   if an advisor without the bootstrap was already injected).
2. Rebuild with `./package-release-linux.sh`.
3. Press **F5** in the game, or **Hot-reload advisor** in the companion's settings.

The bootstrap unloads the running advisor and loads the DLL now on disk; `logs/bootstrap.log` records
each step. The companion window stays open and reloads its page when it sees the new build. A change
to the companion *executable* still needs the window closed and reopened. The old assembly stays in
memory until the game exits (a few MB per reload).

`./package-release-linux.sh --no-hot-reload` leaves the bootstrap out and produces the plain
direct-inject layout of a public release; `package-release.sh` always does.

## Reverting the build system
The original legacy (VS-style) project is preserved as `NGUAdvisor/NGUAdvisor.csproj.legacy`.
