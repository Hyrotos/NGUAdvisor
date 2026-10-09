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

## Reverting the build system
The original legacy (VS-style) project is preserved as `NGUAdvisor/NGUAdvisor.csproj.legacy`.
