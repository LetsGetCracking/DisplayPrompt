using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DisplayPrompt
{
    public enum DisplayMode { PcOnly, Mirror, Extend, SecondOnly, Unknown }

    /// <summary>Wraps the same Windows API that Win+P uses (SetDisplayConfig topologies).</summary>
    public static class DisplayManager
    {
        private const uint QDC_ALL_PATHS = 0x1;
        private const uint QDC_DATABASE_CURRENT = 0x4;

        private const uint SDC_TOPOLOGY_INTERNAL = 0x1;
        private const uint SDC_TOPOLOGY_CLONE = 0x2;
        private const uint SDC_TOPOLOGY_EXTEND = 0x4;
        private const uint SDC_TOPOLOGY_EXTERNAL = 0x8;
        private const uint SDC_APPLY = 0x80;

        private const int ERROR_SUCCESS = 0;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        public static bool Apply(DisplayMode mode)
        {
            uint topology;
            string fallbackArg;
            switch (mode)
            {
                case DisplayMode.PcOnly:     topology = SDC_TOPOLOGY_INTERNAL; fallbackArg = "/internal"; break;
                case DisplayMode.Mirror:     topology = SDC_TOPOLOGY_CLONE;    fallbackArg = "/clone";    break;
                case DisplayMode.Extend:     topology = SDC_TOPOLOGY_EXTEND;   fallbackArg = "/extend";   break;
                case DisplayMode.SecondOnly: topology = SDC_TOPOLOGY_EXTERNAL; fallbackArg = "/external"; break;
                default: return false;
            }

            int result = SetDisplayConfig(0, null, 0, null, SDC_APPLY | topology);
            if (result == ERROR_SUCCESS) return true;

            // Fallback: Windows' built-in switcher
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "DisplaySwitch.exe"), fallbackArg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    p?.WaitForExit(5000);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static DisplayMode GetCurrentMode()
        {
            try
            {
                if (!Query(QDC_DATABASE_CURRENT, out _, out uint topology)) return DisplayMode.Unknown;
                switch (topology)
                {
                    case SDC_TOPOLOGY_INTERNAL: return DisplayMode.PcOnly;
                    case SDC_TOPOLOGY_CLONE:    return DisplayMode.Mirror;
                    case SDC_TOPOLOGY_EXTEND:   return DisplayMode.Extend;
                    case SDC_TOPOLOGY_EXTERNAL: return DisplayMode.SecondOnly;
                    default:                    return DisplayMode.Unknown;
                }
            }
            catch
            {
                return DisplayMode.Unknown;
            }
        }

        /// <summary>Number of physically connected displays (active or not).</summary>
        public static int GetConnectedDisplayCount()
        {
            try
            {
                if (!Query(QDC_ALL_PATHS, out var paths, out _)) return 0;
                var seen = new HashSet<string>();
                foreach (var p in paths)
                {
                    if (p.targetInfo.targetAvailable == 0) continue;
                    seen.Add($"{p.targetInfo.adapterId.HighPart}:{p.targetInfo.adapterId.LowPart}:{p.targetInfo.id}");
                }
                return seen.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static bool Query(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out uint topology)
        {
            paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
            topology = 0;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (GetDisplayConfigBufferSizes(flags, out uint numPaths, out uint numModes) != ERROR_SUCCESS)
                    return false;

                var p = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var m = new DISPLAYCONFIG_MODE_INFO[numModes];

                int r = (flags & QDC_DATABASE_CURRENT) != 0
                    ? QueryDisplayConfigTopology(flags, ref numPaths, p, ref numModes, m, out topology)
                    : QueryDisplayConfig(flags, ref numPaths, p, ref numModes, m, IntPtr.Zero);

                if (r == ERROR_SUCCESS)
                {
                    Array.Resize(ref p, (int)numPaths);
                    paths = p;
                    return true;
                }
                if (r != ERROR_INSUFFICIENT_BUFFER) return false;
            }
            return false;
        }

        // ---------- Native ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags,
            ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
        private static extern int QueryDisplayConfigTopology(uint flags,
            ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            out uint currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPathArrayElements, DISPLAYCONFIG_PATH_INFO[] pathArray,
            uint numModeInfoArrayElements, DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);
    }
}
