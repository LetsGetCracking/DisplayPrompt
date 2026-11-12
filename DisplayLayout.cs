using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DisplayPrompt
{
    public class MonitorInfo
    {
        public string DeviceName;   // e.g. \\.\DISPLAY2
        public int Number;          // 2
        public bool IsPrimary;
        public int X, Y, W, H;      // physical pixels, desktop coordinates

        public int Right => X + W;
        public int Bottom => Y + H;
    }

    /// <summary>Reads and writes monitor positions/resolutions and handles snapping.</summary>
    public static class DisplayLayout
    {
        // ---------- Read ----------

        public static List<MonitorInfo> GetActiveMonitors()
        {
            var list = new List<MonitorInfo>();
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };

            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    var dm = NewDevMode();
                    if (EnumDisplaySettingsEx(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
                    {
                        var match = Regex.Match(dd.DeviceName ?? "", @"(\d+)\s*$");
                        list.Add(new MonitorInfo
                        {
                            DeviceName = dd.DeviceName,
                            Number = match.Success ? int.Parse(match.Groups[1].Value) : list.Count + 1,
                            IsPrimary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                            X = dm.dmPositionX,
                            Y = dm.dmPositionY,
                            W = dm.dmPelsWidth,
                            H = dm.dmPelsHeight
                        });
                    }
                }
                dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            }
            return list.OrderBy(m => m.Number).ToList();
        }

        private static string GetPrimaryDeviceName()
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 &&
                    (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                    return dd.DeviceName;
                dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            }
            return null;
        }

        // ---------- Mirror resolution ----------

        /// <summary>
        /// In mirror mode both screens share one desktop. Picks the highest resolution
        /// Windows offers for that shared desktop (highest refresh rate as tie-break).
        /// Returns the resolution now in use, e.g. "1920 × 1080", or null on failure.
        /// </summary>
        public static string SetBestMirrorResolution()
        {
            string device = GetPrimaryDeviceName();
            if (device == null) return null;

            var cur = NewDevMode();
            if (!EnumDisplaySettingsEx(device, ENUM_CURRENT_SETTINGS, ref cur, 0)) return null;

            DEVMODE best = cur;
            long bestPx = (long)cur.dmPelsWidth * cur.dmPelsHeight;
            int bestHz = cur.dmDisplayFrequency;

            var dm = NewDevMode();
            for (int i = 0; EnumDisplaySettingsEx(device, i, ref dm, 0); i++)
            {
                if (dm.dmBitsPerPel == 32)
                {
                    long px = (long)dm.dmPelsWidth * dm.dmPelsHeight;
                    if (px > bestPx || (px == bestPx && dm.dmDisplayFrequency > bestHz))
                    {
                        best = dm;
                        bestPx = px;
                        bestHz = dm.dmDisplayFrequency;
                    }
                }
                dm = NewDevMode();
            }

            bool same = best.dmPelsWidth == cur.dmPelsWidth &&
                        best.dmPelsHeight == cur.dmPelsHeight &&
                        best.dmDisplayFrequency == cur.dmDisplayFrequency;
            if (same) return Describe(cur);

            best.dmFields = DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            int r = ChangeDisplaySettingsEx(device, ref best, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            return r == DISP_CHANGE_SUCCESSFUL ? Describe(best) : Describe(cur);
        }

        private static string Describe(DEVMODE d) => $"{d.dmPelsWidth} × {d.dmPelsHeight}";

        // ---------- Write positions ----------

        public static bool ApplyPositions(IEnumerable<MonitorInfo> monitors)
        {
            bool ok = true;
            foreach (var m in monitors)
            {
                var dm = NewDevMode();
                if (!EnumDisplaySettingsEx(m.DeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0)) { ok = false; continue; }

                dm.dmPositionX = m.IsPrimary ? 0 : m.X;
                dm.dmPositionY = m.IsPrimary ? 0 : m.Y;
                dm.dmFields = DM_POSITION;

                int r = ChangeDisplaySettingsEx(m.DeviceName, ref dm, IntPtr.Zero,
                    CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                if (r != DISP_CHANGE_SUCCESSFUL) ok = false;
            }

            // Commit all staged changes at once
            int commit = ChangeDisplaySettingsExCommit(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            return ok && commit == DISP_CHANGE_SUCCESSFUL;
        }

        // ---------- Snapping ----------

        /// <summary>Snap 'moved' against the others, then re-attach any monitor left floating.</summary>
        public static void SnapAll(List<MonitorInfo> all, MonitorInfo moved)
        {
            SnapTo(moved, all.Where(o => o != moved).ToList(), all);

            for (int pass = 0; pass < all.Count; pass++)
            {
                var connected = ConnectedToPrimary(all);
                var floating = all.Where(m => !connected.Contains(m)).ToList();
                if (floating.Count == 0) break;
                SnapTo(floating[0], connected.ToList(), all);
            }
        }

        private static void SnapTo(MonitorInfo m, List<MonitorInfo> anchors, List<MonitorInfo> all)
        {
            int bestX = m.X, bestY = m.Y;
            double bestDist = double.MaxValue;
            var others = all.Where(o => o != m).ToList();

            foreach (var o in anchors)
            {
                int thrY = (int)(Math.Min(o.H, m.H) * 0.08);
                int thrX = (int)(Math.Min(o.W, m.W) * 0.08);

                // Left / right of o
                foreach (int x in new[] { o.X - m.W, o.Right })
                {
                    int y = Clamp(m.Y, o.Y - m.H + 1, o.Bottom - 1);
                    if (Math.Abs(y - o.Y) <= thrY) y = o.Y;
                    else if (Math.Abs(y + m.H - o.Bottom) <= thrY) y = o.Bottom - m.H;
                    Consider(x, y);
                }
                // Above / below o
                foreach (int y in new[] { o.Y - m.H, o.Bottom })
                {
                    int x = Clamp(m.X, o.X - m.W + 1, o.Right - 1);
                    if (Math.Abs(x - o.X) <= thrX) x = o.X;
                    else if (Math.Abs(x + m.W - o.Right) <= thrX) x = o.Right - m.W;
                    Consider(x, y);
                }
            }

            m.X = bestX;
            m.Y = bestY;

            void Consider(int x, int y)
            {
                if (others.Any(o => Overlaps(x, y, m.W, m.H, o))) return;
                double d = Math.Pow(x - m.X, 2) + Math.Pow(y - m.Y, 2);
                if (d < bestDist) { bestDist = d; bestX = x; bestY = y; }
            }
        }

        private static HashSet<MonitorInfo> ConnectedToPrimary(List<MonitorInfo> all)
        {
            var set = new HashSet<MonitorInfo>();
            var start = all.FirstOrDefault(m => m.IsPrimary) ?? all.First();
            var queue = new Queue<MonitorInfo>();
            set.Add(start);
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var a = queue.Dequeue();
                foreach (var b in all)
                {
                    if (set.Contains(b) || !Touches(a, b)) continue;
                    set.Add(b);
                    queue.Enqueue(b);
                }
            }
            return set;
        }

        private static bool Overlaps(int x, int y, int w, int h, MonitorInfo o) =>
            x < o.Right && x + w > o.X && y < o.Bottom && y + h > o.Y;

        private static bool Touches(MonitorInfo a, MonitorInfo b)
        {
            bool vOverlap = a.Y < b.Bottom && a.Bottom > b.Y;
            bool hOverlap = a.X < b.Right && a.Right > b.X;
            return ((a.Right == b.X || b.Right == a.X) && vOverlap) ||
                   ((a.Bottom == b.Y || b.Bottom == a.Y) && hOverlap);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        // ---------- Native ----------

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
        private const int DM_POSITION = 0x20;
        private const int DM_BITSPERPEL = 0x40000;
        private const int DM_PELSWIDTH = 0x80000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const uint CDS_UPDATEREGISTRY = 0x1;
        private const uint CDS_NORESET = 0x10000000;
        private const int DISP_CHANGE_SUCCESSFUL = 0;

        private static DEVMODE NewDevMode()
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        private static extern int ChangeDisplaySettingsExCommit(string lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
    }
}
