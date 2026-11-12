# Deploying Display Prompt to many PCs

A guide for IT staff rolling Display Prompt out to classrooms, meeting rooms or a fleet of laptops.

## What gets installed

| Item | Location |
|---|---|
| App | `C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\DisplayPrompt.exe` (one self-contained file, about 150 MB) |
| Start at sign-in (all users) | `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` → `DisplayPrompt` |
| Per-user setting | `HKCU\Software\Ctrl+Alt+Elite\DisplayPrompt` → `BestQuality` (DWORD, default 1) |
| Per-user log | `%LocalAppData%\DisplayPrompt\log.txt` |

The app runs as the signed-in user and needs **no admin rights** to run. Only installing to Program Files needs admin. It makes **no network connections**.

## Option 1: the install script (a few PCs)

Copy `DisplayPrompt.exe`, `install.ps1` and `uninstall.ps1` to the PC. Then, in an **admin** PowerShell in that folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

The script stops any running copy, copies the exe, adds the startup entry, and starts the app.

## Option 2: Intune (Win32 app)

1. Put `DisplayPrompt.exe`, `install.ps1` and `uninstall.ps1` in one folder.
2. Package it with the [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool):
   ```
   IntuneWinAppUtil.exe -c <folder> -s install.ps1 -o <output>
   ```
3. In Intune, add a **Windows app (Win32)**:
   - **Install command:** `powershell.exe -ExecutionPolicy Bypass -File .\install.ps1`
   - **Uninstall command:** `powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1`
   - **Install behaviour:** System
   - **Detection rule:** File exists: `C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\DisplayPrompt.exe`

When installed as **System**, the script's final step (starting the app) runs in the System session and does nothing visible. Users get the app at their next sign-in.

## Option 3: SCCM / ConfigMgr

Create a **Script Installer** deployment type with the same install and uninstall commands and detection rule as Intune.

## Option 4: Group Policy

1. Copy `DisplayPrompt.exe` to every PC (Group Policy Preferences → **Files**) at  
   `C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\DisplayPrompt.exe`
2. Add the startup entry (Group Policy Preferences → **Registry**):
   - Hive: `HKEY_LOCAL_MACHINE`
   - Key: `Software\Microsoft\Windows\CurrentVersion\Run`
   - Value name: `DisplayPrompt`
   - Value: `"C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\DisplayPrompt.exe"`

## Turning Best quality off for everyone

Push this per-user registry value (Group Policy Preferences → **Registry**, User Configuration):

```
HKEY_CURRENT_USER\Software\Ctrl+Alt+Elite\DisplayPrompt
BestQuality = 0   (DWORD)
```

## Updating

Run the new `install.ps1` (or redeploy the package). It stops the running copy, waits for it to close, and replaces the exe.

## Security notes

- The exe isn't code-signed yet, so SmartScreen may warn on first run. Signing it with your organisation's code-signing certificate removes the warning and lets you allow-list it in AppLocker or WDAC:
  ```powershell
  signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 DisplayPrompt.exe
  ```
- The app bundles its own copy of .NET, so Windows Update doesn't patch it. Rebuild with an up-to-date .NET SDK every few months and redeploy.
- Program Files is admin-only, so ordinary users can't replace the exe.
