# Deploying Display Prompt to many PCs

A guide for IT staff rolling Display Prompt out to classrooms, meeting rooms or a fleet of laptops.

## What gets installed

| Item | Location |
|---|---|
| App | `C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\DisplayPrompt.exe` (one self-contained file, about 150 MB) |
| Start at sign-in (all users) | `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` → `DisplayPrompt` |
| Per-user setting | `HKCU\Software\Ctrl+Alt+Elite\DisplayPrompt` → `BestQuality` (DWORD, default 1) |
| Per-user log | `%LocalAppData%\DisplayPrompt\log.txt` |
| Updater script | `C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\updater.ps1` |
| Update task | Task Scheduler → `\Ctrl+Alt+Elite\DisplayPrompt Update` (runs as SYSTEM) |
| Update log | `C:\ProgramData\Ctrl+Alt+Elite\DisplayPrompt\update.log` |
| Update switch (optional) | `HKLM\Software\Ctrl+Alt+Elite\DisplayPrompt` → `AutoUpdate` (DWORD, 0 = off) |

The app runs as the signed-in user and needs **no admin rights** to run. Only installing to Program Files needs admin. Its only network access is the update check against `github.com/LetsGetCracking/DisplayPrompt`.

## Option 1: the install script (a few PCs)

Copy `DisplayPrompt.exe`, `install.ps1`, `uninstall.ps1` and `updater.ps1` to the PC. Then, in an **admin** PowerShell in that folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

The script stops any running copy, copies the exe and scripts, adds the startup entry, registers the auto-update task, and starts the app.

## Option 2: Intune (Win32 app)

1. Put `DisplayPrompt.exe`, `install.ps1`, `uninstall.ps1` and `updater.ps1` in one folder.
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

Because Display Prompt updates itself, Intune only needs the first version. To manage versions through Intune instead, turn auto-update off (below).

## Option 4: Group Policy

1. Copy `DisplayPrompt.exe` to every PC (Group Policy Preferences → **Files**) at  
   `C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\DisplayPrompt.exe`
2. Add the startup entry (Group Policy Preferences → **Registry**):
   - Hive: `HKEY_LOCAL_MACHINE`
   - Key: `Software\Microsoft\Windows\CurrentVersion\Run`
   - Value name: `DisplayPrompt`
   - Value: `"C:\Program Files\Ctrl+Alt+Elite\DisplayPrompt\DisplayPrompt.exe"`

## Automatic updates

The `\Ctrl+Alt+Elite\DisplayPrompt Update` task (created by `install.ps1`) runs `updater.ps1` as SYSTEM:

- **When:** 3 minutes after boot, daily at about 03:17 (with up to 2 hours of random delay so PCs don't all download at once), and whenever the app starts or a user chooses **Check for updates**. Signed-in users are allowed to *run* the task, but not change it.
- **What it does:** reads the latest release tag from `github.com/LetsGetCracking/DisplayPrompt/releases/latest`, downloads `DisplayPrompt-x.y.z-win-x64.zip` and its `.sha256`, verifies the checksum and the exe's version, renames the running exe to `DisplayPrompt.exe.old`, and copies in the new one. Running copies restart into it within a minute.
- **Network:** outbound HTTPS to `github.com` and `objects.githubusercontent.com` only. It doesn't use the GitHub API, so many PCs behind one campus IP won't hit rate limits.

**Turn updates off** on a PC (for example, to control versions through Intune):
```
HKEY_LOCAL_MACHINE\Software\Ctrl+Alt+Elite\DisplayPrompt
AutoUpdate = 0   (DWORD)
```

**Force a check now** (any user): right-click the tray icon, then **Check for updates**, or run
```
schtasks /Run /TN "\Ctrl+Alt+Elite\DisplayPrompt Update"
```

## Turning Best quality off for everyone

Push this per-user registry value (Group Policy Preferences → **Registry**, User Configuration):

```
HKEY_CURRENT_USER\Software\Ctrl+Alt+Elite\DisplayPrompt
BestQuality = 0   (DWORD)
```

## Updating manually

Normally not needed (see Automatic updates). To update by hand, run the new `install.ps1` (or redeploy the package). It stops the running copy, waits for it to close, and replaces the exe.

## Security notes

- The exe isn't code-signed yet, so SmartScreen may warn on first run. Signing it with your organisation's code-signing certificate removes the warning and lets you allow-list it in AppLocker or WDAC:
  ```powershell
  signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 DisplayPrompt.exe
  ```
- The app bundles its own copy of .NET, so Windows Update doesn't patch it. Publish a new release built with an up-to-date .NET SDK every few months; auto-update rolls it out.
- **Auto-update trust:** the updater installs whatever is published as the latest release of the GitHub repository, as SYSTEM. The SHA-256 check protects against corrupted or tampered downloads, but not against someone who controls the repository. Protect the GitHub account with two-factor authentication, and consider code-signing releases. If your organisation needs to approve each version first, set `AutoUpdate = 0` and deploy versions yourself.
- Program Files is admin-only, so ordinary users can't replace the exe.
