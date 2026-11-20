using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DisplayPrompt
{
    public partial class App : Application
    {
        // Mode applied automatically when a new display is connected
        private const DisplayMode DefaultModeOnConnect = DisplayMode.Mirror;

        // If the app stops responding for this long, it restarts itself
        private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(20);

        // Display changes right after start-up are echoes of the change that caused the restart
        private static readonly TimeSpan IgnoreChangesAfterStart = TimeSpan.FromSeconds(8);

        private Mutex _mutex;
        private System.Windows.Forms.NotifyIcon _tray;
        private DisplayWatcher _watcher;
        private PopupWindow _popup;
        private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
        private volatile bool _restarting;

        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private bool _handlingConnect;    // busy switching modes after a plug-in
        private bool _refreshPending;     // refresh once the open panel closes
        private bool _isRetry;            // this copy was started to retry showing the panel

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Never let an unexpected error kill the tray app; log it instead
            DispatcherUnhandledException += (s, a) =>
            {
                Log.Write("UI error: " + a.Exception);
                a.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, a) =>
            {
                Log.Write("Task error: " + a.Exception);
                a.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, a) =>
                Log.Write("Fatal error: " + a.ExceptionObject);

            // Draw with the CPU instead of the graphics card (more reliable across drivers)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            // When restarting, wait for the old copy to exit before taking over
            WaitForPreviousInstance(e.Args);

            // One instance per signed-in user session
            _mutex = new Mutex(true, @"Local\CtrlAltElite.DisplayPrompt", out bool firstInstance);
            if (!firstInstance)
            {
                Shutdown();
                return;
            }

            _isRetry = HasArg(e.Args, "--retry");
            Log.Write($"Started (v{Updater.RunningVersion})" + (e.Args.Length > 0 ? " args: " + string.Join(" ", e.Args) : ""));
            CreateTrayIcon();

            _watcher = new DisplayWatcher();
            _watcher.NewDisplayConnected += OnNewDisplayConnected;
            _watcher.DisplaysChanged += OnDisplaysChanged;
            _watcher.Start();

            StartWatchdog();
            StartUpdateWatch();

            // Check GitHub for a newer version every time the app genuinely starts
            // (not on the quick self-refreshes after display changes)
            if (!HasArg(e.Args, "--after"))
                Task.Run(() => Updater.TriggerCheck("app started")); // don't hold up start-up

            if (HasArg(e.Args, "--show"))
                ShowPopupWhenQuiet(GetArgValue(e.Args, "--info"));
        }

        // ================= Display changes =================

        private async void OnNewDisplayConnected()
        {
            if (_handlingConnect || _restarting) return;
            _handlingConnect = true;
            string info = null;
            try
            {
                Log.Write("New display connected");

                // Switch to the default mode first, then let the user change it
                if (DisplayManager.GetCurrentMode() != DefaultModeOnConnect)
                    await Task.Run(() => DisplayManager.Apply(DefaultModeOnConnect));

                if (AppSettings.BestQuality && DisplayManager.GetCurrentMode() == DisplayMode.Mirror)
                {
                    string res = await Task.Run(() =>
                    {
                        Thread.Sleep(800); // let Windows finish switching to mirror
                        return DisplayLayout.SetBestMirrorResolution();
                    });
                    if (res != null) info = $"Mirroring at {res} (best quality)";
                    Log.Write("Mirror resolution: " + (res ?? "unchanged"));
                }

                // Wait until Windows has finished rearranging the screens
                await WaitForQuietAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(12));
            }
            catch (Exception ex)
            {
                Log.Write("OnNewDisplayConnected failed: " + ex);
            }

            // A copy that was running during the display switch can lose the ability to
            // show its panel, so hand over to a fresh copy, which then shows the panel.
            if (info != null) Restart("Refreshing after new display", "--show", "--info", info);
            else Restart("Refreshing after new display", "--show");
        }

        private void OnDisplaysChanged()
        {
            if (_handlingConnect || _restarting) return;
            if (DateTime.UtcNow - _startedUtc < IgnoreChangesAfterStart) return;

            if (_popup != null)
            {
                // Don't interrupt someone using the panel; refresh when it closes
                _refreshPending = true;
                Log.Write("Display changed - will refresh when the panel closes");
                return;
            }
            Restart("Refreshing after display change");
        }

        /// <summary>Wait until there have been no display changes for 'quiet', or until 'max' has passed.</summary>
        private async Task WaitForQuietAsync(TimeSpan quiet, TimeSpan max)
        {
            var until = DateTime.UtcNow + max;
            while (DateTime.UtcNow < until)
            {
                if (_watcher == null || DateTime.UtcNow - _watcher.LastChangeUtc >= quiet) return;
                await Task.Delay(250);
            }
        }

        private async void ShowPopupWhenQuiet(string info)
        {
            await WaitForQuietAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
            ShowPopup();
            if (!string.IsNullOrEmpty(info)) _popup?.ShowInfo(info);
        }

        // ================= Tray =================

        private static System.Drawing.Icon LoadTrayIcon()
        {
            // Load the embedded .ico at the exact tray size for this screen's DPI
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DisplayPrompt.app.ico"))
                {
                    if (s != null)
                        return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
                }
            }
            catch { }

            try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
            catch { return System.Drawing.SystemIcons.Application; }
        }

        private void CreateTrayIcon()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Display options…", null, (s, a) => ShowPopup());
            menu.Items.Add("Restart Display Prompt", null, (s, a) => Restart("Restart chosen from tray menu"));
            menu.Items.Add("Check for updates", null, (s, a) =>
            {
                bool ok = Updater.TriggerCheck("chosen from tray menu");
                _tray?.ShowBalloonTip(4000, "Display Prompt",
                    ok ? $"Checking for updates… You're on version {Updater.RunningVersion}. If a newer one is found, it installs and restarts automatically."
                       : "Couldn't start the update check. See the log for details.",
                    System.Windows.Forms.ToolTipIcon.Info);
            });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, a) => ExitApp());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = $"Display Prompt {Updater.RunningVersion}",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.MouseClick += (s, a) =>
            {
                if (a.Button != System.Windows.Forms.MouseButtons.Left) return;
                Log.Write("Tray clicked");
                ShowPopup();
            };
        }

        /// <summary>Always opens a brand-new panel, throwing away any old one.</summary>
        public void ShowPopup()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (_popup != null)
                    {
                        var old = _popup;
                        _popup = null;
                        try { old.ForceClose(); } catch { }
                    }

                    var popup = new PopupWindow();
                    popup.Closed += (s, a) =>
                    {
                        if (ReferenceEquals(_popup, s)) _popup = null;
                        if (_popup == null && _refreshPending) Restart("Refreshing after display change");
                    };
                    _popup = popup;
                    popup.Show();
                    popup.Activate();
                    Log.Write("Popup shown (mode: " + DisplayManager.GetCurrentMode() + ")");

                    VerifyPopupVisibleSoon(popup);
                }
                catch (Exception ex)
                {
                    Log.Write("ShowPopup failed: " + ex);
                    _popup = null;
                }
            });
        }

        /// <summary>
        /// Half a second after opening, make sure the panel really is on screen.
        /// If not, restart and show it from a fresh copy (once).
        /// </summary>
        private void VerifyPopupVisibleSoon(PopupWindow popup)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (_restarting || !ReferenceEquals(_popup, popup) || popup.IsClosing) return;
                if (popup.IsFullyOnScreen()) return;

                if (_isRetry)
                {
                    Log.Write("Panel still off screen after a fresh start - leaving it");
                    return;
                }
                Restart("Panel was off screen - restarting to show it", "--show", "--retry");
            };
            t.Start();
        }

        // ================= Auto-update =================

        /// <summary>
        /// Once a minute, see whether the updater task has installed a newer exe.
        /// If so, restart into it (waiting until the panel is closed).
        /// </summary>
        private void StartUpdateWatch()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            t.Tick += (s, e) =>
            {
                if (_restarting || _handlingConnect) return;
                if (!Updater.NewerVersionInstalled(out Version disk)) return;

                if (_popup != null)
                {
                    _refreshPending = true; // restart when the panel closes
                    return;
                }
                t.Stop();
                Restart($"Updated from v{Updater.RunningVersion} to v{disk} - restarting");
            };
            t.Start();
        }

        // ================= Self-healing =================

        /// <summary>
        /// Background check: if the app's UI stops responding for 20 seconds,
        /// log it and restart automatically.
        /// </summary>
        private void StartWatchdog()
        {
            // UI thread updates the heartbeat every 2 seconds
            var beat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            beat.Tick += (s, e) => Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            beat.Start();

            var t = new Thread(() =>
            {
                while (!_restarting)
                {
                    Thread.Sleep(5000);
                    var last = new DateTime(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);
                    if (DateTime.UtcNow - last > HangLimit)
                    {
                        Log.Write("App stopped responding - restarting automatically");
                        RestartFromBackground();
                        return;
                    }
                }
            })
            { IsBackground = true, Name = "DisplayPrompt watchdog" };
            t.Start();
        }

        private void Restart(string reason, params string[] extraArgs)
        {
            if (_restarting) return;
            _restarting = true;
            Log.Write(reason);
            try
            {
                if (_popup != null) { try { _popup.ForceClose(); } catch { } _popup = null; }
                StartReplacement(extraArgs);
                _watcher?.Dispose();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                try { _mutex?.ReleaseMutex(); } catch { }
            }
            catch (Exception ex) { Log.Write("Restart failed: " + ex); }
            Shutdown();
        }

        // Called from the watchdog thread when the UI is frozen: start a fresh copy, then exit hard
        private void RestartFromBackground()
        {
            _restarting = true;
            try
            {
                StartReplacement(Array.Empty<string>());
                try { if (_tray != null) _tray.Visible = false; } catch { }
            }
            catch (Exception ex) { Log.Write("Restart failed: " + ex); }
            Environment.Exit(1);
        }

        private static void StartReplacement(string[] extraArgs)
        {
            // Start the installed exe (after an update, that's the new version)
            string exe = File.Exists(Updater.InstalledExePath) ? Updater.InstalledExePath : Environment.ProcessPath;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--after");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (var a in extraArgs) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }

        private static void WaitForPreviousInstance(string[] args)
        {
            string value = GetArgValue(args, "--after");
            if (!int.TryParse(value, out int pid)) return;
            try
            {
                using (var old = Process.GetProcessById(pid))
                    old.WaitForExit(15000);
            }
            catch { /* already gone */ }
        }

        private static bool HasArg(string[] args, string name) =>
            args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static string GetArgValue(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // ================= Exit =================

        private void ExitApp()
        {
            _restarting = true;
            Log.Write("Exit chosen from tray menu");
            _watcher?.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
