# How Display Prompt works

A short tour of the code for anyone who wants to change or extend it.

Display Prompt is a single **WPF** app on **.NET 8**. It has no main window: it lives in the system tray and shows a small panel when needed. All display changes go through the same Windows APIs that **Win + P** and **Settings › Display** use.

```
 Monitor plugged in
        │
        ▼
 DisplayWatcher ──► count of connected displays went up?
        │                      │ yes
        │                      ▼
        │            App.OnNewDisplayConnected
        │              ├─ DisplayManager.Apply(Mirror)
        │              ├─ DisplayLayout.SetBestMirrorResolution()   (if Best quality is on)
        │              ├─ wait for the screens to settle
        │              └─ restart itself with --show  → fresh copy shows the panel
        ▼
 PopupWindow ──► user picks a mode ──► DisplayManager.Apply(mode)
                      │
                      └─ Extended ──► arrange view ──► DisplayLayout.ApplyPositions()
```

## Files

| File | What it does |
|---|---|
| `App.xaml.cs` | Startup, tray icon, single-instance lock, auto-mirror on connect, self-restart watchdog, error logging |
| `DisplayWatcher.cs` | Hidden window that listens for `WM_DISPLAYCHANGE` and monitor plug/unplug (`WM_DEVICECHANGE` for the monitor device class). Waits 1.5 s for Windows to settle, then compares the number of connected displays |
| `DisplayManager.cs` | Switches between the four modes with `SetDisplayConfig` topology flags (internal / clone / extend / external), reads the current mode with `QueryDisplayConfig`, and counts connected displays. Falls back to `DisplaySwitch.exe` if the API call fails |
| `DisplayLayout.cs` | Reads monitor positions and sizes, writes new positions with `ChangeDisplaySettingsEx`, snaps dragged screens edge-to-edge, and picks the best mirror resolution |
| `PopupWindow.xaml` / `.cs` | The panel: four mode tiles, Best quality switch, 10-second countdown, arrange view, and positioning in the bottom-right corner |
| `IdentifyWindow.cs` | The large screen number shown in the bottom-left of each physical screen while arranging |
| `AppSettings.cs` | Per-user settings in `HKCU\Software\Ctrl+Alt+Elite\DisplayPrompt` |
| `WindowStyling.cs` | Windows 11 rounded corners and border via `DwmSetWindowAttribute` |
| `Updater.cs` | App side of auto-update: starts the update task when the app starts, and restarts into a newer exe once it appears on disk |
| `updater.ps1` | Runs as SYSTEM from the update task: finds the latest GitHub release, verifies the download's SHA-256, and swaps in the new exe |
| `Log.cs` | Small rolling log at `%LocalAppData%\DisplayPrompt\log.txt` (kept under 512 KB) |
| `build.ps1`, `install.ps1`, `uninstall.ps1` | Build; install for all users and register the update task; remove both |

## Key design decisions

### Switching modes
`SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_*)` asks Windows to apply its own saved configuration for that topology, exactly like Win + P. This is more reliable than building display paths by hand, because Windows already knows each monitor's preferred settings.

### Detecting a *new* display
Windows sends several display messages for a single plug-in, and also sends them when the resolution changes. The watcher waits for 1.5 seconds of quiet, then only reacts if the number of **connected** displays (including inactive ones) went up. That stops the panel from popping up when someone just changes the resolution.

### Arranging screens
In Extended mode each monitor has a position on one large virtual desktop. The arrange view draws these to scale. When you drop a box, `SnapAll` slides it to the nearest edge of a neighbouring screen without overlapping, and re-attaches any screen left floating. **Apply** stages every position with `CDS_NORESET`, then commits them all at once so the screens don't flicker one at a time.

### Best mirror resolution
In clone mode both screens share one desktop. `SetBestMirrorResolution` lists the modes Windows offers for that shared desktop and picks the most pixels, using the highest refresh rate as a tie-break.

### Why the panel is a normal window (not see-through)
The first versions used a fully transparent (layered) window for custom rounded corners and a soft shadow. On some graphics drivers that kind of window stops redrawing, or ends up off screen, after a switch to mirror mode, and only an app restart fixed it. Since v1.7 the panel is a normal window. Windows 11 draws the rounded corners and border, and the app double-checks that the panel is fully on screen after every display change.

### Staying alive
A tray app that silently dies is worse than none, so:
- Unhandled errors are logged instead of crashing the app.
- A background watchdog restarts the app if its UI stops responding for 20 seconds.
- Every tray click builds a fresh panel, so a stuck panel can't block it.
- **After any display change, the app restarts itself** once the screens have settled. On some laptop and dock combinations, a copy of the app that was running while the screens switched to mirror can no longer place or draw its panel, while a freshly started copy always can. Restarting takes about a second and is invisible apart from the tray icon blinking.
- Half a second after the panel opens, the app checks that it is fully on screen. If it isn't, the app restarts and shows the panel again (once).

### Auto-update
The app lives in Program Files (admin-only) but runs as an ordinary user, so it can't replace itself. Instead `install.ps1` registers a scheduled task that runs `updater.ps1` as SYSTEM, with permissions that let any signed-in user *start* it but not change it. The app starts that task every time it launches. The updater replaces the exe by renaming the running file (Windows allows renaming, not overwriting, a running exe) and copying the new one in. The app checks the exe's version on disk once a minute and restarts into the newer one.

GitHub Actions stamps the release tag's version into the exe and publishes a `.sha256` file next to the zip, which the updater checks before installing.

## Adding a feature

- **New setting:** add a property to `AppSettings.cs`, then a control to `PopupWindow.xaml`.
- **Different default mode on connect:** change `DefaultModeOnConnect` in `App.xaml.cs`.
- **Different countdown:** change `AutoHideAfter` in `PopupWindow.xaml.cs`.
