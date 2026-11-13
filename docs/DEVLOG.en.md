# GeekCleaner — Development Log

[中文版](DEVLOG.zh-CN.md) · Keep both files in sync: every change gets the same entry in each.

Last updated: 2026-11-13 · Version: 0.5

---

## 1. What this is

A Windows desktop tool, modelled on Geek Uninstaller, that (a) lists all installed programs and removes them together with their registry entries and leftover files, (b) finds junk files and recommends what to clean, and (c) analyses what occupies a disk. One x64 WinForms executable, built with the C# 5 compiler that ships with Windows. UI in Simplified Chinese, dark theme, custom-drawn controls.

## 2. Current features

### Applications page
- Sources: uninstall registry keys (HKLM 64-bit, HKLM `WOW6432Node`, HKCU), unregistered program folders (Program Files, `%LocalAppData%\Programs`, drive roots, `Games`, `SteamLibrary\steamapps\common`, … on every fixed drive), and Store apps via `Get-AppxPackage`.
- Vendor folders with no top-level exe (e.g. `Tencent`, `EA`) are expanded into their sub-programs.
- Columns: name (with icon; a coloured letter tile when the program has none), version, publisher, size, last used, type, advice. Click a header to sort.
- Search box; filter chips: all / desktop / portable / Store / suggested for removal.
- Multi-select (Ctrl/Shift) for batch uninstall or force delete.
- Uninstall flow: run the program's uninstaller (MSI via `msiexec /x`) → wait for the user to confirm it finished → scan leftovers → show the list → delete what the user leaves ticked.
- Force delete: skip the uninstaller, scan and delete.
- Store apps are removed with `Remove-AppxPackage`.
- Other actions: modify/repair, open install folder, jump to the registry key in regedit, remove only the list entry, web search, update via `winget`.

### Leftover scan (`LeftoverScanner`)
- Search terms: cleaned display name (version and bitness stripped), install folder name, uninstall key name; publisher handled separately. A blacklist rejects generic words ("Microsoft", "Update", "Python", …).
- Registry: full parallel walk of `HKCU\Software` and `HKLM\SOFTWARE`. A key matches when its name equals a term (depth ≤ 4); a value matches when its name or data references the install directory. A `…\shell\<verb>\command` hit removes the whole verb key. A publisher key is removed only if every subkey belongs to the program.
- Also: services whose `ImagePath` is inside the install directory, firewall rules, scheduled tasks (XML under `System32\Tasks`), folders named after the program under Roaming / Local / LocalLow / ProgramData / Program Files / Common Files / VirtualStore, Start-menu folders, and `.lnk` files pointing into the install directory.
- Deletion (robust, see `src/Force.cs`): the program enables `SeTakeOwnership`/`SeRestore`/`SeBackup`/`SeDebug` at startup. Processes under the install directory are killed in repeated passes to beat self-restarting watchdogs (e.g. AlibabaProtect). A service is disabled, stopped, and if it will not stop its process is killed by PID, then `sc delete`. On *access denied*, the file/folder's owner is set to Administrators and its DACL replaced with full-control (removing any explicit Deny ACE), then the delete is retried. A registry key that refuses to delete has its ownership taken the same way. Anything still locked by a live process is scheduled for deletion on reboot (`MoveFileEx`). Registry items are exported to `Backups\<timestamp>_<name>.reg` first; emptied parent keys are cleaned up.

### Junk cleanup page
- Targeted categories (system drive): user temp, Windows temp, Windows Update download cache, Delivery Optimization cache, error reports and crash dumps, thumbnail/icon cache, browser caches (Chrome, Edge, Brave, 360, QQ, Firefox), Windows logs, developer caches (pip/npm/NuGet), old installers in Downloads, old Windows installations, recycle bin.
- Full-drive walk (parallel) for: temp files, crash dumps, unfinished downloads, old logs, `.bak`/`.old`, files ≥ 1 GB. Skips Windows, Program Files, `$Recycle.Bin`, `.git`, `WinSxS`, `Package Cache`, reparse points.
- Scoring (`JunkItem.Rate`): importance/safety 50 + idle time 30 + size 20. Levels: *clean first* (≥ 78), *recommended* (≥ 60), *can clean* (≥ 40), *your call*. Caps: risk-3 items (large files) are always *your call*; risk-2 items (backups, old installers, Windows.old) never rise above *can clean*.
- A virtual "overall advice" category lists every item sorted by score.
- One-click selection by level; per-category tick box; multi-select rows with Space or the context menu.
- Cleaning deletes permanently; in-use files are skipped and stay in the list. Windows Update service is stopped and restarted around its cache.

### Disk analysis page
- Read-only parallel walk of one drive.
- Donut chart of space by file category (video, image, audio, document, archive, installer/image, program/library, game/resource data, dev/code, virtual disk, Windows system, other, unreadable/reserved), with legend.
- Cards: used/total, free, file count, folder count.
- File-age distribution in five buckets by last-write time.
- Right-hand list with three modes: folder sizes (two levels), largest files (≥ 100 MB, top 300), extension ranking.
- A one-line summary of findings under the title.

### Shell
- Dark theme, sidebar navigation with per-drive free-space bars, animated busy bar, cancellable long operations.
- App icon generated from vector code (`Logo.Draw`): a gradient "G" ring whose open end breaks into scattering pixel blocks.
- Manifest: `requireAdministrator`, DPI aware, long-path aware.

## 3. Architecture

See the file table in [AGENTS.md](../AGENTS.md). Key points:

- All scanners run on background threads via `Task.Run`; they report progress through an `Action<string>` and honour a `CancellationToken`. Parallel sections convert `AggregateException` back into `OperationCanceledException`.
- UI never disables containers while busy (that greys out labels); instead `MainForm.isBusy` guards every action and the action buttons are disabled individually.
- `DarkList` is an owner-drawn `ListView` used for every list (virtual mode for large ones). It strips `WS_HSCROLL` in `WM_NCCALCSIZE` and stretches one column to fill; it repaints the area below the last row to hide theme-drawn column lines.
- Check state lives in the model objects (`JunkItem.Checked`, `LeftoverItem.Checked`), not in the list view.

## 4. Verification status

Be honest about this section when you update it.

**Verified by running (read-only):**
- Program enumeration on the developer machine: 259 entries.
- Leftover scan for Git: 18 items found in ~14 s (was ~52 s before parallelisation).
- Junk scan of D: and disk analysis of D: (~815k files in ~3 s with a warm cache).
- All three pages and the leftover dialog, by screenshot from a non-elevated build.

**Not executed by the developer agent:**
- Any real uninstall, leftover deletion, registry deletion or backup/restore round-trip.
- Batch uninstall across several programs.
- Junk cleaning (the project owner appears to have run it themselves; no report of problems, but no formal test either).
- Reboot-delete scheduling, service deletion, scheduled-task deletion.
- **The 0.5 force-delete path (take-ownership, ACL reset, watchdog kill, PID-kill of services) has not been run against a real stubborn target.** It compiles and the read-only UI is unaffected; the logic follows standard Windows admin practice but needs a real test. The project owner's own report of AlibabaProtect ("access denied" on 684 items, 0 B freed) is the motivating case and the thing to verify next.
- The elevated exe itself was never launched by the agent (same sources as the tested build).

There are no automated tests.

## 5. Known limitations

- "Last used" comes from UserAssist, which only records launches through Explorer / Start menu / desktop. "No record" does not mean unused; such programs get no advice.
- Steam games often show Steam's own last-launch time because their detected location resolves to the Steam folder.
- Idle time for junk uses the later of last-write and last-access; antivirus scans refresh last-access, so idle time can be understated (the conservative direction).
- Portable detection is heuristic: it lists drivers and updater components too (e.g. `Google \ Update`). Deletion always needs confirmation.
- Some install locations are inferred from the icon or uninstaller path and marked as guessed.
- A few Store apps without a Start-menu entry show an internal ID as their name.
- Confirmation and result dialogs are standard light `MessageBox` windows.
- File deletions have no undo. Protected registry keys (TrustedInstaller-owned) fail and are reported.
- Name-based matching can produce false positives for programs with very generic names; the review list is the safeguard.

## 6. Ideas for next steps

Not commitments; pick what is useful.

- Automated tests for the pure logic: `CleanName`, `ContainsPath`, `IsSafePath`, `JunkItem.Rate`, `Usage` decoding.
- A dry-run mode that writes what would be deleted to a report file.
- Move deleted leftover files to a quarantine folder with a restore action instead of deleting outright.
- Dark confirmation dialogs to replace `MessageBox`.
- Better usage data: Prefetch files, BAM/DAM registry keys, per-game Steam `appmanifest` data.
- Proper names and icons for Store apps (read `AppxManifest.xml` and resolve `ms-resource:` strings).
- Duplicate-file finder and empty-folder finder.
- Click a slice of the donut chart to filter the list; drill down into a folder.
- Startup-item manager and browser-extension manager.
- English UI / localisation.
- Export the program list or analysis to CSV.

## 7. Changelog

### 0.5 — Force delete of stubborn files (2026-11-13)
- New `src/Force.cs`: enables owner/restore/backup/debug privileges; take-ownership + DACL reset for files, folders and registry keys; repeated-pass process kill to beat watchdogs; stop-or-kill-by-PID for services.
- `Util.DeleteTree` rewritten: on access-denied it grabs ownership and retries; on in-use it schedules a reboot-delete; honest byte accounting (reboot-scheduled items free 0 B now).
- Leftover delete: services are disabled + stopped + killed + `sc delete`; protected registry keys have ownership taken before `DeleteSubKeyTree`.
- Motivated by AlibabaProtect resisting removal (684 access-denied items). **Not yet verified against a real target** — see Verification status.

### 0.4 — Batch, priorities, disk analysis (2026-11-12)
- Multi-select batch uninstall / force delete with one merged leftover list.
- Junk scoring and four priority levels; overall-advice category; one-click selection by level; multi-row tick/untick.
- Usage data from UserAssist; "last used" and "advice" columns; "suggested for removal" filter.
- New disk-analysis page with donut chart, age distribution and three ranking lists.
- Fixes found in review: large files were being rated *can clean* → now capped at *your call*; built-in Microsoft Store apps no longer get removal advice; toolbar overlap on the applications page.
- Project moved to `D:\GeekCleaner`; repository published.

All 0.1–0.4 entries are from a single working session on 2026-11-12.

### 0.3 — Redesign
- Dark theme, sidebar, custom controls, owner-drawn lists, app icon and desktop shortcut.
- Junk page changed from a tree to category cards plus a virtual detail list (no 2000-item cap).
- Parallel registry and file walks.
- Type filter chips; friendly names for Store apps; context-menu verb keys removed whole.

### 0.2 — Fixes after first self-test
- Vendor folders expanded into sub-programs instead of being listed as one program.
- Shell-verb registry keys removed entirely rather than leaving a dead menu item.

### 0.1 — First version
- Program listing (registry, portable, Store), uninstall + leftover scan, force delete, winget updates.
- Junk scan and clean for C: and D:.
- Plain WinForms UI with a tab control.
