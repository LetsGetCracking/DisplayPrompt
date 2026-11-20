using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace DisplayPrompt
{
    /// <summary>
    /// App side of auto-update. The actual download and install is done by the
    /// "DisplayPrompt Update" scheduled task (updater.ps1, runs as SYSTEM), because
    /// the app is in Program Files and normally runs without admin rights.
    /// The app only (1) asks that task to run, and (2) restarts itself when a newer
    /// exe has appeared on disk.
    /// </summary>
    public static class Updater
    {
        public const string TaskName = @"\Ctrl+Alt+Elite\DisplayPrompt Update";
        private const string PolicyKey = @"Software\Ctrl+Alt+Elite\DisplayPrompt";

        /// <summary>Version of the copy that is running now.</summary>
        public static Version RunningVersion => Normalize(Assembly.GetExecutingAssembly().GetName().Version);

        /// <summary>Path of the installed exe (the updater replaces this file).</summary>
        public static string InstalledExePath => Path.Combine(AppContext.BaseDirectory, "DisplayPrompt.exe");

        /// <summary>IT can turn updates off: HKLM\Software\Ctrl+Alt+Elite\DisplayPrompt AutoUpdate = 0.</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(PolicyKey))
                    {
                        object v = k?.GetValue("AutoUpdate");
                        return v == null || Convert.ToInt32(v) != 0;
                    }
                }
                catch { return true; }
            }
        }

        /// <summary>Ask the SYSTEM updater task to check GitHub now. Returns false if it couldn't be started.</summary>
        public static bool TriggerCheck(string reason)
        {
            if (!IsEnabled)
            {
                Log.Write("Update check skipped (turned off by AutoUpdate=0)");
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("/Run");
                psi.ArgumentList.Add("/TN");
                psi.ArgumentList.Add(TaskName);

                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    bool ok = p.ExitCode == 0;
                    Log.Write(ok
                        ? $"Update check started ({reason}); running v{RunningVersion}"
                        : $"Update check couldn't start ({reason}): {err.Trim()}");
                    return ok;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Update check failed to start: " + ex.Message);
                return false;
            }
        }

        /// <summary>Version of the exe currently on disk, or null if it can't be read.</summary>
        public static Version DiskVersion()
        {
            try
            {
                string path = InstalledExePath;
                if (!File.Exists(path)) return null;
                var info = FileVersionInfo.GetVersionInfo(path);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            }
            catch { return null; }
        }

        /// <summary>True when the updater has put a newer exe on disk than the one running.</summary>
        public static bool NewerVersionInstalled(out Version disk)
        {
            disk = DiskVersion();
            return disk != null && disk > RunningVersion;
        }

        private static Version Normalize(Version v) =>
            v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}
