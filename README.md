<p align="center"><img src="docs/icon.png" width="120" alt="GeekCleaner icon"></p>

# GeekCleaner

**English** | [简体中文](README.zh-CN.md)

A Windows uninstaller and disk cleaner in the spirit of Geek Uninstaller: it lists every installed program, removes a program together with its registry entries and leftover files, finds junk files and ranks them by cleaning priority, and shows what is taking up a disk.

Single `.exe`, no installer, no dependencies beyond the .NET Framework 4.x that ships with Windows 10/11. The interface is in Simplified Chinese.

## Features

- **Applications** — lists programs from the registry (64-bit, 32-bit, per-user), unregistered/portable program folders on all fixed drives, and Microsoft Store apps. Search, filter by type, sort by any column.
- **Uninstall + leftover cleanup** — runs the program's own uninstaller, then scans the registry (`HKCU\Software`, `HKLM\SOFTWARE` including `WOW6432Node` and `Classes`), services, firewall rules, scheduled tasks, app-data folders and shortcuts. Everything found is listed for review before anything is deleted. Registry items are backed up to a `.reg` file first.
- **Force delete** — same scan without running the uninstaller, for broken or portable programs.
- **Batch mode** — select several programs with Ctrl/Shift and process them in one pass.
- **Usage-based advice** — reads Windows launch records to show when each program was last used, and flags large programs that have not been started for months.
- **Junk cleanup** — scans chosen drives for temp files, caches, crash dumps, update leftovers, the recycle bin and more. Each item is scored on importance, idle time and size, and placed in one of four priority levels. One-click selection by level.
- **Disk analysis** — donut chart of space by file type, folder sizes (two levels), file-age distribution, largest files, and extension ranking. Read-only.
- **Updates** — hands off to `winget` for upgrading a program or listing all available upgrades.

## Build

Requires only Windows with .NET Framework 4.x (present by default). No SDK, no NuGet.

```bat
build.bat
```

This regenerates `src\app.ico` from the vector drawing code and compiles `GeekCleaner.exe` with the C# compiler at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.

The program requests administrator rights on start (it needs them to read and delete system-wide items).

## Safety

This tool permanently deletes files and registry keys. Deleted files do not go to the recycle bin. Registry deletions are backed up under `Backups\`; file deletions are not. Review every list before confirming.

## For contributors and AI agents

Start with [AGENTS.md](AGENTS.md), then read the development log: [English](docs/DEVLOG.en.md) · [中文](docs/DEVLOG.zh-CN.md). It records what is built, what has and has not been verified, the constraints of the toolchain, and ideas for next steps.
