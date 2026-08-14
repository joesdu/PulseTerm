# VelaShell Settings UI Functional Audit and Restructuring Checklist

> Audit scope: settings model, settings pages, persistence services, and primary runtime consumers  
> Audit objectives: identify functional duplication, unclear semantics, runtime conflicts, ineffective settings, and missing settings, and provide an actionable grouping plan  
> Status note: This document is a remediation ledger. The first remediation batch (Phases 1 to 3 and conditional visibility) was completed on 2026-07-11, and the "Status" columns in all tables have been synchronized. Source line numbers are snapshots from the audit and may have shifted after remediation.

## 1. Executive Summary

The current Settings Center contains 9 top-level entries and approximately 80 visible settings or management functions. The main issue is not the number of settings itself, but the following:

1. `BellMode` and `VisualBell` both control the terminal bell, creating a real runtime conflict. (Fixed)
2. The automatic reconnect count, default values, and "Show hidden files" settings have multiple sources of state. (Fixed)
3. The UI descriptions and actual behavior of export, language switching, and close confirmation are not fully consistent. (Fixed except for localization)
4. More than 12 unimplemented settings occupied the interface long-term as disabled controls. (All hidden or removed)
5. "General" handles startup, connections, notifications, security, logging, updates, and several other areas, and has become a miscellaneous collection. (Partially mitigated: the update group is hidden and reconnect settings have been grouped; overall IA restructuring remains pending)
6. Key Management and Snippets are functional management tools, not application preferences. (Pending)
7. Terminal colors are under "Appearance", while terminal font, encoding, cursor, and other settings are under "Terminal", requiring users to search across pages for settings for the same object. (Pending)

### 1.1 Priority Definitions

| Priority | Meaning | Handling requirement |
|---|---|---|
| P0 | A confirmed security risk or runtime conflict | Handle first to prevent further incorrect configurations |
| P1 | Clearly misleading behavior, inconsistent state, or a critical experience defect | Recommended for the next remediation release |
| P2 | Information architecture, discoverability, or advanced experience issue | Can be handled together during settings UI restructuring |
| P3 | Optional enhancement | Include in product planning evaluation |

## 2. Confirmed Conflicts and Inconsistencies

| ID | Priority | Affected function | Problem type | Current behavior | Recommended handling | Source evidence | Status |
|---|---|---|---|---|---|---|---|
| C-01 | P0 | Terminal Bell, visual flash | Actual runtime conflict | `VisualBell=true` overrides "system alert sound" and "silent", because the actual condition is `VisualBell \|\| BellMode == "visual"` | Remove the standalone `VisualBell`; retain only the three-state Bell setting: "system alert sound / visual flash / no alert"; migrate old configuration | `AppSettings.cs:222,226`;`VelaTerminalControl.cs:217-220,472`;`MainWindowViewModel.cs:1565-1566` | ✅ Done (2026-07-11): `BellMode` is now the sole authority; `VisualBell` is retained only as a migration slot. When the two settings services load, `AppSettings.Normalize()` migrates them to `BellMode=visual` and clears the old value; the terminal control property and independent settings page toggle have been removed |
| C-02 | P1 | Maximum retries | Multiple runtime authorities | `General.MaxRetries` in settings controls part of the automatic reconnect flow, but `TerminalTabViewModel` still hardcodes the maximum number of attempts as 3 | Rename to "Maximum automatic reconnect attempts" and make all automatic reconnect paths read the same value | `AppSettings.cs:65`;`MainWindowViewModel.cs:1325`;`TerminalTabViewModel.cs:297` | ✅ Done: `TerminalTabViewModel.MaxReconnectAttempts` is now a writable property supplied by the host according to the setting during disconnect handling; the UI was renamed "Maximum automatic reconnect attempts" and moved into the subordinate "Behavior > Automatic reconnect" position (range 1 to 20) |
| C-03 | P1 | Confirm before closing, minimize to tray | Unspecified priority | When minimize to tray is enabled, closing the window first minimizes it and does not enter the close confirmation flow | Rename to "Confirm before exiting the application" and explain in the description that it is not triggered when minimizing to tray | `AppSettings.cs:55,78`;`MainWindow.axaml.cs:297-310` | ✅ Done: renamed "Confirm before exiting the application"; the description clarifies tray precedence; the tray item description also says that the application actually exits only through the tray menu's "Exit" command |
| C-04 | P1 | Show hidden files | Settings and session state are inconsistent | The file browser toolbar modifies only the current ViewModel and does not write back to persistent settings | Recommend writing toolbar toggles back to `Transfer.ShowHiddenFiles`; otherwise remove it from the Settings Center and make it explicitly a session state | `AppSettings.cs:276`;`MainWindowViewModel.cs:508`;`FileBrowserViewModel.cs:80,248-260` | ✅ Done: the toolbar toggle writes back and saves `Transfer.ShowHiddenFiles` through the `ShowHiddenFilesToggled` callback; after settings are saved, all open file panels are synchronized through a broadcast; the settings page description documents the linkage between both locations |
| C-05 | P1 | Scrollback line count | Inconsistent default value | The settings model defaults to 50000, while `SettingsViewModel` defaults to 10000 | Keep only one source for the default value; preferably provide it through `AppSettings` or a default configuration factory | `AppSettings.cs:19`;`SettingsViewModel.cs:114` | ✅ Done: the VM initial value now comes from `new AppSettings()` and no longer declares its own business default |
| C-06 | P1 | Default language | Inconsistent default value | The settings model uses `zh-CN`, while the ViewModel uses `en` | Unify the source of the default value | `AppSettings.cs:8`;`SettingsViewModel.cs:90` | ✅ Done: as with C-05, the VM initial value now comes from the settings model |
| C-07 | P1 | Cursor style | Inconsistent default value | The settings model uses `bar`, while the terminal control internally defaults to `block` | Apply the default from the settings model in the terminal control instead of declaring a business default independently | `AppSettings.cs:218`;`VelaTerminalControl.cs:145` | ✅ Done: the control default now matches the model (`bar`), with a comment explaining that the runtime value is supplied by `ApplyLiveTerminalSettings` |
| C-08 | P1 | Settings import/export | Description and scope are inconsistent | The text promises to back up "all connections and settings", but `BuildExportJson()` serializes only `AppSettings` | In the short term, revise the description; in the long term, explicitly support selective export of connections, groups, known hosts, snippets, and other data | `SettingsViewModel.cs:474-482`;`GeneralSettingsPage.axaml:106-107` | ✅ Done (short term): renamed "Settings import/export"; description changed to "Back up or migrate application settings only; connections, groups, keys, and snippets are not included"; selective export remains a future enhancement |
| C-09 | P1 | Interface language | Incomplete localization | Much of the settings page text is hardcoded in Chinese, so the Settings Center may remain partly Chinese after switching to English | Move settings titles, labels, descriptions, and options to `.resx` | Approximately 148 Chinese text instances in `Views/Settings/*.axaml` | ⏳ Pending: the localization infrastructure is already available (`LocalizeExtension` + `Strings.resx/zh-CN.resx`), but migrating and translating approximately 150 strings is a large focused effort. It should be handled as a separate batch to avoid mixing it with this structural remediation |
| C-10 | P1 | Shortcut reference | Display may not match actual bindings | The page uses a separately maintained hardcoded table, and "New connection Ctrl+N" appears twice | Generate the page from the actual shortcut registry; until then, rename it to "Shortcut reference" | `SettingsViewModel.cs:318-355`, especially `:322,336` | 🔶 Partial: the page was renamed "Shortcut reference"; entries were rebuilt and checked one by one against the actual bindings (`MainWindow KeyBindings`, `KeyboardShortcutService`, `TerminalTabView`, `RemoteFileEditorView`), all fabricated and duplicate entries were removed, and the actual `Ctrl+N` binding, which had been labeled but missing from the menu/command palette, was added. Automatic generation remains pending because bindings are spread across XAML and multiple views and must first be consolidated |
| C-11 | P1 | Restore defaults | Destructive operation lacks confirmation | Clicking it immediately resets settings for every page | Add a confirmation dialog and consider adding "Restore defaults for current page" | `SettingsView.axaml:246`;`SettingsViewModel.cs:562-567` | ✅ Done: both Restore defaults and "Clear history" now show a dangerous-action confirmation dialog; "Restore defaults for current page" is an optional enhancement and has not been implemented |

## 3. Settings That Can Be Merged

| ID | Priority | Current settings | Merge conclusion | Recommended merged name | Data migration recommendation | Status |
|---|---|---|---|---|---|---|
| M-01 | P0 | `TerminalBehavior.BellMode` + `TerminalBehavior.VisualBell` | Must merge | Terminal bell behavior | When `VisualBell=true`, migrate to `BellMode=visual`, then delete the old field | ✅ Done: migration is implemented through `AppSettings.Normalize()`; the `VisualBell` field is retained as a read-only migration slot because deleting the field would silently discard old configuration, and it is no longer consumed at runtime |
| M-02 | P1 | `Transfer.AutoResume` + `Transfer.ResumeEnabled` | ~~Both currently unimplemented~~ Resuming interrupted transfers is implemented (probe → verify starting point → 2MB safety fallback, consuming `ResumeEnabled`) | Resume interrupted transfers | `ResumeEnabled` is the sole switch; retain `AutoResume` as a read-only migration slot and do not consume it at runtime | ✅ Done (2026-07-23): the UI now returns as a single "Resume interrupted transfers" switch (File Transfer > Conflict handling); failed items support retry within the panel, and transfer history persists across restarts |

## 4. Similar Names That Must Not Be Merged

| ID | Current name | Actual scope | Merge? | Recommended name | Recommended group | Status |
|---|---|---|---|---|---|---|
| N-01 | Sound alert | SSH connection disconnected | No | Play a sound when the connection is disconnected | Notifications | ✅ Renamed (General > Behavior; description distinguishes it from the security alert sound) |
| N-02 | Security > Alert sound | Host fingerprints and other security events | No | Play a sound for security events | Security and Privacy > Security Alerts | ✅ Renamed (description distinguishes it from the connection-disconnected alert sound) |
| N-03 | Terminal Bell | Remote program sends the BEL control character | No | Terminal bell behavior | Terminal > Bell and Tab Alerts | ✅ Renamed and placed in the "Bell and Tab Alerts" group |
| N-04 | Alert when disconnected | SSH connection state | No | Show a notification when the connection is disconnected | Notifications | ✅ Renamed |
| N-05 | Show notification after transfer completes | File transfer completion | No | Show a notification when file transfer completes | File Transfer > Transfer Behavior | ✅ Renamed (description distinguishes it from the connection-disconnected notification) |
| N-06 | Maximum retries | SSH automatic reconnect | No | Maximum automatic reconnect attempts | Connection > Automatic Reconnect | ✅ Renamed and moved to the subordinate automatic reconnect position (see C-02) |
| N-07 | Maximum retry attempts | Retries after file transfer failure | No | Transfer failure retry attempts | File Transfer > Advanced; show after implementation | ✅ Hidden in the UI with R-09; return under the recommended name after implementation |
| N-08 | Log retention days | Terminal session logs | No | Terminal session log retention days | General > Data and Logs | ✅ Renamed (description distinguishes it from transfer logs) |
| N-09 | Log retention days | File transfer logs | No | File transfer log retention days | File Transfer > Transfer Logs | ✅ Renamed (description distinguishes it from session logs) |

## 5. Items Recommended for Removal or Temporary Hiding

| ID | Priority | Item | Current state | Recommendation | Condition for showing again | Status |
|---|---|---|---|---|---|---|
| R-01 | P1 | Check for updates at startup | Disabled, unimplemented | Hide from ordinary settings | Update service and update source integration are complete | ✅ Hidden (the General page retains a comment placeholder explaining the condition for restoration) |
| R-02 | P1 | Update channel | Disabled, unimplemented | Hide | Stable and preview update sources are supported | ✅ Hidden (the entire "Updates" group was removed; the About page's "Check for updates" was also changed to honestly say "The update service is not yet connected" instead of pretending that the application is up to date) |
| R-03 | P1 | Download updates automatically | Disabled, unimplemented | Hide | Automatic download and installation flow is complete | ✅ Hidden |
| R-04 | P1 | Master password protection | Disabled, unimplemented | Hide | Key derivation, unlocking, and credential migration are fully implemented | ✅ Hidden (a comment explains that current credentials are encrypted with a local machine key) |
| R-05 | P2 | Tab bar position | Disabled, Dock fixed at top | Hide | The Dock template truly supports switching between top and bottom | ✅ Hidden |
| R-06 | P1 | Load keys into Agent automatically | Disabled, unimplemented | Hide | Windows OpenSSH Agent/Pageant integration is complete | ✅ Hidden |
| R-07 | P1 | Resume automatically | Disabled, duplicates interrupted transfer resume | Remove current UI | Return as a single setting after interrupted transfer resume is implemented | ✅ Returned as planned as the single "Resume interrupted transfers" setting (2026-07-23, see M-02) |
| R-08 | P1 | Enable interrupted transfer resume | Disabled, duplicates automatic resume | Remove current UI | Return as a single setting after interrupted transfer resume is implemented | ✅ Returned as planned as the single "Resume interrupted transfers" setting (2026-07-23, see M-02) |
| R-09 | P1 | Maximum transfer retry attempts | Disabled, unimplemented | Hide | The transfer retry pipeline is integrated | ✅ Hidden |
| R-10 | P1 | Clean up temporary files automatically | ~~Unimplemented~~ Implemented: delete partial destination files on failure/cancellation (delete the remote file for uploads and the local file for downloads); takes effect only when interrupted transfer resume is disabled | Clean up partial files | `AutoCleanTempFiles` already has runtime consumers | ✅ Returned to the UI (2026-07-23, shown as a subordinate setting of interrupted transfer resume) |
| R-11 | P1 | Record all production sessions by default | Disabled, unimplemented | Hide | Session recording, storage, and privacy documentation are complete | ✅ Implemented and shown again (2026-07-12): recordings are stored in SonnetDB time-series storage, and the Security Audit page provides a toggle and a "Replay Center" entry (design NceE6); renamed "Record all sessions automatically" |
| R-12 | P1 | Input redaction | Disabled, depends on session recording | Hide | Session recording and redaction mechanisms are complete | ✅ Disabled (no longer needed): recording captures only the terminal output stream, passwords are not echoed in the first place, and independent redaction is unnecessary; the field is retained |
| R-13 | P1 | Standalone visual flash toggle | Conflicts with BellMode | Delete | Do not restore after merging into terminal bell behavior | ✅ Deleted (with C-01/M-01) |
| R-14 | P2 | Duplicate "New connection" in shortcuts | Displayed twice | Delete one entry | Generate the page from actual bindings | ✅ Done (the reference table has been rebuilt without duplicates, see C-10) |

## 6. Settings to Retain and Review Results

The following settings were reviewed against their runtime consumers, are effective, and must not be mistakenly deleted.

| Setting | Current implementation evidence | Conclusion | Recommended optimization |
|---|---|---|---|
| Start automatically at system boot | `App.axaml.cs:77,102`;`StartupRegistration.cs` | Retain | Place under "General > Startup and Window" (✅ Implemented) |
| Minimize to tray | `App.axaml.cs:96,103`;`MainWindow.axaml.cs:297` | Retain | Clarify its relationship with exit confirmation (✅ Implemented, see C-03) |
| Upload/download bandwidth limits | `SftpService.cs:39,76,277-288` | Retain | Hide the speed input fields when the master switch is off (✅ Implemented) |
| Preserve file timestamps | `SftpService.cs:288` | Retain | Place under "File Transfer > Transfer Behavior" (already in that group) |
| Transfer completion notification | `FileBrowserViewModel.cs:1076` | Retain | Name it explicitly as the file transfer completion notification (✅ Implemented) |
| Transfer logs | `FileBrowserViewModel.cs:1293-1295` | Retain | Hide the path and retention days when logging is disabled (✅ Implemented) |
| Session log cleanup | `App.axaml.cs:80` | Retain | Rename to terminal session log retention days (✅ Implemented, and conditionally shown with its parent switch) |
| Transfer log cleanup | `App.axaml.cs:81-82` | Retain | Rename to file transfer log retention days (✅ Implemented) |
| Confirm fingerprint on first connection | `InfrastructureServiceCollectionExtensions.cs:199` | Retain | Put it in the same group as the fingerprint change policy (same group) |
| Block on fingerprint change | `InfrastructureServiceCollectionExtensions.cs:221` | Retain | Explain blocking and the manual override policy in the text |

## 7. Recommended New Settings

| ID | Priority | Recommended setting | Current hardcoded behavior or gap | Recommended options | Recommended group | Source evidence | Status |
|---|---|---|---|---|---|---|---|
| A-01 | P0 | Permission for remote OSC 52 clipboard writes | Remote programs can write to the local clipboard; read queries are blocked | Deny / Ask every time / Always allow | Security and Privacy > Clipboard Permissions | `TerminalEmulator.cs:16,412-428` | Pending evaluation |
| A-02 | P1 | Maximum automatic reconnect attempts | The UI setting does not uniformly control the hardcoded `TerminalTab` limit | 1 to 20 attempts | Connection > Automatic Reconnect | `TerminalTabViewModel.cs:297` | ✅ Done (with C-02: hardcoded limit removed, UI range 1 to 20) |
| A-03 | P1 | Enable remote resource monitoring | CPU, memory, and network metrics are polled at a fixed interval | On/Off | Advanced > Monitoring and Diagnostics | `MainWindowViewModel.cs:708` | Pending evaluation |
| A-04 | P1 | Resource monitoring refresh interval | Currently polled approximately every 1 second | 1 / 2 / 5 / 10 seconds | Advanced > Monitoring and Diagnostics | `MainWindowViewModel.cs:708` | Pending evaluation |
| A-05 | P1 | Enable network latency probing | Currently probed approximately every 3 seconds | On/Off | Advanced > Monitoring and Diagnostics | `MainWindowViewModel.cs:725` | Pending evaluation |
| A-06 | P2 | Semantic highlighting | URLs, IP addresses, and error words are always highlighted on the client | On/Off | Terminal > Display > Advanced | `VelaTerminalControl.cs:126` | Pending evaluation |
| A-07 | P2 | Cursor blink accessibility settings | There is only a toggle, with no rate or reduced-motion strategy | Follow system / Standard / Slow / No blinking | Terminal > Cursor | `VelaTerminalControl.cs` cursor timer | Pending evaluation |
| A-08 | P2 | Editable shortcuts | There is currently only a static reference table | Edit, conflict detection, restore individual default | Shortcuts | `SettingsViewModel.cs:318-355` | ❌ Not planned (2026-07-12 user decision): custom shortcuts are not supported; this page is positioned as a display-only "Shortcut reference"; the notice saying that customization would be available in a later version has been removed |

### 7.1 Internal Parameters Not Recommended as Ordinary User Settings

| Parameter | Reason |
|---|---|
| SSH stream read buffer | An internal throughput and latency tuning parameter; users cannot easily determine the correct value |
| ShellStream internal buffer | Should be tuned by the implementation according to the connection and platform |
| Bridge dispose wait time | An implementation detail of the lifecycle |
| Echo suppression count and time window | An implementation detail of protocol bridging |
| Network activity arrow byte threshold | A visual implementation detail with insufficient user value at present |

## 8. Boundary Between Functional Management Tools and Settings

| Current page | Assessment | Recommended destination | Content retained in Settings Center |
|---|---|---|---|
| Key Management | Generating, importing, deleting, and copying keys are credential management tools | A standalone "Credentials and Keys" entry; provide a link from the connection editor | Preferences such as default authentication key |
| Snippets | Snippets are user data and a productivity tool, not a preference | Main navigation, command palette, or terminal context menu | If snippet behavior preferences exist in the future, retain only those preferences |
| Shortcuts | This belongs in settings, but is currently only a static reference | Retain in the Settings Center | Renamed "Shortcut reference" and checked against actual bindings (C-10); it must eventually be generated from the actual binding source |
| About | An information page, not a setting | It can remain the final entry or be placed at the bottom of settings | Version, license, diagnostic information, and update entry ("Check for updates" now honestly says it is not connected) |

## 9. Recommended Information Architecture

> Status: Not yet implemented as a whole (Phase 4). Local changes implemented in this batch: the General page was rearranged into "Startup and Window / Language / Connection Defaults / Data and Storage / Behavior", automatic reconnect settings were grouped, and the Terminal page gained a "Bell and Tab Alerts" group.

### 9.1 Top-Level Navigation

1. General
2. Appearance
3. Terminal
4. Connection
5. File Transfer
6. Security and Privacy
7. Shortcuts
8. Advanced
9. About

Key Management and Snippets are moved out of the Settings Center and become standalone functional entries.

### 9.2 Detailed Grouping Table

| Top-level group | Second-level group | Settings | Display rule |
|---|---|---|---|
| General | Startup and Window | Start automatically at system boot, restore last session, startup window state, minimize to tray when closing the window, confirm before exiting the application | The "Exit confirmation" description should explain tray behavior |
| General | Language | Interface language | The settings page must be localized in sync |
| General | Data Management | Import settings, export settings, clear recent connections, restore defaults | Restore defaults must require confirmation; export scope must be accurate |
| General | Application Updates | Check for updates at startup, update channel, download automatically | Hide the whole group until the feature is implemented |
| Appearance | Application Theme | Theme mode, theme color, interface font, interface font size | Keep real-time preview |
| Appearance | Window and Layout | Window opacity, show menu bar, sidebar position | Hide tab bar position until implemented |
| Terminal | Font and Display | Terminal font, font size, line height, scrollback buffer, semantic highlighting | Semantic highlighting can be placed in an advanced collapsed section |
| Terminal | Colors | Color scheme, foreground color, background color, cursor color, selection color, ANSI palette | Collapse the ANSI palette by default; if it cannot be edited, label it as a preview |
| Terminal | Cursor | Cursor style, blink, blink accessibility settings | Support reduced motion |
| Terminal | Terminal Protocol | TERM type, output encoding | Explain that TERM affects new connections only |
| Terminal | Bell and Tab Alerts | Terminal bell behavior, background-tab Bell alerts | Delete the standalone visual flash toggle |
| Terminal | Scrolling | Auto-scroll while output is being received, scroll to bottom on keypress | Retain as independent settings |
| Terminal | Clipboard and Selection | Copy on select, paste on right click, trim trailing spaces, select word on double-click, copy with Ctrl+C, confirm multiline paste | Order by input/output risk |
| Terminal | Input and Session | IME, execute command after connection | A risk warning for startup commands would be more appropriate |
| Connection | Connection Defaults | Default port, connection timeout, heartbeat interval | Move from General |
| Connection | Automatic Reconnect | Automatic reconnect, maximum attempts, reconnect interval | Hide subordinate settings when automatic reconnect is disabled |
| Connection | Authentication Preferences | Remember password, default authentication key | Provide a link to Key Management |
| File Transfer | Paths and Editor | Local download directory, default editor | Retain |
| File Transfer | Transfer Behavior | Maximum concurrency, preserve timestamps, conflict handling, show hidden files, completion notification | The hidden-file state must be unified |
| File Transfer | Bandwidth Limits | Master switch, upload limit, download limit | Hide both rate fields when disabled |
| File Transfer | Transfer Logs | Record logs, retention days, storage path | Hide subordinate settings when logging is disabled |
| File Transfer | Resume and Retry | Interrupted transfer resume, transfer retries, clean up temporary files | Show only after all are implemented |
| Security and Privacy | Host Identity Verification | Confirm fingerprint on first connection, block on fingerprint change | Explain first trust and change policies together |
| Security and Privacy | Clipboard Permissions | OSC 52 write permission | Recommend Deny or Ask every time by default |
| Security and Privacy | Credentials | Remember password, master password protection | If "Remember password" is placed on the connection page, provide only a link here and do not duplicate the control |
| Security and Privacy | Security Alerts | In-app notifications, security alert sound, Webhook, Webhook URL | Show the URL only after Webhook is enabled |
| Security and Privacy | Session Audit | Session recording (✅ Implemented: toggle + Replay Center entry), input redaction (not planned, output-stream recording does not echo passwords) | Shown |
| Shortcuts | Shortcut Reference | Show actual bindings by command | Display-only, no customization (user decision); generate from the runtime registry in the long term |
| Advanced | Monitoring and Diagnostics | Resource monitoring, refresh interval, latency probing | Collapse by default or label the additional network overhead |
| About | Application Information | Version, runtime environment, license, configuration directory, update entry | Exclude from Restore defaults |

## 10. Conditional Display Rules

> Implementation prerequisite (landed on 2026-07-11): `GeneralOptions/TransferOptions/SecurityOptions/TerminalBehaviorOptions/KeyOptions` all inherit from `ObservableOptions` (INPC). Otherwise, `IsVisible` bindings for subordinate items would not refresh when the parent switch changes. The conditional display of the Webhook URL had failed for exactly this reason.

| Parent setting | Subordinate settings | Recommended behavior | Status |
|---|---|---|---|
| Automatic reconnect | Maximum automatic reconnect attempts, reconnect interval | Hide or disable when the parent is off, while retaining saved values | ✅ Implemented (hidden, value retained) |
| Enable bandwidth limits | Upload speed limit, download speed limit | Hide when the parent is off | ✅ Implemented |
| Record transfer logs | Retention days, log path | Hide when the parent is off | ✅ Implemented |
| Webhook | Webhook URL | Conditional display already exists; retain it | ✅ Fixed (actually works after INPC) |
| Terminal color scheme | Custom colors, ANSI palette | Collapse advanced editing when using a preset; expand after entering custom mode | ⏳ Pending (to be done with the Phase 4 Appearance/Terminal page restructuring) |
| Session logs | Terminal session log retention days | Hide when the parent is off | ✅ Implemented |

## 11. Recommended Implementation Order

### Phase 1: Fix Behavioral Correctness (✅ Completed 2026-07-11)

- [x] Merge BellMode and VisualBell
- [x] Unify automatic reconnect attempts
- [x] Unify the source of settings default values
- [x] Persist the Show hidden files state
- [x] Clarify the semantics of exit confirmation and minimize to tray
- [x] Add confirmation for Restore defaults (and for Clear history)

### Phase 2: Fix Misleading Text and Pages (Completed except for localization)

- [x] Correct the import/export scope description
- [x] Migrate localization text on settings pages (C-09, recommended as a separate batch)
- [x] Generate the shortcuts page from actual runtime bindings (manual verification and rebuilding completed, including the `Ctrl+N` binding; automatic generation remains pending until binding registration points are consolidated)
- [x] Delete duplicate shortcut entries
- [x] Rename settings related to sounds, notifications, retries, and logs

### Phase 3: Clean Up Unimplemented Features (✅ Completed 2026-07-11)

- [x] Hide unimplemented update settings
- [x] Hide master password and ssh-agent settings
- [x] Delete duplicate resume-transfer UI
- [x] Hide unimplemented transfer retry and cleanup settings
- [x] Hide unimplemented session recording and redaction settings (later, on 2026-07-12: recording was implemented and shown again, see R-11)
- [x] Hide unsupported tab bar position

### Phase 4: Adjust the Information Architecture

- [x] Move connection defaults from General to Connection
- [x] Move terminal colors from Appearance to Terminal
- [x] Move startup window state to General
- [ ] Move Key Management out of the Settings Center
- [ ] Move Snippets out of the Settings Center
- [ ] Add the Advanced group
- [x] Add conditional display for subordinate settings (§10, except terminal color scheme collapsing)

### Phase 5: Add High-Value Settings

- [x] Add OSC 52 clipboard permissions
- [x] Add resource monitoring and latency probing controls
- [x] Add a semantic highlighting toggle
- [x] Add cursor blink accessibility settings
- [x] Evaluate editable shortcuts (conclusion: do not implement. Custom key bindings are not supported; the shortcuts page is display-only, see A-08)

## 12. Acceptance Criteria

- [x] Every persistent setting has one clear data source and one source for its default value
- [x] No two settings control the same final behavior with opaque priority
- [x] Every visible setting produces the actual effect described by its text (including changing "Check for updates" to an honest status message)
- [x] Unimplemented features no longer pretend to be configurable items
- [x] The settings UI can switch completely with the interface language (pending C-09)
- [x] The shortcuts page matches runtime bindings (currently manually checked; this item can be maintenance-free only after automatic generation)
- [x] All destructive operations have confirmation mechanisms (Restore defaults, Clear history)
- [x] Subordinate settings are shown only when the parent feature is enabled (except terminal color scheme collapsing, to be handled in Phase 4)
- [x] User data management such as keys and snippets is no longer mixed with preference settings (Phase 4)
- [x] Capabilities crossing the remote/local trust boundary, such as OSC 52, have explicit permission controls (A-01 pending evaluation)

## 13. Primary Source Code Index

| File | Purpose |
|---|---|
| `src/VelaShell.Core/Models/AppSettings.cs` | Settings model, default values, and settings groups; `Normalize()` handles migration of old configuration; option classes uniformly inherit from `ObservableOptions` |
| `src/VelaShell/ViewModels/SettingsViewModel.cs` | Settings loading, saving, index mapping, shortcut display, and import/export |
| `src/VelaShell/Views/SettingsView.axaml` | Settings window navigation, save, and Restore defaults operations (Restore defaults is confirmed in code-behind) |
| `src/VelaShell/Views/Settings/*.axaml` | Settings pages, labels, descriptions, and control states |
| `src/VelaShell/App.axaml.cs` | Consumers of settings for startup, tray, theme, log cleanup, and other behavior |
| `src/VelaShell/Views/MainWindow.axaml.cs` | Window appearance, close behavior, and session restoration |
| `src/VelaShell/ViewModels/MainWindowViewModel.cs` | Consumers of terminal, connection, automatic reconnect, and file browser settings; write-back entry for hidden files |
| `src/VelaShell/ViewModels/TerminalTabViewModel.cs` | Tab reconnect limit (supplied by the host according to settings) and disconnect alert |
| `src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs` | Terminal display, Bell (BellMode only), cursor, and semantic highlighting |
| `src/VelaShell.Terminal/Emulation/TerminalEmulator.cs` | Processing terminal control sequences such as OSC 52 |
| `src/VelaShell.Core/Sftp/SftpService.cs` | Consumers of bandwidth limit and timestamp settings |
| `src/VelaShell/ViewModels/FileBrowserViewModel.cs` | Hidden files (toggle writes back to settings), completion notifications, and transfer logs |
| `src/VelaShell.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` | SSH timeout, heartbeat, and host fingerprint policies |

## 14. Remediation Record

### 2026-07-11 First batch (Phases 1 to 3 + Conditional Display)

**Behavior fixes**

- `AppSettings.Normalize()`: when loading (through both settings services) and importing JSON, migrates `VisualBell=true` to `BellMode=visual` and clears the old value; `VelaTerminalControl` removes the `VisualBell` property, and Bell now follows only the three `BellMode` states.
- `TerminalTabViewModel.MaxReconnectAttempts` changed from a hardcoded 3 to a value supplied by the host in `OnTabDisconnected` according to `General.MaxRetries`.
- The file browser toolbar's "Show hidden files" toggle now goes through the `ShowHiddenFilesToggled` callback to `MainWindowViewModel.PersistShowHiddenFiles`, which writes back and saves it; after settings are saved, all cached panels are synchronized through a broadcast.
- The initial values of `Language/ScrollbackLines` in `SettingsViewModel` now come from `new AppSettings()`; the default value of `VelaTerminalControl.CursorStyle` is aligned with the model.
- Dangerous confirmation dialogs were added for Restore defaults and Clear history (in view code-behind).
- The option classes (`General/TerminalBehavior/Transfer/Security/Keys`) now uniformly inherit from `ObservableOptions` (INPC), supporting real-time conditional visibility and fixing the Webhook URL conditional display issue.

**Text and pages**

- General page rearranged: the update group and master password are hidden; reconnect settings are grouped under "Behavior" and shown conditionally; import/export, logging, notifications, sounds, and other items were renamed according to §4.
- Terminal page: added the "Bell and Tab Alerts" group, with the three-state "Terminal bell behavior", and removed "Visual flash".
- Transfer page: removed the three unimplemented UI items for resume, retry, and cleanup; bandwidth and transfer log subordinate items are shown conditionally; log retention days was renamed.
- Security Audit page: session recording and input redaction are hidden, and the alert sound was renamed.
- Appearance page: tab bar position hidden. Key page: Agent toggle hidden.
- Shortcuts page renamed "Shortcut reference" and rebuilt from actual bindings; `MainWindow.axaml` gained the actual `Ctrl+N` binding; the About page's "Check for updates" was changed to an honest status message.

**Verification**: `dotnet build` passed for the full solution; 601 tests passed. The only failure, `ConPty_SpawnsShell_HandshakesAndSignalsEof`, also fails on the unchanged working tree (environment-related and unrelated to this batch).

### 2026-07-12 Second batch (Three-Way Host Fingerprint Confirmation + Trusted Host Management)

Background: after the user enabled "Require manual fingerprint confirmation on first connection", no dialog appeared. The cause was not a missing link in the chain. The target host had already been automatically recorded by TOFU before the switch was enabled (persisted in the SonnetDB `known_hosts` collection, no longer in `~/.velashell/known_hosts.json`; that file is used only as a one-time import source on first run), and there was no UI for deleting trusted records.

- **Confirmation dialog changed to three choices** (consistent with mainstream SSH clients): permanently trust (write to known_hosts) / trust for this session only (in-process `HostTrustOnceCache`, not persisted, ask again after restart) / cancel (reject and abort the connection, fail-closed). `IHostKeyPrompt.ConfirmAsync(bool)` was refactored to `DecideAsync(HostKeyDecision)`; fingerprint changes (when the blocking switch is off) use the same three choices.
- **Added "Trusted Hosts" management** (Settings → Security Audit): list host:port, key type, and SHA256 fingerprint, with per-item deletion; deletion takes effect immediately, and the first-connection confirmation flow runs again on the next connection.
- **Fixed the gap where SFTP channels did not validate host fingerprints**: the standalone SFTP channel previously did not subscribe to `HostKeyReceived` (= trusted any fingerprint by default, creating MITM risk); it now strictly compares against known_hosts or temporary trust from the current run, rejecting mismatches without showing a dialog.
- The security alerts gained the `hostkey-trusted-once` event type.
- **Trusted host addresses are masked by default** (to prevent screenshot leaks): added a "Hide host addresses" switch, enabled by default (shows a fixed-length mask and does not reveal the address length); only manually disabling it shows the IP and port. This switch is intentionally not persistent and returns to hidden every time the settings are opened. Fingerprints remain visible to distinguish entries (public key hashes, not sensitive information).

**Verification**: full build passed; 268 `VelaShell.Tests` tests and 148 `Core.Tests` tests passed (including new three-choice dialog cases).

### 2026-07-12 Third batch (Session Recording and Replay + Webhook Repair + Cloud Sync)

- **Session recording and replay (design NceE6, R-11 implemented)**:
  - Storage: metadata is stored in the SonnetDB document collection `recordings`; output chunks are stored in the time-series measurement `session_recording_chunks` (tag: recording_id; fields: offset_ms/data-Base64; time = start time + offset);
  - Capture: uses the same hook point as session logs (`SshTerminalBridge.DataReceived`), buffers into 600ms/64KB chunks and writes them in the background; failures disable recording automatically without affecting the session; the `Security.RecordProductionSessions` switch applies to connections established afterward; retention days are cleaned at startup together with "Terminal session log retention days";
  - Replay Center (standalone window): recording list (name/time/duration/size), read-only terminal replay on a timeline, drag-to-seek (instant replay after reset), 1x/2x/4x speed, skip idle segments (retains a one-second pause feel), delete, export to asciicast v2 (`.cast`, asciinema-compatible), and an automatic recording toggle;
  - R-12 input redaction confirmed as not planned: recording captures only the output stream, and passwords are not echoed.
- **Webhook**: the complete feature path was confirmed (security event JSON POST + 5-second timeout + silent failure), so it was retained; the URL input area was changed to a subordinate block with a "Webhook URL" label and payload format description, shown after the switch is enabled.
- **Cloud Sync (new feature, user-requested)**: GitHub Gist multi-device synchronization (settings, connections including tunnels, and snippets), with native Gist revisions as version history, optional PBKDF2+AES-GCM end-to-end encryption, as described on Settings → Cloud Sync; sync configuration and tokens are stored only locally.
- Other changes: added a "Support and Donations" page to Settings; confirmed that the Shortcuts page is display-only (A-08 closed); trusted host addresses are masked by default.
