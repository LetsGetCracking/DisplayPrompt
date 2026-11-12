using System;
using System.IO;

namespace DisplayPrompt
{
    /// <summary>Small rolling log at %LocalAppData%\DisplayPrompt\log.txt for troubleshooting.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DisplayPrompt");
        private static readonly string FilePath = Path.Combine(Dir, "log.txt");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                        File.Delete(FilePath); // keep it small
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
