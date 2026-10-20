using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KIOSK
{
    internal static class Program
    {
        private static Mutex _mutex = null;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOWMAXIMIZED = 3;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            const string mutexName = "Global\\VNPT_IGATE_KIOSK_SINGLE_INSTANCE_MUTEX";
            bool createdNew;

            _mutex = new Mutex(true, mutexName, out createdNew);
            if (!createdNew)
            {
                // Nếu ứng dụng đã mở rồi thì đưa cửa sổ ứng dụng đang chạy lên trước và thoát tiến trình thứ 2
                try
                {
                    Process current = Process.GetCurrentProcess();
                    foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                    {
                        if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                        {
                            if (IsIconic(process.MainWindowHandle))
                            {
                                ShowWindowAsync(process.MainWindowHandle, SW_RESTORE);
                            }
                            ShowWindowAsync(process.MainWindowHandle, SW_SHOWMAXIMIZED);
                            SetForegroundWindow(process.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch
                {
                    // Bỏ qua lỗi nếu không lấy được handle
                }
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new KIOSK());
            }
            finally
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }
        }
    }
}
