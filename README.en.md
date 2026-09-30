# AE Plugin Manager

> [中文说明 / Chinese documentation](README.md) · English

A lightweight plug-in manager for After Effects on Windows: **scan and inventory the plug-ins you already have, detect conflicts and inapplicable plug-ins, and install a dropped archive in one click** — inspired by Blender's BLT add-on manager.

Single-file executable, **121 KB** (123,904 bytes), runs on Windows 10/11 with the .NET Framework runtime that ships with the OS. No dependencies to install.

```
dist\AE插件管理器.exe        ← double-click to run
```

The user interface and the command-line reports are written in Chinese.

---

## 1. Safety design (the most important section)

The tool writes into After Effects' plug-in directories, so irreversible damage is constrained by hard rules:

| Constraint | How it is enforced |
|---|---|
| **No "delete" inside the AE directory** | "Uninstall" **moves** files into the recycle area and can be undone with one click; there is no code path in the program that truly deletes an AE file |
| **Back up before every overwrite** | Each overwrite first copies the original file to `backup\<session ID>\old\` inside the data directory, so a bad install can be undone |
| **Automatic whole-operation rollback on failure** | If any write in an install session fails (file locked, permission denied), every write made by that session is reverted and the AE directory returns to its pre-operation state |
| **Writes only inside AE install directories** | Every target path must resolve inside a discovered AE installation directory or shared plug-ins directory; out-of-bounds writes are refused and logged |
| **The only real deletion targets the backup area** | "Delete backups permanently" removes only this program's own backups under `E:\AE插件管理器\` and never touches the AE directory |
| **Installers are never run on your behalf** | Bundled `.exe`/`.msi` installers are never launched silently; the program only tells you to install them manually (avoiding uncontrolled registry / system-directory writes) |

The data directory defaults to `E:\AE插件管理器\` (on the same drive as AE; it follows AE's drive letter) and contains:

```
E:\AE插件管理器\
  ├─ backup\I20260930-114422\   original files backed up during install, plus the list of files added by that session
  ├─ recycle\U20260930-114613\  files moved out by uninstall (restorable)
  ├─ sessions.tsv               operation records (read by the "Recycle bin" tab)
  └─ log.txt                    detailed log of every write
```

The **Software → Change backup location** menu item moves this directory elsewhere.

---

## 2. Features

### 2.1 Installed plug-ins (inventory)

- Scans `Plug-ins`, `Scripts` and `Presets` of three AE versions (2020 / 2024 / 2025).
- Filter by AE version and by type; each row shows name, type, bitness, size, relative path and modified time.
- Double-click an entry to reveal it in Explorer; **Uninstall to recycle area** moves the selected entry into the recycle area.
- Automatically classifies `.aex` (effects), `.jsx`/`.jsxbin` (scripts), `.ffx` (presets), `*.plugin` (codec packages) and CEP extensions.
- Adobe's own scripts (such as `Scale Composition.jsx`) are excluded from the third-party inventory so they do not add noise.

### 2.2 Conflicts and inapplicable plug-ins (detection + one-click fix)

> A warning is raised only when several versions of the same plug-in conflict **within a single AE version**. Installing one copy per AE version is normal usage and is never flagged.

| Rule | Severity | One-click fix | Notes |
|---|---|---|---|
| Wrong directory | Critical | ✅ move to recycle area | `.aex`/`.8bf` files sitting under `Scripts` — AE only loads from `Plug-ins`, so they are never loaded, and they can also fight with a same-named copy under `Plug-ins`. Measured on this machine: `Scripts\RealGlow.aex` in AE 2024/2025. A one-click fix is offered **only when a same-named copy already exists under `Plug-ins`**; otherwise the tool gives guidance only, so a plug-in is never removed into complete uselessness. |
| Duplicate within one version | Critical | ✅ **you choose which copy to keep** | The same plug-in name exists more than once inside one AE version (for example `Plug-ins\RealGlow.aex` and `Plug-ins\Trapcode\RealGlow.aex`). AE loads only one of them, and which one depends on directory scan order. **Choose which copy to keep…** opens a list showing **size / bitness / full path / modified time** for each copy; you tick the one to keep and the others move to the recycle area. The tool does not decide for you. |
| Bitness mismatch | Critical | ✅ move to recycle area | 32-bit / ARM plug-ins are silently ignored by 64-bit AE (determined by reading the PE header, not guessed from the file name) |
| AE is running | Warning | show how to close it | Prompts you to close AE first; otherwise locked plug-in files make writes fail (your processes are never force-killed) |
| Install leftovers | Info | ✅ move to recycle area | `.bak`/`.old`/`.tmp` and macOS `._*` resource-fork files |
| Orphaned resource directory | Note | — | Resource directories such as `<plugin>_ffx` left behind after the plug-in was removed |

Selecting any issue shows a **[Fix plan]** below the list that states which files will be touched and what the result will be; **One-click fix** then executes it. Every fix action is a **move to the recycle area**, never a real deletion, and can be restored from the recycle bin at any time. The toolbar reports how many items are fixable ("N fixable items").

### 2.3 Install flow (drag in → choose version → one-click install)

Drag a **`.zip` / `.rar` / `.7z` archive, an extracted folder, or a single `.aex`/`.jsx`/`.jsxbin`/`.ffx` file** onto the window and the tool will:

1. **Extract only what is needed** — tutorial videos, images, URL shortcuts and advertising folders are skipped automatically. Measured on the `AutoSway` package: an 85 MB archive where only the 4.4 MB plug-in is installed and 80 MB of video is skipped.
2. **List the installable contents and let you choose** — when a package contains a Chinese version, an English version and a Mac version, or both a main script and a separate presets directory, a candidate list appears in the middle of the window (the recommended entry is pre-ticked and marked ★), for example:
   ```
   ★ Root directory (all contents) · 3 files · 4.4 MB
     AutoSway_ffx · 2 files · 30 KB
   ```
   Switching the selection recomputes the plan live — the tool never silently decides which one to install.
3. **Locate the payload root automatically** — wrapper directories such as `Deep Glow v1.5.5\` are absorbed, and files land where your existing directory layout expects them, e.g. `Plug-ins\Deep Glow.aex`.
4. **Carry dependency directories along** — for script plug-ins, `AutoSway_ffx\` is installed alongside the script into `Scripts\ScriptUI Panels\`.
5. **Map file types to destinations** — `.aex`/`.dll → Plug-ins`, `.jsx`/`.jsxbin → Scripts\ScriptUI Panels`, `.ffx → Presets`, `.plugin → (Media Core plug-ins)`.
6. **Plan first, write later** — it lists "which file → which directory → whether an existing file is overwritten", you tick the AE versions to install into, and execution starts only after you confirm, with an automatic backup beforehand.
7. **Chinese file-name compatibility** — many Chinese plug-in packages store file names in GBK without setting the UTF-8 flag; the tool decodes them with the system code page (otherwise extraction produces piles of garbled directory names).
8. **Installer-only packages still work** — when a package contains no directly copyable plug-in files and only `.exe`/`.msi` installers (for example HeatDistortion), the tool lists every installer in the package and **recommends** one (native architecture and Chinese version preferred; installers with a `fix` suffix are ranked lower). After you confirm, it **launches the installer wizard**:
   - nothing is run silently, no hidden parameters are added, and it never clicks Next for you;
   - when the installer finishes, the tool **automatically re-scans** the AE directories and tells you exactly which plug-ins were added or updated;
   - if the installer needs administrator rights (common when installing into `C:\Program Files\Adobe\Common\Plug-ins`), a separate **Run installer as administrator** button is available.

   Measured with a self-made installer to validate the chain: installer-only package → the installer appears in the list → run it → the payload is written into the AE directory → the automatic re-scan reports "Installation complete, 1 plug-in added/updated: AE 2024  __TEST_FAKE.aex".

### 2.4 Recycle bin (rollback)

- Every uninstall or overwrite produces a session record showing the operation type, time, contents and file count.
- **One-click restore** puts the files back in their original locations (for install sessions it restores the originals that were overwritten).
- **Delete backups permanently** removes data from the backup area only.

---

## 3. Hotkeys

| Key | Action |
|---|---|
| `F5` | Rescan |
| `Ctrl` + `Tab` | Switch tabs |
| `Ctrl` + `1`~`4` | Jump directly to tab N |

> For installer-only packages, the install page offers the buttons **Run selected installer** (current privileges) and **Run installer as administrator**. Both show a confirmation dialog stating that nothing is run silently and no hidden parameters are added.

---

## 4. Build & CLI self-check

### Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Compiles with the system `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` and produces `dist\AE插件管理器.exe` (~121 KB, zero dependencies).

### Command-line self-check

Every command is either read-only or takes an explicit action, which makes troubleshooting and scripting straightforward:

```powershell
# List the discovered AE installations and shared plug-ins directories
AE插件管理器.exe --list

# Identify only; print the install plan without writing any file
AE插件管理器.exe --detect "D:\xxx\Deep Glow v1.5.5 Win.zip" --version 2024

# Conflict detection report
AE插件管理器.exe --conflicts

# Install / uninstall to recycle area / list sessions / restore (shares the same execution and backup logic as the UI)
AE插件管理器.exe --install "<package path>" --version 2024
AE插件管理器.exe --uninstall "<file or directory inside an AE directory>"
AE插件管理器.exe --sessions
AE插件管理器.exe --restore <session ID>

# UI self-check: render the 4 tabs to PNG off-screen (does not steal mouse or keyboard focus)
AE插件管理器.exe --shot "<output directory>" [--detect "<package path>"] [--detect2 "<another package>"] [--choice <candidate index>]
```

Reports are written to `%TEMP%\AEPluginManager-report.txt` (UTF-8, to avoid console encoding problems).

---

## 5. Source layout

```
src\
  Model.cs      Data model: plug-in categories, AE instances, installed entries, conflicts (with fix plans), version candidates, install plans
  AeEnv.cs      AE environment discovery (E:\Adobe, C:\Program Files\Adobe, registry InstallPath)
  Scanner.cs    Read-only scanner (reads only; modifies no file)
  Conflicts.cs  Conflict rules, fix-plan generation and text reports
  Archive.cs    Extraction: native zip (with GBK file-name restoration); rar/7z via system bsdtar / 7-Zip / WinRAR
  Installer.cs  Install planning: identification (extract + classify + candidate list) and plan generation (payload root, dependency directories, path mapping)
  Executor.cs   Write execution: backup, failure rollback, uninstall to recycle area, restore, session manifest, safety valves
  Fs.cs         Filesystem helpers: directory creation, file and directory copy, directory size, cleanup
  Ui.cs         UI primitives and font fallback
  MainForm.cs   Main window (4 tabs + drag and drop + candidate selection + one-click fix)
  Program.cs    Entry point and command-line self-check
tools\
  make-icon.ps1     Generates app.ico (one-off tool)
  capture.ps1       Captures window screenshots (for troubleshooting)
  capture-tabs.ps1  Captures per-tab screenshots (for troubleshooting)
```

> Note: `tools\capture*.ps1` bring the window to the foreground and are meant for troubleshooting only. For routine UI verification use `--shot` above, which renders off-screen and never touches your desktop.

---

## 6. Verified results

Measured on this machine (AE 2020 / 2024 / 2025):

| Item | Result |
|---|---|
| AE discovery | Correctly identified 3 versions + 2 shared plug-ins directories (`Support Files` was once misdetected as a version; fixed) |
| Read-only scan | 103 third-party entries |
| Conflict detection | **3 items**: 2 critical (`Scripts\RealGlow.aex` misplaced in AE 2024/2025, matching a manual count) + 1 info (`RealGlow.aex.bak` leftover). All three come with a one-click fix. As requested, **coexistence across AE versions is no longer reported** (it previously produced 20 noise items of the form "installed in both AE 2024/2025"); only genuine conflicts within a single version remain. The earlier false positives that grouped `AEPixelSorter2`/`DeepGlow2`/`Anywhere2` were eliminated, and the high-false-positive "orphaned preset" rule was removed |
| Multiple copies in one version | A temporary copy (`Plug-ins\Trapcode\RealGlow.aex`) was created to verify this: correctly reported as `AE 2024: "RealGlow" has 2 copies`, with the fix plan **Choose which copy to keep…** listing size/path/time for both copies; the item disappeared after execution and the kept copy was intact. **The selection dialog itself cannot be verified off-screen** (it opens a modal window); the rest of the chain was measured |
| Package identification | All 11 real plug-in packages correct (Deep Glow, Deep Glow 2, AutoSway, Reka Grid, Saber, AEPixelSorter, Workflower, Loopflow, FXConsole, Motion, …); Mac packages, installer-only packages and a directly dropped `.exe` all produced the correct manual-install conclusion |
| Candidate selection | Deep Glow produced 3 candidates and recommended the Chinese version by default; AutoSway produced "Root directory (all contents)" and "AutoSway_ffx", defaulting to the root (main script + dependency directory, 3 files), and the plan recomputed live when the selection changed |
| End to end (AE 2024) | Fresh install → files written; overwrite install → original moved to backup; uninstall → moved to the recycle area; restore → files returned. All test traces were cleaned up and no existing plug-in was modified |
| UI | All 4 tabs render correctly (off-screen verification screenshots, including the candidate list and the "Fix plan" column) |
| Artifact | Single file, 121 KB (123,904 bytes), no external dependencies |

> The `Deep Glow.aex` and the temporary `Trapcode\RealGlow.aex` copy placed under `E:\Adobe\Adobe After Effects 2024\Support Files\Plug-ins\` during testing were deleted, and the `E:\AE插件管理器\` test data was cleared. The `Plug-ins` root of AE 2024 / 2025 still contains **9 / 11 files** respectively, matching this machine's original state file by file. Your existing `Scripts\RealGlow.aex` and `RealGlow.aex.bak` were only reported, never touched.
>
> **UI verification does not steal your mouse and keyboard**: `--shot` uses `DrawToBitmap` to render off-screen and never activates the window.

---

## 7. Known limits (what this tool does not do)

1. **Installers run as wizards, never silently** — the tool launches the `.exe`/`.msi` installer wizard and hands control to you. It adds no `/S`, `/VERYSILENT` or similar parameters and never clicks Next for you. This removes the risk of uncontrolled silent writes to the registry or system directories; the cost is that you have to click through the wizard.
2. **Encrypted archives are not handled** — if a password is required, the tool tells you to extract manually and drag the result in. It never prompts for or attempts to crack a password.
3. **`C:\Program Files\Adobe\Common\Plug-ins` requires administrator rights** — the tool runs with your current user privileges by default. If an installer fails to write there, use **Run installer as administrator** (this triggers a UAC prompt).
4. **`.aex` SDK version numbers are not parsed** — only PE bitness (32/64-bit) is validated. Whether a plug-in supports a given AE major version depends on metadata the plug-in itself must provide and cannot be reliably inferred, so the tool avoids speculative errors.
5. **Forced operations are allowed while AE is running** — with a second confirmation; the write may fail, and any failure triggers a full rollback.
6. **No cross-AE-version newness comparison** — the same plug-in installed in different AE versions is normal usage, so it is neither reported nor cleaned up (this rule was deliberately removed at the user's request).
7. **If an installer installs somewhere else, the tool can only honestly report "no new files found"** — it lists the three common causes (installation cancelled / installed into the shared plug-ins directory / permission denied) and never pretends to have succeeded.
