using System;
using System.Runtime.InteropServices;

namespace DisplayPrompt
{
    /// <summary>
    /// Native Windows 11 rounded corners and border for normal (non see-through) windows.
    /// On Windows 10 these calls are ignored and the window simply has square corners.
    /// </summary>
    public static class WindowStyling
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;

        public static void ApplyRoundedDark(IntPtr hwnd, byte r, byte g, byte b)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int on = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));

                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int colorRef = r | (g << 8) | (b << 16); // COLORREF = 0x00BBGGRR
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
            }
            catch { /* older Windows: ignore */ }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
