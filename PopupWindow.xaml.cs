using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DisplayPrompt
{
    public partial class PopupWindow : Window
    {
        private static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(10);

        private static readonly Brush PrimaryBoxBg = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF));
        private static readonly Brush OtherBoxBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x46));
        private static readonly Brush OtherBoxHover = new SolidColorBrush(Color.FromRgb(0x46, 0x4A, 0x55));
        private static readonly Brush BoxBorder = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        private static readonly Brush BoxBorderActive = Brushes.White;

        private bool _closing;
        private bool _busy;
        private bool _arranging;
        private bool _initializing = true;

        // Countdown
        private readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private TimeSpan _remaining = AutoHideAfter;
        private DateTime _lastTick;

        // Arrange view
        private List<MonitorInfo> _monitors = new List<MonitorInfo>();
        private readonly List<IdentifyWindow> _identify = new List<IdentifyWindow>();
        private double _scale, _offX, _offY;
        private MonitorInfo _dragMon;
        private Border _dragBox;
        private Point _grab;

        public PopupWindow()
        {
            InitializeComponent();

            // Restore the saved "Best quality" setting without triggering a change
            QualityToggle.IsChecked = AppSettings.BestQuality;
            _initializing = false;

            Loaded += (s, e) =>
            {
                MarkCurrentMode();
                Reposition();
                AnimateIn();
                StartCountdown();
            };
            SourceInitialized += (s, e) =>
                WindowStyling.ApplyRoundedDark(new WindowInteropHelper(this).Handle, 0x3A, 0x3D, 0x46);
            SizeChanged += (s, e) => Reposition();
            DpiChanged += (s, e) => Reposition();
            LocationChanged += (s, e) => EnsureOnScreen();

            // Re-check shortly after opening, in case Windows moves the panel during a display switch
            ContentRendered += (s, e) =>
            {
                foreach (int ms in new[] { 300, 1200, 3000 })
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                    t.Tick += (ts, te) => { t.Stop(); EnsureOnScreen(); };
                    t.Start();
                }
            };
            LayoutCanvas.SizeChanged += (s, e) => { if (_arranging) RenderLayout(); };

            // Screen size/resolution changes (e.g. switching to mirror) -> keep the panel on-screen
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Closed += (s, e) =>
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _closeFallback?.Stop();
                CloseIdentify();
            };

            // Pause the countdown while the user is hovering the panel
            MouseEnter += (s, e) => _tick.Stop();
            MouseLeave += (s, e) => { if (!_busy && !_closing && !_arranging) ResumeCountdown(); };

            _tick.Tick += OnTick;
        }

        /// <summary>Show a short info line under the tiles (e.g. the resolution in use).</summary>
        public void ShowInfo(string text) => ShowStatus(text);

        // ================= Countdown =================

        private void StartCountdown()
        {
            _remaining = AutoHideAfter;
            CountdownScale.ScaleX = 1;
            ResumeCountdown();
        }

        private void ResumeCountdown()
        {
            _lastTick = DateTime.UtcNow;
            _tick.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            _remaining -= now - _lastTick;
            _lastTick = now;

            if (_remaining <= TimeSpan.Zero)
            {
                _tick.Stop();
                CountdownScale.ScaleX = 0;
                HideAnimated();
                return;
            }
            CountdownScale.ScaleX = _remaining.TotalMilliseconds / AutoHideAfter.TotalMilliseconds;
        }

        // ================= Layout =================

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // Raised on a background thread; wait a moment for Windows to settle
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(500);
                if (!_closing) Reposition();
            }));
        }

        /// <summary>
        /// Bottom-right of the primary screen, above the taskbar. Uses live physical-pixel
        /// sizes so it stays on-screen after resolution/DPI changes.
        /// </summary>
        public void Reposition()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                if (!GetPrimaryWorkArea(out RECT wa)) return;
                if (!GetWindowRect(hwnd, out RECT r)) return;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;

                // 12px gap from the screen edge and taskbar (scaled for this screen's DPI)
                int gap = (int)Math.Round(12 * VisualTreeHelper.GetDpi(this).DpiScaleX);
                int x = Math.Max(wa.Left, wa.Right - w - gap);
                int y = Math.Max(wa.Top, wa.Bottom - h - gap);

                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

                GetWindowRect(hwnd, out RECT after);
                Log.Write($"Popup placed: wanted {x},{y} size {w}x{h}; actual {after.Left},{after.Top}-" +
                          $"{after.Right},{after.Bottom}; work area {wa.Left},{wa.Top}-{wa.Right},{wa.Bottom}; " +
                          $"dpi {VisualTreeHelper.GetDpi(this).PixelsPerInchX}");

                EnsureOnScreen();
            }
            catch (Exception ex)
            {
                Log.Write("Reposition failed: " + ex.Message);
            }
        }

        /// <summary>True if the whole panel is inside the main screen's visible area.</summary>
        public bool IsFullyOnScreen()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || !IsVisible) return false;
                if (!GetPrimaryWorkArea(out RECT wa) || !GetWindowRect(hwnd, out RECT r)) return false;
                if (r.Right - r.Left < 50 || r.Bottom - r.Top < 50) return false;

                const int tol = 2;
                bool ok = r.Left >= wa.Left - tol && r.Top >= wa.Top - tol &&
                          r.Right <= wa.Right + tol && r.Bottom <= wa.Bottom + tol;
                if (!ok)
                    Log.Write($"Panel check: window {r.Left},{r.Top}-{r.Right},{r.Bottom} " +
                              $"outside work area {wa.Left},{wa.Top}-{wa.Right},{wa.Bottom}");
                return ok;
            }
            catch { return false; }
        }

        /// <summary>If any part of the panel is outside the main screen, pull it back in.</summary>
        private void EnsureOnScreen()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || _closing) return;
                if (!GetPrimaryWorkArea(out RECT wa) || !GetWindowRect(hwnd, out RECT r)) return;

                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                int x = r.Left, y = r.Top;
                if (x + w > wa.Right) x = wa.Right - w;
                if (y + h > wa.Bottom) y = wa.Bottom - h;
                if (x < wa.Left) x = wa.Left;
                if (y < wa.Top) y = wa.Top;

                if (x != r.Left || y != r.Top)
                {
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    Log.Write($"Popup was off-screen at {r.Left},{r.Top} - moved to {x},{y}");
                }
            }
            catch { }
        }

        /// <summary>Work area (screen minus taskbar) of the main screen, in physical pixels.</summary>
        private static bool GetPrimaryWorkArea(out RECT wa)
        {
            wa = default;
            IntPtr mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                wa = mi.rcWork;
                return true;
            }
            return SystemParametersInfo(SPI_GETWORKAREA, 0, out wa, 0);
        }

        // Drag the panel by its header
        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch { }
        }

        private void MarkCurrentMode()
        {
            Button active = null;
            switch (DisplayManager.GetCurrentMode())
            {
                case DisplayMode.PcOnly:     active = PcOnlyBtn; break;
                case DisplayMode.Mirror:     active = MirrorBtn; break;
                case DisplayMode.Extend:     active = ExtendBtn; break;
                case DisplayMode.SecondOnly: active = SecondOnlyBtn; break;
            }
            foreach (var b in new[] { PcOnlyBtn, MirrorBtn, ExtendBtn, SecondOnlyBtn })
                b.Tag = b == active ? "Active" : null;
        }

        // ================= Best quality switch =================

        private async void QualityToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing) return;

            bool on = QualityToggle.IsChecked == true;
            AppSettings.BestQuality = on;

            if (!on)
            {
                ShowStatus("Best quality off — Windows will choose the mirror resolution.");
                return;
            }

            // Already mirroring? Apply the best resolution straight away.
            if (DisplayManager.GetCurrentMode() == DisplayMode.Mirror)
            {
                QualityToggle.IsEnabled = false;
                ShowStatus("Finding the best resolution…");
                string res = await Task.Run(() => DisplayLayout.SetBestMirrorResolution());
                QualityToggle.IsEnabled = true;
                ShowStatus(res != null ? $"Mirroring at {res} (best quality)" : "Couldn't change the resolution.");
            }
            else
            {
                ShowStatus("Best quality on — used next time you mirror.");
            }
        }

        // ================= Mode selection =================

        private async void Option_Click(object sender, RoutedEventArgs e)
        {
            DisplayMode mode =
                sender == PcOnlyBtn ? DisplayMode.PcOnly :
                sender == MirrorBtn ? DisplayMode.Mirror :
                sender == ExtendBtn ? DisplayMode.Extend :
                DisplayMode.SecondOnly;

            _busy = true;
            _tick.Stop();
            SetTilesEnabled(false);
            QualityToggle.IsEnabled = false;
            ShowStatus("Applying…");

            bool ok = await Task.Run(() => DisplayManager.Apply(mode));

            if (!ok)
            {
                _busy = false;
                ShowStatus("Couldn't switch display mode. Try Windows + P.");
                SetTilesEnabled(true);
                QualityToggle.IsEnabled = true;
                StartCountdown();
                return;
            }

            if (mode == DisplayMode.Mirror && AppSettings.BestQuality)
            {
                string res = await Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(800); // let Windows finish switching to mirror
                    return DisplayLayout.SetBestMirrorResolution();
                });
                if (res != null)
                {
                    ShowStatus($"Mirroring at {res} (best quality)");
                    await Task.Delay(1800); // give people a moment to read it
                }
                HideAnimated();
                return;
            }

            if (mode == DisplayMode.Extend)
            {
                // Wait for Windows to finish bringing the extra screens online
                var monitors = await Task.Run(() =>
                {
                    List<MonitorInfo> list = null;
                    for (int i = 0; i < 12; i++)
                    {
                        list = DisplayLayout.GetActiveMonitors();
                        if (list.Count >= 2) break;
                        System.Threading.Thread.Sleep(250);
                    }
                    return list;
                });

                if (monitors != null && monitors.Count >= 2)
                {
                    EnterArrangeMode(monitors);
                    return;
                }
            }

            // Selection made: hide straight away (app stays in the tray)
            HideAnimated();
        }

        private void Hide_Click(object sender, RoutedEventArgs e) => HideAnimated();

        private void SetTilesEnabled(bool enabled)
        {
            PcOnlyBtn.IsEnabled = MirrorBtn.IsEnabled = ExtendBtn.IsEnabled = SecondOnlyBtn.IsEnabled = enabled;
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }

        private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;

        // ================= Arrange view =================

        private void EnterArrangeMode(List<MonitorInfo> monitors)
        {
            _arranging = true;
            _busy = false;
            _monitors = monitors;
            _tick.Stop();

            TitleText.Text = "Arrange displays";
            SubtitleText.Text = "Drag a screen to position it";
            HideStatus();
            QualityToggle.Visibility = Visibility.Collapsed; // only applies to mirroring
            ModePanel.Visibility = Visibility.Collapsed;
            CountdownTrack.Visibility = Visibility.Collapsed;
            ArrangePanel.Visibility = Visibility.Visible;

            Grid.SetColumnSpan(HideBtn, 1);
            ApplyBtn.Visibility = Visibility.Visible;

            ShowIdentify();
            RenderLayout();
        }

        private void RenderLayout()
        {
            double cw = LayoutCanvas.ActualWidth, ch = LayoutCanvas.ActualHeight;
            if (cw <= 0 || ch <= 0 || _monitors.Count == 0) return;

            LayoutCanvas.Children.Clear();

            int minX = _monitors.Min(m => m.X), minY = _monitors.Min(m => m.Y);
            int maxX = _monitors.Max(m => m.Right), maxY = _monitors.Max(m => m.Bottom);
            double bw = maxX - minX, bh = maxY - minY;
            const double pad = 20;

            _scale = Math.Min((cw - 2 * pad) / bw, (ch - 2 * pad) / bh);
            _offX = (cw - bw * _scale) / 2 - minX * _scale;
            _offY = (ch - bh * _scale) / 2 - minY * _scale;

            foreach (var m in _monitors)
            {
                var box = BuildBox(m);
                Canvas.SetLeft(box, m.X * _scale + _offX + 1.5);
                Canvas.SetTop(box, m.Y * _scale + _offY + 1.5);
                LayoutCanvas.Children.Add(box);
            }
        }

        private Border BuildBox(MonitorInfo m)
        {
            double w = Math.Max(10, m.W * _scale - 3);
            double h = Math.Max(10, m.H * _scale - 3);

            var box = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(6),
                Background = m.IsPrimary ? PrimaryBoxBg : OtherBoxBg,
                BorderBrush = BoxBorder,
                BorderThickness = new Thickness(1.5),
                Cursor = m.IsPrimary ? Cursors.Arrow : Cursors.SizeAll,
                Tag = m,
                Child = new TextBlock
                {
                    Text = m.Number.ToString(),
                    FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                    FontSize = Math.Max(14, Math.Min(34, h * 0.42)),
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                ToolTip = m.IsPrimary ? "Main display" : null
            };

            box.MouseEnter += (s, e) =>
            {
                Highlight(m.Number, true);
                if (!m.IsPrimary && _dragBox == null) box.Background = OtherBoxHover;
            };
            box.MouseLeave += (s, e) =>
            {
                if (_dragBox == box) return;
                Highlight(m.Number, false);
                if (!m.IsPrimary) box.Background = OtherBoxBg;
            };

            if (!m.IsPrimary)
            {
                box.MouseLeftButtonDown += Box_MouseDown;
                box.MouseMove += Box_MouseMove;
                box.MouseLeftButtonUp += Box_MouseUp;
            }
            return box;
        }

        private void Box_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragBox = (Border)sender;
            _dragMon = (MonitorInfo)_dragBox.Tag;
            _grab = e.GetPosition(_dragBox);
            _dragBox.BorderBrush = BoxBorderActive;
            _dragBox.BorderThickness = new Thickness(2);
            Panel.SetZIndex(_dragBox, 10);
            Highlight(_dragMon.Number, true);
            _dragBox.CaptureMouse();
            e.Handled = true;
        }

        private void Box_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragBox == null || !_dragBox.IsMouseCaptured) return;

            var p = e.GetPosition(LayoutCanvas);
            double left = Math.Max(0, Math.Min(LayoutCanvas.ActualWidth - _dragBox.Width, p.X - _grab.X));
            double top = Math.Max(0, Math.Min(LayoutCanvas.ActualHeight - _dragBox.Height, p.Y - _grab.Y));
            Canvas.SetLeft(_dragBox, left);
            Canvas.SetTop(_dragBox, top);
        }

        private void Box_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragBox == null) return;
            _dragBox.ReleaseMouseCapture();

            // Canvas position -> desktop pixels, then snap edge-to-edge like Windows
            _dragMon.X = (int)Math.Round((Canvas.GetLeft(_dragBox) - 1.5 - _offX) / _scale);
            _dragMon.Y = (int)Math.Round((Canvas.GetTop(_dragBox) - 1.5 - _offY) / _scale);
            DisplayLayout.SnapAll(_monitors, _dragMon);

            Highlight(_dragMon.Number, false);
            _dragBox = null;
            _dragMon = null;
            RenderLayout();
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            ApplyBtn.IsEnabled = false;
            ShowStatus("Applying…");

            var snapshot = _monitors.ToList();
            bool ok = await Task.Run(() => DisplayLayout.ApplyPositions(snapshot));

            if (ok)
            {
                HideAnimated();
            }
            else
            {
                ShowStatus("Couldn't apply that layout. Try moving the screen again.");
                ApplyBtn.IsEnabled = true;
            }
        }

        // ================= Identify overlays =================

        private void ShowIdentify()
        {
            CloseIdentify();
            foreach (var m in _monitors)
            {
                var w = new IdentifyWindow(m);
                _identify.Add(w);
                w.Show();
            }
        }

        private void Highlight(int number, bool on)
        {
            foreach (var w in _identify)
                w.SetHighlighted(on && w.Number == number);
        }

        private void CloseIdentify()
        {
            foreach (var w in _identify)
            {
                try { w.Close(); } catch { }
            }
            _identify.Clear();
        }

        // ================= Animation =================

        private void AnimateIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(260);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            Slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, dur) { EasingFunction = ease });

            // Safety net: if Windows pauses drawing (e.g. mid display switch), the fade-in can stall
            // and leave an invisible panel. Force it fully visible shortly after.
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            settle.Tick += (s, e) =>
            {
                settle.Stop();
                if (_closing) return;
                Root.BeginAnimation(OpacityProperty, null);
                Root.Opacity = 1;
                Slide.BeginAnimation(TranslateTransform.YProperty, null);
                Slide.Y = 0;
                Reposition();
            };
            settle.Start();
        }

        public bool IsClosing => _closing;

        private DispatcherTimer _closeFallback;
        private bool _closed;

        public void HideAnimated()
        {
            if (_closing) return;
            _closing = true;
            _tick.Stop();
            CloseIdentify();

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur = TimeSpan.FromMilliseconds(180);
            var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            fade.Completed += (s, e) => ForceClose();
            Slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, 8, dur) { EasingFunction = ease });
            Root.BeginAnimation(OpacityProperty, fade);

            // Safety net: if Windows pauses drawing (e.g. while switching to mirror),
            // the fade may never finish - close anyway after a short wait.
            _closeFallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _closeFallback.Tick += (s, e) => ForceClose();
            _closeFallback.Start();
        }

        /// <summary>Close immediately, no animation. Safe to call more than once.</summary>
        public void ForceClose()
        {
            if (_closed) return;
            _closed = true;
            _closing = true;
            _closeFallback?.Stop();
            _tick.Stop();
            CloseIdentify();
            try { Close(); } catch { }
        }

        // ---------- Native ----------

        private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint SPI_GETWORKAREA = 0x0030;
        private const uint MONITOR_DEFAULTTOPRIMARY = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
