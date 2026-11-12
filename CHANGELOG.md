# Changelog

## 1.8.0
- The panel is always kept fully on screen: its position is re-checked after it opens and after every display change.
- The panel can be dragged by its header.
- The log records more detail about where the panel is placed.

## 1.7.0
- **Fixed:** the panel could be invisible or stuck off screen in mirror mode. The panel and screen numbers are now normal windows, with Windows 11 rounded corners.

## 1.6.0
- The panel is drawn with the CPU (software rendering) for reliability across graphics drivers.
- The screen size is read directly from Windows.

## 1.5.0
- The app restarts itself automatically if it stops responding.
- New **Restart Display Prompt** item in the tray menu.
- Every tray click opens a fresh panel.

## 1.4.0
- **Fixed:** the tray icon sometimes stopped opening the panel after switching modes.
- Errors are logged to `%LocalAppData%\DisplayPrompt\log.txt` instead of crashing the app.
- `DisplaySwitch.exe` is launched by its full path.

## 1.3.0
- New **Best quality** switch: mirror at the highest resolution both screens support.

## 1.2.0
- One-step `build.ps1`.
- New app icon (two stacked screens), used for the exe, the tray and the panel header.
- More reliable installer: waits for the running copy to close before replacing it.

## 1.1.0
- New screens default to **Mirroring**.
- **Extended** opens an arrange view: drag screens around the main screen, with snapping.
- A large screen number is shown in the bottom-left of each display while arranging.

## 1.0.0
- First release: pop-up with This PC only, Mirroring, Extended and Second screen only.
- 10-second auto-hide countdown and a Hide button.
- Tray icon, and installs for all users.
