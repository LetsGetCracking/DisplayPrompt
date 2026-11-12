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

        private Mutex _mutex;
        private System.Windows.Forms.NotifyIcon _tray;
        private DisplayWatcher _watcher;
        private PopupWindow _popup;
        private long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
        private volatile bool _restarting;

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

            // Draw with the CPU instead of the graphics card. Some graphics drivers don't draw
            // see-through (rounded) windows while screens are mirrored; software rendering
            // always works and is plenty fast for this small panel.
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

            Log.Write("Started (v1.8)");
            CreateTrayIcon();

            _watcher = new DisplayWatcher();
            _watcher.NewDisplayConnected += OnNewDisplayConnected;
            _watcher.Start();

            StartWatchdog();

            if (e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
                ShowPopup();
        }

        // ================= Display connected =================

        private async void OnNewDisplayConnected()
        {
            try
            {
                Log.Write("New display connected");

                // Switch to the default mode first, then let the user change it
                if (DisplayManager.GetCurrentMode() != DefaultModeOnConnect)
                    await Task.Run(() => DisplayManager.Apply(DefaultModeOnConnect));

                string info = null;
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

                ShowPopup();
                if (info != null) _popup?.ShowInfo(info);
            }
            catch (Exception ex)
            {
                Log.Write("OnNewDisplayConnected failed: " + ex);
                ShowPopup();
            }
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
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, a) => ExitApp());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Display Prompt",
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
                    popup.Closed += (s, a) => { if (ReferenceEquals(_popup, s)) _popup = null; };
                    _popup = popup;
                    popup.Show();
                    popup.Activate();
                    Log.Write("Popup shown (mode: " + DisplayManager.GetCurrentMode() + ")");
                }
                catch (Exception ex)
                {
                    Log.Write("ShowPopup failed: " + ex);
                    _popup = null;
                }
            });
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

        private void Restart(string reason)
        {
            if (_restarting) return;
            _restarting = true;
            Log.Write(reason);
            try
            {
                StartReplacement();
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
                StartReplacement();
                try { if (_tray != null) _tray.Visible = false; } catch { }
            }
            catch (Exception ex) { Log.Write("Restart failed: " + ex); }
            Environment.Exit(1);
        }

        private static void StartReplacement()
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath,
                "--after " + Environment.ProcessId) { UseShellExecute = false });
        }

        private static void WaitForPreviousInstance(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--after", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
            try
            {
                using (var old = Process.GetProcessById(pid))
                    old.WaitForExit(15000);
            }
            catch { /* already gone */ }
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
