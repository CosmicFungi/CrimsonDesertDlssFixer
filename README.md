# Crimson Desert — DLSS, MFG & RHI Repair Tool

A free, open-source Windows utility that diagnoses and fixes **Crimson Desert**'s **DLSS**, **Frame Generation**, **Multi-Frame Generation (MFG)**, inverted FG on/off behavior, and screen blinking/flashing — especially when a clean Steam reinstall does not fix the issue after using **RHI** (`RankFTW/RHI`), DLSS/MFG overriding mods, or global NVIDIA App settings.

---

## Symptoms This Fixes

- **DLSS / Frame Generation / MFG still broken after a clean game reinstall** (Steam clean installs do not reset NVIDIA Driver DRS profiles, global Streamline overrides, `%LOCALAPPDATA%\Pearl Abyss` configs, `%LOCALAPPDATA%\RHI` caches, or extra mod folders like `bin64\_BACKUPNV`).
- **Frame Generation / MFG behaves inverted** (looks ON when set to OFF, and OFF when set to ON) — caused by Global **NVIDIA Smooth Motion (`0xB0D384C0`)** colliding with Streamline's `sl.dlss_g.dll` swapchain hook (`slHookGetBuffer: proxyBuffer is NULL`).
- **Strange graphics blinking / flashing / subtle pulsing lighting** — caused by Global **NVIDIA Smooth Motion**, Global **RTX HDR (`0x00DD48FB`)** colliding with Crimson Desert's native in-game HDR, Global **RTX Dynamic Vibrance / `DeepDVC` (`0x00980880`)** hooking Streamline's `sl.deepdvc.dll`, Global **Driver Sharpening (`0x00598928`)**, Global **DLSS Model Preset Profile (`0x00634291`)**, or Global **Forced V-Sync (`0x00A879CF`)**.
- **Thousands of Streamline errors** (`kBufferTypeDepth` / `kBufferTypeMotionVectors`) caused by Global **`Enable Streamline override` (`0x10E41E06 = 1`)** injecting mismatched OTA Streamline DLLs over the game's `bin64\sl.*.dll` files.

---

## What It Checks & Fixes

1. **NVIDIA Driver Global (`Base Profile`) & `Crimson Desert` DRS Profiles** (via native `nvapi64.dll` + internal NVAPI v2 + direct `nvdrsdb0/1.bin` inspection):
   - Deletes stale/forced DLSS, DLSS-RR, DLSS-FG, MFG, and Streamline overrides (`0x10E41E06`, `0x10308298`, `0x104D6667`, `0x10AFB768`, `0x10BD9423`, `0x10E41E01-03`, `0x10E41DF*`, etc.) using `NvAPI_DRS_DeleteProfileSetting`.
   - Explicitly disables **NVIDIA Smooth Motion (`0xB0D384C0 = 0`)**, **Low Latency override (`0x0005F543 = 0`)**, **RTX HDR (`0x00DD48FB = 0`)**, **Global DLSS Model Preset override (`0x00634291 = 0`)**, **RTX Dynamic Vibrance / `DeepDVC` (`0x00980880 = 0`)**, **Driver Sharpening (`0x00598928 = 0`)**, **Transparency Supersampling (`0x10D48A85 = 0`)**, and **Forced V-Sync (`0x00A879CF`)** on the **`Crimson Desert` profile only** — preserving your Global settings for all other games.
2. **Pearl Abyss AppData XML Configs** (`%LOCALAPPDATA%\Pearl Abyss\CD\`):
   - Detects Read-Only locks, malformed XML, modded non-native DLSS 5 / OptiScaler strings, or invalid `_numFramesToGenerate` overrides while respecting legitimate native in-game DLSS 4.5 choices.
   - **Never touches your save slots** (`CD\save\<steamid>\slot*`).
3. **Game Directory (`bin64`) Mod Quarantine & DLL Verification**:
   - Detects and safely quarantines (moves, never permanently deletes) leftover RHI / DLSS 5 / Neural Rendering / MFG unlocker / OptiScaler / ReShade / RenoDX proxy DLLs and backup folders (`_BACKUPNV`, `dxgi.dll`, `version.dll`, `winmm.dll`, `nvngx_dlssnr.dll`, `*.addon64`, etc.).
   - Verifies all 9 official `nvngx_dlss*` and `sl.*` DLLs are present and Authenticode-signed by NVIDIA.
4. **RHI Cache Cleanup** (`%LOCALAPPDATA%\RHI`):
   - Cleans stale Crimson Desert DLSS-NR / ShortFuse / FG entries in `dlss_trusted_paths.json`, `game_library.json`, and `settings.json`.
5. **Streamline Log & Shader Cache Health**:
   - Analyzes the latest `%LOCALAPPDATA%\Pearl Abyss\log\Launcher_*.log` for Streamline version mismatches and `proxyBuffer is NULL` errors.
   - Offers targeted recent shader cache cleanup (last 72h) when broken overrides are detected, keeping older games' shader caches intact.

---

## Safety & One-Click Rollback

Before changing anything, the tool creates a full timestamped backup in:
```text
%LOCALAPPDATA%\CrimsonDesertDlssFixer\Backups\backup_<timestamp>\
```
Every modified DRS value, XML file, RHI JSON file, and quarantined mod file is recorded in `backup_manifest.json` and can be undone anytime with **Restore Last Backup** (or `--restore`).

---

## Quick Start

1. Close **Crimson Desert** and **RHI** (if running).
2. Download and run `CrimsonDesertDlssFixer.exe` (standalone single-file build — **no .NET installation required**).
   - *Note: Because this is an open-source unsigned executable, Windows SmartScreen may show a prompt. Click **More info -> Run anyway**, or inspect and build from source below.*
3. Review the detected issues and click **Fix Selected Issues (Auto-Backup)**.
4. Launch **Crimson Desert** and set your preferred DLSS & Frame Generation options in-game.

### Command-Line Options
```powershell
CrimsonDesertDlssFixer.exe --scan          # Read-only diagnostic report
CrimsonDesertDlssFixer.exe --scan --json   # JSON diagnostic report
CrimsonDesertDlssFixer.exe --fix           # Apply all recommended fixes with automatic backup
CrimsonDesertDlssFixer.exe --restore       # Restore from the latest backup
```

---

## Building From Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/) on Windows x64:

```powershell
# Standard build
dotnet build -c Release

# Standalone single-file executable (no .NET runtime needed on target PCs)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## License

MIT License — see [LICENSE](LICENSE).
