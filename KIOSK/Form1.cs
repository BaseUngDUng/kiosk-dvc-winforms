using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KIOSK
{
    public partial class KIOSK : Form
    {
        [DllImport("user32.dll")]
        private static extern int ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern int SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        // Thêm API để set display resolution
        [DllImport("user32.dll")]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll")]
        private static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);

        // Low-level Keyboard Hook để chặn Alt+Tab, WinKey và bắt phím tắt Thu nhỏ / Thoát ngay cả khi focus nằm trong WebView2
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int LLKHF_ALTDOWN = 0x20;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _keyboardProc;
        private IntPtr _keyboardHookId = IntPtr.Zero;
        private bool isPasswordDialogOpen = false;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
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
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
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

        private Timer refreshTimer;
        private bool hasRefreshed = false;

        private string web1;
        private string web2;
        private string configFilePath;

        private int refreshDelayMs;
        private bool isFirstRun = true;

        // Thêm đường dẫn download và print
        private string downloadPath;
        private string printPath;

        // Lưu trữ Token và Kiosk Session trong RAM C#
        private string cachedOAuth2Token = "";
        private string cachedKioskId = "";
        private string cachedKioskCode = "";
        private string cachedSessionId = "";
        private string cachedLastMinistryCode = "";
        private string cachedLastMinistryTitle = "";
        private readonly List<object> kioskUsingHistory = new List<object>();
        private string lastAutoLoggedSessionId = "";
        private bool isKioskUsingLogVisible = false;
        private DateTime lastLogCleanupTime = DateTime.MinValue;
        private const int LOG_RETENTION_DAYS = 3;

        // Constants for window messages
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;

        // Thêm panel điều khiển cho cảm ứng
        private Panel controlPanel;
        private Button btnUltraViewer;
        private Timer hideControlPanelTimer;
        private bool isCheckingUpdate = false;
        private Point touchStartPoint;
        private bool isTouchDragging = false;
        private const int SWIPE_THRESHOLD = 100;
        private const int CONTROL_PANEL_HEIGHT = 120;

        public KIOSK()
        {
            InitializeComponent();

            // Set màn hình 1920x1080
            SetScreenResolution(1920, 1080);
            // Set TopMost = true ngay từ đầu
            this.TopMost = true;

            // Cấu hình form cho cảm ứng
            ConfigureTouchScreen();

            // Load icon từ embedded resource
            LoadIconFromResource();

            // Xác định đường dẫn file cấu hình
            configFilePath = Path.Combine(Application.StartupPath, "kiosk_config.dat");

            // Kiểm tra lần chạy đầu tiên
            CheckFirstRun();

            LoadConfigStrict();

            // Tạo thư mục download và print
            CreateDirectories();

            // Tự động dọn dẹp log quá 3 ngày và nạp lịch sử log 3 ngày gần nhất
            CleanupAndLoadKioskUsingLogs(true);

            // Tạo panel điều khiển
            CreateControlPanel();

            InitWebView();

            // Đăng ký sự kiện cảm ứng
            RegisterTouchEvents();

            // Ẩn taskbar
            HideTaskbar();
            // Ẩn 2 nút Thoát (X) và Thu nhỏ (-) trên màn hình (chuyển sang dùng phím tắt)
            ConfigureDeleteButton();
            ConfigureMiniButton();
            ConfigureHomeButton();
            ConfigureUltraViewerButton();

            // Cài đặt Keyboard Hook mức hệ thống để chặn Alt+Tab, WinKey và bắt phím tắt Thu nhỏ / Thoát
            InstallKeyboardHook();

            // Khởi chạy kiểm tra cập nhật phiên bản tự động
            StartUpdateCheckTimer();
        }

        private void InstallKeyboardHook()
        {
            try
            {
                if (_keyboardHookId == IntPtr.Zero)
                {
                    _keyboardProc = KeyboardHookCallback;
                    using (Process curProcess = Process.GetCurrentProcess())
                    using (ProcessModule curModule = curProcess.MainModule)
                    {
                        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(curModule.ModuleName), 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Không thể cài Keyboard Hook: {ex.Message}");
            }
        }

        private void UninstallKeyboardHook()
        {
            try
            {
                if (_keyboardHookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_keyboardHookId);
                    _keyboardHookId = IntPtr.Zero;
                }
            }
            catch { }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                KBDLLHOOKSTRUCT kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = kb.vkCode;
                bool altDown = (kb.flags & LLKHF_ALTDOWN) != 0 || (GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0;
                bool ctrlDown = (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;

                // Khi Kiosk đang mở (không ở trạng thái thu nhỏ), khóa tuyệt đối Alt+Tab, Alt+Esc, Ctrl+Esc, phím Windows
                if (this.WindowState != FormWindowState.Minimized)
                {
                    if ((vkCode == (int)Keys.Tab && altDown) ||
                        (vkCode == (int)Keys.Escape && altDown) ||
                        (vkCode == (int)Keys.Escape && ctrlDown) ||
                        vkCode == (int)Keys.LWin ||
                        vkCode == (int)Keys.RWin)
                    {
                        return (IntPtr)1; // Chặn phím
                    }

                    // Phím tắt Thu nhỏ ứng dụng: Ctrl + M (hoặc Ctrl + Shift + M)
                    if (ctrlDown && vkCode == (int)Keys.M)
                    {
                        if (!isPasswordDialogOpen)
                        {
                            BeginInvoke(new Action(() => btnMini_Click(this, EventArgs.Empty)));
                        }
                        return (IntPtr)1;
                    }

                    // Phím tắt Thoát ứng dụng: Ctrl + Q (hoặc Alt + F4)
                    if ((ctrlDown && vkCode == (int)Keys.Q) || (altDown && vkCode == (int)Keys.F4))
                    {
                        if (!isPasswordDialogOpen)
                        {
                            BeginInvoke(new Action(() => btnDelete_Click(this, EventArgs.Empty)));
                        }
                        return (IntPtr)1;
                    }

                    // Phím tắt Mở thanh công cụ quản trị: Ctrl + H
                    if (ctrlDown && vkCode == (int)Keys.H)
                    {
                        if (!isPasswordDialogOpen)
                        {
                            BeginInvoke(new Action(() => ShowControlPanel()));
                        }
                        return (IntPtr)1;
                    }

                    // Phím tắt Mở nhanh UltraViewer: Ctrl + U
                    if (ctrlDown && vkCode == (int)Keys.U)
                    {
                        BeginInvoke(new Action(() => OpenUltraViewer()));
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
        }

        // Method hỗ trợ tạm thời tắt TopMost để mở ứng dụng khác
        private void TemporarilyDisableTopMost(Action action)
        {
            try
            {
                // Tắt TopMost
                this.TopMost = false;

                // Thực hiện hành động (mở folder/file)
                action?.Invoke();

                // Tạo timer để set lại TopMost sau 3 giây
                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                timer.Interval = 3000; // 3 giây
                timer.Tick += (s, e) =>
                {
                    this.TopMost = true;
                    timer.Stop();
                    timer.Dispose();

                    // Đảm bảo form được đưa lên trên cùng
                    this.BringToFront();
                    SetForegroundWindow(this.Handle);
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khi tạm thời tắt TopMost: {ex.Message}");
                this.TopMost = true; // Đảm bảo set lại nếu có lỗi
            }
        }
        private void ConfigureMiniButton()
        {
            // Ẩn nút Mini trên màn hình để người dân không bấm nhầm (sử dụng phím tắt Ctrl + M để thu nhỏ)
            if (btnMini != null)
            {
                btnMini.Visible = false;
                btnMini.Enabled = false;
            }
        }
        private void PositionControlButtons()
        {
            if (btnDelete == null || btnDelete.IsDisposed || btnMini == null || btnMini.IsDisposed) return;
        }

        private void ConfigureDeleteButton()
        {
            // Ẩn nút Thoát (X) trên màn hình để người dân không bấm nhầm (sử dụng phím tắt Ctrl + Q hoặc Alt + F4 để thoát)
            if (btnDelete != null)
            {
                btnDelete.Visible = false;
                btnDelete.Enabled = false;
            }
        }

        private void PositionDeleteButton()
        {
            if (btnDelete == null || btnDelete.IsDisposed) return;
        }
        private void CreateDirectories()
        {
            try
            {
                // Lấy đường dẫn download từ cấu hình
                downloadPath = ConfigurationManager.AppSettings["DOWNLOAD_PATH"];
                if (string.IsNullOrWhiteSpace(downloadPath))
                {
                    downloadPath = ConfigurationManager.AppSettings["KioskDownload"];
                }

                // Lấy đường dẫn print từ cấu hình (mặc định là thư mục Print trong download)
                printPath = ConfigurationManager.AppSettings["PRINT_PATH"];
                if (string.IsNullOrWhiteSpace(printPath))
                {
                    printPath = Path.Combine(downloadPath);
                }

                // Tạo thư mục nếu chưa tồn tại
                foreach (string path in new[] { downloadPath, printPath })
                {
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                        Console.WriteLine($"Đã tạo thư mục: {path}");
                    }
                }

                // Kiểm tra quyền ghi
                string testFile = Path.Combine(downloadPath, "test.txt");
                File.WriteAllText(testFile, "Test write permission");
                File.Delete(testFile);

                Console.WriteLine("Quyền ghi file OK");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không thể tạo hoặc ghi vào thư mục:\n{ex.Message}",
                    "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);

                // Fallback to temp folder
                downloadPath = Path.Combine(Path.GetTempPath(), "KioskDownload");
                printPath = Path.Combine(downloadPath, "Print");
                Directory.CreateDirectory(downloadPath);
                Directory.CreateDirectory(printPath);
                Console.WriteLine($"Sử dụng thư mục tạm: {downloadPath}");
            }
        }
        private void LoadConfigStrict()
        {
            web1 = ConfigurationManager.AppSettings["WEB1"];
            web2 = ConfigurationManager.AppSettings["WEB2"];

            bool refreshOk = int.TryParse(
                ConfigurationManager.AppSettings["REFRESH_DELAY_MS"],
                out refreshDelayMs);

            // Kiểm tra và set giá trị mặc định nếu không hợp lệ
            if (!refreshOk || refreshDelayMs <= 0)
            {
                refreshDelayMs = 300000; // Mặc định 5 phút
            }

            if (string.IsNullOrWhiteSpace(web1) || string.IsNullOrWhiteSpace(web2))
            {
                MessageBox.Show(
                    "Cấu hình WEB1 hoặc WEB2 không hợp lệ trong App.config.\n" +
                    "Vui lòng kiểm tra lại cấu hình.",
                    "Lỗi cấu hình",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                Application.Exit();
            }

            Console.WriteLine($"WEB1: {web1}");
            Console.WriteLine($"WEB2: {web2}");
            Console.WriteLine($"Refresh delay: {refreshDelayMs}ms");
        }
        private void CheckFirstRun()
        {
            // Nếu file cấu hình tồn tại, đây không phải lần chạy đầu tiên
            isFirstRun = !File.Exists(configFilePath);

            // Log để kiểm tra
            Console.WriteLine($"Is first run: {isFirstRun}");
            if (!isFirstRun)
            {
                Console.WriteLine($"Config file exists at: {configFilePath}");
            }
        }
        private void CoreWebView2_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                var download = e.DownloadOperation;

                string fileName = download.ResultFilePath;

                // Nếu không có extension
                if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                {
                    fileName = Path.Combine(downloadPath,
                        $"document_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
                }

                fileName = GetUniqueFilePath(fileName);

                e.ResultFilePath = fileName;

                Console.WriteLine("Downloading: " + fileName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Download error: " + ex.Message);
            }
        }
        private string GetUniqueFilePath(string filePath)
        {
            string directory = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string extension = Path.GetExtension(filePath);
            int count = 1;

            while (File.Exists(filePath))
            {
                filePath = Path.Combine(directory, $"{fileName} ({count}){extension}");
                count++;
            }

            return filePath;
        }

        private static readonly Dictionary<string, string> DvcqgDeptByAgencyCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "H50.03",  "019d2bbb-6a49-7176-81a5-df1aa384df40" },
            { "H50.101", "019d2be3-6a6d-70f1-92ac-b8fa79425fb7" },
            { "H50.102", "019d2be3-6ab5-7783-98b8-bd576283ad67" },
            { "H50.103", "019d2be3-6ac0-76d0-8205-2ef1d59d4ab3" },
            { "H50.104", "019d2be3-6ac7-73cd-b865-755fb992138e" },
            { "H50.105", "019d2be3-6ad9-7672-ab27-c0b2da4a6d93" },
            { "H50.106", "019d2be3-6ae6-71be-a578-e51a25d6b7c5" },
            { "H50.107", "019d2be3-6af4-706b-9f97-31c551a8938b" },
            { "H50.108", "019d2be3-6b02-702e-be4b-f85271a0b6c2" },
            { "H50.109", "019d2be3-6b0e-73e6-851f-b70cb7b7b4e4" },
            { "H50.110", "019d2be3-6b19-74a9-a732-ec8b775fa706" },
            { "H50.111", "019d2be3-6b27-7646-9d57-d88b17309e06" },
            { "H50.112", "019d2be3-6b33-7088-a73b-e50780877a3f" },
            { "H50.113", "019d2be3-6b3f-710c-8dc0-cf33b5b52110" },
            { "H50.114", "019d2be3-6b4d-70f2-9234-6ee3c3a67c52" },
            { "H50.115", "019d2be3-6b5b-72dc-b711-0f1d82f1b053" },
            { "H50.116", "019d2be3-6b69-74d8-a4a0-a0fd86472c26" },
            { "H50.117", "019d2be3-6b75-712f-bc0c-d783e9e4a1aa" },
            { "H50.118", "019d2be3-6b81-754e-b62c-8413a7f5e1cc" },
            { "H50.119", "019d2be3-6b8e-7054-8150-e574fb7a66a9" },
            { "H50.120", "019d2be3-6b9a-7483-b06a-c1c6c61c63c9" },
            { "H50.121", "019d2be3-6ba6-7290-9f57-67b14a1d5fd8" },
            { "H50.122", "019d2be3-6bb2-7639-8e92-b786a7180fb4" },
            { "H50.123", "019d2be3-6bc0-709d-a916-94fb680b7714" },
            { "H50.124", "019d2be3-6bcd-7288-902f-4ab32bc2eb83" },
            { "H50.125", "019d2be3-6bd9-7470-9c2a-5922ccfb66ab" },
            { "H50.126", "019d2be3-6be5-7773-84c6-a09d9d9b4f78" },
            { "H50.127", "019d2be3-6bf3-710c-851c-9e42d4145020" },
            { "H50.128", "019d2be3-6bff-7307-b0db-d16d91bf717a" },
            { "H50.129", "019d2be3-6c0c-73a1-9621-98bc4eb11e87" },
            { "H50.130", "019d2be3-6c18-7733-9493-4e0e00462334" },
            { "H50.131", "019d2be3-6c26-7331-8b48-30f698fd9346" },
            { "H50.132", "019d2be3-6c33-702c-80e6-21a2d4c8309a" },
            { "H50.133", "019d2be3-6c40-7151-a1c1-90a2a92a1739" },
            { "H50.134", "019d2be3-6c4c-77de-97f9-55b30a43b68b" },
            { "H50.135", "019d2be3-6c59-7252-91eb-219ab6962a84" },
            { "H50.136", "019d2be3-6c66-702b-94cf-525d3bf7a955" },
            { "H50.137", "019d2be3-6c73-7468-b742-674e239e8d20" },
            { "H50.138", "019d2be3-6c80-7218-a9d4-94be9ef1e04b" },
            { "H50.139", "019d2be3-6c8c-770a-8443-17b12232c191" },
            { "H50.140", "019d2be3-6c99-7552-9004-f8b67b14ebad" },
            { "H50.141", "019d2be3-6ca5-7023-b122-3ca622f78440" },
            { "H50.142", "019d2be3-6cb1-740b-8c80-ca337e8496c1" },
            { "H50.143", "019d2be3-6cbe-77a9-87bc-f5e6413b060d" },
            { "H50.144", "019d2be3-6ccb-730d-9d9b-d95cd0c8eb27" },
            { "H50.145", "019d2be3-6cd8-75b7-a036-b6b0c0155cb6" },
            { "H50.146", "019d2be3-6ce5-71a7-8336-62b8861e6147" },
            { "H50.147", "019d2be3-6cf1-7549-9072-7ff21a8e43ab" },
            { "H50.148", "019d2be3-6cfd-72d9-b044-d11408bf468f" },
            { "H50.149", "019d2be3-6d0a-71a6-845b-3c053b165787" },
            { "H50.150", "019d2be3-6d17-713c-96f9-a84bb3fa78f8" },
            { "H50.151", "019d2be3-6d24-75a2-b628-2367657a6bd8" },
            { "H50.152", "019d2be3-6d30-7500-965f-946c62441406" },
            { "H50.153", "019d2be3-6d3c-72ab-88c5-ca43025eb93f" },
            { "H50.154", "019d2be3-6d49-7134-8d85-8a05d113972c" },
            { "H50.155", "019d2be3-6d56-77b2-b78c-d562fd5e373a" },
            { "H50.156", "019d2be3-6d62-75b0-9a7d-2f7fd8051153" },
            { "H50.157", "019d2be3-6d6e-705c-a9f8-6db4910a9407" },
            { "H50.158", "019d2be3-6d7b-7552-91e7-1bc704fa187c" },
            { "H50.159", "019d2be3-6d87-70dc-9d0d-2f93e79b14bb" },
            { "H50.160", "019d2be3-6d94-76ab-b636-d868c6403765" },
            { "H50.161", "019d2be3-6da0-7764-8bc1-4dc77e97a7bf" },
            { "H50.162", "019d2be3-6dad-72d4-942c-6cb3a45cc35d" },
            { "H50.163", "019d2be3-6db9-7674-9604-d74c67f00043" },
            { "H50.164", "019d2be3-6dc6-7229-a349-91c8140fa726" },
            { "H50.165", "019d2be3-6dd2-7635-9305-2b8a316bf992" },
            { "H50.166", "019d2be3-6ddf-704f-8066-8565d7524bf4" },
            { "H50.167", "019d2be3-6deb-75a1-b152-a91428ef0e8a" },
            { "H50.168", "019d2be3-6df7-7745-a259-25048a2ef867" },
            { "H50.169", "019d2be3-6e04-7252-8873-9ef43fa6510e" },
            { "H50.170", "019d2be3-6e10-7605-8d8a-664a9a2e9829" },
            { "H50.171", "019d2be3-6e1d-76e3-a9b9-a69d51313514" },
            { "H50.172", "019d2be3-6e29-7257-97ca-d79a1ff72c97" },
            { "H50.173", "019d2be3-6e36-7490-90c1-11820aa1e6cf" },
            { "H50.174", "019d2be3-6e42-76be-8e24-21f43a51bd35" },
            { "H50.175", "019d2be3-6e4f-70d3-a64f-657b654ff282" },
            { "H50.176", "019d2be3-6e5b-76bc-b232-99ec5986df33" },
            { "H50.177", "019d2be3-6e67-768d-8209-2f447cc1144b" },
            { "H50.178", "019d2be3-6e74-7151-8e69-4fc152263423" }
        };

        private static readonly Dictionary<string, string> DvcqgCaseByFormalityId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "019d2bbd-486d-777d-9cd7-c3b9ef2015aa", "019d2bbe-be3c-7672-b1a5-df1aa384d158" }, // 1.000656 - Khai tử
            { "019d2bbd-47b2-720e-9262-3b695711d292", "019d2bbe-c094-7674-9604-d74c67f00001" }, // 1.001193 - Khai sinh
            { "019d2bbd-47b4-7341-b959-55b30a43b29a", "019d2bbe-c072-72d4-942c-6cb3a45cc35d" }, // 2.000635 - Cấp bản sao trích lục hộ tịch
            { "019d2bbd-4799-7252-8873-9ef43fa6522e", "019d2bbe-bc93-7783-98b8-bd576283af52" }, // 1.004859 - Xác nhận tình trạng hôn nhân
            { "019d2bbd-48b0-7764-8bc1-4dc77e97a6b3", "019d2bbe-c172-7290-9f57-67b14a1d5120" }  // 1.004873 - Kết hôn
        };

        private bool isVneIdCleanedForCurrentLogin = false;

        private void CoreWebView2_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(e.Uri))
                    return;

                Uri uri = new Uri(e.Uri);
                string host = (uri.Host ?? "").ToLowerInvariant();

                // 1. Chặn phiên VNeID cũ: Khi bắt đầu chuyển hướng từ Kiosk vào trang đăng nhập VNeID (sso.dancuquocgia.gov.vn / vneid.gov.vn),
                // xóa sạch toàn bộ localStorage, sessionStorage, IndexedDB và Cookie của VNeID trước khi tải trang
                if (host.Contains("dancuquocgia.gov.vn") || host.Contains("vneid.gov.vn"))
                {
                    if (!isVneIdCleanedForCurrentLogin)
                    {
                        isVneIdCleanedForCurrentLogin = true;
                        string targetVneIdUrl = e.Uri;
                        e.Cancel = true;
                        BeginInvoke(new Action(async () =>
                        {
                            try
                            {
                                await ClearVneIdOnlyAsync();
                                webView21.CoreWebView2.Navigate(targetVneIdUrl);
                            }
                            catch { }
                        }));
                        return;
                    }
                }
                else if (host.Contains("kiosk.") || host.Contains("localhost"))
                {
                    isVneIdCleanedForCurrentLogin = false;
                }

                // 2. Chuyển hướng 5 thủ tục hộ tịch Bộ Tư pháp trên dichvucong.gov.vn về đúng xã/phường cấu hình
                if (string.IsNullOrEmpty(cachedKioskCode))
                    return;

                string agCode = cachedKioskCode.Contains(",") ? cachedKioskCode.Split(',')[0].Trim() : cachedKioskCode.Trim();
                if (!DvcqgDeptByAgencyCode.TryGetValue(agCode, out string targetDeptId) || string.IsNullOrEmpty(targetDeptId))
                    return;

                if (!uri.Host.EndsWith("dichvucong.gov.vn", StringComparison.OrdinalIgnoreCase))
                    return;

                bool isSearchPage = uri.AbsolutePath.IndexOf("/tim-kiem-thu-tuc-hanh-chinh", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isSubmitPage = uri.AbsolutePath.IndexOf("/nop-ho-so", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isSearchPage && !isSubmitPage)
                    return;

                string query = uri.Query.TrimStart('?');
                if (string.IsNullOrEmpty(query))
                    return;

                string formalityId = "";
                string currentDeptId = "";
                foreach (string part in query.Split('&'))
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    int eqIdx = part.IndexOf('=');
                    if (eqIdx <= 0) continue;
                    string k = part.Substring(0, eqIdx);
                    string v = Uri.UnescapeDataString(part.Substring(eqIdx + 1));
                    if (k.Equals("formalityId", StringComparison.OrdinalIgnoreCase))
                        formalityId = v.Trim();
                    else if (k.Equals("departmentId", StringComparison.OrdinalIgnoreCase))
                        currentDeptId = v.Trim();
                }

                if (!string.IsNullOrEmpty(formalityId) && DvcqgCaseByFormalityId.TryGetValue(formalityId, out string formalityCaseId))
                {
                    if (string.IsNullOrEmpty(cachedLastMinistryCode))
                    {
                        cachedLastMinistryCode = "G13";
                        cachedLastMinistryTitle = "Bộ Tư pháp";
                    }

                    if (isSearchPage || !string.Equals(currentDeptId, targetDeptId, StringComparison.OrdinalIgnoreCase))
                    {
                        string newUrl = $"https://dichvucong.gov.vn/nop-ho-so?formalityCaseId={formalityCaseId}&formalityId={formalityId}&departmentId={targetDeptId}&categoryId=019d2bbb-69ee-7079-a6b4-536173b5e483&isFormalityCaseTest=false";
                        Console.WriteLine($"[KIOSK WinForms] Chuyển hướng thủ tục Bộ Tư pháp về đúng đơn vị ({agCode} -> {targetDeptId}): {newUrl}");
                        e.Cancel = true;
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                webView21.CoreWebView2.Navigate(newUrl);
                            }
                            catch { }
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi kiểm tra điều hướng DVCQG: {ex.Message}");
            }
        }

        private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // Khi WEB1 load xong và là lần chạy đầu tiên, tạo file cấu hình
            if (isFirstRun && webView21.Source != null &&
                webView21.Source.ToString().StartsWith(web1) && e.IsSuccess)
            {
                try
                {
                    // Tạo file cấu hình để đánh dấu đã chạy lần đầu
                    File.WriteAllText(configFilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                    // Log để kiểm tra
                    Console.WriteLine($"Đã tạo file cấu hình tại: {configFilePath}");
                    Console.WriteLine("Application will stay on WEB1 until manually closed");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Không thể tạo file cấu hình: {ex.Message}");
                }
            }

            // Log khi load trang thành công & tự động bắt Token nếu đang ở domain Kiosk
            if (e.IsSuccess && webView21.Source != null)
            {
                Console.WriteLine($"Navigation completed to: {webView21.Source}");
                string currentUrl = webView21.Source.ToString().ToLower();
                string kioskOrigin = "";
                try
                {
                    if (!string.IsNullOrEmpty(web2))
                        kioskOrigin = new Uri(web2).Host.ToLower();
                }
                catch { }

                // Nếu đang ở domain của Kiosk (ví dụ kiosk.quangtri.gov.vn hoặc localhost) -> Tự động lưu Token và Session vào RAM C#
                bool isKioskDomain = (!string.IsNullOrEmpty(kioskOrigin) && currentUrl.Contains(kioskOrigin))
                                     || currentUrl.Contains("kiosk.")
                                     || currentUrl.Contains("localhost");
                if (isKioskDomain)
                {
                    await CaptureKioskAuthInfoAsync();
                }

                // Chỉ hiển thị bảng log KioskUsing trên màn hình khi quản trị viên đã bấm Ctrl + H để mở log
                if (isKioskUsingLogVisible)
                {
                    RenderKioskUsingOverlayInWebView();
                }
            }
        }

        private async Task CaptureKioskAuthInfoAsync()
        {
            try
            {
                string tokenJson = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('OAuth2TOKEN') || localStorage.getItem('access_token') || ''");
                if (!string.IsNullOrEmpty(tokenJson) && tokenJson != "null" && tokenJson != "\"\"")
                {
                    string parsed = JsonConvert.DeserializeObject<string>(tokenJson);
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        cachedOAuth2Token = parsed;
                        Console.WriteLine("[KIOSK WinForms] Đã cập nhật cached OAuth2TOKEN từ Kiosk thành công.");
                    }
                }

                string kioskIdJson = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('agencyId') || localStorage.getItem('agency_Id') || localStorage.getItem('kiosk_id') || localStorage.getItem('kioskId') || ''");
                if (!string.IsNullOrEmpty(kioskIdJson) && kioskIdJson != "null" && kioskIdJson != "\"\"")
                {
                    string parsed = JsonConvert.DeserializeObject<string>(kioskIdJson);
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        cachedKioskId = parsed.Contains(",") ? parsed.Split(',')[0].Trim() : parsed.Trim();
                    }
                }

                string kioskCodeJson = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('agencyCode') || localStorage.getItem('agency_Code') || localStorage.getItem('kiosk_code') || localStorage.getItem('kioskCode') || ''");
                if (!string.IsNullOrEmpty(kioskCodeJson) && kioskCodeJson != "null" && kioskCodeJson != "\"\"")
                {
                    string parsed = JsonConvert.DeserializeObject<string>(kioskCodeJson);
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        cachedKioskCode = parsed.Contains(",") ? parsed.Split(',')[0].Trim() : parsed.Trim();
                    }
                }

                string sessionIdJson = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('kiosk_session_id') || localStorage.getItem('session_id') || localStorage.getItem('sessionId') || ''");
                if (!string.IsNullOrEmpty(sessionIdJson) && sessionIdJson != "null" && sessionIdJson != "\"\"")
                {
                    string parsed = JsonConvert.DeserializeObject<string>(sessionIdJson);
                    if (!string.IsNullOrEmpty(parsed)) cachedSessionId = parsed.Trim();
                }

                string lastMinCodeJson = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('kioskLastMinistryCode') || ''");
                if (!string.IsNullOrEmpty(lastMinCodeJson) && lastMinCodeJson != "null")
                {
                    string parsed = JsonConvert.DeserializeObject<string>(lastMinCodeJson);
                    cachedLastMinistryCode = parsed != null ? parsed.Trim() : "";
                }

                string lastMinTitleJson = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('kioskLastMinistryTitle') || ''");
                if (!string.IsNullOrEmpty(lastMinTitleJson) && lastMinTitleJson != "null")
                {
                    string parsed = JsonConvert.DeserializeObject<string>(lastMinTitleJson);
                    cachedLastMinistryTitle = parsed != null ? parsed.Trim() : "";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Capture auth info error: {ex.Message}");
            }
        }
        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "document";

            // Loại bỏ các ký tự không hợp lệ trong tên file
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName.Trim();
        }
        private async void InitWebView()
        {
            try
            {
                await webView21.EnsureCoreWebView2Async();

                var s = webView21.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = true;
                s.IsZoomControlEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsScriptEnabled = true;

                // Cấu hình download
                webView21.CoreWebView2.Profile.DefaultDownloadFolderPath = downloadPath;

                // Xử lý sự kiện download
                webView21.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;

                // Xử lý sự kiện điều hướng để tự động gắn đúng departmentId xã/phường cho 5 thủ tục Bộ Tư pháp trên dichvucong.gov.vn
                webView21.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;

                // Xử lý sự kiện khi navigation hoàn tất
                webView21.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

                // Xử lý sự kiện mở cửa sổ mới
                webView21.CoreWebView2.NewWindowRequested += (sender, e) =>
                {
                    e.Handled = true; // Chặn mở cửa sổ trình duyệt ngoài (Edge/Chrome)
                    if (!string.IsNullOrEmpty(e.Uri))
                    {
                        webView21.CoreWebView2.Navigate(e.Uri); // Mở thẳng link Bộ ngay trong Webview Kiosk
                    }
                };

                // Đọc và tiêm script theo dõi mã hồ sơ Bộ/Ngành
                try
                {
                    string scriptPath = Path.Combine(Application.StartupPath, "kiosk-extension", "content-script.js");
                    if (!File.Exists(scriptPath))
                    {
                        string devPath = Path.Combine(Application.StartupPath, "..", "..", "kiosk-extension", "content-script.js");
                        if (File.Exists(devPath))
                        {
                            scriptPath = devPath;
                        }
                    }

                    if (File.Exists(scriptPath))
                    {
                        string kioskMonitorScript = File.ReadAllText(scriptPath);
                        await webView21.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(kioskMonitorScript);
                        Console.WriteLine("Đã tiêm content-script.js theo dõi hồ sơ Bộ/Ngành vào WebView2");
                    }
                }
                catch (Exception exScript)
                {
                    Console.WriteLine($"Lỗi tiêm content-script.js: {exScript.Message}");
                }

                // Inject script tự động bắt và đồng bộ OAuth2TOKEN, agencyId, agencyCode từ Kiosk về C#
                // đồng thời xử lý triệt để lỗi đăng nhập CCCD/VNeID & định tuyến 5 thủ tục hộ tịch về đúng xã/phường
                await webView21.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (function() {
                        try {
                            var host = (window.location.hostname || '').toLowerCase();
                            var isKioskHost = host.includes('kiosk.') || host.includes('localhost');

                            var origGetItem = localStorage.getItem;
                            var origSetItem = localStorage.setItem;
                            var origRemoveItem = localStorage.removeItem;

                            var dvcqgDeptByAgencyCode = {
                                'H50.03':  '019d2bbb-6a49-7176-81a5-df1aa384df40',
                                'H50.101': '019d2be3-6a6d-70f1-92ac-b8fa79425fb7',
                                'H50.102': '019d2be3-6ab5-7783-98b8-bd576283ad67',
                                'H50.103': '019d2be3-6ac0-76d0-8205-2ef1d59d4ab3',
                                'H50.104': '019d2be3-6ac7-73cd-b865-755fb992138e',
                                'H50.105': '019d2be3-6ad9-7672-ab27-c0b2da4a6d93',
                                'H50.106': '019d2be3-6ae6-71be-a578-e51a25d6b7c5',
                                'H50.107': '019d2be3-6af4-706b-9f97-31c551a8938b',
                                'H50.108': '019d2be3-6b02-702e-be4b-f85271a0b6c2',
                                'H50.109': '019d2be3-6b0e-73e6-851f-b70cb7b7b4e4',
                                'H50.110': '019d2be3-6b19-74a9-a732-ec8b775fa706',
                                'H50.111': '019d2be3-6b27-7646-9d57-d88b17309e06',
                                'H50.112': '019d2be3-6b33-7088-a73b-e50780877a3f',
                                'H50.113': '019d2be3-6b3f-710c-8dc0-cf33b5b52110',
                                'H50.114': '019d2be3-6b4d-70f2-9234-6ee3c3a67c52',
                                'H50.115': '019d2be3-6b5b-72dc-b711-0f1d82f1b053',
                                'H50.116': '019d2be3-6b69-74d8-a4a0-a0fd86472c26',
                                'H50.117': '019d2be3-6b75-712f-bc0c-d783e9e4a1aa',
                                'H50.118': '019d2be3-6b81-754e-b62c-8413a7f5e1cc',
                                'H50.119': '019d2be3-6b8e-7054-8150-e574fb7a66a9',
                                'H50.120': '019d2be3-6b9a-7483-b06a-c1c6c61c63c9',
                                'H50.121': '019d2be3-6ba6-7290-9f57-67b14a1d5fd8',
                                'H50.122': '019d2be3-6bb2-7639-8e92-b786a7180fb4',
                                'H50.123': '019d2be3-6bc0-709d-a916-94fb680b7714',
                                'H50.124': '019d2be3-6bcd-7288-902f-4ab32bc2eb83',
                                'H50.125': '019d2be3-6bd9-7470-9c2a-5922ccfb66ab',
                                'H50.126': '019d2be3-6be5-7773-84c6-a09d9d9b4f78',
                                'H50.127': '019d2be3-6bf3-710c-851c-9e42d4145020',
                                'H50.128': '019d2be3-6bff-7307-b0db-d16d91bf717a',
                                'H50.129': '019d2be3-6c0c-73a1-9621-98bc4eb11e87',
                                'H50.130': '019d2be3-6c18-7733-9493-4e0e00462334',
                                'H50.131': '019d2be3-6c26-7331-8b48-30f698fd9346',
                                'H50.132': '019d2be3-6c33-702c-80e6-21a2d4c8309a',
                                'H50.133': '019d2be3-6c40-7151-a1c1-90a2a92a1739',
                                'H50.134': '019d2be3-6c4c-77de-97f9-55b30a43b68b',
                                'H50.135': '019d2be3-6c59-7252-91eb-219ab6962a84',
                                'H50.136': '019d2be3-6c66-702b-94cf-525d3bf7a955',
                                'H50.137': '019d2be3-6c73-7468-b742-674e239e8d20',
                                'H50.138': '019d2be3-6c80-7218-a9d4-94be9ef1e04b',
                                'H50.139': '019d2be3-6c8c-770a-8443-17b12232c191',
                                'H50.140': '019d2be3-6c99-7552-9004-f8b67b14ebad',
                                'H50.141': '019d2be3-6ca5-7023-b122-3ca622f78440',
                                'H50.142': '019d2be3-6cb1-740b-8c80-ca337e8496c1',
                                'H50.143': '019d2be3-6cbe-77a9-87bc-f5e6413b060d',
                                'H50.144': '019d2be3-6ccb-730d-9d9b-d95cd0c8eb27',
                                'H50.145': '019d2be3-6cd8-75b7-a036-b6b0c0155cb6',
                                'H50.146': '019d2be3-6ce5-71a7-8336-62b8861e6147',
                                'H50.147': '019d2be3-6cf1-7549-9072-7ff21a8e43ab',
                                'H50.148': '019d2be3-6cfd-72d9-b044-d11408bf468f',
                                'H50.149': '019d2be3-6d0a-71a6-845b-3c053b165787',
                                'H50.150': '019d2be3-6d17-713c-96f9-a84bb3fa78f8',
                                'H50.151': '019d2be3-6d24-75a2-b628-2367657a6bd8',
                                'H50.152': '019d2be3-6d30-7500-965f-946c62441406',
                                'H50.153': '019d2be3-6d3c-72ab-88c5-ca43025eb93f',
                                'H50.154': '019d2be3-6d49-7134-8d85-8a05d113972c',
                                'H50.155': '019d2be3-6d56-77b2-b78c-d562fd5e373a',
                                'H50.156': '019d2be3-6d62-75b0-9a7d-2f7fd8051153',
                                'H50.157': '019d2be3-6d6e-705c-a9f8-6db4910a9407',
                                'H50.158': '019d2be3-6d7b-7552-91e7-1bc704fa187c',
                                'H50.159': '019d2be3-6d87-70dc-9d0d-2f93e79b14bb',
                                'H50.160': '019d2be3-6d94-76ab-b636-d868c6403765',
                                'H50.161': '019d2be3-6da0-7764-8bc1-4dc77e97a7bf',
                                'H50.162': '019d2be3-6dad-72d4-942c-6cb3a45cc35d',
                                'H50.163': '019d2be3-6db9-7674-9604-d74c67f00043',
                                'H50.164': '019d2be3-6dc6-7229-a349-91c8140fa726',
                                'H50.165': '019d2be3-6dd2-7635-9305-2b8a316bf992',
                                'H50.166': '019d2be3-6ddf-704f-8066-8565d7524bf4',
                                'H50.167': '019d2be3-6deb-75a1-b152-a91428ef0e8a',
                                'H50.168': '019d2be3-6df7-7745-a259-25048a2ef867',
                                'H50.169': '019d2be3-6e04-7252-8873-9ef43fa6510e',
                                'H50.170': '019d2be3-6e10-7605-8d8a-664a9a2e9829',
                                'H50.171': '019d2be3-6e1d-76e3-a9b9-a69d51313514',
                                'H50.172': '019d2be3-6e29-7257-97ca-d79a1ff72c97',
                                'H50.173': '019d2be3-6e36-7490-90c1-11820aa1e6cf',
                                'H50.174': '019d2be3-6e42-76be-8e24-21f43a51bd35',
                                'H50.175': '019d2be3-6e4f-70d3-a64f-657b654ff282',
                                'H50.176': '019d2be3-6e5b-76bc-b232-99ec5986df33',
                                'H50.177': '019d2be3-6e67-768d-8209-2f447cc1144b',
                                'H50.178': '019d2be3-6e74-7151-8e69-4fc152263423'
                            };

                            var dvcqgFiveProcs = {
                                '1.000656': { formalityId: '019d2bbd-486d-777d-9cd7-c3b9ef2015aa', formalityCaseId: '019d2bbe-be3c-7672-b1a5-df1aa384d158', categoryId: '019d2bbb-69ee-7079-a6b4-536173b5e483' },
                                '1.001193': { formalityId: '019d2bbd-47b2-720e-9262-3b695711d292', formalityCaseId: '019d2bbe-c094-7674-9604-d74c67f00001', categoryId: '019d2bbb-69ee-7079-a6b4-536173b5e483' },
                                '2.000635': { formalityId: '019d2bbd-47b4-7341-b959-55b30a43b29a', formalityCaseId: '019d2bbe-c072-72d4-942c-6cb3a45cc35d', categoryId: '019d2bbb-69ee-7079-a6b4-536173b5e483' },
                                '1.004859': { formalityId: '019d2bbd-4799-7252-8873-9ef43fa6522e', formalityCaseId: '019d2bbe-bc93-7783-98b8-bd576283af52', categoryId: '019d2bbb-69ee-7079-a6b4-536173b5e483' },
                                '1.004873': { formalityId: '019d2bbd-48b0-7764-8bc1-4dc77e97a6b3', formalityCaseId: '019d2bbe-c172-7290-9f57-67b14a1d5120', categoryId: '019d2bbb-69ee-7079-a6b4-536173b5e483' }
                            };

                            function hasValidCCCD() {
                                try {
                                    var raw = origGetItem.call(localStorage, 'dataScanner');
                                    return !!(raw && raw !== 'null' && raw !== 'undefined' && (raw.indexOf('cardNumber') !== -1 || raw.indexOf('identityNumber12') !== -1));
                                } catch (e) {
                                    return false;
                                }
                            }

                            // Nếu đang ở trang VNeID (sso.dancuquocgia.gov.vn / vneid.gov.vn) và vừa bắt đầu lượt đăng nhập mới:
                            // Xóa sạch localStorage, sessionStorage và cookie của VNeID ngay khi tạo Document để không bao giờ bị dính phiên VNeID cũ!
                            if (host.includes('dancuquocgia.gov.vn') || host.includes('vneid.gov.vn')) {
                                if (!sessionStorage.getItem('__kiosk_vneid_entered')) {
                                    try { localStorage.clear(); } catch (e) {}
                                    try { sessionStorage.clear(); } catch (e) {}
                                    try { sessionStorage.setItem('__kiosk_vneid_entered', 'true'); } catch (e) {}
                                    try {
                                        var cookies = document.cookie.split(';');
                                        for (var ci = 0; ci < cookies.length; ci++) {
                                            var c = cookies[ci];
                                            var eqPos = c.indexOf('=');
                                            var cname = eqPos > -1 ? c.substr(0, eqPos).trim() : c.trim();
                                            if (cname) {
                                                document.cookie = cname + '=;expires=Thu, 01 Jan 1970 00:00:00 GMT;path=/';
                                                document.cookie = cname + '=;expires=Thu, 01 Jan 1970 00:00:00 GMT;path=/;domain=.dancuquocgia.gov.vn';
                                                document.cookie = cname + '=;expires=Thu, 01 Jan 1970 00:00:00 GMT;path=/;domain=sso.dancuquocgia.gov.vn';
                                            }
                                        }
                                    } catch (e) {}
                                }
                            }

                            if (isKioskHost) {
                                var search = window.location.search || '';
                                var isExplicitLogout = (search.indexOf('logout=true') !== -1 || search.indexOf('isLogout=true') !== -1);
                                var isFirstBootLoad = !sessionStorage.getItem('__kiosk_boot_cleaned');
                                if (isFirstBootLoad) {
                                    sessionStorage.setItem('__kiosk_boot_cleaned', 'true');
                                }

                                if (isExplicitLogout || isFirstBootLoad) {
                                    try {
                                        window.__kioskAllowClearScanner = true;
                                        sessionStorage.removeItem('kiosk_is_thidiem');
                                        sessionStorage.removeItem('kiosk_citizen_logged_in');
                                        origRemoveItem.call(localStorage, 'kiosk_citizen_logged_in');
                                        origRemoveItem.call(localStorage, 'dataScanner');
                                        origRemoveItem.call(localStorage, 'tempUsername');
                                        origRemoveItem.call(localStorage, 'tempUID');
                                        origRemoveItem.call(localStorage, 'kiosk_session_id');
                                        origSetItem.call(localStorage, 'usernameToken', '');
                                        origSetItem.call(localStorage, 'isLoggedIn', 'false');
                                        window.__kioskAllowClearScanner = false;
                                        if (window.chrome && window.chrome.webview) {
                                            window.chrome.webview.postMessage({ type: 'KIOSK_CLEAR_SESSION' });
                                        }
                                    } catch (e) {}
                                }

                                if (window.location.pathname.indexOf('/kiosk/callback') !== -1 && search.indexOf('saved=true') !== -1) {
                                    origRemoveItem.call(localStorage, 'kiosk_session_id');
                                }

                                // 1. Theo dõi chế độ Thủ tục hành chính thí điểm (isThiDiem=true)
                                if (search.indexOf('isThiDiem=true') !== -1 || search.indexOf('thidiem=true') !== -1) {
                                    sessionStorage.setItem('kiosk_is_thidiem', 'true');
                                } else if (
                                    window.location.pathname.indexOf('/kiosk/nophoso') !== -1 &&
                                    search.indexOf('isUserLogin=true') === -1 &&
                                    (window.location.hash || '').indexOf('code=') === -1
                                ) {
                                    sessionStorage.setItem('kiosk_is_thidiem', 'false');
                                }

                                // 2. Làm sạch URL ngay lập tức nếu bị redirect về //vi/kiosk/nophoso hoặc dính ?isUserLogin=true
                                if (window.location.pathname.indexOf('//') !== -1 || search.indexOf('isUserLogin=true') !== -1) {
                                    var cleanPath = window.location.pathname.replace(/\/+/g, '/');
                                    var params = new URLSearchParams(search);
                                    params.delete('isUserLogin');
                                    if (sessionStorage.getItem('kiosk_is_thidiem') === 'true') {
                                        params.set('isThiDiem', 'true');
                                    }
                                    var newQuery = params.toString();
                                    var cleanUrl = cleanPath + (newQuery ? '?' + newQuery : '') + (window.location.hash || '');
                                    window.history.replaceState(window.history.state, '', cleanUrl);
                                }

                                // 3. Chuẩn hóa cấu hình trong JSON.parse:
                                // - Sửa domainKiosk tránh nhảy trang
                                // - Tắt popupConfirmUser & idCheck.provider khi đã quét CCCD hoặc đăng nhập VNeID (tránh hiện lại popup khi từ Trang chủ vào lại Nộp hồ sơ)
                                // - Tự động thay đúng departmentId của xã/phường đang cấu hình cho 5 thủ tục hộ tịch trên dichvucong.gov.vn
                                var origJsonParse = JSON.parse;
                                JSON.parse = function() {
                                    var parsed = origJsonParse.apply(this, arguments);
                                    try {
                                        if (parsed && typeof parsed === 'object') {
                                            var cfg = parsed.configuration || (parsed.domainKiosk !== undefined ? parsed : null);
                                            if (cfg && typeof cfg === 'object' && cfg.domainKiosk !== undefined) {
                                                var isThiDiemNow = (window.location.search || '').indexOf('isThiDiem=true') !== -1 ||
                                                                   sessionStorage.getItem('kiosk_is_thidiem') === 'true';
                                                cfg.domainKiosk = window.location.origin + '/vi/kiosk/nophoso' + (isThiDiemNow ? '?isThiDiem=true' : '');
                                                cfg.confirmUserLogoutFirst = false;

                                                if (hasValidCCCD() || origGetItem.call(localStorage, 'isLoggedIn') === 'true') {
                                                    cfg.popupConfirmUser = false;
                                                    if (cfg.idCheck && typeof cfg.idCheck === 'object') {
                                                        cfg.idCheck.provider = '';
                                                    }
                                                }

                                                if (cfg.qtiLinkBoNganh && Array.isArray(cfg.qtiLinkBoNganh.procedureLinks)) {
                                                    var agRaw = (origGetItem.call(localStorage, 'agencyCode') || origGetItem.call(localStorage, 'agency_Code') || '').trim();
                                                    var agCode = agRaw.indexOf(',') !== -1 ? agRaw.split(',')[0].trim() : agRaw;
                                                    var deptId = dvcqgDeptByAgencyCode[agCode];
                                                    if (deptId) {
                                                        for (var i = 0; i < cfg.qtiLinkBoNganh.procedureLinks.length; i++) {
                                                            var item = cfg.qtiLinkBoNganh.procedureLinks[i];
                                                            if (item && item.code && dvcqgFiveProcs[item.code]) {
                                                                var meta = dvcqgFiveProcs[item.code];
                                                                item.url = 'https://dichvucong.gov.vn/nop-ho-so?formalityCaseId=' + meta.formalityCaseId +
                                                                           '&formalityId=' + meta.formalityId +
                                                                           '&departmentId=' + deptId +
                                                                           '&categoryId=' + meta.categoryId +
                                                                           '&isFormalityCaseTest=false';
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    } catch (e) {}
                                    return parsed;
                                };

                                // 4. Chặn reload trang tự động khi rời khỏi màn hình chi tiết nộp hồ sơ (/kiosk/nophoso/<id>) về Trang chủ
                                var origMatch = String.prototype.match;
                                String.prototype.match = function(regexp) {
                                    if (regexp && regexp.source === '^\\/kiosk\\/nophoso\\/[0-9a-zA-Z]+$') {
                                        setTimeout(function() {
                                            try {
                                                var header = document.querySelector('.header');
                                                if (header) header.style.display = '';
                                                var footer = document.querySelector('.footer');
                                                if (footer) footer.style.display = '';
                                                var pageTitle = document.querySelector('.page-title');
                                                if (pageTitle) pageTitle.style.display = '';
                                            } catch (e) {}
                                        }, 50);
                                        return null;
                                    }
                                    return origMatch.apply(this, arguments);
                                };

                                // 5. Đảm bảo luôn có OAuth2TOKEN cho các API tra cứu thủ tục khi đăng nhập bằng CCCD (isLoggedIn = 'false')
                                // Tuyệt đối KHÔNG trả về isLoggedIn = 'true' giả khi quét CCCD vì sẽ làm BasedataService/BasepadService bỏ qua Bearer OAuth2TOKEN!
                                localStorage.getItem = function(k) {
                                    var val = origGetItem.apply(this, arguments);
                                    if (k === 'OAuth2TOKEN' && (!val || val === 'null' || val === 'undefined')) {
                                        var pubToken = origGetItem.call(localStorage, 'OAuth2TOKEN_PUBLIC');
                                        if (pubToken && pubToken !== 'null' && pubToken !== 'undefined') {
                                            return pubToken;
                                        }
                                    }
                                    return val;
                                };

                                // Bảo vệ dataScanner không bị xóa ngoài ý muốn trừ khi bấm Đăng xuất hoặc chuyển sang đăng nhập VNeID
                                localStorage.removeItem = function(k) {
                                    if (k === 'dataScanner' && !window.__kioskAllowClearScanner) {
                                        var curSearch = window.location.search || '';
                                        if (curSearch.indexOf('logout=true') === -1 && curSearch.indexOf('isLogout=true') === -1 && curSearch.indexOf('isUserLogin=true') === -1) {
                                            return;
                                        }
                                    }
                                    return origRemoveItem.apply(this, arguments);
                                };

                                // Tự động đóng popup xác thực nếu người dùng đã quét CCCD hoặc đã đăng nhập VNeID (chỉ bấm nút 'Xác nhận' hoặc 'Tiếp tục', KHÔNG BAO GIỜ bấm nhầm 'Đăng nhập')
                                // Đồng thời nếu mở popup khi chưa đăng nhập thì dọn sẵn phiên SSO/VNeID cũ trong nền
                                var hasCleanedForPopup = false;
                                setInterval(function() {
                                    try {
                                        var popupEl = document.querySelector('.confirm-user');
                                        if (!popupEl) {
                                            hasCleanedForPopup = false;
                                            return;
                                        }
                                        if (hasValidCCCD() || origGetItem.call(localStorage, 'isLoggedIn') === 'true') {
                                            var btns = popupEl.querySelectorAll('button');
                                            for (var bi = 0; bi < btns.length; bi++) {
                                                var bTxt = (btns[bi].textContent || '').trim();
                                                if (bTxt === 'Xác nhận' || bTxt === 'Tiếp tục') {
                                                    btns[bi].click();
                                                    break;
                                                }
                                            }
                                        } else if (!hasCleanedForPopup) {
                                            hasCleanedForPopup = true;
                                            if (window.chrome && window.chrome.webview) {
                                                window.chrome.webview.postMessage({ type: 'KIOSK_CLEAR_SESSION' });
                                            }
                                        }
                                    } catch (e) {}
                                }, 250);

                                // 6. Loại bỏ inline onclick reload trên nút Trang chủ và bắt sự kiện bấm nút Đăng xuất / Đăng nhập
                                function sanitizeKioskHeaderLinks() {
                                    try {
                                        var links = document.querySelectorAll('a[onclick*=""location.reload""]');
                                        for (var i = 0; i < links.length; i++) {
                                            var a = links[i];
                                            var isLogoutBtn = a.classList.contains('-dangxuat') ||
                                                              (a.getAttribute('href') || '').indexOf('logout=true') !== -1;
                                            if (!isLogoutBtn) {
                                                a.removeAttribute('onclick');
                                                a.onclick = null;
                                            }
                                        }
                                    } catch (e) {}
                                }

                                document.addEventListener('DOMContentLoaded', sanitizeKioskHeaderLinks);
                                document.addEventListener('pointerdown', function(ev) {
                                    try {
                                        sanitizeKioskHeaderLinks();
                                        // Nếu bấm vào nút 'Đăng nhập' trong popup confirm-user -> xóa sạch phiên VNeID/Keycloak cũ ngay lập tức trước khi chuyển trang
                                        var btn = ev.target && ev.target.closest ? ev.target.closest('.confirm-user button') : null;
                                        if (btn && (btn.textContent || '').trim().indexOf('Đăng nhập') !== -1) {
                                            window.__kioskAllowClearScanner = true;
                                            origRemoveItem.call(localStorage, 'dataScanner');
                                            origRemoveItem.call(localStorage, 'tempUsername');
                                            origRemoveItem.call(localStorage, 'tempUID');
                                            origSetItem.call(localStorage, 'isLoggedIn', 'false');
                                            window.__kioskAllowClearScanner = false;
                                            if (window.chrome && window.chrome.webview) {
                                                window.chrome.webview.postMessage({ type: 'KIOSK_CLEAR_SESSION' });
                                            }
                                        }

                                        var target = ev.target && ev.target.closest ? ev.target.closest('a') : null;
                                        if (!target) return;
                                        var href = target.getAttribute('href') || '';
                                        if (href.indexOf('isThiDiem=true') !== -1) {
                                            sessionStorage.setItem('kiosk_is_thidiem', 'true');
                                        } else if (href.indexOf('/kiosk/nophoso') !== -1 && href.indexOf('isThiDiem=true') === -1) {
                                            sessionStorage.setItem('kiosk_is_thidiem', 'false');
                                        }
                                    } catch (e) {}
                                }, true);

                                document.addEventListener('click', function(ev) {
                                    try {
                                        sanitizeKioskHeaderLinks();
                                        var target = ev.target && ev.target.closest ? ev.target.closest('a') : null;
                                        if (!target) return;
                                        var href = target.getAttribute('href') || '';
                                        var isLogoutClick = target.classList.contains('-dangxuat') || href.indexOf('logout=true') !== -1;
                                        if (isLogoutClick) {
                                            ev.preventDefault();
                                            ev.stopPropagation();
                                            window.__kioskAllowClearScanner = true;
                                            var agCode = origGetItem.call(localStorage, 'agencyCode') || '';
                                            target.removeAttribute('onclick');
                                            target.onclick = null;
                                            sessionStorage.removeItem('kiosk_is_thidiem');
                                            sessionStorage.removeItem('kiosk_citizen_logged_in');
                                            origRemoveItem.call(localStorage, 'kiosk_citizen_logged_in');
                                            origRemoveItem.call(localStorage, 'dataScanner');
                                            origRemoveItem.call(localStorage, 'tempUsername');
                                            origRemoveItem.call(localStorage, 'tempUID');
                                            origRemoveItem.call(localStorage, 'kiosk_session_id');
                                            origSetItem.call(localStorage, 'usernameToken', '');
                                            origSetItem.call(localStorage, 'isLoggedIn', 'false');
                                            window.__kioskAllowClearScanner = false;
                                            if (window.chrome && window.chrome.webview) {
                                                window.chrome.webview.postMessage({
                                                    type: 'KIOSK_LOGOUT',
                                                    origin: window.location.origin,
                                                    mcq: agCode
                                                });
                                            }
                                        }
                                    } catch (e) {}
                                }, true);
                            }
                        } catch (e) {}

                        function genMongoObjectId() {
                            var ts = Math.floor(Date.now() / 1000).toString(16).padStart(8, '0');
                            var rand = '';
                            for (var i = 0; i < 16; i++) {
                                rand += Math.floor(Math.random() * 16).toString(16);
                            }
                            return ts + rand;
                        }

                        var lastSyncSignature = '';

                        function syncKioskAuthToCSharp() {
                            try {
                                if (!window.chrome || !window.chrome.webview) return;
                                var host = (window.location.hostname || '').toLowerCase();
                                if (!host.includes('kiosk.') && !host.includes('localhost')) return;

                                var token = origGetItem.call(localStorage, 'OAuth2TOKEN') || origGetItem.call(localStorage, 'OAuth2TOKEN_PUBLIC') || origGetItem.call(localStorage, 'access_token') || '';
                                var kioskId = origGetItem.call(localStorage, 'agencyId') || origGetItem.call(localStorage, 'agency_Id') || '';
                                var kioskCode = origGetItem.call(localStorage, 'agencyCode') || origGetItem.call(localStorage, 'agency_Code') || '';
                                var sessionId = origGetItem.call(localStorage, 'kiosk_session_id') || '';
                                var lastMinCode = origGetItem.call(localStorage, 'kioskLastMinistryCode') || '';
                                var lastMinTitle = origGetItem.call(localStorage, 'kioskLastMinistryTitle') || '';

                                var sig = [token, kioskId, kioskCode, sessionId, lastMinCode, lastMinTitle].join('|');
                                if ((token || kioskId || kioskCode) && sig !== lastSyncSignature) {
                                    lastSyncSignature = sig;
                                    window.chrome.webview.postMessage({
                                        type: 'KIOSK_AUTH_SYNC',
                                        token: token,
                                        kiosk_id: kioskId,
                                        kiosk_code: kioskCode,
                                        session_id: sessionId,
                                        last_ministry_code: lastMinCode,
                                        last_ministry_title: lastMinTitle
                                    });
                                }
                            } catch (e) {}
                        }

                        // 1. Hook vào localStorage.setItem để bắt ngay lập tức khi Angular ghi token, mã đơn vị/mã bộ hoặc khi quét CCCD trong popup
                        try {
                            localStorage.setItem = function(k, v) {
                                origSetItem.apply(this, arguments);
                                if (k === 'isLoggedIn' && v === 'true') {
                                    try { sessionStorage.setItem('kiosk_citizen_logged_in', 'true'); } catch(e) {}
                                }
                                if (isKioskHost && ((k === 'dataScanner' && hasValidCCCD()) || (k === 'isLoggedIn' && v === 'true'))) {
                                    try {
                                        var curSid = origGetItem.call(localStorage, 'kiosk_session_id') || '';
                                        if (!curSid || curSid.length !== 24) {
                                            curSid = genMongoObjectId();
                                            origSetItem.call(localStorage, 'kiosk_session_id', curSid);
                                            syncKioskAuthToCSharp();
                                            if (window.chrome && window.chrome.webview) {
                                                window.chrome.webview.postMessage({
                                                    type: 'KIOSK_AUTO_LOGIN_TRACK',
                                                    session_id: curSid,
                                                    mode: k === 'dataScanner' ? 'Quét CCCD' : 'VNeID'
                                                });
                                            }
                                        }
                                    } catch (e) {}
                                }
                                if (
                                    k === 'OAuth2TOKEN' ||
                                    k === 'OAuth2TOKEN_PUBLIC' ||
                                    k === 'access_token' ||
                                    k === 'agencyId' ||
                                    k === 'agencyCode' ||
                                    k === 'kiosk_session_id' ||
                                    k === 'kioskLastMinistryCode' ||
                                    k === 'kioskLastMinistryTitle'
                                ) {
                                    syncKioskAuthToCSharp();
                                }
                            };
                        } catch(e) {}

                        // 1b. Hook XMLHttpRequest, fetch và console trên trang Kiosk để bắt mọi log gửi kiosk-using từ Web Angular
                        if (isKioskHost) {
                            try {
                                var origXhrOpen = XMLHttpRequest.prototype.open;
                                var origXhrSend = XMLHttpRequest.prototype.send;
                                XMLHttpRequest.prototype.open = function(method, url) {
                                    this.__kioskMethod = method;
                                    this.__kioskUrl = String(url || '');
                                    return origXhrOpen.apply(this, arguments);
                                };
                                XMLHttpRequest.prototype.send = function(body) {
                                    try {
                                        if (this.__kioskUrl && (this.__kioskUrl.indexOf('kiosk-using') !== -1 || this.__kioskUrl.indexOf('kiosk_using') !== -1)) {
                                            var bodyStr = typeof body === 'string' ? body : '';
                                            this.__kioskPayload = bodyStr;
                                            if (window.chrome && window.chrome.webview) {
                                                window.chrome.webview.postMessage({
                                                    type: 'KIOSK_USING_WEB_LOG',
                                                    phase: 'SENDING',
                                                    url: this.__kioskUrl,
                                                    payload: bodyStr
                                                });
                                            }
                                            var xhrRef = this;
                                            this.addEventListener('loadend', function() {
                                                try {
                                                    var respTxt = '';
                                                    try { respTxt = String(xhrRef.responseText || '').substring(0, 1000); } catch (e) {}
                                                    if (window.chrome && window.chrome.webview) {
                                                        window.chrome.webview.postMessage({
                                                            type: 'KIOSK_USING_WEB_LOG',
                                                            phase: 'RESPONSE',
                                                            url: xhrRef.__kioskUrl,
                                                            status: xhrRef.status,
                                                            payload: xhrRef.__kioskPayload || '',
                                                            response: respTxt
                                                        });
                                                    }
                                                } catch (e) {}
                                            });
                                        }
                                    } catch (e) {}
                                    return origXhrSend.apply(this, arguments);
                                };

                                var origConsoleLog = console.log;
                                var origConsoleWarn = console.warn;
                                var origConsoleError = console.error;
                                function forwardKioskUsingConsole(level, args) {
                                    try {
                                        if (!args || !args.length) return;
                                        var first = String(args[0] || '');
                                        if (first.indexOf('[KioskUsing]') !== -1 && window.chrome && window.chrome.webview) {
                                            var parts = [];
                                            for (var i = 0; i < args.length; i++) {
                                                var a = args[i];
                                                if (typeof a === 'object' && a !== null) {
                                                    try { parts.push(JSON.stringify(a)); } catch (e) { parts.push(String(a)); }
                                                } else {
                                                    parts.push(String(a));
                                                }
                                            }
                                            window.chrome.webview.postMessage({
                                                type: 'KIOSK_USING_CONSOLE',
                                                level: level,
                                                message: parts.join(' ')
                                            });
                                        }
                                    } catch (e) {}
                                }
                                console.log = function() {
                                    forwardKioskUsingConsole('INFO', arguments);
                                    return origConsoleLog.apply(this, arguments);
                                };
                                console.warn = function() {
                                    forwardKioskUsingConsole('WARN', arguments);
                                    return origConsoleWarn.apply(this, arguments);
                                };
                                console.error = function() {
                                    forwardKioskUsingConsole('ERROR', arguments);
                                    return origConsoleError.apply(this, arguments);
                                };
                            } catch (e) {}
                        }

                        // 2. Quét kiểm tra sau khi DOM khởi tạo
                        setTimeout(syncKioskAuthToCSharp, 1500);
                        document.addEventListener('visibilitychange', syncKioskAuthToCSharp);
                    })();
                ");

                // Inject script để xử lý in ấn từ JavaScript (đã sửa lỗi cú pháp C# lẫn trong JS)
                await webView21.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    window.print = function() {
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage({
                                type: 'print',
                                title: document.title,
                                url: window.location.href
                            });
                        }
                    };
                ");

                // Xử lý message từ JavaScript
                webView21.CoreWebView2.WebMessageReceived += (sender, e) =>
                {
                    try
                    {
                        // Sử dụng Newtonsoft.Json để parse
                        var message = JsonConvert.DeserializeObject<dynamic>(e.WebMessageAsJson);
                        string type = message.type;

                        if (type == "KIOSK_AUTH_SYNC")
                        {
                            string token = message.token;
                            if (!string.IsNullOrEmpty(token) && token != cachedOAuth2Token)
                            {
                                cachedOAuth2Token = token;
                                Console.WriteLine($"[KIOSK WinForms] >>> ĐÃ BẮT & LƯU TRƯỚC OAuth2TOKEN VÀO C# RAM (Length={cachedOAuth2Token.Length})");
                            }

                            string kioskId = message.kiosk_id;
                            if (!string.IsNullOrEmpty(kioskId))
                            {
                                cachedKioskId = kioskId.Contains(",") ? kioskId.Split(',')[0].Trim() : kioskId.Trim();
                            }

                            string kioskCode = message.kiosk_code;
                            if (!string.IsNullOrEmpty(kioskCode))
                            {
                                cachedKioskCode = kioskCode.Contains(",") ? kioskCode.Split(',')[0].Trim() : kioskCode.Trim();
                            }

                            string sessionId = message.session_id;
                            if (!string.IsNullOrEmpty(sessionId)) cachedSessionId = sessionId.Trim();

                            string lastMinCode = message.last_ministry_code;
                            if (lastMinCode != null) cachedLastMinistryCode = lastMinCode.Trim();

                            string lastMinTitle = message.last_ministry_title;
                            if (lastMinTitle != null) cachedLastMinistryTitle = lastMinTitle.Trim();
                        }
                        else if (type == "KIOSK_USING_WEB_LOG")
                        {
                            string phase = message.phase != null ? (string)message.phase : "";
                            string url = message.url != null ? (string)message.url : "";
                            string payload = message.payload != null ? (string)message.payload : "";
                            string response = message.response != null ? (string)message.response : "";
                            int status = message.status != null ? (int)message.status : 0;

                            if (phase == "SENDING")
                            {
                                RecordAndShowKioskUsingLog("Web (Angular)", "ĐANG GỬI API kiosk-using", url, "PENDING...", payload, "", true);
                            }
                            else
                            {
                                bool ok = status >= 200 && status < 300;
                                RecordAndShowKioskUsingLog("Web (Angular)", "PHẢN HỒI API kiosk-using", url, $"HTTP {status}", payload, response, ok);
                            }
                        }
                        else if (type == "KIOSK_USING_CONSOLE")
                        {
                            string level = message.level != null ? (string)message.level : "INFO";
                            string msgText = message.message != null ? (string)message.message : "";
                            bool ok = level != "WARN" && level != "ERROR";
                            RecordAndShowKioskUsingLog("Web Console", $"Console [{level}]", "", level, msgText, "", ok);
                        }
                        else if (type == "KIOSK_AUTO_LOGIN_TRACK")
                        {
                            string sid = message.session_id != null ? (string)message.session_id : "";
                            string mode = message.mode != null ? (string)message.mode : "CCCD";
                            if (!string.IsNullOrEmpty(sid) && sid != lastAutoLoggedSessionId)
                            {
                                lastAutoLoggedSessionId = sid;
                                cachedSessionId = sid;
                                _ = Task.Run(async () =>
                                {
                                    await SaveKioskUsingActionAsync("login", 1, "", "", $"Tự động tạo session & gửi login ({mode})");
                                });
                            }
                        }
                        else if (type == "KIOSK_HIDE_LOG_PANEL")
                        {
                            isKioskUsingLogVisible = false;
                        }
                        else if (type == "KIOSK_OPEN_LOG_FILE")
                        {
                            BeginInvoke(new Action(() => OpenKioskUsingLogFile()));
                        }
                        else if (type == "KIOSK_CLEAR_SESSION")
                        {
                            cachedSessionId = "";
                            cachedLastMinistryCode = "";
                            cachedLastMinistryTitle = "";
                            isVneIdCleanedForCurrentLogin = false;
                            BeginInvoke(new Action(async () =>
                            {
                                await ClearCitizenCookiesAsync();
                            }));
                        }
                        else if (type == "KIOSK_LOGOUT")
                        {
                            cachedSessionId = "";
                            cachedLastMinistryCode = "";
                            cachedLastMinistryTitle = "";
                            isVneIdCleanedForCurrentLogin = false;
                            string origin = message.origin != null ? (string)message.origin : "";
                            string mcq = message.mcq != null ? (string)message.mcq : cachedKioskCode;
                            BeginInvoke(new Action(async () =>
                            {
                                await ClearCitizenCookiesAsync();
                                if (string.IsNullOrEmpty(origin))
                                {
                                    try
                                    {
                                        Uri web2Uri = new Uri(web2);
                                        origin = web2Uri.GetLeftPart(UriPartial.Authority);
                                    }
                                    catch
                                    {
                                        origin = "https://kiosk.quangtri.gov.vn";
                                    }
                                }
                                string homeUrl = $"{origin.TrimEnd('/')}/vi/kiosk/home-qti?mcq={Uri.EscapeDataString(mcq ?? "")}";
                                Console.WriteLine($"[KIOSK WinForms] Đã xóa sạch phiên VNeID/SSO và điều hướng về Trang chủ: {homeUrl}");
                                webView21.CoreWebView2.Navigate(homeUrl);
                            }));
                        }
                        else if (type == "print")
                        {
                            // Xử lý in ấn
                            string title = message.title;
                            BeginInvoke(new Action(() => {
                                PrintCurrentPage(title);
                            }));
                        }
                        else if (type == "download")
                        {
                            // Xử lý download PDF
                            string pdfUrl = message.url;
                            string title = message.title;
                            string fileName = $"{SanitizeFileName(title)}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                            BeginInvoke(new Action(() => {
                                DownloadPdfFile(pdfUrl, fileName);
                            }));
                        }
                        else if (type == "KIOSK_MINISTRY_DOSSIER_SUBMITTED")
                        {
                            string currentPageUrl = webView21.Source != null ? webView21.Source.ToString() : "";
                            if (currentPageUrl.IndexOf("documentedit.aspx", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Console.WriteLine($"[KIOSK WinForms] Bỏ qua callback sớm trên trang đang soạn thảo/đính kèm file: {currentPageUrl}");
                                return;
                            }

                            string maSoHoSo = message.maSoHoSo ?? "";
                            string ministryCode = message.ministryCode ?? message.boCode ?? "";
                            string ministryTitle = message.ministryTitle ?? message.boName ?? "";

                            // Nếu cổng ngoài (VD: dichvucong.gov.vn cho 5 thủ tục Bộ Tư pháp) trả về mã mặc định 'BN', lấy mã Bộ đã lưu trước khi mở link
                            if ((string.IsNullOrEmpty(ministryCode) || ministryCode.ToUpper() == "BN") && !string.IsNullOrEmpty(cachedLastMinistryCode))
                            {
                                ministryCode = cachedLastMinistryCode;
                            }
                            if ((string.IsNullOrEmpty(ministryTitle) || ministryTitle == "Cơ quan tiếp nhận" || ministryTitle.Contains("Cổng Dịch vụ công Quốc gia")) && !string.IsNullOrEmpty(cachedLastMinistryTitle))
                            {
                                ministryTitle = cachedLastMinistryTitle;
                            }

                            Console.WriteLine($"[KIOSK WinForms] Nhận mã hồ sơ từ Bộ: {maSoHoSo} - {ministryCode} - {ministryTitle}");

                            // 1. Kích hoạt luồng chạy ngầm C# gọi API lưu xuống Backend ngay lập tức
                            _ = Task.Run(async () =>
                            {
                                await SaveKioskUsingDirectlyAsync(maSoHoSo, ministryCode, ministryTitle);
                            });

                            // 2. Lấy chính xác Origin (Scheme + Host + Port) của Web2
                            string origin = "http://localhost:4200";
                            if (!string.IsNullOrEmpty(web2))
                            {
                                try
                                {
                                    Uri web2Uri = new Uri(web2);
                                    origin = web2Uri.GetLeftPart(UriPartial.Authority);
                                }
                                catch
                                {
                                    origin = web2.TrimEnd('/');
                                }
                            }
                            else if (webView21.Source != null)
                            {
                                origin = $"{webView21.Source.Scheme}://{webView21.Source.Authority}";
                            }

                            string callbackUrl = $"{origin}/vi/kiosk/callback?maSoHoSo={Uri.EscapeDataString(maSoHoSo)}&ministryCode={Uri.EscapeDataString(ministryCode)}&ministryTitle={Uri.EscapeDataString(ministryTitle)}&saved=true";
                            cachedLastMinistryCode = "";
                            cachedLastMinistryTitle = "";

                            Console.WriteLine($"[KIOSK WinForms] Điều hướng về Callback: {callbackUrl}");
                            BeginInvoke(new Action(() =>
                            {
                                webView21.CoreWebView2.Navigate(callbackUrl);
                            }));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Lỗi xử lý message: {ex.Message}");
                    }
                };

                // Dọn sạch phiên VNeID / SSO cũ ngay khi khởi động ứng dụng Kiosk
                await ClearCitizenCookiesAsync();

                // Nếu là lần chạy đầu tiên, load WEB1 (không timer)
                if (isFirstRun)
                {
                    Console.WriteLine("First run - Loading WEB1 for configuration");
                    webView21.Source = new Uri(web1);
                }
                else
                {
                    // Các lần sau, load thẳng WEB2
                    Console.WriteLine("Subsequent run - Loading WEB2 (KIOSK mode)");
                    webView21.Source = new Uri(web2);

                    // Bắt đầu đếm thời gian refresh
                    StartRefreshTimer();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khởi tạo WebView: {ex.Message}", "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private string GenerateMongoObjectId()
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string hexTime = timestamp.ToString("x8");
            Random rnd = new Random(Guid.NewGuid().GetHashCode());
            StringBuilder sb = new StringBuilder(hexTime, 24);
            for (int i = 0; i < 16; i++)
            {
                sb.Append(rnd.Next(0, 16).ToString("x"));
            }
            return sb.ToString();
        }

        private async Task ClearVneIdOnlyAsync()
        {
            try
            {
                if (webView21?.CoreWebView2 == null)
                    return;

                string[] vneIdOrigins = new string[]
                {
                    "https://sso.dancuquocgia.gov.vn",
                    "https://dancuquocgia.gov.vn",
                    "https://vneid.gov.vn"
                };

                foreach (string origin in vneIdOrigins)
                {
                    try
                    {
                        await webView21.CoreWebView2.CallDevToolsProtocolMethodAsync(
                            "Storage.clearDataForOrigin",
                            $"{{\"origin\":\"{origin}\",\"storageTypes\":\"all\"}}");
                    }
                    catch { }

                    try
                    {
                        var cookies = await webView21.CoreWebView2.CookieManager.GetCookiesAsync(origin);
                        if (cookies != null)
                        {
                            foreach (var c in cookies)
                            {
                                webView21.CoreWebView2.CookieManager.DeleteCookie(c);
                            }
                        }
                    }
                    catch { }
                }

                Console.WriteLine("[KIOSK WinForms] Đã xóa sạch toàn bộ Storage & Cookie của VNeID (sso.dancuquocgia.gov.vn).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi ClearVneIdOnlyAsync: {ex.Message}");
            }
        }

        private async Task ClearCitizenCookiesAsync()
        {
            try
            {
                if (webView21?.CoreWebView2 != null)
                {
                    webView21.CoreWebView2.CookieManager.DeleteAllCookies();

                    try
                    {
                        await webView21.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCookies", "{}");
                    }
                    catch { }

                    string[] ssoOrigins = new string[]
                    {
                        "https://sso.dancuquocgia.gov.vn",
                        "https://dancuquocgia.gov.vn",
                        "https://vneid.gov.vn",
                        "https://id.dichvucong.gov.vn",
                        "https://dichvucong.gov.vn",
                        "https://dichvucong.quangtri.gov.vn",
                        "https://id.dichvucong.quangtri.gov.vn",
                        "https://sso.quangtri.gov.vn",
                        "https://motcua.quangtri.gov.vn",
                        "https://lgsp.quangtri.gov.vn",
                        "https://vnptigate.vn",
                        "https://id.vnpt.com.vn"
                    };

                    foreach (string origin in ssoOrigins)
                    {
                        try
                        {
                            await webView21.CoreWebView2.CallDevToolsProtocolMethodAsync(
                                "Storage.clearDataForOrigin",
                                $"{{\"origin\":\"{origin}\",\"storageTypes\":\"all\"}}");
                        }
                        catch { }
                    }

                    Console.WriteLine("[KIOSK WinForms] Đã xóa toàn bộ Cookie & Storage (localStorage/sessionStorage/IndexedDB) phiên đăng nhập VNeID/SSO/Bộ Ngành.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi khi xóa Cookie/Storage: {ex.Message}");
            }
        }

        private void CleanupAndLoadKioskUsingLogs(bool loadIntoMemory)
        {
            try
            {
                lastLogCleanupTime = DateTime.Now;
                DateTime cutoff = DateTime.Now.AddDays(-LOG_RETENTION_DAYS);
                string jsonlPath = Path.Combine(Application.StartupPath, "kiosk_using_history.jsonl");
                string logFilePath = Path.Combine(Application.StartupPath, "kiosk_using.log");

                if (!File.Exists(jsonlPath))
                {
                    if (File.Exists(logFilePath) && File.GetLastWriteTime(logFilePath) < cutoff)
                    {
                        try { File.Delete(logFilePath); } catch { }
                    }
                    return;
                }

                string[] lines = File.ReadAllLines(jsonlPath, Encoding.UTF8);
                List<string> keptLines = new List<string>();
                List<dynamic> keptObjects = new List<dynamic>();
                bool hasExpired = false;

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        dynamic obj = JsonConvert.DeserializeObject(line);
                        string timeStr = obj.time != null ? (string)obj.time : "";
                        if (DateTime.TryParse(timeStr, out DateTime entryTime))
                        {
                            if (entryTime >= cutoff)
                            {
                                keptLines.Add(line);
                                keptObjects.Add(obj);
                            }
                            else
                            {
                                hasExpired = true;
                            }
                        }
                    }
                    catch
                    {
                        hasExpired = true;
                    }
                }

                // Nếu có bản ghi quá 3 ngày -> ghi đè lại cả file .jsonl và file .log chỉ giữ log trong 3 ngày gần nhất
                if (hasExpired)
                {
                    if (keptLines.Count == 0)
                    {
                        try { File.Delete(jsonlPath); } catch { }
                        try { File.Delete(logFilePath); } catch { }
                    }
                    else
                    {
                        File.WriteAllLines(jsonlPath, keptLines, Encoding.UTF8);
                        StringBuilder sbLog = new StringBuilder();
                        foreach (dynamic item in keptObjects)
                        {
                            sbLog.AppendLine($"==================== [{(string)item.time}] ====================");
                            sbLog.AppendLine($"Nguồn   : {(string)item.source} | {(string)item.action}");
                            string u = item.url != null ? (string)item.url : "";
                            if (!string.IsNullOrEmpty(u)) sbLog.AppendLine($"URL     : {u}");
                            sbLog.AppendLine($"Trạng thái: {(string)item.status}");
                            string p = item.payload != null ? (string)item.payload : "";
                            if (!string.IsNullOrEmpty(p)) sbLog.AppendLine($"Payload : {p}");
                            string r = item.response != null ? (string)item.response : "";
                            if (!string.IsNullOrEmpty(r)) sbLog.AppendLine($"Response: {r}");
                            sbLog.AppendLine();
                        }
                        File.WriteAllText(logFilePath, sbLog.ToString(), Encoding.UTF8);
                    }
                }

                if (loadIntoMemory && keptObjects.Count > 0)
                {
                    lock (kioskUsingHistory)
                    {
                        kioskUsingHistory.Clear();
                        for (int i = keptObjects.Count - 1; i >= 0 && kioskUsingHistory.Count < 40; i--)
                        {
                            var it = keptObjects[i];
                            kioskUsingHistory.Add(new
                            {
                                time = (string)(it.time ?? ""),
                                source = (string)(it.source ?? ""),
                                action = (string)(it.action ?? ""),
                                url = (string)(it.url ?? ""),
                                status = (string)(it.status ?? ""),
                                payload = (string)(it.payload ?? ""),
                                response = (string)(it.response ?? ""),
                                ok = it.ok != null ? (bool)it.ok : true
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi dọn log 3 ngày: {ex.Message}");
            }
        }

        private void RecordAndShowKioskUsingLog(string source, string actionInfo, string url, string statusText, string requestJson, string responseBody, bool isSuccess)
        {
            try
            {
                if ((DateTime.Now - lastLogCleanupTime).TotalHours >= 1)
                {
                    CleanupAndLoadKioskUsingLogs(false);
                }

                string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = new
                {
                    time = timeStr,
                    source = source ?? "",
                    action = actionInfo ?? "",
                    url = url ?? "",
                    status = statusText ?? "",
                    payload = requestJson ?? "",
                    response = responseBody ?? "",
                    ok = isSuccess
                };

                lock (kioskUsingHistory)
                {
                    kioskUsingHistory.Insert(0, logEntry);
                    if (kioskUsingHistory.Count > 40)
                    {
                        kioskUsingHistory.RemoveAt(kioskUsingHistory.Count - 1);
                    }
                }

                try
                {
                    string jsonlPath = Path.Combine(Application.StartupPath, "kiosk_using_history.jsonl");
                    File.AppendAllText(jsonlPath, JsonConvert.SerializeObject(logEntry) + Environment.NewLine, Encoding.UTF8);

                    string logFilePath = Path.Combine(Application.StartupPath, "kiosk_using.log");
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"==================== [{timeStr}] ====================");
                    sb.AppendLine($"Nguồn   : {source} | {actionInfo}");
                    if (!string.IsNullOrEmpty(url)) sb.AppendLine($"URL     : {url}");
                    sb.AppendLine($"Trạng thái: {statusText}");
                    if (!string.IsNullOrEmpty(requestJson)) sb.AppendLine($"Payload : {requestJson}");
                    if (!string.IsNullOrEmpty(responseBody)) sb.AppendLine($"Response: {responseBody}");
                    File.AppendAllText(logFilePath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
                }
                catch { }

                // Chỉ hiển thị trực tiếp lên màn hình nếu người quản trị đang bật bảng log qua Ctrl + H
                if (isKioskUsingLogVisible)
                {
                    RenderKioskUsingOverlayInWebView();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi ghi log KioskUsing: {ex.Message}");
            }
        }

        private void OpenKioskUsingLogFile()
        {
            try
            {
                CleanupAndLoadKioskUsingLogs(false);
                string logFilePath = Path.Combine(Application.StartupPath, "kiosk_using.log");
                if (!File.Exists(logFilePath))
                {
                    File.WriteAllText(logFilePath, $"=== Chưa có log KioskUsing trong {LOG_RETENTION_DAYS} ngày gần nhất ==={Environment.NewLine}", Encoding.UTF8);
                }
                TemporarilyDisableTopMost(() =>
                {
                    Process.Start("notepad.exe", $"\"{logFilePath}\"");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi mở file log: {ex.Message}");
            }
        }

        private void RenderKioskUsingOverlayInWebView()
        {
            try
            {
                if (this.IsDisposed || webView21 == null) return;
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(RenderKioskUsingOverlayInWebView));
                    return;
                }
                if (webView21.CoreWebView2 == null) return;

                string logsJson;
                lock (kioskUsingHistory)
                {
                    logsJson = JsonConvert.SerializeObject(kioskUsingHistory);
                }

                string script = $@"
                    (function() {{
                        try {{
                            var logs = {logsJson};
                            var panel = document.getElementById('kiosk-using-debug-panel');
                            if (!panel) {{
                                panel = document.createElement('div');
                                panel.id = 'kiosk-using-debug-panel';
                                panel.style.cssText = 'position:fixed;bottom:12px;right:12px;width:620px;max-height:460px;background:rgba(15,23,42,0.97);color:#f8fafc;border:2px solid #38bdf8;border-radius:10px;z-index:2147483647;font-family:Consolas,monospace;font-size:12px;box-shadow:0 10px 30px rgba(0,0,0,0.65);display:flex;flex-direction:column;overflow:hidden;';
                                panel.innerHTML =
                                    '<div id=""kiosk-using-debug-header"" style=""background:#0284c7;color:#fff;padding:8px 12px;font-weight:bold;display:flex;justify-content:space-between;align-items:center;"">' +
                                        '<span>📡 LOG KIOSK-USING (Tự xóa sau 3 ngày) - [' + logs.length + ']</span>' +
                                        '<div>' +
                                            '<button id=""kiosk-using-btn-file"" style=""background:#0f766e;color:#fff;border:1px solid #5eead4;border-radius:4px;padding:2px 8px;margin-right:6px;cursor:pointer;font-size:11px;"">📂 Mở file .log</button>' +
                                            '<button id=""kiosk-using-btn-toggle"" style=""background:#0369a1;color:#fff;border:1px solid #7dd3fc;border-radius:4px;padding:2px 8px;margin-right:6px;cursor:pointer;font-size:11px;"">Thu gọn</button>' +
                                            '<button id=""kiosk-using-btn-close"" style=""background:#dc2626;color:#fff;border:none;border-radius:4px;padding:2px 8px;cursor:pointer;font-size:11px;"">Ẩn</button>' +
                                        '</div>' +
                                    '</div>' +
                                    '<div id=""kiosk-using-debug-body"" style=""padding:8px 10px;overflow-y:auto;max-height:400px;display:flex;flex-direction:column;gap:8px;""></div>';
                                (document.body || document.documentElement).appendChild(panel);

                                document.getElementById('kiosk-using-btn-file').onclick = function(ev) {{
                                    ev.stopPropagation();
                                    if (window.chrome && window.chrome.webview) {{
                                        window.chrome.webview.postMessage({{ type: 'KIOSK_OPEN_LOG_FILE' }});
                                    }}
                                }};
                                document.getElementById('kiosk-using-btn-toggle').onclick = function(ev) {{
                                    ev.stopPropagation();
                                    var b = document.getElementById('kiosk-using-debug-body');
                                    if (b.style.display === 'none') {{
                                        b.style.display = 'flex';
                                        this.textContent = 'Thu gọn';
                                    }} else {{
                                        b.style.display = 'none';
                                        this.textContent = 'Mở rộng';
                                    }}
                                }};
                                document.getElementById('kiosk-using-btn-close').onclick = function(ev) {{
                                    ev.stopPropagation();
                                    panel.style.display = 'none';
                                    if (window.chrome && window.chrome.webview) {{
                                        window.chrome.webview.postMessage({{ type: 'KIOSK_HIDE_LOG_PANEL' }});
                                    }}
                                }};
                            }} else {{
                                panel.style.display = 'flex';
                                var titleSpan = panel.querySelector('#kiosk-using-debug-header span');
                                if (titleSpan) titleSpan.textContent = '📡 LOG KIOSK-USING (Tự xóa sau 3 ngày) - [' + logs.length + ']';
                            }}

                            var bodyEl = document.getElementById('kiosk-using-debug-body');
                            if (bodyEl) {{
                                if (!logs.length) {{
                                    bodyEl.innerHTML = '<div style=""color:#94a3b8;padding:12px;text-align:center;"">Chưa có bản ghi KioskUsing nào trong 3 ngày gần nhất.</div>';
                                }} else {{
                                    var html = '';
                                    for (var i = 0; i < logs.length; i++) {{
                                        var item = logs[i];
                                        var badgeColor = item.status.indexOf('PENDING') !== -1 ? '#eab308' : (item.ok ? '#16a34a' : '#dc2626');
                                        var esc = function(s) {{
                                            return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
                                        }};
                                        html += '<div style=""background:#1e293b;border-left:4px solid ' + badgeColor + ';padding:6px 8px;border-radius:4px;word-break:break-all;"">' +
                                            '<div style=""display:flex;justify-content:space-between;margin-bottom:4px;"">' +
                                                '<span style=""color:#38bdf8;font-weight:bold;"">[' + esc(item.time) + '] ' + esc(item.source) + '</span>' +
                                                '<span style=""background:' + badgeColor + ';color:#fff;padding:1px 6px;border-radius:3px;font-weight:bold;"">' + esc(item.status) + '</span>' +
                                            '</div>' +
                                            '<div style=""color:#e2e8f0;margin-bottom:2px;""><b>Hành động:</b> ' + esc(item.action) + '</div>' +
                                            (item.url ? '<div style=""color:#94a3b8;font-size:11px;margin-bottom:2px;""><b>URL:</b> ' + esc(item.url) + '</div>' : '') +
                                            (item.payload ? '<div style=""color:#fde047;font-size:11px;margin-top:3px;""><b>Payload:</b> ' + esc(item.payload) + '</div>' : '') +
                                            (item.response ? '<div style=""color:#86efac;font-size:11px;margin-top:3px;""><b>Response:</b> ' + esc(item.response) + '</div>' : '') +
                                        '</div>';
                                    }}
                                    bodyEl.innerHTML = html;
                                }}
                            }}
                        }} catch (e) {{}}
                    }})();
                ";

                _ = webView21.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch { }
        }

        private async Task SaveKioskUsingDirectlyAsync(string maSoHoSo, string ministryCode, string ministryTitle)
        {
            await SaveKioskUsingActionAsync("submit_dossier", 1, ministryCode, ministryTitle, $"Nộp HS Bộ/Ngành (Mã HS: {maSoHoSo})");
        }

        private async Task SaveKioskUsingActionAsync(string actionName, int statusAction, string ministryCode, string ministryTitle, string note)
        {
            string apiUrl = ConfigurationManager.AppSettings["PADMAN_API"];
            string jsonBody = "";
            try
            {
                // 1. Sử dụng Token và Session đã được C# capture sẵn từ Kiosk (fallback đọc từ WebView2 nếu có)
                string token = cachedOAuth2Token;
                string kioskId = cachedKioskId;
                string kioskCode = cachedKioskCode;
                string sessionId = cachedSessionId;

                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(kioskId) || string.IsNullOrEmpty(kioskCode) || string.IsNullOrEmpty(sessionId))
                {
                    try
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        BeginInvoke(new Action(async () =>
                        {
                            try
                            {
                                await CaptureKioskAuthInfoAsync();
                            }
                            catch { }
                            finally
                            {
                                tcs.TrySetResult(true);
                            }
                        }));
                        await Task.WhenAny(tcs.Task, Task.Delay(1500));
                        if (string.IsNullOrEmpty(token)) token = cachedOAuth2Token;
                        if (string.IsNullOrEmpty(kioskId)) kioskId = cachedKioskId;
                        if (string.IsNullOrEmpty(kioskCode)) kioskCode = cachedKioskCode;
                        if (string.IsNullOrEmpty(sessionId)) sessionId = cachedSessionId;
                    }
                    catch { }
                }

                // Đảm bảo ObjectId hợp lệ (24 ký tự hex) cho MongoDB
                if (string.IsNullOrEmpty(kioskId) || kioskId.Length != 24)
                {
                    kioskId = "600000000000000000000001";
                }
                if (string.IsNullOrEmpty(sessionId) || sessionId.Length != 24)
                {
                    sessionId = GenerateMongoObjectId();
                    cachedSessionId = sessionId;
                }

                List<string> action = new List<string> { actionName };
                object payload;
                if (!string.IsNullOrEmpty(ministryCode) || !string.IsNullOrEmpty(ministryTitle))
                {
                    payload = new
                    {
                        kiosk_id = kioskId,
                        kiosk_code = kioskCode,
                        session_id = sessionId,
                        actions_performed = action,
                        status_action = statusAction,
                        ministryCode = ministryCode,
                        ministryTitle = ministryTitle
                    };
                }
                else
                {
                    payload = new
                    {
                        kiosk_id = kioskId,
                        kiosk_code = kioskCode,
                        session_id = sessionId,
                        actions_performed = action,
                        status_action = statusAction
                    };
                }

                jsonBody = JsonConvert.SerializeObject(payload);
                string tokenStatus = string.IsNullOrEmpty(token) ? "NO_TOKEN" : $"Bearer (len={token.Length})";
                Console.WriteLine($"[KIOSK WinForms] Gửi API KioskUsing ({note}) tới: {apiUrl} - Token: {tokenStatus} - Payload: {jsonBody}");

                RecordAndShowKioskUsingLog(
                    "C# WinForms",
                    $"{note} [Token: {tokenStatus}]",
                    apiUrl,
                    "PENDING...",
                    jsonBody,
                    "",
                    true);

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(8);
                    if (!string.IsNullOrEmpty(token))
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }

                    var content = new System.Net.Http.StringContent(jsonBody, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(apiUrl, content);
                    string respBody = "";
                    try
                    {
                        respBody = await response.Content.ReadAsStringAsync();
                    }
                    catch { }

                    Console.WriteLine($"[KIOSK WinForms] Kết quả lưu KioskUsing từ C#: StatusCode = {(int)response.StatusCode} ({response.StatusCode}) - Body = {respBody}");
                    RecordAndShowKioskUsingLog(
                        "C# WinForms",
                        $"{note} [Token: {tokenStatus}]",
                        apiUrl,
                        $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                        jsonBody,
                        respBody,
                        response.IsSuccessStatusCode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi khi C# gọi API KioskUsing ngầm: {ex.Message}");
                RecordAndShowKioskUsingLog(
                    "C# WinForms",
                    $"{note} (LỖI EXCEPTION)",
                    apiUrl,
                    "EXCEPTION",
                    jsonBody,
                    ex.Message,
                    false);
            }
        }

        private void StartRefreshTimer()
        {
            try
            {
                // Chỉ tạo refresh timer nếu đang ở chế độ KIOSK (không phải lần đầu)
                if (!isFirstRun)
                {
                    // Đảm bảo interval > 0
                    if (refreshDelayMs <= 0)
                    {
                        refreshDelayMs = 300000; // Mặc định 5 phút nếu không hợp lệ
                    }

                    refreshTimer = new Timer
                    {
                        Interval = refreshDelayMs
                    };
                    refreshTimer.Tick += RefreshTimer_Tick;
                    refreshTimer.Start();

                    Console.WriteLine($"Refresh timer started with interval: {refreshDelayMs}ms");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khởi tạo refresh timer: {ex.Message}");
            }
        }
        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            if (hasRefreshed) return;

            try
            {
                if (webView21.Source == null)
                    return;

                string currentUrl = webView21.Source.ToString().ToLower();

                // tuyệt đối KHÔNG tự động Reload nếu người dân đang ở màn hình nộp hồ sơ, tra cứu, đánh giá hoặc cổng Bộ/Ngành
                if (
                    !currentUrl.StartsWith(web2.ToLower()) ||
                    currentUrl.Contains("/nophoso") ||
                    currentUrl.Contains("/capnhathoso") ||
                    currentUrl.Contains("/tracuu") ||
                    currentUrl.Contains("/danhgia") ||
                    currentUrl.Contains("/callback")
                )
                {
                    return;
                }

                hasRefreshed = true;

                if (refreshTimer != null)
                {
                    refreshTimer.Stop();
                    refreshTimer.Tick -= RefreshTimer_Tick;
                    refreshTimer.Dispose();
                    refreshTimer = null;
                }

                webView21.CoreWebView2.Reload();
                Console.WriteLine($"Đã reload trang web at {DateTime.Now}");

                // Reset flag để có thể refresh lại lần sau
                hasRefreshed = false;

                // Khởi động lại timer
                if (refreshTimer == null)
                {
                    refreshTimer = new Timer
                    {
                        Interval = refreshDelayMs
                    };
                    refreshTimer.Tick += RefreshTimer_Tick;
                    refreshTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khi refresh: {ex.Message}");
            }
        }
        private void HideTaskbar()
        {
            try
            {
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                if (taskbarHandle != IntPtr.Zero)
                {
                    ShowWindow(taskbarHandle, 0);
                }

                // Ẩn thêm Start button nếu cần
                IntPtr startHandle = FindWindow("Button", "Start");
                if (startHandle != IntPtr.Zero)
                {
                    ShowWindow(startHandle, 0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Không thể ẩn taskbar: {ex.Message}");
            }
        }
        private void LoadIconFromResource()
        {
            try
            {
                // Cách 1: Load từ file (nếu có)
                string iconPath = Path.Combine(Application.StartupPath, "vnpt.ico");
                if (File.Exists(iconPath))
                {
                    using (var stream = File.OpenRead(iconPath))
                    {
                        this.Icon = new Icon(stream);
                    }
                    Console.WriteLine("Loaded icon from file");
                    return;
                }

                // Cách 2: Load từ embedded resource
                var assembly = Assembly.GetExecutingAssembly();
                string[] resourceNames = assembly.GetManifestResourceNames();

                // Tìm resource icon
                string iconResource = resourceNames.FirstOrDefault(r => r.EndsWith(".ico"));

                if (iconResource != null)
                {
                    using (Stream stream = assembly.GetManifestResourceStream(iconResource))
                    {
                        if (stream != null)
                        {
                            this.Icon = new Icon(stream);
                            Console.WriteLine($"Loaded icon from resource: {iconResource}");

                            // Set icon cho cả big và small
                            IntPtr iconHandle = this.Icon.Handle;
                            SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_BIG, iconHandle);
                            SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL, iconHandle);
                            return;
                        }
                    }
                }

                // Cách 3: Tạo icon tạm nếu không tìm thấy
                this.Icon = CreateDefaultIcon();
                Console.WriteLine("Using default icon");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading icon: {ex.Message}");
                this.Icon = SystemIcons.Application;
            }
        }
        private Icon CreateDefaultIcon()
        {
            Bitmap bmp = new Bitmap(64, 64);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Màu xanh VNPT
                g.Clear(Color.FromArgb(0, 85, 160));

                // Vẽ chữ VNPT
                using (Font font = new Font("Arial", 16, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("VNPT", font, brush, 8, 20);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        private void SetScreenResolution(int width, int height)
        {
            try
            {
                DEVMODE dm = new DEVMODE();
                dm.dmDeviceName = new string(new char[32]);
                dm.dmFormName = new string(new char[32]);
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                if (EnumDisplaySettings(null, -1, ref dm))
                {
                    dm.dmPelsWidth = width;
                    dm.dmPelsHeight = height;
                    dm.dmFields = 0x100000 | 0x200000; // DM_PELSWIDTH | DM_PELSHEIGHT

                    int result = ChangeDisplaySettings(ref dm, 0);

                    if (result == 0)
                    {
                        Console.WriteLine($"Đã đặt độ phân giải {width}x{height}");
                    }
                    else
                    {
                        Console.WriteLine($"Không thể đặt độ phân giải, mã lỗi: {result}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi set resolution: {ex.Message}");
            }
        }

        private void ConfigureTouchScreen()
        {
            // Cấu hình form cho màn hình cảm ứng
            this.FormBorderStyle = FormBorderStyle.None; // Không border để dễ vuốt
            this.WindowState = FormWindowState.Maximized;
            this.TopMost = false; // Luôn ở trên cùng
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ControlBox = false; // Ẩn control box
            this.Text = "HỆ THỐNG TRA CỨU - NỘP HỒ SƠ - LẤY SỐ";

            // Kích thước màn hình 1920x1080
            this.Size = new Size(1920, 1080);

            // Double buffer để tránh giật khi vuốt
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.DoubleBuffer |
                         ControlStyles.ResizeRedraw, true);
            this.UpdateStyles();
        }

        private void RegisterTouchEvents()
        {
            // Đăng ký sự kiện chuột để mô phỏng cảm ứng
            this.MouseDown += KIOSK_MouseDown;
            this.MouseMove += KIOSK_MouseMove;
            this.MouseUp += KIOSK_MouseUp;

            // Cho phép cảm ứng
            this.SetStyle(ControlStyles.StandardClick, true);
        }

        private void CreateControlPanel()
        {
            controlPanel = new Panel
            {
                Height = CONTROL_PANEL_HEIGHT,
                Width = this.Width,
                BackColor = Color.FromArgb(200, 0, 85, 160), // Màu xanh VNPT trong suốt
                Visible = false,
                Dock = DockStyle.Top
            };

            // Tạo các nút điều khiển
            int buttonWidth = 140;
            int buttonHeight = 70;
            int spacing = 15;
            int startX = 40;
            int startY = (CONTROL_PANEL_HEIGHT - buttonHeight) / 2;

            // Nút Home
            Button btnHome = CreateControlButton("🏠 Home", startX, startY, buttonWidth, buttonHeight);
            btnHome.Click += (s, e) => NavigateHome();
            controlPanel.Controls.Add(btnHome);

            // Nút Refresh
            Button btnRefresh = CreateControlButton("🔄 Refresh", startX + buttonWidth + spacing, startY, buttonWidth, buttonHeight);
            btnRefresh.Click += (s, e) => RefreshPage();
            controlPanel.Controls.Add(btnRefresh);

            // Nút Download Folder
            Button btnDownload = CreateControlButton("📁 Downloads", startX + (buttonWidth + spacing) * 2, startY, buttonWidth, buttonHeight);
            btnDownload.Click += (s, e) => OpenDownloadFolder();
            controlPanel.Controls.Add(btnDownload);

            // Nút Print Folder
            Button btnPrint = CreateControlButton("🖨️ Print", startX + (buttonWidth + spacing) * 3, startY, buttonWidth, buttonHeight);
            btnPrint.Click += (s, e) => OpenPrintFolder();
            controlPanel.Controls.Add(btnPrint);

            // Nút Cấu hình đơn vị (luôn hiển thị khi bấm Ctrl + H để cho phép chọn/set lại cấu hình đơn vị bất kỳ lúc nào)
            int nextColIndex = 4;
            Button btnUnitConfig = CreateControlButton("⚙ Cấu hình ĐV", startX + (buttonWidth + spacing) * nextColIndex, startY, buttonWidth + 25, buttonHeight);
            btnUnitConfig.Click += (s, e) => OpenUnitConfiguration();
            controlPanel.Controls.Add(btnUnitConfig);
            nextColIndex++;

            // Nút Xem Log KioskUsing (mở bảng log trên màn hình & cho phép mở file log 3 ngày gần nhất)
            Button btnViewLog = CreateControlButton("📋 Xem Log", startX + (buttonWidth + spacing) * nextColIndex + 25, startY, buttonWidth + 10, buttonHeight);
            btnViewLog.Click += (s, e) => OpenKioskUsingLogPanel();
            controlPanel.Controls.Add(btnViewLog);
            nextColIndex++;

            // Nút Mở UltraViewer hỗ trợ từ xa
            Button btnUltraCtrl = CreateControlButton("🖥 UltraViewer", startX + (buttonWidth + spacing) * nextColIndex + 35, startY, buttonWidth + 25, buttonHeight);
            btnUltraCtrl.Click += (s, e) => OpenUltraViewer();
            controlPanel.Controls.Add(btnUltraCtrl);
            nextColIndex++;

            // Nút Kiểm tra cập nhật (Update)
            string currentVer = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            Button btnUpdate = CreateControlButton($"⬆ v{currentVer}", startX + (buttonWidth + spacing) * nextColIndex + 60, startY, buttonWidth + 20, buttonHeight);
            btnUpdate.Click += async (s, e) => await CheckAndApplyUpdateAsync(true);
            controlPanel.Controls.Add(btnUpdate);

            // Nút Exit
            Button btnExit = CreateControlButton("✖ Exit", this.Width - buttonWidth - 40, startY, buttonWidth, buttonHeight);
            btnExit.Click += (s, e) => ExitApplication();
            controlPanel.Controls.Add(btnExit);

            this.Controls.Add(controlPanel);
            controlPanel.BringToFront();

            // Timer để tự động ẩn panel
            hideControlPanelTimer = new Timer { Interval = 5000 };
            hideControlPanelTimer.Tick += (s, e) =>
            {
                if (!controlPanel.IsDisposed)
                {
                    controlPanel.Visible = false;
                    hideControlPanelTimer.Stop();
                }
            };
        }

        private void StartUpdateCheckTimer()
        {
            try
            {
                // Chỉ kiểm tra cập nhật 1 lần duy nhất sau khi khởi động ứng dụng 5 giây (không chạy timer lặp để tránh nặng máy Kiosk)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    await CheckAndApplyUpdateAsync(false);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoUpdate] Lỗi kiểm tra khi khởi động: {ex.Message}");
            }
        }

        private string FindExtractedAppRoot(string dir)
        {
            if (File.Exists(Path.Combine(dir, "KIOSK.exe")))
                return dir;

            foreach (string subDir in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(subDir, "KIOSK.exe")))
                    return subDir;
            }
            return dir;
        }

        private async Task CheckAndApplyUpdateAsync(bool isManual = false)
        {
            if (isCheckingUpdate) return;
            isCheckingUpdate = true;

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                string rawUrls = ConfigurationManager.AppSettings["UPDATE_URL"];
                if (string.IsNullOrWhiteSpace(rawUrls))
                {
                    rawUrls = "http://localhost:8248/version.json,http://113.161.12.112:2480/version.json,http://113.161.12.112:248/version.json";
                }
                else if (rawUrls.IndexOf("113.161.12.112:2480", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    rawUrls = "http://113.161.12.112:2480/version.json," + rawUrls;
                }

                string[] candidateUrls = rawUrls.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                string versionJsonContent = null;
                string activeVersionUrl = null;

                using (HttpClient checkClient = new HttpClient { Timeout = TimeSpan.FromSeconds(4) })
                {
                    foreach (string rawCandidate in candidateUrls)
                    {
                        string candidate = rawCandidate.Trim();
                        if (string.IsNullOrEmpty(candidate)) continue;

                        try
                        {
                            string sep = candidate.Contains("?") ? "&" : "?";
                            string noCacheUrl = $"{candidate}{sep}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                            var resp = await checkClient.GetAsync(noCacheUrl);
                            if (resp.IsSuccessStatusCode)
                            {
                                versionJsonContent = await resp.Content.ReadAsStringAsync();
                                activeVersionUrl = candidate;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[AutoUpdate] Không kết nối được {candidate}: {ex.Message}");
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(versionJsonContent) || string.IsNullOrWhiteSpace(activeVersionUrl))
                {
                    if (isManual)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            ShowNotification("Không kết nối được máy chủ cập nhật", false);
                        }));
                    }
                    return;
                }

                dynamic meta = JsonConvert.DeserializeObject(versionJsonContent);
                string remoteVerStr = (string)(meta.version ?? meta.Version ?? "");
                string downloadUrl = (string)(meta.downloadUrl ?? meta.DownloadUrl ?? "KIOSK_Update.zip");
                bool overwriteConfig = false;
                try { overwriteConfig = (bool)(meta.overwriteConfig ?? false); } catch { }

                Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (!Version.TryParse(remoteVerStr, out Version remoteVersion))
                {
                    Console.WriteLine($"[AutoUpdate] Định dạng version không hợp lệ: {remoteVerStr}");
                    return;
                }

                Console.WriteLine($"[AutoUpdate] Current={currentVersion}, Remote={remoteVersion} (from {activeVersionUrl})");

                if (remoteVersion <= currentVersion)
                {
                    if (isManual)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            ShowNotification($"Đang ở bản mới nhất (v{currentVersion})", true);
                        }));
                    }
                    return;
                }

                // Nếu downloadUrl là đường dẫn tương đối (VD: "KIOSK_Update.zip"), tự động ghép với địa chỉ máy chủ của version.json
                if (!downloadUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Uri baseUri = new Uri(activeVersionUrl);
                    downloadUrl = new Uri(baseUri, downloadUrl).ToString();
                }

                BeginInvoke(new Action(() =>
                {
                    ShowNotification($"Đang tải bản cập nhật v{remoteVersion}...", true);
                }));

                string tempRoot = Path.Combine(Path.GetTempPath(), "KIOSK_AutoUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string zipPath = Path.Combine(tempRoot, "KIOSK_Update.zip");
                string extractDir = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(extractDir);

                using (HttpClient dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    string sep = downloadUrl.Contains("?") ? "&" : "?";
                    byte[] zipBytes = await dlClient.GetByteArrayAsync($"{downloadUrl}{sep}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                    File.WriteAllBytes(zipPath, zipBytes);
                }

                ZipFile.ExtractToDirectory(zipPath, extractDir);
                string sourceAppDir = FindExtractedAppRoot(extractDir);

                // Giữ nguyên file cấu hình riêng và file log 3 ngày của từng máy Kiosk xã/phường (trừ khi version.json bật overwriteConfig = true)
                if (!overwriteConfig)
                {
                    string[] configFilesToKeep = { "KIOSK.exe.config", "App.config", "kiosk_config.dat", "kiosk_using.log", "kiosk_using_history.jsonl" };
                    foreach (string cfg in configFilesToKeep)
                    {
                        string p = Path.Combine(sourceAppDir, cfg);
                        if (File.Exists(p))
                        {
                            try { File.Delete(p); } catch { }
                        }
                    }
                }

                string appDir = Application.StartupPath.TrimEnd('\\');
                string exePath = Application.ExecutablePath;
                int currentPid = Process.GetCurrentProcess().Id;
                string batPath = Path.Combine(tempRoot, "apply_update.bat");

                string batContent =
                    "@echo off\r\n" +
                    "chcp 65001 >nul\r\n" +
                    "timeout /t 2 /nobreak >nul\r\n" +
                    $"taskkill /F /PID {currentPid} >nul 2>&1\r\n" +
                    "timeout /t 1 /nobreak >nul\r\n" +
                    $"xcopy /E /Y /I \"{sourceAppDir}\\*\" \"{appDir}\\\" >nul\r\n" +
                    $"start \"\" \"{exePath}\"\r\n" +
                    $"rmdir /S /Q \"{tempRoot}\" >nul 2>&1\r\n";

                File.WriteAllText(batPath, batContent, new UTF8Encoding(false));

                Console.WriteLine($"[AutoUpdate] Đã sẵn sàng cập nhật lên v{remoteVersion}. Khởi động lại ứng dụng...");

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);

                BeginInvoke(new Action(() =>
                {
                    ShowTaskbar();
                    Application.Exit();
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoUpdate] Lỗi cập nhật: {ex.Message}");
                if (isManual)
                {
                    BeginInvoke(new Action(() =>
                    {
                        ShowNotification("Lỗi khi cập nhật phiên bản mới", false);
                    }));
                }
            }
            finally
            {
                isCheckingUpdate = false;
            }
        }

        // Sửa method ExitApplication (yêu cầu mật khẩu VNPT@!56 giống phím tắt thoát)
        private void ExitApplication()
        {
            btnDelete_Click(this, EventArgs.Empty);
        }
        private void ShowTaskbar()
        {
            try
            {
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                if (taskbarHandle != IntPtr.Zero)
                {
                    ShowWindow(taskbarHandle, 1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Không thể hiện taskbar: {ex.Message}");
            }
        }
        // Sửa method OpenSettings
        private void OpenSettings()
        {
            try
            {
                // Mở file cấu hình để chỉnh sửa
                string configFile = Path.Combine(Application.StartupPath, "App.config");
                if (!File.Exists(configFile))
                {
                    configFile = Path.Combine(Application.StartupPath, "KIOSK.exe.config");
                }

                if (File.Exists(configFile))
                {
                    TemporarilyDisableTopMost(() =>
                    {
                        System.Diagnostics.Process.Start("notepad.exe", configFile);
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi mở settings: {ex.Message}");
                this.TopMost = true;
            }
        }
        private Button CreateControlButton(string text, int x, int y, int width, int height)
        {
            return new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(0, 85, 160),
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
        }

        private void KIOSK_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                touchStartPoint = e.Location;
                isTouchDragging = true;
            }
        }

        private void KIOSK_MouseMove(object sender, MouseEventArgs e)
        {
            if (isTouchDragging && e.Button == MouseButtons.Left)
            {
                int deltaY = e.Y - touchStartPoint.Y;

                // Vuốt xuống từ mép trên màn hình
                if (touchStartPoint.Y < 100 && deltaY > SWIPE_THRESHOLD)
                {
                    ShowControlPanel();
                    isTouchDragging = false;
                }
            }
        }

        private void KIOSK_MouseUp(object sender, MouseEventArgs e)
        {
            isTouchDragging = false;
        }

        private void OpenKioskUsingLogPanel()
        {
            try
            {
                CleanupAndLoadKioskUsingLogs(true);
                isKioskUsingLogVisible = true;
                RenderKioskUsingOverlayInWebView();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi mở bảng log KioskUsing: {ex.Message}");
            }
        }

        private void ShowControlPanel()
        {
            if (controlPanel.InvokeRequired)
            {
                controlPanel.Invoke(new Action(ShowControlPanel));
                return;
            }

            controlPanel.Visible = true;
            controlPanel.BringToFront();

            // Khi bấm Ctrl + H -> tự động mở luôn bảng kiểm tra log KioskUsing
            OpenKioskUsingLogPanel();

            hideControlPanelTimer.Stop();
            hideControlPanelTimer.Start();
        }

        private void OpenUnitConfiguration()
        {
            try
            {
                if (controlPanel != null && !controlPanel.IsDisposed)
                {
                    controlPanel.Visible = false;
                }

                if (webView21?.CoreWebView2 != null && !string.IsNullOrWhiteSpace(web1))
                {
                    webView21.CoreWebView2.Navigate(web1);
                    ShowNotification("Đang mở màn hình cấu hình đơn vị...", true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi mở cấu hình đơn vị: {ex.Message}");
            }
        }

        private void NavigateHome()
        {
            try
            {
                if (controlPanel != null && !controlPanel.IsDisposed)
                {
                    controlPanel.Visible = false;
                }

                if (webView21.CoreWebView2 != null)
                {
                    isFirstRun = false;
                    webView21.CoreWebView2.Navigate(web2);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi navigate home: {ex.Message}");
            }
        }

        private void RefreshPage()
        {
            try
            {
                if (webView21.CoreWebView2 != null)
                {
                    webView21.CoreWebView2.Reload();
                    ShowNotification("Đang làm mới trang...", true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi refresh: {ex.Message}");
            }
        }

        private void OpenDownloadFolder()
        {
            try
            {
                if (Directory.Exists(downloadPath))
                {
                    // Tạm thời disable TopMost để folder hiển thị phía trước
                    this.TopMost = false;

                    // Mở folder và đảm bảo nó hiển thị phía trước
                    System.Diagnostics.Process.Start("explorer.exe", downloadPath);

                    // Đợi một chút để folder hiển thị rồi set lại TopMost
                    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                    timer.Interval = 2000; // 2 giây
                    timer.Tick += (s, e) =>
                    {
                        this.TopMost = false;
                        timer.Stop();
                        timer.Dispose();
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi mở download folder: {ex.Message}");
                this.TopMost = true;
            }
        }

        private void OpenPrintFolder()
        {
            try
            {
                if (Directory.Exists(printPath))
                {
                    // Tạm thời disable TopMost để folder hiển thị phía trước
                    this.TopMost = false;

                    // Mở folder và đảm bảo nó hiển thị phía trước
                    System.Diagnostics.Process.Start("explorer.exe", printPath);

                    // Đợi một chút để folder hiển thị rồi set lại TopMost
                    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                    timer.Interval = 2000; // 2 giây
                    timer.Tick += (s, e) =>
                    {
                        this.TopMost = false;
                        timer.Stop();
                        timer.Dispose();
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi mở print folder: {ex.Message}");
                this.TopMost = true;
            }
        }

        // Sửa method ShowNotification để không bị ẩn sau form
        private void ShowNotification(string message, bool isSuccess)
        {
            // Tạo form thông báo riêng thay vì label trên form
            Form notifyForm = new Form
            {
                Text = "Thông báo",
                Size = new Size(450, 150),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true, // Form thông báo cũng TopMost
                ShowInTaskbar = false,
                BackColor = Color.White
            };

            // Panel chính
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = isSuccess ? Color.FromArgb(240, 255, 240) : Color.FromArgb(255, 240, 240)
            };

            // Icon
            Label lblIcon = new Label
            {
                Text = isSuccess ? "✓" : "✗",
                Font = new Font("Segoe UI", 24, FontStyle.Bold),
                ForeColor = isSuccess ? Color.Green : Color.Red,
                Location = new Point(20, 20),
                Size = new Size(50, 50),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Message
            Label lblMessage = new Label
            {
                Text = message,
                Font = new Font("Segoe UI", 11),
                Location = new Point(80, 20),
                Size = new Size(340, 60),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Button OK
            Button btnOK = new Button
            {
                Text = "ĐÓNG",
                Location = new Point(175, 85),
                Size = new Size(100, 30),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 85, 160),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnOK.FlatAppearance.BorderSize = 0;
            btnOK.Click += (s, e) => notifyForm.Close();

            panel.Controls.Add(lblIcon);
            panel.Controls.Add(lblMessage);
            panel.Controls.Add(btnOK);
            notifyForm.Controls.Add(panel);

            // Tự động đóng sau 5 giây
            System.Windows.Forms.Timer autoCloseTimer = new System.Windows.Forms.Timer();
            autoCloseTimer.Interval = 5000;
            autoCloseTimer.Tick += (s, e) =>
            {
                autoCloseTimer.Stop();
                autoCloseTimer.Dispose();
                if (!notifyForm.IsDisposed)
                {
                    notifyForm.Close();
                }
            };
            autoCloseTimer.Start();

            notifyForm.Show();
        }

        // Sửa method PrintCurrentPage
        private async void PrintCurrentPage(string title)
        {
            try
            {
                // Tạo tên file PDF
                string fileName = $"{SanitizeFileName(title)}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    fileName += ".pdf";
                }
                string filePath = GetUniqueFilePath(Path.Combine(printPath, fileName));

                // Cấu hình in ấn
                var printSettings = webView21.CoreWebView2.Environment.CreatePrintSettings();
                printSettings.ShouldPrintBackgrounds = true;
                printSettings.ScaleFactor = 1.0;
                printSettings.Orientation = CoreWebView2PrintOrientation.Portrait;
                printSettings.PageWidth = 8.27;
                printSettings.PageHeight = 11.69;
                printSettings.MarginTop = 0.4;
                printSettings.MarginBottom = 0.4;
                printSettings.MarginLeft = 0.4;
                printSettings.MarginRight = 0.4;

                // Thực hiện in ra file PDF
                bool success = await webView21.CoreWebView2.PrintToPdfAsync(filePath, printSettings);

                if (success)
                {
                    string message = $"Đã in thành công!\nFile PDF: {fileName}\nThư mục: {printPath}";
                    ShowNotification(message, true);
                    Console.WriteLine($"Print successful: {filePath}");

                    // Hỏi người dùng có muốn mở thư mục chứa file không
                    DialogResult result = MessageBox.Show(
                        $"Đã in thành công!\nFile: {fileName}\n\nBạn có muốn mở thư mục chứa file?",
                        "In thành công",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);

                    if (result == DialogResult.Yes)
                    {
                        TemporarilyDisableTopMost(() =>
                        {
                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                        });
                    }
                }
                else
                {
                    ShowNotification("In thất bại. Vui lòng thử lại!", false);
                    Console.WriteLine("Print failed");
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"Lỗi khi in: {ex.Message}", false);
                Console.WriteLine($"Lỗi khi in: {ex.Message}");
                this.TopMost = true;
            }
        }


        // Sửa method DownloadPdfFile
        private async void DownloadPdfFile(string pdfUrl, string fileName)
        {
            try
            {
                using (var client = new WebClient())
                {
                    if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        fileName += ".pdf";
                    }
                    string filePath = GetUniqueFilePath(Path.Combine(downloadPath, fileName));

                    await client.DownloadFileTaskAsync(new Uri(pdfUrl), filePath);

                    string message = $"Đã tải file PDF:\n{fileName}\nThư mục: {downloadPath}";
                    ShowNotification(message, true);
                    Console.WriteLine($"Download successful: {filePath}");

                    // Hỏi người dùng có muốn mở thư mục không
                    DialogResult result = MessageBox.Show(
                        $"Đã tải file thành công!\nFile: {fileName}\n\nBạn có muốn mở thư mục chứa file?",
                        "Tải file thành công",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);

                    if (result == DialogResult.Yes)
                    {
                        this.TopMost = false;
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");

                        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                        timer.Interval = 2000;
                        timer.Tick += (s, e) =>
                        {
                            this.TopMost = false;
                            timer.Stop();
                            timer.Dispose();
                        };
                        timer.Start();
                    }
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"Lỗi tải PDF: {ex.Message}", false);
                Console.WriteLine($"Lỗi tải PDF: {ex.Message}");
                this.TopMost = true;
            }
        }

        // Thêm method để xử lý mở file bất kỳ
        private void OpenFileWithExplorer(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    this.TopMost = false;

                    // Mở folder và chọn file
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");

                    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                    timer.Interval = 2000;
                    timer.Tick += (s, e) =>
                    {
                        this.TopMost = false;
                        timer.Stop();
                        timer.Dispose();
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi mở file: {ex.Message}");
                this.TopMost = true;
            }
        }
        //protected override void OnFormClosing(FormClosingEventArgs e)
        //{
        //    // Hiện lại taskbar khi ứng dụng đóng
        //    ShowTaskbar();

        //    // Dọn dẹp timer
        //    if (refreshTimer != null)
        //    {
        //        refreshTimer.Stop();
        //        refreshTimer.Dispose();
        //    }

        //    if (hideControlPanelTimer != null)
        //    {
        //        hideControlPanelTimer.Stop();
        //        hideControlPanelTimer.Dispose();
        //    }

        //    base.OnFormClosing(e);
        //}

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Giữ nguyên code xử lý phím tắt...
            // Chặn Alt+F4
            if (keyData == (Keys.Alt | Keys.F4))
            {
                return true;
            }

            // Chặn Ctrl+Alt+Del
            if (keyData == (Keys.Control | Keys.Alt | Keys.Delete))
            {
                return true;
            }

            // Chặn Alt+Tab
            if (keyData == (Keys.Alt | Keys.Tab))
            {
                return true;
            }

            // Chặn Windows key
            if (keyData == Keys.LWin || keyData == Keys.RWin)
            {
                return true;
            }

            // Thêm phím tắt Ctrl+Q để thoát
            if (keyData == (Keys.Control | Keys.Q))
            {
                ExitApplication();
                return true;
            }

            // Phím tắt Ctrl+H để hiện control panel
            if (keyData == (Keys.Control | Keys.H))
            {
                ShowControlPanel();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }
        private void KIOSK_Load_1(object sender, EventArgs e)
        {

        }

        // Sửa method btnDelete_Click
        private void btnDelete_Click(object sender, EventArgs e)
        {
            if (isPasswordDialogOpen) return;
            isPasswordDialogOpen = true;

            try
            {
                // Tạm thời tắt TopMost để hiển thị form nhập mật khẩu
                this.TopMost = false;

                // Tạo form nhập mật khẩu
                using (Form passwordForm = new Form())
                {
                    passwordForm.Text = "XÁC NHẬN THOÁT";
                    passwordForm.Size = new Size(400, 250);
                    passwordForm.StartPosition = FormStartPosition.CenterScreen;
                    passwordForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                    passwordForm.MaximizeBox = false;
                    passwordForm.MinimizeBox = false;
                    passwordForm.TopMost = true; // Form nhập mật khẩu TopMost
                    passwordForm.ShowInTaskbar = true;
                    passwordForm.ControlBox = true;

                    // Icon cho form
                    if (this.Icon != null)
                    {
                        passwordForm.Icon = this.Icon;
                    }

                    // Panel chính
                    Panel mainPanel = new Panel
                    {
                        Dock = DockStyle.Fill,
                        Padding = new Padding(20),
                        BackColor = Color.White
                    };

                    // Panel cho nội dung
                    Panel contentPanel = new Panel
                    {
                        Dock = DockStyle.Fill,
                        Padding = new Padding(10)
                    };

                    // Label hướng dẫn
                    Label lblMessage = new Label
                    {
                        Text = "Vui lòng nhập mật khẩu:",
                        Location = new Point(20, 20),
                        Size = new Size(340, 25),
                        Font = new Font("Segoe UI", 11)
                    };

                    // TextBox nhập mật khẩu
                    TextBox txtPassword = new TextBox
                    {
                        Location = new Point(20, 55),
                        Size = new Size(340, 30),
                        Font = new Font("Segoe UI", 12),
                        PasswordChar = '*',
                        UseSystemPasswordChar = true
                    };
                    txtPassword.Focus();

                    // Panel cho các nút
                    Panel buttonPanel = new Panel
                    {
                        Location = new Point(20, 100),
                        Size = new Size(340, 50)
                    };

                    // Nút OK
                    Button btnOK = new Button
                    {
                        Text = "ĐỒNG Ý",
                        Location = new Point(40, 10),
                        Size = new Size(120, 35),
                        Font = new Font("Segoe UI", 11, FontStyle.Bold),
                        BackColor = Color.FromArgb(0, 85, 160),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Cursor = Cursors.Hand,
                        DialogResult = DialogResult.OK
                    };
                    btnOK.FlatAppearance.BorderSize = 0;

                    // Nút Hủy
                    Button btnCancel = new Button
                    {
                        Text = "HỦY",
                        Location = new Point(180, 10),
                        Size = new Size(120, 35),
                        Font = new Font("Segoe UI", 11, FontStyle.Bold),
                        BackColor = Color.Gray,
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Cursor = Cursors.Hand,
                        DialogResult = DialogResult.Cancel
                    };
                    btnCancel.FlatAppearance.BorderSize = 0;

                    buttonPanel.Controls.Add(btnOK);
                    buttonPanel.Controls.Add(btnCancel);

                    contentPanel.Controls.Add(lblMessage);
                    contentPanel.Controls.Add(txtPassword);
                    contentPanel.Controls.Add(buttonPanel);

                    mainPanel.Controls.Add(contentPanel);
                    passwordForm.Controls.Add(mainPanel);

                    passwordForm.AcceptButton = btnOK;
                    passwordForm.CancelButton = btnCancel;

                    DialogResult result = passwordForm.ShowDialog(this);

                    if (result == DialogResult.OK)
                    {
                        if (txtPassword.Text == "VNPT@!56")
                        {
                            DialogResult confirm = MessageBox.Show(
                                "Bạn có chắc chắn muốn thoát ứng dụng?",
                                "Xác nhận",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2);

                            if (confirm == DialogResult.Yes)
                            {
                                UninstallKeyboardHook();
                                ShowTaskbar();
                                Application.Exit();
                            }
                            else
                            {
                                this.TopMost = true; // Set lại TopMost nếu không thoát
                            }
                        }
                        else
                        {
                            MessageBox.Show("Mật khẩu không đúng!\nVui lòng thử lại.",
                                "Lỗi",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            this.TopMost = true; // Set lại TopMost
                        }
                    }
                    else
                    {
                        this.TopMost = true; // Set lại TopMost nếu hủy
                    }
                }
            }
            finally
            {
                isPasswordDialogOpen = false;
            }
        }
        private void btnMini_Click(object sender, EventArgs e)
        {
            if (isPasswordDialogOpen) return;
            isPasswordDialogOpen = true;

            try
            {
                // Tạm thời tắt TopMost để hiển thị form nhập mật khẩu
                this.TopMost = false;

                // Tạo form nhập mật khẩu
                using (Form passwordForm = new Form())
                {
                    passwordForm.Text = "XÁC NHẬN THU NHỎ";
                    passwordForm.Size = new Size(400, 250);
                    passwordForm.StartPosition = FormStartPosition.CenterScreen;
                    passwordForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                    passwordForm.MaximizeBox = false;
                    passwordForm.MinimizeBox = false;
                    passwordForm.TopMost = true; // Form nhập mật khẩu TopMost
                    passwordForm.ShowInTaskbar = true;
                    passwordForm.ControlBox = true;

                    // Icon cho form
                    if (this.Icon != null)
                    {
                        passwordForm.Icon = this.Icon;
                    }

                    // Panel chính
                    Panel mainPanel = new Panel
                    {
                        Dock = DockStyle.Fill,
                        Padding = new Padding(20),
                        BackColor = Color.White
                    };

                    // Panel cho nội dung
                    Panel contentPanel = new Panel
                    {
                        Dock = DockStyle.Fill,
                        Padding = new Padding(10)
                    };

                    // Label hướng dẫn
                    Label lblMessage = new Label
                    {
                        Text = "Vui lòng nhập mật khẩu để thu nhỏ:",
                        Location = new Point(20, 20),
                        Size = new Size(340, 25),
                        Font = new Font("Segoe UI", 11)
                    };

                    // TextBox nhập mật khẩu
                    TextBox txtPassword = new TextBox
                    {
                        Location = new Point(20, 55),
                        Size = new Size(340, 30),
                        Font = new Font("Segoe UI", 12),
                        PasswordChar = '*',
                        UseSystemPasswordChar = true
                    };
                    txtPassword.Focus();

                    // Panel cho các nút
                    Panel buttonPanel = new Panel
                    {
                        Location = new Point(20, 100),
                        Size = new Size(340, 50)
                    };

                    // Nút OK
                    Button btnOK = new Button
                    {
                        Text = "ĐỒNG Ý",
                        Location = new Point(40, 10),
                        Size = new Size(120, 35),
                        Font = new Font("Segoe UI", 11, FontStyle.Bold),
                        BackColor = Color.FromArgb(0, 85, 160),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Cursor = Cursors.Hand,
                        DialogResult = DialogResult.OK
                    };
                    btnOK.FlatAppearance.BorderSize = 0;

                    // Nút Hủy
                    Button btnCancel = new Button
                    {
                        Text = "HỦY",
                        Location = new Point(180, 10),
                        Size = new Size(120, 35),
                        Font = new Font("Segoe UI", 11, FontStyle.Bold),
                        BackColor = Color.Gray,
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Cursor = Cursors.Hand,
                        DialogResult = DialogResult.Cancel
                    };
                    btnCancel.FlatAppearance.BorderSize = 0;

                    buttonPanel.Controls.Add(btnOK);
                    buttonPanel.Controls.Add(btnCancel);

                    contentPanel.Controls.Add(lblMessage);
                    contentPanel.Controls.Add(txtPassword);
                    contentPanel.Controls.Add(buttonPanel);

                    mainPanel.Controls.Add(contentPanel);
                    passwordForm.Controls.Add(mainPanel);

                    passwordForm.AcceptButton = btnOK;
                    passwordForm.CancelButton = btnCancel;

                    DialogResult result = passwordForm.ShowDialog(this);

                    if (result == DialogResult.OK)
                    {
                        if (txtPassword.Text == "VNPT@!56")
                        {
                            DialogResult confirm = MessageBox.Show(
                                "Bạn có chắc chắn muốn thu nhỏ ứng dụng?",
                                "Xác nhận",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2);

                            if (confirm == DialogResult.Yes)
                            {
                                // Hiện taskbar trước khi thu nhỏ
                                ShowTaskbar();

                                // Thu nhỏ form
                                this.WindowState = FormWindowState.Minimized;

                                // Log để debug
                                Console.WriteLine($"Application minimized at {DateTime.Now}");
                            }
                            else
                            {
                                this.TopMost = true; // Set lại TopMost nếu không thu nhỏ
                            }
                        }
                        else
                        {
                            MessageBox.Show("Mật khẩu không đúng!\nVui lòng thử lại.",
                                "Lỗi",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            this.TopMost = true; // Set lại TopMost
                        }
                    }
                    else
                    {
                        this.TopMost = true; // Set lại TopMost nếu hủy
                    }
                }
            }
            finally
            {
                isPasswordDialogOpen = false;
            }
        }
        // Thêm method để khôi phục ứng dụng
        private void RestoreApplication()
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                // Ẩn taskbar lại khi khôi phục
                HideTaskbar();

                this.WindowState = FormWindowState.Normal;
                this.WindowState = FormWindowState.Maximized; // Đảm bảo fullscreen
                this.Show();
                this.BringToFront();
                SetForegroundWindow(this.Handle);
            }
        }

        // Sửa method OnResize để xử lý đúng
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            // Khi form được khôi phục từ minimized
            if (this.WindowState == FormWindowState.Normal || this.WindowState == FormWindowState.Maximized)
            {
                // Ẩn taskbar
                HideTaskbar();

                // Đảm bảo fullscreen
                if (this.WindowState != FormWindowState.Maximized)
                {
                    this.WindowState = FormWindowState.Maximized;
                }
            }
            // Khi form bị thu nhỏ
            else if (this.WindowState == FormWindowState.Minimized)
            {
                // Taskbar đã được hiện trong btnMini_Click
                // Không cần làm gì thêm
            }

            // Cập nhật vị trí các nút
            PositionControlButtons();
        }

        // Thêm xử lý sự kiện Activated để đảm bảo taskbar ẩn khi form active
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);

            // Khi form được kích hoạt, ẩn taskbar
            if (this.WindowState != FormWindowState.Minimized)
            {
                HideTaskbar();
            }
        }

        // Thêm xử lý sự kiện Deactivate để hiện taskbar khi mất focus
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            // Chỉ hiện taskbar nếu form không bị thu nhỏ
            // Tránh trường hợp user click sang ứng dụng khác
            if (this.WindowState != FormWindowState.Minimized)
            {
                // Không hiện taskbar khi mất focus để tránh lộ taskbar
                // User có thể dùng Alt+Tab để chuyển ứng dụng
            }
        }
        private void ConfigureHomeButton()
        {
            btnHome.Text = "";
            btnHome.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            btnHome.Size = new Size(50, 50);

            btnHome.BackColor = Color.Transparent;
            btnHome.UseVisualStyleBackColor = false;

            btnHome.ForeColor = Color.Transparent;
            btnHome.FlatStyle = FlatStyle.Flat;

            btnHome.FlatAppearance.BorderSize = 0;
            btnHome.FlatAppearance.MouseOverBackColor = Color.Transparent;
            btnHome.FlatAppearance.MouseDownBackColor = Color.Transparent;

            btnHome.Cursor = Cursors.Hand;

            // vẽ icon Home
            btnHome.Paint += (s, e) =>
            {
                Button btn = s as Button;
                if (btn != null)
                {
                    using (Pen pen = new Pen(Color.White, 2))
                    {
                        // mái nhà
                        e.Graphics.DrawLine(pen, btn.Width / 2, 6, 6, btn.Height / 2);
                        e.Graphics.DrawLine(pen, btn.Width / 2, 6, btn.Width - 6, btn.Height / 2);

                        // thân nhà
                        e.Graphics.DrawRectangle(pen, 8, btn.Height / 2, btn.Width - 16, btn.Height / 2 - 8);
                    }
                }
            };

            PositionHomeButton();

            this.Resize += (s, e) => PositionHomeButton();

            btnHome.BringToFront();
        }
        private void PositionHomeButton()
        {
            if (btnHome == null || btnHome.IsDisposed) return;

            btnHome.Location = new Point(
                20,
                this.ClientSize.Height - btnHome.Height - 5
            );

            Console.WriteLine($"Home button positioned at: {btnHome.Location}");
        }

        private void ConfigureUltraViewerButton()
        {
            btnUltraViewer = new Button
            {
                Text = "🎧 Hỗ trợ (UltraViewer)",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Size = new Size(165, 38),
                BackColor = Color.FromArgb(14, 116, 144),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleCenter
            };
            btnUltraViewer.FlatAppearance.BorderSize = 1;
            btnUltraViewer.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
            btnUltraViewer.FlatAppearance.MouseOverBackColor = Color.FromArgb(2, 132, 199);
            btnUltraViewer.FlatAppearance.MouseDownBackColor = Color.FromArgb(3, 105, 161);

            btnUltraViewer.Click += (s, e) => OpenUltraViewer();

            this.Controls.Add(btnUltraViewer);
            PositionUltraViewerButton();

            this.Resize += (s, e) => PositionUltraViewerButton();

            btnUltraViewer.BringToFront();
        }

        private void PositionUltraViewerButton()
        {
            if (btnUltraViewer == null || btnUltraViewer.IsDisposed) return;

            btnUltraViewer.Location = new Point(
                this.ClientSize.Width - btnUltraViewer.Width - 20,
                this.ClientSize.Height - btnUltraViewer.Height - 8
            );
            btnUltraViewer.BringToFront();
        }

        private string FindUltraViewerExecutable()
        {
            try
            {
                string customPath = ConfigurationManager.AppSettings["ULTRAVIEWER_PATH"];
                if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
                {
                    return customPath;
                }
            }
            catch { }

            string[] candidatePaths = new string[]
            {
                @"C:\Program Files (x86)\UltraViewer\UltraViewer_Desktop.exe",
                @"C:\Program Files\UltraViewer\UltraViewer_Desktop.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "UltraViewer", "UltraViewer_Desktop.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UltraViewer", "UltraViewer_Desktop.exe"),
                @"D:\UltraViewer\UltraViewer_Desktop.exe",
                @"D:\Program Files (x86)\UltraViewer\UltraViewer_Desktop.exe",
                @"D:\Program Files\UltraViewer\UltraViewer_Desktop.exe"
            };

            foreach (string path in candidatePaths)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch { }
            }

            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\UltraViewer_is1")
                              ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UltraViewer_is1"))
                {
                    if (key != null)
                    {
                        string installLoc = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(installLoc))
                        {
                            string exe = Path.Combine(installLoc.Trim('\"'), "UltraViewer_Desktop.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private void OpenUltraViewer()
        {
            try
            {
                if (controlPanel != null && !controlPanel.IsDisposed)
                {
                    controlPanel.Visible = false;
                }

                string exePath = FindUltraViewerExecutable();
                if (string.IsNullOrEmpty(exePath))
                {
                    ShowNotification("Máy Kiosk này chưa cài đặt phần mềm UltraViewer!", false);
                    return;
                }

                // Tắt TopMost của Kiosk để cửa sổ UltraViewer nổi lên trên màn hình Kiosk
                this.TopMost = false;

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = true
                };
                Process.Start(psi);

                // Đảm bảo nếu UltraViewer đang ẩn dưới khay hệ thống (System Tray) hoặc bị thu nhỏ thì sẽ bật nổi lên trước màn hình
                _ = Task.Run(async () =>
                {
                    int[] delays = new int[] { 300, 900, 1800 };
                    foreach (int d in delays)
                    {
                        await Task.Delay(d);
                        try
                        {
                            Process[] procs = Process.GetProcessesByName("UltraViewer_Desktop");
                            foreach (Process p in procs)
                            {
                                if (p.MainWindowHandle != IntPtr.Zero)
                                {
                                    ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE = 9
                                    SetForegroundWindow(p.MainWindowHandle);
                                }
                            }
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KIOSK WinForms] Lỗi mở UltraViewer: {ex.Message}");
                ShowNotification($"Không thể mở UltraViewer: {ex.Message}", false);
            }
        }

        private async void btnHome_Click(object sender, EventArgs e)
        {
            try
            {
                if (webView21.CoreWebView2 != null)
                {
                    // Khi bấm nút Trang chủ (Home): tuyệt đối KHÔNG xóa cookie và KHÔNG đăng xuất phiên công dân/cán bộ.
                    // Nếu đang ở trên domain Kiosk, ưu tiên điều hướng SPA về /kiosk/home để giữ nguyên phiên làm việc.
                    string currentUrl = webView21.Source != null ? webView21.Source.ToString() : "";
                    string currentLower = currentUrl.ToLower();
                    string kioskHost = "";
                    string kioskOrigin = "";
                    try
                    {
                        if (!string.IsNullOrEmpty(web2))
                        {
                            Uri w2 = new Uri(web2);
                            kioskHost = w2.Host.ToLower();
                            kioskOrigin = w2.GetLeftPart(UriPartial.Authority);
                        }
                    }
                    catch { }

                    bool isOnKiosk = (!string.IsNullOrEmpty(kioskHost) && currentLower.Contains(kioskHost))
                                     || currentLower.Contains("kiosk.")
                                     || currentLower.Contains("localhost");

                    if (isOnKiosk)
                    {
                        string spaResult = await webView21.CoreWebView2.ExecuteScriptAsync(@"
                            (function() {
                                try {
                                    if (window.location.pathname.indexOf('/kiosk/home') !== -1) {
                                        return 'ALREADY_HOME';
                                    }
                                    var homeLinks = document.querySelectorAll('a[href*=""/kiosk/home""]');
                                    for (var i = 0; i < homeLinks.length; i++) {
                                        var a = homeLinks[i];
                                        var href = a.getAttribute('href') || '';
                                        if (!a.classList.contains('-dangxuat') && href.indexOf('logout=true') === -1) {
                                            a.removeAttribute('onclick');
                                            a.onclick = null;
                                            a.click();
                                            return 'SPA_NAVIGATED';
                                        }
                                    }
                                } catch (e) {}
                                return 'FALLBACK';
                            })();
                        ");
                        if (spaResult != null && (spaResult.Contains("ALREADY_HOME") || spaResult.Contains("SPA_NAVIGATED")))
                        {
                            return;
                        }
                    }

                    // Nếu đang ở trên cổng Bộ/Ngành (hoặc không tìm thấy link SPA), điều hướng về trang chủ Kiosk mà KHÔNG xóa cookie
                    string targetUrl = web2;
                    if (!string.IsNullOrEmpty(kioskOrigin))
                    {
                        targetUrl = !string.IsNullOrEmpty(cachedKioskCode)
                            ? $"{kioskOrigin}/vi/kiosk/home?mcq={Uri.EscapeDataString(cachedKioskCode)}"
                            : $"{kioskOrigin}/vi/kiosk/home";
                    }
                    webView21.CoreWebView2.Navigate(targetUrl);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Home navigation error: {ex.Message}");
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Nếu đóng bằng Application.Exit thì cho phép
            if (e.CloseReason == CloseReason.ApplicationExitCall)
            {
                UninstallKeyboardHook();
                ShowTaskbar();
                base.OnFormClosing(e);
                return;
            }

            // Nếu người dùng cố tắt bằng Alt+F4 / Taskbar / X
            e.Cancel = true;

            if (isPasswordDialogOpen)
            {
                base.OnFormClosing(e);
                return;
            }

            isPasswordDialogOpen = true;
            try
            {
                this.TopMost = false;

                using (Form passwordForm = new Form())
                {
                    passwordForm.Text = "XÁC NHẬN THOÁT";
                    passwordForm.Size = new Size(350, 200);
                    passwordForm.StartPosition = FormStartPosition.CenterScreen;
                    passwordForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                    passwordForm.MaximizeBox = false;
                    passwordForm.MinimizeBox = false;
                    passwordForm.TopMost = true;

                    Label lbl = new Label()
                    {
                        Text = "Nhập mật khẩu để thoát:",
                        Location = new Point(20, 20),
                        Size = new Size(300, 25)
                    };

                    TextBox txtPassword = new TextBox()
                    {
                        Location = new Point(20, 50),
                        Width = 280,
                        PasswordChar = '*'
                    };

                    Button btnOK = new Button()
                    {
                        Text = "Đồng ý",
                        Location = new Point(60, 100),
                        Width = 100
                    };

                    Button btnCancel = new Button()
                    {
                        Text = "Hủy",
                        Location = new Point(180, 100),
                        Width = 100
                    };

                    passwordForm.Controls.Add(lbl);
                    passwordForm.Controls.Add(txtPassword);
                    passwordForm.Controls.Add(btnOK);
                    passwordForm.Controls.Add(btnCancel);

                    bool allowClose = false;

                    btnOK.Click += (s, ev) =>
                    {
                        if (txtPassword.Text == "VNPT@!56")
                        {
                            allowClose = true;
                            passwordForm.Close();
                        }
                        else
                        {
                            MessageBox.Show("Mật khẩu không đúng!");
                        }
                    };

                    btnCancel.Click += (s, ev) =>
                    {
                        passwordForm.Close();
                    };

                    passwordForm.ShowDialog();

                    if (allowClose)
                    {
                        e.Cancel = false;
                        UninstallKeyboardHook();
                        ShowTaskbar();
                    }
                    else
                    {
                        this.TopMost = true;
                    }
                }
            }
            finally
            {
                isPasswordDialogOpen = false;
            }

            base.OnFormClosing(e);
        }
    }
}