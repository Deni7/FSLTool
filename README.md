# FSLTool — FSLogix profile manager

**English** | [Українська](README.uk.md)

A native Windows utility (WPF, .NET 10) for the file server that hosts FSLogix containers (`Profile_*.vhdx`, `ODFC_*.vhdx`).

## Features

| | |
|---|---|
| **Interface** | Two tabs in the top bar: "Containers" (cards, table, details) and "Settings" (paths, thresholds, cleanup rules, theme and language). Next to them are the "Log" button (operation log above the status bar; the button shows the number of new warnings and errors), language, theme toggle and help. Cards, buttons and fields have rounded corners; container actions are grouped in a separate dark card: the main ones are visible at once, the rarer ones are under "More actions". The Manrope font is embedded in the exe. |
| **Theme** | Light and dark. By default it follows Windows (the app mode in Windows settings, and changes along with it); the theme button in the top bar or the "Same as Windows / Light / Dark" choice in settings switches it immediately, without restarting, in all open windows, including the window title bar color (Windows 11). The choice is remembered for the current Windows user (`HKCU\Software\FSLTool\Theme`). |
| **Paths** | Added on the "Settings" page via a dialog or typed manually (UNC too). Stored in `%ProgramData%\FSLTool\settings.json`; after a restart the scan starts automatically. For each path the free space on the storage is shown (a bar with the same thresholds as for containers) together with how much the found containers take; bars for all storages are also shown next to the "Containers" tab header. |
| **Visualization** | Cards (total / overfull / in session / orphans / disabled in AD / stale / worth compacting), a usage histogram, top largest, colored usage bars in the table, filters and search. Table rows have two lines: the container type is shown under the file name, the maximum size under the size, flags under the modification date, operation progress in the "State" column. The "✓" check-mark column can be sorted too: checked rows gather at the top or at the bottom. The details panel shows usage as a ring, size history and (via the "All details" button) the rest of the container data. |
| **Custom filter** | The "Filter" button next to the search box (Ctrl+Shift+F) opens a builder: conditions on ~50 container fields (text, size, number, date, yes/no, choice), operators for text (contains, equals, wildcard, regular expression, empty…), numbers and sizes (≥, <, from … to, unknown), dates (older than N days, before / after / between dates). Conditions are combined in "all (AND)" / "any (OR)" groups with "NOT" inversion; groups can be nested to any depth, and any condition or group can be temporarily disabled. The table updates while editing, and the filter is shown in words above it. Filters can be saved under a name in `%ProgramData%\FSLTool\filters.json` (shared by all administrators) and applied in headless mode with `--where "Name"`. A condition with an invalid value is highlighted and ignored. |
| **Usage** | Without attaching the disk: file size ÷ maximum size from the VHD/VHDX header (marked `*`). After "Analyze" the exact value from the NTFS volume inside is used. |
| **Mounted** | Open SMB files on the server (`MSFT_SmbOpenFile`, fallback — `NetFileEnum`): shows from which RDS host and under which user. Disks attached on this server are detected with a single `GetAllAttachedVirtualDiskPhysicalPaths` call. During a scan, container files on this server are not opened: previously, a trial open of 711 VHDX files took up to ~3 min because of antivirus and SMB leases held by RDS hosts. Locking by another process is checked before each action, for the file that action works on. For paths on another server, the trial open during the scan remains. |
| **Session on RDS host** | For a container opened by a session host you can send a message to the user's session (`WTSSendMessage`) or log the session off (`WTSLogoffSession`). On logoff FSLogix detaches the profile cleanly, so this is safer than "Unmount". Requires administrator rights on the host and RPC access to it. |
| **Unmount** | Force-closes the SMB handle (`ForceClose`) or detaches a locally attached disk. A warning is shown first. |
| **Measure** | Fast, a few seconds per container: attaches the disk **read-only** and determines the real usage, how much compaction will reclaim and the FS state, without walking folders. For checked containers, up to 4 run at once. The "Worth compacting" card shows how many free containers have not been measured yet. Its filter selects only those worth compacting, and the "Not measured yet: N →" link in the filter bar opens the rest so you can check and measure them. |
| **Analysis** | Attaches the disk **read-only** (up to 3 containers at once; large folders are walked by several threads): real usage, largest folders (AppData broken down to the `AppData\Local\Vendor` level), how much each rule would free, up to 20 largest files (from 100 MB, e.g. `*.ost`, `*.pst`, ISO) with a 🗑 button to delete a file from the container (same protections as cleanup: `globalExclude` is never deleted; then ReTrim and compaction, if enabled). Results are stored in `%ProgramData%\FSLTool\analysis-cache.json` and are available after a restart (and to other administrators) until the container file size changes. |
| **Cleanup** | Attaches the disk read-write without a drive letter, deletes data according to the rules, runs ReTrim, detaches the disk and (if enabled) compacts the VHDX. |
| **Compaction** | Full compaction via `CompactVirtualDisk` (disk attached read-only). The Hyper-V role is not required. After a measurement or analysis you can see roughly how much compaction will reclaim (file size − used in volume): the "Compaction" column and the "Worth compacting" card, with a threshold in GB in settings. |
| **Resize** | Increases the maximum size of a VHD(X) (`ResizeVirtualDisk`, for VHD `ExpandVirtualDisk`) and extends the partition inside to the new end (`MSFT_Partition.Resize`). The size is given as an absolute value (`50`) or an increment (`+10`) in GB. The Hyper-V role is not required. `SizeInMBs` in FSLogix only affects new containers, so this is how an existing user gets more space. |
| **File system** | During analysis the NTFS "needs check" flag (dirty bit) is read. It is shown in the flags as "FS: check". "Check FS" attaches the disk read-write and runs `MSFT_Volume.Repair` (equivalent of `chkdsk /f`). |
| **Batch mode** | Check several containers and the action runs for each of them: changes go one by one, measurement and analysis in parallel. Busy containers are skipped. The "is the container free" check before each action is fast: first a trial open of the file and `NetFileEnum` by path. A full WMI query of the server's open files is made only when `NetFileEnum` is unavailable. |
| **Scan speed** | After listing files, VHDX headers, attachment state and AD accounts are read concurrently, so a scan takes as long as the slowest of these phases. The duration of each phase is written to the log (the `time:` line). |
| **Size history** | Every scan (and headless mode) records the container size in `%ProgramData%\FSLTool\history.json`: one point per day, kept for 400 days. The "Growth" column shows how much the file grew in 30 days and a forecast of when usage will reach the "Overfull" threshold. Only increases count: drops after compaction are ignored. The forecast appears once at least 7 days of history are collected. The column can be sorted by growth rate. The details panel has a size chart. |
| **AD accounts** | For accounts that still exist, an LDAP query checks whether they are disabled or expired (`userAccountControl`, `accountExpires`) and the last logon date (`lastLogonTimestamp`). The "Disabled in AD" card works as a filter. |
| **Email report** | Headless mode can send an HTML email styled like the app with a summary (run status, operation result, container state cards, storages) and details (result per container, containers needing attention, log warnings), with the run output and a CSV of all containers attached. See "Headless mode". |
| **Audit** | Every action is written to `%ProgramData%\FSLTool\logs\fsltool-yyyyMMdd.log` with the administrator's name. |
| **Interface language** | Ukrainian and English. By default — the Windows display language; if there is no translation for it — English. Choosing a language on the "Settings" page or with the language button in the top bar changes it immediately, without restarting (window, dialogs, statuses, operation log, help), and is remembered for the current Windows user (`HKCU\Software\FSLTool\Language`). Headless mode uses the same language or `--lang`. |

## Security

- The container state is re-checked before every operation. A container in use is **never** attached.
- Attachment is temporary: if the utility crashes, Windows detaches the disk itself when the handle closes.
- Junctions and symlinks are not followed; only the link itself is deleted. This matters for profiles (for example, `Application Data` pointing to itself).
- Paths in rules cannot escape the container root (`..` is forbidden) and cannot point to the root itself.
- `globalExclude` (by default `NTUSER.DAT*`, `UsrClass.dat*`, `desktop.ini`) is never deleted by any rule.
- `SeBackupPrivilege` and `SeRestorePrivilege` are enabled to get past restrictive ACLs in profiles.
- The SMTP password for the email report is not stored in settings.json and not passed on the command line: the app reads it from an environment variable whose name is set in the settings.

## Cleanup rules

The file `%ProgramData%\FSLTool\cleanup-rules.json` is created on first run from the built-in template. Open it with the "Edit" button; after changes, click "Reload".

```json
{
  "globalExclude": [ "desktop.ini", "NTUSER.DAT*" ],
  "rules": [
    {
      "name": "Google Chrome — кеш",
      "enabled": true,
      "containers": [ "Profile" ],          // Profile | ODFC | Any
      "paths": [ "AppData\\Local\\Google\\Chrome\\User Data\\*\\Cache" ],
      "action": "DeleteContents",           // DeleteContents | DeleteFolder | DeleteFiles
      "include": [ "*" ],                   // file masks
      "exclude": [ ],                       // never delete
      "olderThanDays": 0                    // only files older than N days
    }
  ]
}
```

- Paths are relative to the container's root folder (`Profile` or `ODFC` inside the VHDX).
- The `$volume\` prefix means a path from the volume root, e.g. `$volume\$RECYCLE.BIN`.
- Path segments support the `*` and `?` wildcards, and `**` means any depth (slow, use with care).
- `DeleteContents` empties the folder, `DeleteFolder` deletes the folder entirely, `DeleteFiles` deletes only files matching the `include` masks directly in the folder.

The optional field `"names": { "en": "..." }` sets the rule name in other interface languages; without it, `name` is shown. The built-in template already has English names; a file created by a previous version is not changed — add `names` manually if needed.

The template already contains rules for Temp, INetCache, WER and dumps, thumbnail cache, Chrome, Edge, Firefox, Teams classic, new Teams and the Recycle Bin. A rule for old Downloads exists but is disabled.

## Headless mode (Task Scheduler)

```
FSLTool.exe scan | analyze | compact | clean [options]
```

| Command | What it does |
|---|---|
| `scan` | Scan: state, accounts, AD, size history recording. A daily `scan` fills the history even when nobody opens the window. |
| `analyze` | Analyzes free containers (read-only), updates the analysis cache. |
| `compact` | Compacts free containers that compaction will shrink by at least `--min-gain` GB. Containers without analysis data are quickly measured first: read-only attach, a few seconds. |
| `clean` | Cleanup by rules, followed by compaction if it is enabled in settings (`--no-compact` turns it off). |

Options:
- `--filter all|problem|critical|stale|orphan|disabled|compactable` — which containers to process;
- `--user "<mask>"` — select by user or file;
- `--where "<name>"` — select by a saved custom filter (the "Filter" button in the window, `%ProgramData%\FSLTool\filters.json`), combined with `--filter` and `--user`. If the filter does not exist, is empty or contains a condition with an error, the run does not start (code 2), so that not all containers get processed. The "Checked in the table" and "Operation in progress" fields are always "no" here; does not affect `scan`;
- `--min-gain <GB>` — minimum gain from compaction;
- `--max-minutes <N>` — night window: after N minutes no new containers are started;
- `--max-count <N>` — process at most N containers;
- `--dry-run` — only show the plan, change nothing;
- `--lang uk|en` — language of the console output, log and email. By default — the language chosen in the app for this Windows user, otherwise the Windows display language, otherwise English (a task running as SYSTEM usually gets the server's display language).

Email report:
- `--mail-to "a@x; b@x"` — recipients; without them (and without `mail.to` in settings.json) no report is sent;
- `--mail-on always|problems|errors` — when to send: always (default); if there are errors, skips, overfull containers or storages, or a limit ≤ 30 days; only when the exit code is not 0;
- `--smtp <server[:port]>` (default port 25), `--smtp-ssl` (STARTTLS; implicit TLS on 465 is not supported), `--mail-from <address>` (default `fsltool@<computer FQDN>`);
- `--smtp-user <login>` and `--smtp-password-env <VARIABLE>` — authentication; the password is taken from an environment variable (for SYSTEM — a system variable). Without a login the email is sent anonymously;
- `--no-mail` — do not send, even if configured in settings.json.

Defaults are in the `mail` section of `%ProgramData%\FSLTool\settings.json` (`smtpHost`, `smtpPort`, `ssl`, `from`, `to`, `user`, `passwordEnv`, `when`); they are not in the app window. If `mail.to` is filled in, all headless runs send the report. Mail configuration errors are detected before work starts (code 2).

The email is HTML in the app's colors: run status and parameters, operation result cards (or the plan for `--dry-run`) and container state cards, storages, details per container (errors and skips first), containers needing attention, log warnings and errors. Tables are limited to 50 rows. Attached are the full run output (`.log`) and all containers (`.csv`). An email with a non-zero exit code is marked as important.

Mounted containers are always skipped. The checks are the same as in the window. Everything is written to the log with the `[CLI]` tag. The full reference is printed by `FSLTool.exe /?`.

Exit codes: `0` — success, `1` — there were errors, `2` — invalid parameters, `3` — critical error, `4` — cancelled, `5` — another instance is already running, `6` — the work succeeded but the email report was not sent (with other codes a mail error is only written to the log).

Example: every night at 01:00 compact everything that will reclaim at least 2 GB, for no more than 4 hours:

```bat
schtasks /create /tn "FSLTool compact" /sc daily /st 01:00 /ru SYSTEM /rl HIGHEST ^
  /tr "\"C:\Tools\FSLTool.exe\" compact --min-gain 2 --max-minutes 240"
```

Daily scan for history:

```bat
schtasks /create /tn "FSLTool scan" /sc daily /st 06:00 /ru SYSTEM /rl HIGHEST /tr "\"C:\Tools\FSLTool.exe\" scan"
```

The same, emailing administrators only when there are problems:

```bat
schtasks /create /tn "FSLTool scan" /sc daily /st 06:00 /ru SYSTEM /rl HIGHEST ^
  /tr "\"C:\Tools\FSLTool.exe\" scan --mail-to admins@contoso.com --smtp smtp.contoso.local --mail-on problems"
```

- The task must run with the highest privileges (`/rl HIGHEST`).
- Under SYSTEM, network resources and AD are accessed as the computer account. If the profiles are on **another** server, use a service account with administrator rights.
- In cmd, run via `start /wait FSLTool.exe …`, otherwise the command prompt will not wait for completion.

## Documentation

- **In the app:** the **"?"** button in the top bar, "Help" on the "Settings" page or the F1 key opens the built-in help describing all features and modes. It has a table of contents and search (Ctrl+F, Enter — next match). The text lives in [`src/FSLTool/Docs/Help.md`](src/FSLTool/Docs/Help.md) (Ukrainian) and [`src/FSLTool/Docs/Help.en.md`](src/FSLTool/Docs/Help.en.md) (English) and is embedded in the exe; help is shown in the interface language.
- **Headless mode:** `FSLTool.exe /?`.
- **This README:** an overview for deployment and maintenance. The Ukrainian version is [`README.uk.md`](README.uk.md).

> **When changing features, update the documentation in the same change:** the built-in help `Docs/Help.md` and `Docs/Help.en.md`, interface strings in both languages (`Localization/*.uk.json`, `*.en.json`), this `README.md` together with `README.uk.md` and, if headless mode changes, the `Headless.Help` text. The rule is recorded in [`CLAUDE.md`](CLAUDE.md).

## Build and deployment

```bash
dotnet publish src/FSLTool -c Release -o publish
```

Result: a single file `publish\FSLTool.exe` (self-contained, x64, about 62 MB). Copy it to the file server and run it: it will request administrator rights itself. .NET does not need to be installed on the server.

Requirements: Windows Server 2016+ / Windows 10+, local administrator rights on the file server.

## Notes

- If a path is added as `\\THISSERVER\share`, it is converted to a local one via `Win32_Share` so SMB sessions are detected correctly. For paths on **another** server only the "Locked" state is detected, without the host name.
- If the volume does not appear after attaching, check the SAN policy (`diskpart` → `san`). With the `OfflineAll` policy disks are attached Offline.
- "Orphan" means the SID cannot be translated to an account (the user was deleted from AD). If the domain controller is unavailable, the container is not marked as an orphan.
- "Disabled in AD" means the account exists but is disabled or expired. Such containers can be deleted with the "Delete profile" button just like stale ones: the button appears when at least one stale or disabled container is selected, and is enabled only if **all** selected containers match one of these criteria and are not mounted. Before deleting a recently modified container, the account state is queried in AD again. If AD does not confirm the account is disabled (including when the domain controller is unavailable), the container is skipped. `lastLogonTimestamp` replicates with a delay of up to ~14 days.
- Theme colors are in `src/FSLTool/Themes/Palette.Light.xaml` and `Palette.Dark.xaml` (the same set of keys), element styles in `Themes/Theme.xaml`; colors are used only via `DynamicResource`, otherwise switching the theme will not change them. The Manrope font (`src/FSLTool/Assets/Fonts`) is distributed under the SIL Open Font License 1.1 (`OFL.txt` next to it). The static files were generated from the variable `Manrope[wght].ttf` from the google/fonts repository (`fontTools.varLib.instancer` with `overlap=REMOVE`, hinting removed): WPF renders overlapping contours and Google Fonts' autohinting with artifacts, so the files served by fonts.googleapis.com are not suitable.
- Interface strings live in `src/FSLTool/Localization/<area>.<language>.json` and are embedded in the exe. A new language is added with a pair of files `<area>.<code>.json` for all areas (the `Lang.Native` key is the language name) and `Docs/Help.<code>.md`. A missing key is shown in English. Operation results, log entries and statuses created before switching the language stay in the language they were created in; likewise, the custom filter fields "AD status (text)", "Flags (text)" and "Last operation result" are compared with text in the current language.
