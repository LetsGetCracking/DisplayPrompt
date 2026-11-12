using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DisplayPrompt
{
    /// <summary>
    /// Listens for monitor plug/unplug and display changes. Raises NewDisplayConnected
    /// when the number of connected displays goes up.
    /// </summary>
    public sealed class DisplayWatcher : IDisposable
    {
        public event Action NewDisplayConnected;

        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        private static readonly Guid GUID_DEVINTERFACE_MONITOR = new Guid("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

        private HwndSource _source;
        private IntPtr _notifyHandle = IntPtr.Zero;
        private DispatcherTimer _debounce;
        private int _lastCount;

        public void Start()
        {
            _lastCount = DisplayManager.GetConnectedDisplayCount();

            // Hidden top-level window (receives WM_DISPLAYCHANGE broadcasts + device notifications)
            var parameters = new HwndSourceParameters("DisplayPromptWatcher")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE)),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid = GUID_DEVINTERFACE_MONITOR
            };
            _notifyHandle = RegisterDeviceNotification(_source.Handle, ref filter, 0);

            // Windows fires several events per plug-in; wait for things to settle
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                Check();
            };
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DISPLAYCHANGE)
            {
                Kick();
            }
            else if (msg == WM_DEVICECHANGE)
            {
                int evt = (int)wParam.ToInt64();
                if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE) Kick();
            }
            return IntPtr.Zero;
        }

        private void Kick()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void Check()
        {
            int count = DisplayManager.GetConnectedDisplayCount();
            bool added = count > _lastCount && count >= 2;
            _lastCount = count;
            if (added) NewDisplayConnected?.Invoke();
        }

        public void Dispose()
        {
            _debounce?.Stop();
            if (_notifyHandle != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_notifyHandle);
                _notifyHandle = IntPtr.Zero;
            }
            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            public short dbcc_name;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, ref DEV_BROADCAST_DEVICEINTERFACE notificationFilter, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);
    }
}
