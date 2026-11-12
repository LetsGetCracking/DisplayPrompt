using Microsoft.Win32;

namespace DisplayPrompt
{
    /// <summary>Per-user settings stored in HKCU\Software\Ctrl+Alt+Elite\DisplayPrompt.</summary>
    public static class AppSettings
    {
        private const string KeyPath = @"Software\Ctrl+Alt+Elite\DisplayPrompt";

        /// <summary>Mirror at the highest resolution both screens support. Default: on.</summary>
        public static bool BestQuality
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    {
                        object v = k?.GetValue("BestQuality");
                        return v == null || (int)v != 0;
                    }
                }
                catch { return true; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                        k.SetValue("BestQuality", value ? 1 : 0, RegistryValueKind.DWord);
                }
                catch { }
            }
        }
    }
}
