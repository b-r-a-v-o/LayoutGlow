// LayoutGlow - tray app for Bloody B180R (and KD2-era Bloody keyboards).
//  - Double-tap Left Shift: convert selected text EN<->RU layout (or last word).
//  - Backlight follows input layout: EN = green, RU = red (configurable).
// Build: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe -out:LayoutGlow.exe LayoutGlow.cs -r:System.Windows.Forms.dll -r:System.Drawing.dll
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LayoutGlow
{
    // ---------------- Settings ----------------
    internal sealed class Settings
    {
        public bool ConvertEnabled = true;
        public bool BacklightEnabled = true;
        public int DoubleTapMs = 600;
        public byte EnR = 0, EnG = 255, EnB = 0;
        public byte RuR = 255, RuG = 0, RuB = 0;
        public bool SwitchLayoutAfterConvert = true;
        public bool LogEnabled = false; // debug log to LayoutGlow.log next to the exe

        public static string PathName
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LayoutGlow.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(PathName)) return s;
                foreach (var line in File.ReadAllLines(PathName))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    var k = parts[0].Trim().ToLowerInvariant();
                    var v = parts[1].Trim();
                    if (k == "convertenabled") s.ConvertEnabled = v == "1";
                    else if (k == "backlightenabled") s.BacklightEnabled = v == "1";
                    else if (k == "doubletapms") s.DoubleTapMs = int.Parse(v);
                    else if (k == "switchlayout") s.SwitchLayoutAfterConvert = v == "1";
                    else if (k == "logenabled") s.LogEnabled = v == "1";
                    else if (k == "encolor") ParseColor(v, out s.EnR, out s.EnG, out s.EnB);
                    else if (k == "rucolor") ParseColor(v, out s.RuR, out s.RuG, out s.RuB);
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("ConvertEnabled=" + (ConvertEnabled ? "1" : "0"));
                sb.AppendLine("BacklightEnabled=" + (BacklightEnabled ? "1" : "0"));
                sb.AppendLine("DoubleTapMs=" + DoubleTapMs);
                sb.AppendLine("SwitchLayout=" + (SwitchLayoutAfterConvert ? "1" : "0"));
                sb.AppendLine("LogEnabled=" + (LogEnabled ? "1" : "0"));
                sb.AppendLine("EnColor=" + EnR + "," + EnG + "," + EnB);
                sb.AppendLine("RuColor=" + RuR + "," + RuG + "," + RuB);
                File.WriteAllText(PathName, sb.ToString());
            }
            catch { }
        }

        private static void ParseColor(string v, out byte r, out byte g, out byte b)
        {
            r = 0; g = 0; b = 0;
            var p = v.Split(',');
            if (p.Length == 3)
            {
                byte.TryParse(p[0], out r);
                byte.TryParse(p[1], out g);
                byte.TryParse(p[2], out b);
            }
        }
    }

    // ---------------- Bloody KD2-protocol backlight ----------------
    internal sealed class BloodyBacklight : IDisposable
    {
        private const ushort VendorId = 0x09DA;
        private const string PreferPid = "pid_fa14"; // B180R/B338R keyboard; the mouse is 54A7
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const int ErrorNoMoreItems = 259;
        private const int ReportLength = 64;

        private readonly object _sync = new object();
        private readonly Thread _worker;
        private bool _disposed;
        private bool _initialized;
        private bool _enabled;   // false = do not touch the keyboard at all
        private bool _dirty;
        private byte _cr, _cg, _cb;
        private const int PollMs = 50;
        private const int KeepaliveMs = 5000; // re-send color periodically (recovery after keyboard replug)
        private int _sinceLastSend = KeepaliveMs;

        public BloodyBacklight()
        {
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Start();
        }

        public bool DeviceFound { get; private set; }

        // Master switch for the debug log, set once at startup from Settings.
        public static bool LoggingEnabled;

        internal static void Log(string msg)
        {
            if (!LoggingEnabled) return;
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LayoutGlow.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        // Sets the target color; the worker pushes it to the keyboard (firmware holds it after commit).
        public void SetColor(byte r, byte g, byte b)
        {
            lock (_sync)
            {
                _cr = r; _cg = g; _cb = b;
                _enabled = true;
                _dirty = true;
            }
        }

        // Fully disables backlight control: no device enumeration, no HID traffic.
        public void StopStreaming()
        {
            lock (_sync)
            {
                _enabled = false;
                _dirty = false;
                _initialized = false;
                DeviceFound = false;
            }
        }

        private void WorkerLoop()
        {
            while (!_disposed)
            {
                bool send;
                byte r = 0, g = 0, b = 0;
                lock (_sync)
                {
                    _sinceLastSend += PollMs;
                    send = _enabled && (_dirty || (_initialized && _sinceLastSend >= KeepaliveMs));
                    if (send)
                    {
                        r = _cr; g = _cg; b = _cb;
                        _dirty = false;
                        _sinceLastSend = 0;
                    }
                }
                if (send)
                {
                    try { SendFrameOnce(r, g, b); }
                    catch (Exception ex)
                    {
                        _initialized = false;
                        lock (_sync) _dirty = true; // retry
                        Log("backlight error: " + ex.Message);
                        Thread.Sleep(1000);
                    }
                }
                Thread.Sleep(PollMs);
            }
        }

        private void SendFrameOnce(byte r, byte g, byte b)
        {
            string path = FindLightingInterface();
            if (path == null)
            {
                if (DeviceFound) Log("backlight: device lost");
                DeviceFound = false;
                _initialized = false;
                Thread.Sleep(2000);
                return;
            }
            if (!DeviceFound) Log("backlight: device found, color R=" + r + " G=" + g + " B=" + b);
            DeviceFound = true;
            using (var handle = OpenForWrite(path))
            {
                if (!_initialized)
                {
                    SendReports(handle, BuildInitReports(), 8);
                    _initialized = true;
                    Log("backlight: init sent");
                }
                // Exactly like KeyDominator2: single 07 03 06 02 frame with RGB triplets
                // at offset 8, then 07 05 commit. The firmware holds the color afterwards.
                var frame = Packet(0x07, 0x03, 0x06, 0x02, 0x00, 0x00, 0x00, 0x00);
                for (int i = 8; i + 2 < ReportLength; i += 3)
                {
                    frame[i] = r; frame[i + 1] = g; frame[i + 2] = b;
                }
                SendReports(handle, new[] { frame, Packet(0x07, 0x05) }, 5);
            }
        }

        private static void SendReports(SafeFileHandle handle, IEnumerable<byte[]> reports, int delayMs)
        {
            foreach (var report in reports)
            {
                if (!HidD_SetFeature(handle, report, report.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (delayMs > 0) Thread.Sleep(delayMs);
            }
        }

        private static byte[] Packet(params byte[] prefix)
        {
            var report = new byte[ReportLength];
            Buffer.BlockCopy(prefix, 0, report, 0, prefix.Length);
            return report;
        }

        private static byte[][] BuildInitReports()
        {
            return new[]
            {
                Packet(0x07, 0x1F), Packet(0x07, 0x1F), Packet(0x07, 0x05), Packet(0x07, 0x1F),
                Packet(0x07, 0x1F), Packet(0x07, 0x1F), Packet(0x07, 0x29), Packet(0x07, 0x1F),
                Packet(0x07, 0x1F), Packet(0x07, 0x1F), Packet(0x07, 0x05), Packet(0x07, 0x07),
                Packet(0x07, 0x1F), Packet(0x07, 0x1F), Packet(0x07, 0x2A), Packet(0x07, 0x1F),
                Packet(0x07, 0x1F), Packet(0x07, 0x2A), Packet(0x07, 0x1F), Packet(0x07, 0x1F),
                Packet(0x07, 0x29), Packet(0x07, 0x1E, 0x01),
                Packet(0x07, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01),
                Packet(0x07, 0x05), Packet(0x07, 0x2F, 0x00, 0x2E),
                Packet(0x07, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x80),
                Packet(0x07, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x80),
                Packet(0x07, 0x03, 0x06, 0x05), Packet(0x07, 0x06),
                Packet(0x07, 0x03, 0x06, 0x01), Packet(0x07, 0x03, 0x06, 0x05),
                Packet(0x07, 0x03, 0x06, 0x01), Packet(0x07, 0x03, 0x06, 0x02),
                Packet(0x07, 0x05)
            };
        }

        private static string FindLightingInterface()
        {
            string fallback = null;
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            var infoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (infoSet == new IntPtr(-1)) return null;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var interfaceData = new SpDeviceInterfaceData();
                    interfaceData.Size = Marshal.SizeOf(typeof(SpDeviceInterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    {
                        if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                        continue;
                    }
                    int requiredSize;
                    SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                    var detailBuffer = Marshal.AllocHGlobal(requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, detailBuffer, requiredSize, out requiredSize, IntPtr.Zero))
                            continue;
                        var path = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
                        if (String.IsNullOrEmpty(path)) continue;
                        var lower = path.ToLowerInvariant();
                        if (!lower.Contains("vid_09da") || !lower.Contains("col04")) continue;
                        if (!HasFeatureReport(path)) continue;
                        if (lower.Contains(PreferPid)) return path;
                        if (fallback == null) fallback = path;
                    }
                    finally { Marshal.FreeHGlobal(detailBuffer); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(infoSet); }
            return fallback;
        }

        private static bool HasFeatureReport(string path)
        {
            using (var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                var attributes = new HiddAttributes();
                attributes.Size = Marshal.SizeOf(typeof(HiddAttributes));
                if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != VendorId) return false;
                IntPtr preparsed;
                if (!HidD_GetPreparsedData(handle, out preparsed)) return false;
                try
                {
                    HidpCaps caps;
                    return HidP_GetCaps(preparsed, out caps) >= 0 && caps.FeatureReportByteLength >= ReportLength;
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }
        }

        private static SafeFileHandle OpenForWrite(string path)
        {
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            handle = CreateFile(path, GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(err);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HiddAttributes { public int Size; public ushort VendorId; public ushort ProductId; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public short Usage; public short UsagePage;
            public short InputReportByteLength; public short OutputReportByteLength; public short FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public short[] Reserved;
            public short NumberLinkCollectionNodes;
            public short NumberInputButtonCaps; public short NumberInputValueCaps; public short NumberInputDataIndices;
            public short NumberOutputButtonCaps; public short NumberOutputValueCaps; public short NumberOutputDataIndices;
            public short NumberFeatureButtonCaps; public short NumberFeatureValueCaps; public short NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parentWindow, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData interfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData interfaceData, IntPtr detailData, int detailDataSize, out int requiredSize, IntPtr deviceInfoData);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] reportBuffer, int reportLength);
    }

    // ---------------- Input layout monitor ----------------
    internal static class LayoutInfo
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);

        public static bool IsRussianForeground()
        {
            var hwnd = GetForegroundWindow();
            uint pid;
            uint tid = GetWindowThreadProcessId(hwnd, out pid);
            var hkl = GetKeyboardLayout(tid);
            int langId = (int)(hkl.ToInt64() & 0xFFFF);
            return langId == 0x0419;
        }
    }

    // ---------------- Text layout converter ----------------
    internal static class TextConverter
    {
        private static readonly Dictionary<char, char> EnToRu = new Dictionary<char, char>();
        private static readonly Dictionary<char, char> RuToEn = new Dictionary<char, char>();

        static TextConverter()
        {
            AddPair('`', 'ё'); AddPair('~', 'Ё');
            AddPair('@', '"'); AddPair('#', '№'); AddPair('$', ';');
            AddPair('^', ':'); AddPair('&', '?'); AddPair('/', '.');
            AddPair('q', 'й'); AddPair('w', 'ц'); AddPair('e', 'у'); AddPair('r', 'к');
            AddPair('t', 'е'); AddPair('y', 'н'); AddPair('u', 'г'); AddPair('i', 'ш');
            AddPair('o', 'щ'); AddPair('p', 'з'); AddPair('[', 'х'); AddPair(']', 'ъ');
            AddPair('a', 'ф'); AddPair('s', 'ы'); AddPair('d', 'в'); AddPair('f', 'а');
            AddPair('g', 'п'); AddPair('h', 'р'); AddPair('j', 'о'); AddPair('k', 'л');
            AddPair('l', 'д'); AddPair(';', 'ж'); AddPair('\'', 'э');
            AddPair('z', 'я'); AddPair('x', 'ч'); AddPair('c', 'с'); AddPair('v', 'м');
            AddPair('b', 'и'); AddPair('n', 'т'); AddPair('m', 'ь'); AddPair(',', 'б');
            AddPair('.', 'ю');
            AddPair('Q', 'Й'); AddPair('W', 'Ц'); AddPair('E', 'У'); AddPair('R', 'К');
            AddPair('T', 'Е'); AddPair('Y', 'Н'); AddPair('U', 'Г'); AddPair('I', 'Ш');
            AddPair('O', 'Щ'); AddPair('P', 'З'); AddPair('{', 'Х'); AddPair('}', 'Ъ');
            AddPair('A', 'Ф'); AddPair('S', 'Ы'); AddPair('D', 'В'); AddPair('F', 'А');
            AddPair('G', 'П'); AddPair('H', 'Р'); AddPair('J', 'О'); AddPair('K', 'Л');
            AddPair('L', 'Д'); AddPair(':', 'Ж'); AddPair('"', 'Э');
            AddPair('Z', 'Я'); AddPair('X', 'Ч'); AddPair('C', 'С'); AddPair('V', 'М');
            AddPair('B', 'И'); AddPair('N', 'Т'); AddPair('M', 'Ь'); AddPair('<', 'Б');
            AddPair('>', 'Ю'); AddPair('?', ',');
        }

        private static void AddPair(char en, char ru)
        {
            if (!EnToRu.ContainsKey(en)) EnToRu.Add(en, ru);
            if (!RuToEn.ContainsKey(ru)) RuToEn.Add(ru, en);
        }

        public static string Convert(string text)
        {
            bool hasCyrillic = false;
            foreach (char c in text)
            {
                if (RuToEn.ContainsKey(c)) { hasCyrillic = true; break; }
            }
            var map = hasCyrillic ? RuToEn : EnToRu;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                char mapped;
                sb.Append(map.TryGetValue(c, out mapped) ? mapped : c);
            }
            return sb.ToString();
        }
    }

    // ---------------- Keyboard hook + conversion orchestration ----------------
    internal sealed class ConvertEngine : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int VkLShift = 0xA0;
        private const uint LlkhfInjected = 0x00000010;
        private const uint KeyeventfKeyup = 0x0002;
        private const int VkControl = 0x11;
        private const int VkC = 0x43;
        private const int VkV = 0x56;
        private const int VkLeft = 0x25;
        private const int WmInputLangChangeRequest = 0x0050;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr DwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint DwFlags;
            public uint Time;
            public IntPtr DwExtraInfo;
        }

        // INPUT union must be full-size (MOUSEINPUT is the largest member, 32 bytes on x64),
        // otherwise SendInput rejects the whole batch with ERROR_INVALID_PARAMETER.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mi;
            [FieldOffset(0)] public KeyboardInput Ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort WVk;
            public ushort WScan;
            public uint DwFlags;
            public uint Time;
            public IntPtr DwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);
        // CharSet.Unicode is critical: with the default Ansi marshalling the CLR
        // reinterpreted the UTF-16 output as bytes, truncating every non-ASCII
        // char to its low byte ("привет" -> "?@825B").
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);

        private readonly Settings _settings;
        private readonly Action _onConverted;
        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;
        private DateTime _lastLShiftUp = DateTime.MinValue;
        private bool _otherKeySinceLastShift;
        private readonly object _convertLock = new object();
        private bool _converting;

        // ---- last-typed-word tracking (console-safe conversion, Punto/Caramba style) ----
        private const int MaxWordLen = 64;
        private readonly StringBuilder _word = new StringBuilder(MaxWordLen);
        private readonly object _wordLock = new object();
        private bool _shiftDown, _ctrlDown, _altDown, _capsOn;
        private IntPtr _lastForeground = IntPtr.Zero;

        public ConvertEngine(Settings settings, Action onConverted)
        {
            _settings = settings;
            _onConverted = onConverted;
            _proc = HookCallback;
            _mouseProc = MouseHookCallback;
        }

        public void Install()
        {
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                var module = GetModuleHandle(curModule.ModuleName);
                _hookId = SetWindowsHookEx(WhKeyboardLl, _proc, module, 0);
                _mouseHookId = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            }
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
        }

        // A mouse click can move the caret, invalidating the tracked word (typing
        // backspaces would then erase the wrong text). Console carets always sit at
        // the end of the input line, so clicks there are harmless.
        private const int WhMouseLl = 14;
        private const int WmLButtonDown = 0x0201;
        private const int WmRButtonDown = 0x0204;
        private const int WmMButtonDown = 0x0207;
        private IntPtr _mouseHookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _mouseProc;

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _settings.ConvertEnabled)
            {
                int msg = wParam.ToInt32();
                if (msg == WmLButtonDown || msg == WmRButtonDown || msg == WmMButtonDown)
                {
                    try
                    {
                        if (!IsConsoleWindow(GetForegroundWindow())) ClearTrackedWord();
                    }
                    catch { }
                }
            }
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _settings.ConvertEnabled)
            {
                var data = (KbdLlHookStruct)Marshal.PtrToStructure(lParam, typeof(KbdLlHookStruct));
                bool injected = (data.Flags & LlkhfInjected) != 0;
                int msg = wParam.ToInt32();
                if (!injected)
                {
                    bool isDown = msg == WmKeyDown || msg == WmSysKeyDown;
                    bool isUp = msg == WmKeyUp || msg == WmSysKeyUp;
                    // Never let tracking code kill the hook.
                    try { TrackModifiers(data.VkCode, isDown, isUp); } catch { }
                    try { TrackLayoutHotkey(data.VkCode, isDown, isUp); } catch { }
                    if (data.VkCode == VkLShift)
                    {
                        if (isUp)
                        {
                            var now = DateTime.UtcNow;
                            var elapsed = (now - _lastLShiftUp).TotalMilliseconds;
                            if (!_otherKeySinceLastShift && elapsed >= 40 && elapsed <= _settings.DoubleTapMs)
                            {
                                _lastLShiftUp = DateTime.MinValue;
                                _otherKeySinceLastShift = true;
                                BloodyBacklight.Log("hook: double-tap");
                                TriggerConvert();
                            }
                            else
                            {
                                if (elapsed >= 40 && elapsed <= _settings.DoubleTapMs + 600)
                                    BloodyBacklight.Log("hook: near-miss (" + (_otherKeySinceLastShift ? "other key" : "slow tap") + ", " + (int)elapsed + "ms)");
                                _lastLShiftUp = now;
                                _otherKeySinceLastShift = false;
                            }
                        }
                    }
                    else if (isDown)
                    {
                        _otherKeySinceLastShift = true;
                        try { TrackTypedChar(data); } catch { }
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void TriggerConvert()
        {
            lock (_convertLock)
            {
                if (_converting) return;
                _converting = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { DoConvert(); }
                catch { }
                finally
                {
                    lock (_convertLock) _converting = false;
                }
            });
        }

        // ---- typed-word tracking ----
        private const int VkBack = 0x08;
        private const int VkTab = 0x09;
        private const int VkReturn = 0x0D;
        private const int VkEscape = 0x1B;
        private const int VkSpace = 0x20;
        private const int VkCapital = 0x14;
        private const int VkRShift = 0xA1;
        private const int VkLControl = 0xA2;
        private const int VkRControl = 0xA3;
        private const int VkLMenu = 0xA4;
        private const int VkRMenu = 0xA5;

        private void TrackModifiers(uint vk, bool isDown, bool isUp)
        {
            if (vk == VkLShift || vk == VkRShift || vk == 0x10)
            {
                if (isDown) _shiftDown = true;
                if (isUp) _shiftDown = false;
            }
            else if (vk == VkLControl || vk == VkRControl || vk == 0x11)
            {
                if (isDown) _ctrlDown = true;
                if (isUp) _ctrlDown = false;
            }
            else if (vk == VkLMenu || vk == VkRMenu || vk == 0x12)
            {
                if (isDown) _altDown = true;
                if (isUp) _altDown = false;
            }
            else if (vk == VkCapital && isDown)
            {
                _capsOn = !_capsOn;
            }
        }

        // ---- layout-switch hotkey tracking ----
        // Console windows report HKL=0 and ignore WM_INPUTLANGCHANGEREQUEST, so for
        // them the real layout is unreadable. We track the system toggle hotkeys
        // (Ctrl+Shift / Alt+Shift / Win+Space) ourselves; regular windows re-sync
        // this state from their HKL on every poll, so it stays self-correcting.
        private bool _winDown;
        private bool _toggleComboFired;
        private bool _trackedRussian;
        private readonly object _layoutLock = new object();

        public bool TrackedRussian
        {
            get { lock (_layoutLock) return _trackedRussian; }
        }

        public void SyncTrackedRussian(bool rus)
        {
            lock (_layoutLock) _trackedRussian = rus;
        }

        private void ToggleTrackedRussian(string why)
        {
            lock (_layoutLock) _trackedRussian = !_trackedRussian;
            BloodyBacklight.Log("layout: toggle by " + why + " -> " + (_trackedRussian ? "RU" : "EN"));
        }

        private void TrackLayoutHotkey(uint vk, bool isDown, bool isUp)
        {
            if (vk == 0x5B || vk == 0x5C) // LWIN/RWIN
            {
                if (isDown) _winDown = true;
                if (isUp) { _winDown = false; _toggleComboFired = false; }
                return;
            }
            bool isShift = vk == VkLShift || vk == VkRShift || vk == 0x10;
            bool isCtrl = vk == VkLControl || vk == VkRControl || vk == 0x11;
            bool isAlt = vk == VkLMenu || vk == VkRMenu || vk == 0x12;
            if (isUp)
            {
                if (isShift || isCtrl || isAlt || vk == VkSpace) _toggleComboFired = false;
                return;
            }
            if (!isDown || _toggleComboFired) return;
            bool combo =
                (isShift && (_ctrlDown || _altDown)) ||
                (isCtrl && _shiftDown) ||
                (isAlt && _shiftDown) ||
                (vk == VkSpace && _winDown);
            if (combo)
            {
                _toggleComboFired = true;
                ToggleTrackedRussian("hotkey");
            }
        }

        private void TrackTypedChar(KbdLlHookStruct data)
        {
            uint vk = data.VkCode;
            if (vk == VkBack)
            {
                lock (_wordLock) { if (_word.Length > 0) _word.Length--; }
                return;
            }
            // word boundaries / caret moves invalidate the tracked word
            if (vk == VkSpace || vk == VkReturn || vk == VkTab || vk == VkEscape ||
                vk == VkLeft || vk == 0x27 || vk == 0x26 || vk == 0x28 ||       // left/right/up/down
                vk == 0x23 || vk == 0x24 || vk == 0x21 || vk == 0x22 ||         // end/home/pgup/pgdn
                vk == 0x2E || vk == 0x2D)                                       // del/ins
            {
                lock (_wordLock) _word.Length = 0;
                return;
            }
            if (_ctrlDown || _altDown) return; // shortcuts are not text

            var hwnd = GetForegroundWindow();
            uint pid;
            uint tid = GetWindowThreadProcessId(hwnd, out pid);
            IntPtr hkl;
            if (IsConsoleWindow(hwnd))
            {
                // Console threads report HKL=0: translate keys using the layout
                // we track ourselves (toggle hotkeys / our own conversions).
                hkl = TrackedRussian ? GetRuHkl() : GetEnHkl();
            }
            else
            {
                hkl = GetKeyboardLayout(tid);
            }
            var state = new byte[256];
            if (_shiftDown) state[0x10] = 0x80;
            if (_ctrlDown) state[0x11] = 0x80;
            if (_capsOn) state[VkCapital] = 0x01;
            var sb = new StringBuilder(4);
            int n = ToUnicodeEx(vk, data.ScanCode, state, sb, sb.Capacity, 0, hkl);
            if (n >= 1)
            {
                char c = sb[0];
                if (c >= 0x20)
                {
                    lock (_wordLock)
                    {
                        if (_word.Length >= MaxWordLen) _word.Remove(0, 1);
                        _word.Append(c);
                    }
                }
            }
        }

        // Called periodically by the tray timer: clears the tracked word on focus change.
        public void NoteForeground()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != _lastForeground)
            {
                _lastForeground = hwnd;
                lock (_wordLock) _word.Length = 0;
            }
        }

        private string TakeTrackedWord()
        {
            lock (_wordLock)
            {
                string w = _word.ToString();
                _word.Length = 0;
                return w;
            }
        }

        private void ClearTrackedWord()
        {
            lock (_wordLock) _word.Length = 0;
        }

        // Explicitly loaded layout handles for console key translation.
        private static IntPtr _enHkl = IntPtr.Zero;
        private static IntPtr _ruHkl = IntPtr.Zero;
        private const uint KlfNotellshell = 0x00000080;

        private static IntPtr GetEnHkl()
        {
            if (_enHkl == IntPtr.Zero) _enHkl = LoadKeyboardLayout("00000409", KlfNotellshell);
            return _enHkl;
        }

        private static IntPtr GetRuHkl()
        {
            if (_ruHkl == IntPtr.Zero) _ruHkl = LoadKeyboardLayout("00000419", KlfNotellshell);
            return _ruHkl;
        }

        // After a conversion the converted word becomes the new "last typed word",
        // so a repeated double-tap toggles it back (Caramba-style).
        private void SeedTrackedWord(string word)
        {
            lock (_wordLock)
            {
                _word.Length = 0;
                if (!String.IsNullOrEmpty(word) && word.Length <= MaxWordLen)
                    _word.Append(word);
            }
        }

        // Scan-code based injection: arrows are extended keys and are ignored by many
        // apps when synthesized by virtual-key code alone. Physical-like delivery requires
        // KEYEVENTF_SCANCODE (+ KEYEVENTF_EXTENDEDKEY for arrows etc.).
        private const uint KeyeventfScancode = 0x0008;
        private const uint KeyeventfExtendedkey = 0x0001;

        private static void ScanFor(int vk, out ushort scan, out bool extended)
        {
            switch (vk)
            {
                case VkControl: scan = 0x1D; extended = false; return;
                case VkLShift: scan = 0x2A; extended = false; return;
                case VkC: scan = 0x2E; extended = false; return;
                case VkV: scan = 0x2F; extended = false; return;
                case VkLeft: scan = 0x4B; extended = true; return;
                case VkBack: scan = 0x0E; extended = false; return;
                case VkLMenu: scan = 0x38; extended = false; return;
                case VkOem3: scan = 0x29; extended = false; return; // `~ key
                case VkInsert: scan = 0x52; extended = true; return;
                default: scan = 0; extended = false; return;
            }
        }

        private static void SendKeyCombo(bool ctrl, bool shift, params int[] keys)
        {
            var inputs = new List<Input>();
            if (ctrl) inputs.Add(KeyDown(VkControl));
            if (shift) inputs.Add(KeyDown(VkLShift));
            foreach (var k in keys) inputs.Add(KeyDown(k));
            for (int i = keys.Length - 1; i >= 0; i--) inputs.Add(KeyUp(keys[i]));
            if (shift) inputs.Add(KeyUp(VkLShift));
            if (ctrl) inputs.Add(KeyUp(VkControl));
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input)));
        }

        private const int VkOem3 = 0xC0; // `~ key (grave accent)
        private const int VkInsert = 0x2D;

        // Sends the real system layout-switch hotkey (read from the registry) as
        // physical-like input. Needed for consoles, which ignore
        // WM_INPUTLANGCHANGEREQUEST but do react to the actual hotkey.
        private static void SendLayoutToggleHotkey()
        {
            string hk = "1";
            try
            {
                var v = Registry.GetValue("HKEY_CURRENT_USER\\Keyboard Layout\\Toggle", "Hotkey", null);
                if (v != null) hk = v.ToString();
            }
            catch { }
            switch (hk)
            {
                case "1": // Left Alt + Shift
                    SendModifiersPulse(VkLMenu, VkLShift);
                    break;
                case "2": // Ctrl + Shift
                    SendModifiersPulse(VkControl, VkLShift);
                    break;
                case "4": // grave accent
                    SendKeyCombo(false, false, VkOem3);
                    break;
                // "3" = disabled: nothing to send
            }
        }

        private static void SendModifiersPulse(int mod1, int mod2)
        {
            var inputs = new List<Input>();
            inputs.Add(KeyDown(mod1));
            inputs.Add(KeyDown(mod2));
            Thread.Sleep(30);
            inputs.Add(KeyUp(mod2));
            inputs.Add(KeyUp(mod1));
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input)));
        }

        private static Input KeyDown(int vk)
        {
            ushort scan; bool ext;
            ScanFor(vk, out scan, out ext);
            var i = new Input();
            i.Type = 1;
            i.U.Ki.WScan = scan;
            i.U.Ki.DwFlags = KeyeventfScancode | (ext ? KeyeventfExtendedkey : 0u);
            return i;
        }

        private static Input KeyUp(int vk)
        {
            ushort scan; bool ext;
            ScanFor(vk, out scan, out ext);
            var i = new Input();
            i.Type = 1;
            i.U.Ki.WScan = scan;
            i.U.Ki.DwFlags = KeyeventfScancode | KeyeventfKeyup | (ext ? KeyeventfExtendedkey : 0u);
            return i;
        }

        private static string GetClipboardTextWithRetries()
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    string result = null;
                    var t = new Thread((ThreadStart)delegate
                    {
                        try { if (Clipboard.ContainsText()) result = Clipboard.GetText(); }
                        catch { }
                    });
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    t.Join(300);
                    return result;
                }
                catch { Thread.Sleep(50); }
            }
            return null;
        }

        private static void SetClipboardText(string text)
        {
            var t = new Thread((ThreadStart)delegate
            {
                for (int i = 0; i < 10; i++)
                {
                    try { Clipboard.SetText(text); return; }
                    catch { Thread.Sleep(50); }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(1500);
        }

        // Waits until the clipboard sequence number changes (copy landed) or timeout.
        private static uint WaitClipboardChange(uint seq, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            uint cur;
            do
            {
                Thread.Sleep(40);
                cur = GetClipboardSequenceNumber();
            } while (cur == seq && DateTime.UtcNow < deadline);
            return cur;
        }

        private const uint KeyeventfUnicode = 0x0004;

        // Types text layout-independently via KEYEVENTF_UNICODE (works in consoles too).
        private static void TypeUnicodeText(string text)
        {
            var inputs = new List<Input>(text.Length * 2);
            foreach (char c in text)
            {
                var down = new Input();
                down.Type = 1;
                down.U.Ki.WScan = c;
                down.U.Ki.DwFlags = KeyeventfUnicode;
                inputs.Add(down);
                var up = new Input();
                up.Type = 1;
                up.U.Ki.WScan = c;
                up.U.Ki.DwFlags = KeyeventfUnicode | KeyeventfKeyup;
                inputs.Add(up);
            }
            if (inputs.Count > 0)
                SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input)));
        }

        private static void SendBackspaces(int count)
        {
            var inputs = new List<Input>(count * 2);
            for (int i = 0; i < count; i++)
            {
                inputs.Add(KeyDown(VkBack));
                inputs.Add(KeyUp(VkBack));
            }
            if (inputs.Count > 0)
                SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input)));
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool IsConsoleClass(string cls)
        {
            return cls == "ConsoleWindowClass" ||              // classic conhost (cmd, PowerShell)
                   cls == "CASCADIA_HOSTING_WINDOW_CLASS" || // Windows Terminal
                   cls == "PseudoConsoleWindow";
        }

        private static bool IsConsoleWindow(IntPtr hwnd)
        {
            return IsConsoleClass(ClassNameOf(hwnd));
        }

        // GUI terminal emulators: no console HKL quirks, but Ctrl+C means SIGINT
        // there and Unicode key injection is mangled — treat like consoles for
        // conversion purposes (word erase + paste), keep normal HKL polling.
        private static bool IsTerminalClass(string cls)
        {
            return IsConsoleClass(cls) ||
                   cls == "mintty" ||             // Git Bash / Cygwin / MSYS2
                   cls == "VirtualConsoleClass";  // ConEmu / Cmder
        }

        public static bool IsConsoleForeground()
        {
            return IsConsoleWindow(GetForegroundWindow());
        }

        private void FinishConvert(bool console)
        {
            if (_settings.SwitchLayoutAfterConvert)
            {
                var hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    if (console)
                    {
                        Thread.Sleep(120); // let the console digest the retyped text first
                        SendLayoutToggleHotkey(); // consoles ignore WM_INPUTLANGCHANGEREQUEST
                    }
                    else
                    {
                        PostMessage(hwnd, WmInputLangChangeRequest, new IntPtr(2), IntPtr.Zero);
                    }
                    ToggleTrackedRussian("convert");
                }
            }
            var cb = _onConverted;
            if (cb != null) cb();
        }

        // Types text by pasting it via Shift+Insert. Works in editors, consoles
        // and terminal emulators alike, and unlike KEYEVENTF_UNICODE injection it
        // is not mangled by terminals (mintty/ConEmu break VK_PACKET chars).
        private static void PasteText(string text)
        {
            SetClipboardText(text);
            Thread.Sleep(50);
            SendKeyCombo(false, true, VkInsert); // Shift+Insert = paste everywhere
        }

        private void DoConvert()
        {
            var fg = GetForegroundWindow();
            string fgClass = ClassNameOf(fg);
            bool console = IsConsoleClass(fgClass);
            bool terminal = IsTerminalClass(fgClass);
            BloodyBacklight.Log("convert: fg=\"" + fgClass + "\" console=" + console + " terminal=" + terminal);

            if (!terminal)
            {
                // 1) selection-based conversion via clipboard (editors etc.)
                uint seqBefore = GetClipboardSequenceNumber();
                string clipBefore = GetClipboardTextWithRetries();

                SendKeyCombo(true, false, VkC);
                uint seqAfter = WaitClipboardChange(seqBefore, 400);
                string selected = seqAfter != seqBefore ? GetClipboardTextWithRetries() : null;
                BloodyBacklight.Log("convert: path1 sel=" + (selected == null ? "null" : "\"" + selected + "\""));

                if (!String.IsNullOrEmpty(selected))
                {
                    string convertedSel = TextConverter.Convert(selected);
                    SetClipboardText(convertedSel);
                    SendKeyCombo(true, false, VkV);
                    Thread.Sleep(150);
                    RestoreClipboard(clipBefore); // keep user's clipboard untouched
                    TakeTrackedWord();
                    FinishConvert(console);
                    return;
                }

                // 2) no selection -> convert the tracked last-typed word by erasing
                //    and pasting it back converted (no selection flicker).
                string tracked = TakeTrackedWord();
                BloodyBacklight.Log("convert: word=\"" + tracked + "\"");
                if (tracked.Length > 0)
                {
                    string convertedTracked = TextConverter.Convert(tracked);
                    SendBackspaces(tracked.Length);
                    Thread.Sleep(80);
                    PasteText(convertedTracked);
                    Thread.Sleep(150);
                    RestoreClipboard(clipBefore);
                    SeedTrackedWord(convertedTracked);
                    FinishConvert(console);
                    return;
                }

                // 3) fallback (e.g., caret placed by mouse, nothing tracked):
                //    synthesize a word selection and copy it.
                SendKeyCombo(true, true, VkLeft);
                Thread.Sleep(150); // let the app apply the synthetic selection
                SendKeyCombo(true, false, VkC);
                uint seqAfter2 = WaitClipboardChange(seqAfter, 500);
                selected = seqAfter2 != seqAfter ? GetClipboardTextWithRetries() : null;
                BloodyBacklight.Log("convert: path2 sel=" + (selected == null ? "null" : "\"" + selected + "\""));
                if (!String.IsNullOrEmpty(selected))
                {
                    string convertedWord = TextConverter.Convert(selected);
                    SetClipboardText(convertedWord);
                    SendKeyCombo(true, false, VkV);
                    Thread.Sleep(150);
                    RestoreClipboard(clipBefore);
                    SeedTrackedWord(convertedWord);
                    FinishConvert(console);
                    return;
                }
                RestoreClipboard(clipBefore);
                return;
            }

            // Terminal/console path: erase the tracked last-typed word and paste it
            // back converted. No Ctrl+C here — in terminals that means SIGINT.
            string word = TakeTrackedWord();
            BloodyBacklight.Log("convert: terminal word=\"" + word + "\"");
            if (word.Length > 0)
            {
                string clipBeforeT = GetClipboardTextWithRetries();
                string converted = TextConverter.Convert(word);
                SendBackspaces(word.Length);
                Thread.Sleep(80);
                PasteText(converted);
                Thread.Sleep(150);
                RestoreClipboard(clipBeforeT);
                SeedTrackedWord(converted);
                FinishConvert(console);
            }
        }

        private static void RestoreClipboard(string text)
        {
            if (text != null) SetClipboardText(text);
        }
    }

    // ---------------- Tray application ----------------
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly Settings _settings;
        private readonly BloodyBacklight _backlight;
        private readonly ConvertEngine _engine;
        private readonly System.Windows.Forms.Timer _layoutTimer;
        private bool? _lastRussian;
        private readonly ToolStripMenuItem _miConvert;
        private readonly ToolStripMenuItem _miBacklight;
        private readonly ToolStripMenuItem _miAutostart;
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "LayoutGlow";

        public TrayApp()
        {
            _settings = Settings.Load();
            BloodyBacklight.LoggingEnabled = _settings.LogEnabled;
            _backlight = new BloodyBacklight();
            _engine = new ConvertEngine(_settings, OnConverted);
            _engine.Install();

            _miConvert = new ToolStripMenuItem("Конвертация по 2xLShift", null, OnToggleConvert);
            _miConvert.Checked = _settings.ConvertEnabled;
            _miBacklight = new ToolStripMenuItem("Подсветка раскладки", null, OnToggleBacklight);
            _miBacklight.Checked = _settings.BacklightEnabled;
            _miAutostart = new ToolStripMenuItem("Автозагрузка", null, OnToggleAutostart);
            _miAutostart.Checked = IsAutostart();

            var menu = new ContextMenuStrip();
            menu.Items.Add(_miConvert);
            menu.Items.Add(_miBacklight);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Цвет английской раскладки...", null, OnPickEnColor));
            menu.Items.Add(new ToolStripMenuItem("Цвет русской раскладки...", null, OnPickRuColor));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miAutostart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выход", null, OnExit));

            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = menu;
            _tray.Text = "LayoutGlow: 2xLShift — смена раскладки";
            _tray.Icon = BuildIcon(false);
            _tray.Visible = true;

            _layoutTimer = new System.Windows.Forms.Timer();
            _layoutTimer.Interval = 100;
            _layoutTimer.Tick += OnLayoutTick;
            _layoutTimer.Start();
        }

        private bool _pendingRussian;
        private int _pendingCount;

        private void OnLayoutTick(object sender, EventArgs e)
        {
            _engine.NoteForeground(); // invalidate tracked word on focus change
            bool isConsole = ConvertEngine.IsConsoleForeground();
            bool rus;
            if (isConsole)
            {
                // Consoles report HKL=0: use the state we track from toggle hotkeys.
                rus = _engine.TrackedRussian;
            }
            else
            {
                rus = LayoutInfo.IsRussianForeground();
                _engine.SyncTrackedRussian(rus); // authoritative source: keep tracker in sync
            }

            // Anti-flicker: accept a layout change only after it stays stable for
            // a couple of polls (the foreground thread's HKL oscillates briefly
            // while Windows switches layouts). The tracked console state is set
            // digitally by us, so it needs no debounce at all.
            int need = isConsole ? 1 : 2;
            if (_lastRussian.HasValue && _lastRussian.Value == rus)
            {
                _pendingCount = 0;
                return;
            }
            if (_pendingCount > 0 && _pendingRussian == rus)
            {
                _pendingCount++;
            }
            else
            {
                _pendingRussian = rus;
                _pendingCount = 1;
            }
            if (_pendingCount < need) return;
            _pendingCount = 0;

            _lastRussian = rus;
            if (_settings.LogEnabled)
            {
                try
                {
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LayoutGlow.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " layout=" + (rus ? "RU" : "EN") + Environment.NewLine);
                }
                catch { }
            }
            _tray.Icon = BuildIcon(rus);
            if (_settings.BacklightEnabled)
            {
                if (rus) _backlight.SetColor(_settings.RuR, _settings.RuG, _settings.RuB);
                else _backlight.SetColor(_settings.EnR, _settings.EnG, _settings.EnB);
            }
        }

        private void OnConverted()
        {
            // layout will change; force timer refresh
            _lastRussian = null;
        }

        private static Icon BuildIcon(bool russian)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                var color = russian ? Color.FromArgb(230, 40, 40) : Color.FromArgb(30, 180, 60);
                using (var brush = new SolidBrush(color))
                    g.FillEllipse(brush, 1, 1, 14, 14);
                using (var pen = new Pen(Color.White, 1))
                    g.DrawEllipse(pen, 1, 1, 13, 13);
            }
            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        private void OnToggleConvert(object sender, EventArgs e)
        {
            _settings.ConvertEnabled = !_settings.ConvertEnabled;
            _miConvert.Checked = _settings.ConvertEnabled;
            _settings.Save();
        }

        private void OnToggleBacklight(object sender, EventArgs e)
        {
            _settings.BacklightEnabled = !_settings.BacklightEnabled;
            _miBacklight.Checked = _settings.BacklightEnabled;
            _settings.Save();
            if (_settings.BacklightEnabled) _lastRussian = null; // re-apply
            else _backlight.StopStreaming();
        }

        private void OnPickEnColor(object sender, EventArgs e)
        {
            byte r, g, b;
            if (PickColor(_settings.EnR, _settings.EnG, _settings.EnB, out r, out g, out b))
            {
                _settings.EnR = r; _settings.EnG = g; _settings.EnB = b;
                _settings.Save();
                _lastRussian = null; // re-apply current layout color
            }
        }

        private void OnPickRuColor(object sender, EventArgs e)
        {
            byte r, g, b;
            if (PickColor(_settings.RuR, _settings.RuG, _settings.RuB, out r, out g, out b))
            {
                _settings.RuR = r; _settings.RuG = g; _settings.RuB = b;
                _settings.Save();
                _lastRussian = null; // re-apply current layout color
            }
        }

        private static bool PickColor(byte r0, byte g0, byte b0, out byte r, out byte g, out byte b)
        {
            r = r0; g = g0; b = b0;
            using (var dlg = new ColorDialog())
            {
                dlg.Color = Color.FromArgb(r0, g0, b0);
                dlg.FullOpen = true;
                if (dlg.ShowDialog() != DialogResult.OK) return false;
                r = dlg.Color.R; g = dlg.Color.G; b = dlg.Color.B;
                return true;
            }
        }

        private static bool IsAutostart()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(RunValueName) != null;
            }
        }

        private void OnToggleAutostart(object sender, EventArgs e)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;
                if (IsAutostart())
                {
                    key.DeleteValue(RunValueName, false);
                    _miAutostart.Checked = false;
                }
                else
                {
                    key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    _miAutostart.Checked = true;
                }
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            _tray.Visible = false;
            _engine.Dispose();
            _backlight.Dispose();
            Application.Exit();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (new Mutex(true, "LayoutGlowSingleInstance", out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.Run(new TrayApp());
            }
        }
    }
}
