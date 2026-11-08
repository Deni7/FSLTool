<!--
  FSLTool built-in help (“Help” window, F1). Embedded in the exe as a resource.
  WHEN FEATURES CHANGE, UPDATE THIS FILE AND THE PROJECT README.md (see CLAUDE.md).
  Supported markup: # ## ### headings (## go into the table of contents), paragraphs, "- " lists (nested — 2 spaces),
  "| a | b |" tables, ``` code blocks, `code`, **bold**. HTML comments (like this one) are not shown.
-->

# FSLTool — Help

FSLTool helps the administrator of a file server that hosts FSLogix containers (`Profile_*.vhdx`, `ODFC_*.vhdx`). It shows how full the profiles are and who is using them right now, and lets you clean up and compact containers, increase their size and check their file system. Some tasks can run on a schedule without a window.

The program runs with administrator rights. It never attaches a container that someone is using. Every action is written to the log.

## Getting started

- Open the **“Settings”** tab and add a container folder: use the **“Browse for folder…”** button, or type a path (UNC works too, e.g. `\\server\profiles`) and press Enter or **“Add”**.
- A scan starts automatically. To run it again, click **“Scan”** above the table.
- Review the **thresholds** on the same page and click **“Apply and save”**.
- On the **“Containers”** tab, select a container in the table to see its details on the right, or check several with the checkboxes for a batch action.

Settings are shared by all administrators of the server: they are stored in `%ProgramData%\FSLTool`. The theme and the interface language are personal to each Windows user.

## App window

In the top row:

- **“Containers” / “Settings” tabs** in the middle. “Containers” holds the table, the cards and the details; “Settings” holds paths, thresholds, cleanup rules, theme and language.
- **“Log”** opens and closes the operation log above the status bar. The number on the button counts warnings and errors that appeared while the log was closed; opening the log resets it.
- **Language button** (e.g. “EN”): a list of languages, the current one is ticked.
- **Theme button** (moon / sun) switches between the light and dark theme. This is an explicit choice: after it the app no longer follows the Windows theme (to go back, choose “Same as Windows” in Settings).
- **“?”** (or F1) opens this help.

Under the “Profile containers” title you see the number of containers, their total size and the time of the last scan. To the right of the title, each path from the settings has a storage usage bar, the percentage and the free space (colored by the same thresholds as containers); the **“+”** button opens Settings.

At the bottom of the window is the **status bar**: what is happening now, a progress bar and a **“Cancel”** button while a scan or an operation runs.

## Settings

### Profile paths

For each path you see:

- a storage usage bar (colored by the same thresholds as containers);
- free space: “X free of Y” (for an unavailable path, the Windows error text);
- how many containers were found and how much space they take.

The ✕ button removes the path from the list (no files are deleted). A new path is added with the box at the bottom of the card (Enter or **“Add”**) or with **“Browse for folder…”**; adding or removing a path starts a scan.

### Thresholds

| Setting | Default | Meaning |
|---|---|---|
| Warning, % | 80 | From this usage the container is yellow (“near limit”) |
| Overfull, % | 90 | From this usage the container is red; the forecast is calculated up to this threshold |
| Stale, days | 90 | A container not modified for longer than this is considered stale |
| Worth compacting from, GB | 1 | Minimum gain for a container to appear in “Worth compacting” |

Type a value or use the **−** / **+** buttons (percent and gigabytes step by 1, days by 10). Changes take effect only after **“Apply and save”**; until then “Threshold changes are not saved yet” is shown next to the button. On save, percentages are limited to 1–100, days to at least 1 and gigabytes to at least 0. **“Restore recommended”** fills in the defaults from the table (they also need to be saved).

The **“Compact after cleanup”** switch compacts the VHDX right after “Clean up”, “Delete folder” and “Delete file”. It applies immediately and is written to `settings.json` together with the thresholds.

### Cleanup rules

The card shows how many rules are active, how many masks the global exclusions have, and the names of the first six active rules. If the rules file cannot be read, an error message is shown instead. **“Edit”** opens the rules file in Notepad. After making changes, click **“Reload”**. The format is described in the “Cleanup rules” section.

### Appearance: theme and language

**Theme**: **“Same as Windows”** (default: light or dark depending on the app mode in Windows settings, and follows it when it changes), **“Light”** or **“Dark”**. The theme switches immediately, without a restart, in every open window. The choice is remembered for the current Windows user (`HKCU\Software\FSLTool\Theme`).

**Interface language**: **“System”** (default), **“Українська”** or **“English”**. “System” is the Windows display language; if the app has no translation for it, English is used. The language changes immediately, without a restart: the window, dialogs, statuses, the operation log and this help. The choice is remembered for the current Windows user (`HKCU\Software\FSLTool\Language`), so every administrator of the server sees their own language.

Operation results, log entries and statuses created before a language switch stay in the language they were created in. The custom filter fields “AD status (text)”, “Flags (text)” and “Last operation result” are compared with the text in the current language. Saved filters do not depend on the language.

### Logs and help

- **“Open logs”** opens the folder with the audit logs.
- **“Help”** (or F1) opens this window.

## Cards and filters

The cards above the table show totals. Clicking a card filters the table; clicking it again removes the filter.

| Card | What it shows |
|---|---|
| Containers | All containers found and their total size |
| Overfull | Usage ≥ the “Overfull” threshold (+ the number near the limit) |
| Mounted | Open right now: in a session on an RDS host, attached locally or locked |
| Orphans | The SID does not resolve to an account: the user was deleted from AD |
| Disabled in AD | The account exists but is disabled or expired |
| Stale | Not modified for longer than the “Stale” threshold (and not mounted right now) |
| Worth compacting | According to measurement or analysis, compaction reclaims at least the “Worth compacting from” threshold. The number below shows how many free containers have not been measured yet. Clicking selects only those worth compacting. The **“Not measured yet: N →”** link in the filter bar shows free containers without exact data, and **“← Worth compacting”** takes you back |

The active card filter is shown above the table as a label with the filter name; its **✕** removes the filter. “Showing N of M” is shown there too.

The **search** box filters by user, path, state and host. **“Check visible”** / **“Uncheck”** set or clear the checkboxes in all rows currently visible.

## Custom filter

The **“Filter”** button next to the search box (or **Ctrl+Shift+F**) opens the filter builder. It selects containers by any combination of conditions. The table updates immediately as you edit, and the top of the window shows “Showing N of M”. The custom filter works together with the search and the card filter: a row is shown only if it passes all three.

When the filter is active, the button is highlighted and shows the number of conditions, and a label with a plain-language description appears above the table, for example “Usage ≥ 80 % AND NOT (Connection state equals In session OR Orphan)”. Clicking the description opens the editor; the **✕** on the label removes all conditions. The custom filter is not kept between runs unless you save it under a name.

### Conditions

Each condition consists of a field, an operator and a value:

| Field type | Fields | Operators |
|---|---|---|
| Text | User, account, name from the folder name, SID, AD state, full path, folder, file name, path from the “Paths” list, session host, opened by, flags, large folders and files from the analysis, last operation result | contains, does not contain, equals, does not equal, starts with, ends with, matches mask (`*` and `?`), does not match mask, regular expression, not regular expression, empty, not empty |
| Size | File size, maximum size, volume used / free / capacity, compaction reclaims, cleanup estimate, growth over the period and per day, largest file | ≥, >, ≤, <, =, ≠, from … to, unknown, known |
| Number | Usage (%), days to limit, number of open SMB handles | the same |
| Date | Modified, last logon (AD), measured / analyzed, refreshed | older than N days, in the last N days, before date, after date, between dates, unknown, known |
| Yes / no | Orphan, disabled in AD, stale, has RW / diff disk, meets deletion criteria, usage measured exactly, worth compacting, limit ≤ 30 days, mounted, checked in the table, operation in progress | yes, no |
| Choice | Container type, disk format, usage level, connection state, file system | equals, does not equal |

Value details:
- A size is entered with a unit `B`, `KB`, `MB`, `GB`, `TB` (`500 MB`, `1.5 TB`). A number without a unit means GB. Comma and period are equivalent. “=” allows a 0.5 % tolerance for sizes and 0.5 percentage points for usage.
- A date is entered as `dd.mm.yyyy` (or `yyyy-mm-dd`). “After date” excludes the day itself; “before date” and “between dates” include the end day.
- Text is compared case-insensitively. A mask or regular expression is matched against the whole value: the mask `CONTOSO\a*` finds all users starting with “a”. In the “Large folders” and “Large files” fields each path is on its own line: “equals”, “starts with” and “ends with” check each path separately, while a mask or expression works within a line.
- If a value is unknown (the container has not been measured yet, there is no history or AD data), a comparison condition is not met. To select such containers, use the “unknown” / “empty” operators.
- A condition with an error (invalid number, date or regular expression) is highlighted in red and is **ignored** until fixed.
- The checkbox on the left temporarily disables a condition or group without removing it.

### AND / OR / NOT groups

Conditions are combined into a group. In the “Show if matching” list, choose **“all conditions (AND)”** or **“any condition (OR)”**. The **“NOT”** checkbox inverts the group: containers that do not match it are shown. The **“+ Group”** button adds a nested group with its own mode and its own “NOT”; nesting depth is unlimited. An empty group does not restrict anything.

Example: “Stale or disabled in AD, larger than 5 GB, excluding service accounts” is an “all conditions” group: “File size ≥ 5”, a nested “any condition” group with “Stale — yes” and “Disabled in AD — yes”, and the condition “User — does not match mask — `*\svc_*`”.

### Saved filters

**“Save as…”** saves the current filter under a name in `%ProgramData%\FSLTool\filters.json`. Saved filters are shared by all administrators of the server. Selecting a name in the “Saved” list loads the filter (the file is re-read, so changes by other administrators are visible). Saving under the same name overwrites the filter (with confirmation if it is a different filter). **“Delete”** deletes the saved filter; the conditions in the editor remain. **“Clear”** removes all conditions. Saves and deletions are written to the log.

A saved filter can be used in headless mode: `FSLTool.exe compact --where "Name"` processes only the containers that pass it (see “Headless mode”).

## Container table

Each row has two levels: the main value on top, details below it.

| Column | Top line | Bottom line |
|---|---|---|
| ✓ | Checkbox for batch actions. Clicking the “✓” header groups checked containers: first click on top, next click at the bottom | |
| User | A circle with initials (the colour is fixed per name; “?” is an orphan without a name) and the account (DOMAIN\user) or the name from the folder name | File name and type (Profile, ODFC or Unknown); full path in the tooltip |
| Usage | Bar and percentage. A `*` mark means an estimate from the file size; without `*` it is the exact value from the analysis | |
| Size | VHDX file size | “of …” — the maximum size from the header |
| Compaction | Roughly how much compaction reclaims (after analysis); highlighted if above the threshold | |
| Growth | How much the file grew over 30 days | Forecast “limit ≈ in N days” (red if ≤ 30) |
| State | Coloured label: Free / In session / Local / Locked | Session host. During an operation the state is replaced by the stage, percentage and a progress bar |
| Modified | Time of the last file change | Flags (see below) |
| ↻ | Refresh information for this container only | |

Any column can be sorted by clicking its header: “Size” sorts by file size, “Growth” by growth rate. “✓” groups checked containers and keeps the usage order within the group. While you are checking boxes, rows are not reordered; to regroup, click the “✓” header again. If the window is narrow, the table scrolls horizontally.

### Flags

- **orphan**: the account was deleted from AD.
- **disabled in AD** / **expired**: the account is inactive in AD.
- **>N days**: the container is stale.
- **RW-diff**: there is an `RW.VHDX` differencing disk next to it.
- **FS: check**: the NTFS inside is marked as needing a check.

### States

- **Free**: the container is not open over SMB and not attached on this server.
- **In session**: the container is open over SMB from an RDS host (the host name and user are shown).
- **Local**: the disk is attached on this server.
- **Locked**: another process holds the file. For paths on another server this is the only state available.

During a scan, containers on this server are **not opened**. With hundreds of VHDX files this used to take minutes: the antivirus scans the file when it is opened, and the RDS host holding the file must first release its SMB lease. So a lock held by another local process (for example, a backup) is detected not during the scan but **before each action**: at that point exactly that file is opened for the check, and a busy container is skipped. For paths on another server, files are still opened during the scan.

### Refreshing

- **“Scan”**: full scan of all paths.
- **“Refresh visible”**: re-read size, header, state and account for visible rows only. The ▾ next to it lets you refresh checked or all containers.
- **↻ in a row**: refresh one container.
- **“Cancel”** in the status bar: stop the scan or operation.

## Details panel

- **Who**: initials, account, SID and full path; the state label (with the session host) and the container flags.
- **Usage**: a ring with the percentage (colored by the thresholds), next to it the file size and maximum size, below them used in volume, how much compaction reclaims and the file system state.
- **Size history**: a chart of the size over the whole history, growth and forecast.
- **“All details”** expands the rest: modification time, session, when the information was refreshed and when it was analyzed, the AD account state and last logon date.
- **Operation progress**: stage, percentage, details and time. The “Cancel” button is here too.
- **Actions** (dark card): the header says what they apply to — the selected container or the checked ones (with the count). “Measure”, “Analyze”, “Clean up” (highlighted; after an analysis it shows how much the rules would free) and “Compact” are always visible, as is “Delete profile” when it is available. **“More actions”** expands “Increase size”, “Check FS”, “Show in Explorer”, the RDS session buttons and “Unmount”. The actions are described below.
- **Can be freed** (after analysis): how much each cleanup rule would free.
- **Largest folders** (≥ 100 MB, after analysis): clicking a row opens its subfolders; “←” and the breadcrumbs take you back. In the overview, AppData is broken down to the `AppData\Local\Vendor` level.
- **Largest files** (≥ 100 MB, up to 20, after analysis): name, folder, modification date and size; full path in the tooltip. The trash button deletes the file from the container (see “Delete a file from a container”).

## Container actions

Each action first checks again that the container is free. Busy containers are skipped.

### Measure

A quick action: a few seconds per container. Attaches the disk **read-only** and determines:

- the actual volume usage (usage without `*`);
- how much compaction reclaims (the “Compaction” column, the “Worth compacting” card);
- whether the file system is marked as “needs a check”.

Folders are not traversed, so the largest folders and files require “Analyze”. If several containers are checked, up to 4 are measured at a time.

Typical workflow:

- click the “Worth compacting” card, then the “Not measured yet: N →” link;
- “Check visible”, then “Measure”;
- the “← Worth compacting” link shows the containers worth compacting;
- “Check visible”, then “Compact”.

### Analyze

Attaches the disk **read-only** and determines the same as “Measure”, plus:

- the largest folders and files;
- how much each rule would free.

Analysis walks through all profile files, so it takes longer. Large folders are traversed in several threads. If several containers are checked, up to 3 are analyzed at a time.

Measurement and analysis results are saved and shown after a restart (also to other administrators) until the container file size changes.

### Clean up

Attaches the disk read-write, deletes data according to the cleanup rules, runs ReTrim, detaches the disk and (if enabled) compacts the VHDX.

### Compact

Returns free space from the VHDX to the server disk (`CompactVirtualDisk`, full mode). Hyper-V is not required. To find containers where this makes sense, use the “Worth compacting” card.

### Increase size

Increases the maximum size of the VHD(X) and extends the partition inside to the new end of the disk. The size is entered in GB:

- `50`: set 50 GB (containers that are already this size or larger are skipped);
- `+10`: add 10 GB to the current size of each container.

Before running, a “before → after” list and the free space on the storage are shown. This tool cannot shrink the size back. `SizeInMBs` in the FSLogix settings applies only to new containers, so this is how you add space for existing users.

### Check FS

Checks and repairs the NTFS inside the container, like `chkdsk /f` (`MSFT_Volume.Repair`). While repairing, chkdsk may delete records that cannot be recovered. The result appears in the field below the buttons, and details in the Windows event log (source Chkdsk).

### Unmount

Forcibly closes the SMB handle or detaches a locally attached disk. **If the user is in a session, unsaved data may be corrupted.** It is safer to use the “Log off” button first.

### Show in Explorer

Opens the container folder with the file selected.

### Delete profile

Only containers that meet at least one criterion can be deleted:

- **stale**: not modified for longer than the “Stale, days” threshold;
- **disabled in AD**: the account is disabled or expired, even if the container was modified recently.

The button appears if at least one selected container meets the criteria. It is enabled only if **all** selected containers can be deleted, i.e. they meet the criteria and are not mounted right now. Otherwise, a note under the button explains how many containers are in the way and which ones.

- The confirmation lists the deletion reason and the last modification date for each container.
- The file age is checked again before deletion. If by age the container is not stale, the account is **queried in AD again**. The container is deleted only if AD confirms that the account is still disabled or expired. If the domain controller is unavailable, the container is skipped.
- The user folder is deleted only if no other containers (e.g. ODFC) remain in it. Otherwise only the container file and its service files are deleted.
- Several containers require double confirmation.
- There is no recycle bin on a network share: deleted data cannot be recovered.

### Delete a folder from a container

In the “Largest folders” list each folder has a trash button. The folder is deleted inside the container with the same safeguards as during cleanup. Then, if enabled, the VHDX is compacted. The root, `AppData`, `AppData\Local`, `AppData\Roaming` and `AppData\LocalLow` cannot be deleted.

### Delete a file from a container

In the “Largest files” list each file has a trash button. Exactly that file is deleted; files with similar names and files with the same name in other folders are not touched. The safeguards are the same as during cleanup: files from `globalExclude` (`NTUSER.DAT*`, `UsrClass.dat*`…) are not deleted, and the button is disabled for them. After deletion, ReTrim runs and, if enabled, the VHDX is compacted. The file list and folder sizes are updated immediately.

- `*.ost`: the Outlook mailbox cache. Outlook recreates it and downloads the mail from the server again; the confirmation warns about this.
- `*.pst`: a local mail archive; its contents may exist **only** in this file. The confirmation reminds you of this separately.

## Session on RDS host

This block of buttons (under “More actions” in the Actions card) appears if the container is open over SMB from a known RDS host.

- **“Message…”**: send text to the user's session (for example, to ask them to sign out). Also works for several checked containers.
- **“Log off”**: log the user off the session on the host. A list of the sessions found is shown first. During logoff FSLogix detaches the profile cleanly, so this is safer than “Unmount”. The container states are refreshed after a few seconds.

Requires administrator rights on the RDS host and network access to it (RPC). The session is looked up by the container owner's account.

## Batch mode

Check several containers with the checkboxes. The “Clean up”, “Compact”, “Increase size”, “Check FS” and “Delete profile” actions run for each of them in turn. During such an action the details panel shows the container currently being processed.

Read-only actions run in parallel: “Measure” processes up to 4 containers at a time, “Analyze” up to 3. The progress of each is shown in its row (the “State” column).

Busy containers are skipped. The summary and total time appear in the status bar and in the log.

## Size history and forecast

- Every scan (in the window and in headless mode) records the size of each container: one point per day, 400 days.
- **Growth** is calculated as the sum of daily **increases** in file size over 30 days. Drops after compaction or cleanup are not subtracted.
- **Forecast** shows when usage will reach the “Overfull” threshold at the current growth rate. It appears once there are at least 7 days of history, and only if the limit is less than a year away.
- To collect history daily even when nobody opens the window, schedule `FSLTool.exe scan` (see “Headless mode”).

## AD accounts

During a scan the container SID is resolved to an account. If it cannot be resolved, the container is considered an **orphan**. For existing accounts, an LDAP query checks:

- disabled account (`userAccountControl`);
- expired account (`accountExpires`);
- last logon date (`lastLogonTimestamp`, replicated with a delay of up to ~14 days).

If the domain controller is unavailable, nothing is flagged. Containers of disabled and expired accounts can be deleted with the “Delete profile” button even if they were modified recently. The AD state is checked again before deletion (see “Delete profile”).

## Cleanup rules

The `%ProgramData%\FSLTool\cleanup-rules.json` file is created on first run from the built-in template.

```
{
  "globalExclude": [ "desktop.ini", "NTUSER.DAT*" ],
  "rules": [
    {
      "name": "Google Chrome — кеш",
      "enabled": true,
      "containers": [ "Profile" ],
      "paths": [ "AppData\\Local\\Google\\Chrome\\User Data\\*\\Cache" ],
      "action": "DeleteContents",
      "include": [ "*" ],
      "exclude": [ ],
      "olderThanDays": 0
    }
  ]
}
```

| Field | Meaning |
|---|---|
| containers | `Profile`, `ODFC` or `Any` |
| paths | Relative to the container root folder; `$volume\` — from the volume root; `*`, `?` in segments, `**` — any depth (slow) |
| action | `DeleteContents` — folder contents; `DeleteFolder` — the whole folder; `DeleteFiles` — only files matching the `include` masks directly in the folder |
| include / exclude | File masks; `exclude` is never deleted |
| olderThanDays | Only files older than N days |
| names | Optional: the rule name in other interface languages, e.g. `{ "en": "Google Chrome — cache" }`; without a translation `name` is shown |
| globalExclude | Never deleted by any rule |

`..` in paths is not allowed. Junctions and symlinks are not followed: only the link itself is deleted.

## Headless mode

For Task Scheduler: the program is started with arguments and runs without a window.

```
FSLTool.exe scan | analyze | compact | clean [options]
```

| Command | What it does |
|---|---|
| scan | Scan: state, accounts, AD, size history recording |
| analyze | Analysis of free containers (read-only), updates the analysis cache |
| compact | Compacts free containers with a gain ≥ `--min-gain`; containers without analysis data are quickly measured first |
| clean | Cleanup by rules (+ compaction if enabled in settings) |

| Option | Meaning |
|---|---|
| --filter | `all`, `problem`, `critical`, `stale`, `orphan`, `disabled`, `compactable` (default `all`) |
| --user "mask" | Only users or files matching the mask, e.g. `"CONTOSO\a*"` |
| --where "name" | Only containers that pass a saved custom filter (the “Filter” button in the window, `filters.json`). Combines with `--filter` and `--user` (a container must pass all of them). The name is case-insensitive. If the filter does not exist, has no enabled condition, or an enabled condition has an error, the run does not start (code 2), so that not all containers get processed. The fields “Checked in the table” and “Operation in progress” are always “no” in headless mode. Has no effect for `scan` |
| --min-gain GB | For compact: minimum gain (default — the threshold from settings) |
| --no-measure | For compact: do not measure containers without analysis data (they are skipped) |
| --no-compact | For clean: do not compact after cleanup |
| --max-minutes N | Do not start a new container after N minutes |
| --max-count N | Process at most N containers |
| --dry-run | Only show the plan, change nothing |
| --lang uk, --lang en | Language of the console output, log and email report. Default: the language chosen in the app for this Windows user, else the Windows display language, else English (tasks running as SYSTEM usually get the server's display language) |
| --mail-to "addresses" | Email the report; separate multiple addresses with `;` or `,` |
| --mail-on when | `always` — always (default), `problems` — only if there is something to check, `errors` — only when the exit code is not 0 |
| --smtp server[:port] | SMTP server (default port 25) |
| --smtp-ssl | STARTTLS, e.g. port 587 (implicit TLS on port 465 is not supported) |
| --mail-from address | Sender (default `fsltool@<computer FQDN>`) |
| --smtp-user login | SMTP authentication; without it the message is sent anonymously |
| --smtp-password-env VARIABLE | Name of the environment variable that holds the SMTP password |
| --no-mail | Do not send the report even if it is configured in settings.json |

- Mounted containers are always skipped. The checks and the log (marked `[CLI]`) are the same as in the window.
- Only one headless instance can run at a time.
- Console help: `FSLTool.exe /?`. For running it by hand from cmd or PowerShell, see “Running manually from a console” below.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Errors with some containers |
| 2 | Invalid arguments or no paths configured |
| 3 | Fatal error |
| 4 | Cancelled |
| 5 | Another instance is already running |
| 6 | Work succeeded, but the email report was not sent |

Example: every night at 01:00, compact everything that reclaims ≥ 2 GB, for no more than 4 hours:

```
schtasks /create /tn "FSLTool compact" /sc daily /st 01:00 /ru SYSTEM /rl HIGHEST /tr "\"C:\Tools\FSLTool.exe\" compact --min-gain 2 --max-minutes 240"
```

The task must run with highest privileges. Under SYSTEM, network shares and AD are accessed as the computer account. If the profiles are on another server, use a service account with administrator rights.

### Running manually from a console

To test a command or run it once, you can start it by hand.

- The console must be opened **as administrator**. Otherwise Windows will not start the program: PowerShell reports “The requested operation requires elevation”.
- **cmd:** `start /wait FSLTool.exe clean --max-minutes 240`. Without `start /wait` the command prompt does not wait for completion and does not show the output.
- **PowerShell:** `.\FSLTool.exe clean --max-minutes 240 | Out-Default`. `| Out-Default` makes PowerShell wait for completion; the exit code is in `$LASTEXITCODE`. `start /wait` does not work in PowerShell: there `start` means `Start-Process`, which has a different syntax.
- **Cyrillic in PowerShell.** The program writes UTF-8, while PowerShell reads the output in the console code page by default (866 on Ukrainian systems), so Cyrillic text (Ukrainian output, user and folder names) appears garbled. The work and the log are not affected. To display it correctly, run this in the same window first:

```
[Console]::OutputEncoding = [Text.Encoding]::UTF8
.\FSLTool.exe clean --max-minutes 240 | Out-Default
```

To avoid typing the first line every time, add it to your PowerShell profile (`notepad $PROFILE`).

What the console shows: first the scan phases (looking for containers, VHD(X) headers, mount state, accounts and AD) and a summary; then for each container a `[N/total]` line, the operation stages (no percentages) and the result; finally the overall summary. On a big server the scan and single stages (deleting by rules, compaction) can take several minutes without new lines. Phases and `[N/total]` lines go only to the console and to the report's `.log` attachment; everything else is also written to the audit log.

### Email report

When a command finishes, the program can send a report email. It uses the program's colors (dark theme) and contains:

- a header with the run status (success, errors, cancelled, etc.), command, selection, start time, duration and exit code;
- operation result cards (for analyze, compact, clean): succeeded, skipped, errors, not started due to `--max-minutes`, how much the files shrank. For `--dry-run` the plan is shown instead;
- container state cards, as above the table in the window: total, overfull and near limit, mounted, orphans, disabled in AD, stale, worth compacting, limit ≤ 30 days;
- storage: usage, free space and containers for each path;
- details for each container: size before → after, time, result and message. Errors and skips come first;
- “Need attention”: containers with usage from the “Warning” threshold, with a forecast limit ≤ 30 days, or with a file system that needs a check;
- log warnings and errors for this run (the last 40).

Tables in the email are limited to 50 rows. Attached: the full run output (`.log`) and all containers (`.csv`, `;` delimiter, opens in Excel) if the scan managed to finish. If the run ended with a non-zero exit code, the email is marked as high importance.

Settings are read from the `mail` section of `%ProgramData%\FSLTool\settings.json` (they are not in the program window — edit the file); command-line options take precedence:

```
"mail": {
  "smtpHost": "smtp.contoso.local",
  "smtpPort": 25,
  "ssl": false,
  "from": "fsltool@contoso.com",
  "to": [ "admins@contoso.com" ],
  "user": "",
  "passwordEnv": "",
  "when": "always"
}
```

- The report is sent only when recipients are known (`--mail-to` or `mail.to`). If `mail.to` is set, **all** headless runs send the report; `--no-mail` disables it for an individual task.
- `--mail-on problems` sends the email if the exit code is not 0, there are skipped or not started containers, there are overfull containers or storage, or there are containers with a limit ≤ 30 days.
- The SMTP password is neither stored nor passed in arguments: specify the name of an environment variable (`--smtp-password-env` or `mail.passwordEnv`). For a task running as SYSTEM it must be a system variable (`setx /M`).
- Mail setting errors (no server, invalid address, password variable not set) are detected before any work starts: exit code 2, nothing is executed.
- If the email could not be sent, the error is written to the log. The exit code becomes 6 if the work itself succeeded; otherwise the work's exit code is kept.

Example: a daily scan that sends an email only when there are problems:

```
schtasks /create /tn "FSLTool scan" /sc daily /st 06:00 /ru SYSTEM /rl HIGHEST /tr "\"C:\Tools\FSLTool.exe\" scan --mail-to admins@contoso.com --smtp smtp.contoso.local --mail-on problems"
```

## Security

- The container state is checked again before every operation. A container that someone is using is never attached.
- Attachment is temporary: if the program crashes, Windows detaches the disk itself.
- Analysis and measurement are read-only.
- Dangerous actions require confirmation, where the default button is “Cancel”.
- `SeBackupPrivilege` and `SeRestorePrivilege` are enabled to bypass restrictive ACLs in profiles.
- Every action is written to the audit log with the administrator's name.

## Data files

All in `%ProgramData%\FSLTool`, shared by all administrators and by headless mode:

| File | Contents |
|---|---|
| settings.json | Paths, thresholds, window settings, mail for the headless report (`mail`) |
| cleanup-rules.json | Cleanup rules |
| vsize-cache.json | Maximum sizes from VHDX headers |
| analysis-cache.json | Analysis results (usage, folders, files, rule estimates, FS state) |
| history.json | Daily size history |
| filters.json | Saved custom filters (the “Filter” button next to search) |
| logs\fsltool-yyyyMMdd.log | Audit log |

## Troubleshooting

- **The volume does not appear after attaching.** Check the SAN policy (`diskpart` → `san`): with the `OfflineAll` policy, disks are attached Offline.
- **No host name in the “In session” state.** For paths on another server only “Locked” can be detected. Add the path as `\\THISSERVER\share` or as a local path.
- **Orphans are not flagged / no AD data.** The domain controller is unavailable; see the log for details.
- **“Log off” does not find the session.** Administrator rights and RPC access to the RDS host are required.
- **The size was increased but the partition was not extended.** The data is intact. Repeat the operation or extend the partition manually (`diskpart` → `extend`).
- **The email report does not arrive.** Look in the log for the line `[CLI] failed to send the report` (exit code 6). Check that the server accepts mail from this computer anonymously, or set `--smtp-user` and `--smtp-password-env`. For port 465 (implicit TLS), use 587 with `--smtp-ssl`.
- **What happened?** The “Log” button at the top of the window shows the log of the current session. The full log can be opened with the “Open logs” button.
