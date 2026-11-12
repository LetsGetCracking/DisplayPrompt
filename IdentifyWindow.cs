using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DisplayPrompt
{
    /// <summary>Big monitor number shown in the bottom-left of a physical screen (like Windows "Identify").</summary>
    public class IdentifyWindow : Window
    {
        private static readonly Brush NormalBg = new SolidColorBrush(Color.FromRgb(0x1C, 0x1D, 0x22));
        private static readonly Brush ActiveBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x75));
        private const int EdgeMargin = 24; // physical px

        private readonly MonitorInfo _monitor;
        private IntPtr _hwnd;

        public int Number => _monitor.Number;

        public IdentifyWindow(MonitorInfo monitor)
        {
            _monitor = monitor;

            // Normal window (not see-through) so it always draws, even after display switches
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            Background = NormalBg;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            Width = 170;
            Height = 190;
            WindowStartupLocation = WindowStartupLocation.Manual;

            Content = new TextBlock
            {
                Text = monitor.Number.ToString(),
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                FontSize = 120,
                FontWeight = FontWeights.Light,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;

                // Click-through, never takes focus, no taskbar/Alt-Tab entry
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE,
                    ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                SetLayeredWindowAttributes(_hwnd, 0, 240, LWA_ALPHA); // whole-window alpha (safe)

                WindowStyling.ApplyRoundedDark(_hwnd, 0x55, 0x5A, 0x66);

                // Move onto the target monitor first so WPF picks up that monitor's DPI
                SetWindowPos(_hwnd, IntPtr.Zero, _monitor.X + EdgeMargin, _monitor.Y + EdgeMargin, 0, 0, SWP_FLAGS);
            };
            ContentRendered += (s, e) =>
            {
                PlaceBottomLeft();
                Dispatcher.BeginInvoke(new Action(PlaceBottomLeft), DispatcherPriority.ApplicationIdle);
            };
        }

        private void PlaceBottomLeft()
        {
            if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out RECT r)) return;
            int h = r.Bottom - r.Top;
            SetWindowPos(_hwnd, IntPtr.Zero,
                _monitor.X + EdgeMargin,
                _monitor.Y + _monitor.H - h - EdgeMargin,
                0, 0, SWP_FLAGS);
        }

        public void SetHighlighted(bool on)
        {
            Background = on ? ActiveBg : NormalBg;
            if (on) WindowStyling.ApplyRoundedDark(_hwnd, 0x4F, 0x8C, 0xFF);
            else WindowStyling.ApplyRoundedDark(_hwnd, 0x55, 0x5A, 0x66);
        }

        // ---------- Native ----------

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint LWA_ALPHA = 0x2;
        private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        private const uint SWP_FLAGS = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
