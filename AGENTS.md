# AGENTS.md

Guidance for AI coding agents (and humans) picking up this project.
中文读者请直接看 [docs/DEVLOG.zh-CN.md](docs/DEVLOG.zh-CN.md)，内容与本文件加英文日志等价。

## Read first

1. [docs/DEVLOG.en.md](docs/DEVLOG.en.md) — full status, architecture, verification record, known limits, ideas. ([中文版](docs/DEVLOG.zh-CN.md))
2. `build.bat` — the whole build.

**When you change the project, append an entry to both DEVLOG files** (same content, two languages) and update their "Current features" and "Verification status" sections. That log is how the next agent learns where things stand.

## Hard constraints

- **C# 5 only.** The compiler is the one bundled with .NET Framework 4.x (`csc.exe` v4.0.30319). Not available: string interpolation (`$""`), `?.`, `nameof`, `out var`, expression-bodied members, auto-property initializers, pattern matching, tuples syntax, local functions, `catch when`. `async`/`await`, lambdas, LINQ and `Tuple.Create` are fine.
- **No external packages.** Only assemblies in the default `csc.rsp` (System, System.Core, System.Drawing, System.Windows.Forms, …).
- **x64, WinForms, single exe.** UI is hand-built in code; there is no designer file.
- **UI language is Simplified Chinese.** Source files are UTF-8; always compile with `/codepage:65001`.

## Safety rules for this codebase

This program deletes files and registry keys with administrator rights. Keep these invariants:

- Nothing is deleted without the user confirming a visible list.
- Any path deleted as a whole directory must pass `Util.IsSafePath` (blocks drive roots, Windows, Program Files, the user profile and their parents).
- Path matching uses `Util.ContainsPath`, which requires a path boundary after the match so `C:\App` does not match `C:\App2`.
- Registry keys/values are exported to `Backups\*.reg` before deletion (`LeftoverScanner.Backup`).
- Junk items whose importance cannot be judged (`Risk == 3`, e.g. large files) are capped at the lowest priority level and are never selected by the one-click buttons.
- **Do not run destructive paths against the developer's real machine while testing.** Use the read-only scans and the screenshot mode below.

## How to test without admin rights

`GeekCleaner.exe` has a manifest requiring elevation, so an agent usually cannot launch it. Use:

```bat
tools\dev-shots.bat
```

It builds a non-elevated copy and runs it with `--shot <dir>`, which loads the app list, runs a junk scan and a disk analysis on drive D (read-only), saves `apps.png`, `junk.png`, `disk.png` into `tools\out\`, and exits. Look at those images to check UI changes. The screenshots contain the machine's real program names and paths: **never commit them** (`tools\out\` and `preview-*.png` are git-ignored).

## Layout

| File | Role |
|---|---|
| `src/Common.cs` | `Util` (paths, sizes, delete, process helpers), `Native` P/Invoke |
| `src/Apps.cs` | `AppEntry`, `AppScanner` (registry / portable / Store), `Usage` (UserAssist launch records), uninstall advice |
| `src/Leftovers.cs` | `LeftoverScanner` — registry walk, services, firewall, tasks, files; backup and delete |
| `src/Junk.cs` | `JunkScanner` — junk categories, full-drive walk, scoring (`JunkItem.Rate`), cleaning |
| `src/Analyze.cs` | `DiskAnalyzer` (read-only stats), `AnalysisView` (donut chart control) |
| `src/Theme.cs` | colours, fonts, `Logo` (vector icon), custom controls (`Btn`, `Chip`, `SearchBox`, `Sidebar`, `DarkList`, …) |
| `src/MainForm.cs` | the three pages and all UI flow; `Program.Main`; `--shot` mode |
| `src/LeftoverForm.cs` | confirmation dialog for leftovers |
| `tools/IconGen.cs` | renders `Logo` into a multi-size `.ico` |
