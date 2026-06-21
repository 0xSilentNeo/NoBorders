using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace NoBorder
{
    internal static class Program
    {
        // ---- DWM ----
        private const int DWMWA_BORDER_COLOR = 34;
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

        // ---- Window enumeration ----
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        private const uint GA_ROOT = 2;

        // ---- WinEvent hook (catches snap/move/show/restore system-wide) ----
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        // Event range constants (winuser.h)
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private const uint WM_QUIT = 0x0012;

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "NoBorderWatcher";

        // Keep delegate alive for the lifetime of the hook (prevents GC collecting it)
        private static WinEventDelegate? _hookDelegate;
        private static readonly HashSet<IntPtr> _recentlyHandled = new();

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "--once":
                    ApplyToAllTopLevelWindows();
                    Console.WriteLine("Applied DWMWA_COLOR_NONE to all visible top-level windows.");
                    return 0;

                case "--watch":
                    RunWatcher();
                    return 0;

                case "--install-startup":
                    InstallStartup();
                    return 0;

                case "--uninstall-startup":
                    UninstallStartup();
                    return 0;

                default:
                    PrintUsage();
                    return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("NoBorder - removes the white DWM border around windows (e.g. when snapped)");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  NoBorder.exe --once              Apply once to all current windows, then exit");
            Console.WriteLine("  NoBorder.exe --watch             Run continuously, applying to new/changed windows");
            Console.WriteLine("  NoBorder.exe --install-startup   Register --watch to run automatically at login");
            Console.WriteLine("  NoBorder.exe --uninstall-startup Remove the startup entry");
        }

        // ---------------- Core border removal ----------------

        private static void RemoveBorder(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            uint colorNone = DWMWA_COLOR_NONE;
            // Result is ignored: some windows (e.g. cloaked/system windows) will reject this; that's fine.
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        }

        private static bool IsRealTopLevelWindow(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd)) return false;
            if (GetAncestor(hwnd, GA_ROOT) != hwnd) return false;
            if (GetWindowTextLength(hwnd) == 0) return false;
            return true;
        }

        private static void ApplyToAllTopLevelWindows()
        {
            EnumWindows((hwnd, _) =>
            {
                if (IsRealTopLevelWindow(hwnd))
                {
                    RemoveBorder(hwnd);
                }
                return true;
            }, IntPtr.Zero);
        }

        // ---------------- Watch mode ----------------

        private static void RunWatcher()
        {
            // Apply immediately to anything already open.
            ApplyToAllTopLevelWindows();

            _hookDelegate = WinEventCallback;

            // One hook covering foreground/move/minimize-restore events system-wide.
            IntPtr hook1 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            IntPtr hook2 = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero, _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            IntPtr hook3 = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            IntPtr hook4 = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
                IntPtr.Zero, _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            IntPtr hook5 = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

            Console.WriteLine("NoBorder is watching. Press Ctrl+C or close this window to stop.");

            // Standard Win32 message loop (required for WinEvent hooks to fire).
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) != 0)
            {
                if (msg.message == WM_QUIT) break;
            }

            UnhookWinEvent(hook1);
            UnhookWinEvent(hook2);
            UnhookWinEvent(hook3);
            UnhookWinEvent(hook4);
            UnhookWinEvent(hook5);
        }

        private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // idObject 0 == OBJID_WINDOW; ignore child/control-level events.
            if (idObject != 0 || hwnd == IntPtr.Zero) return;
            if (!IsRealTopLevelWindow(hwnd)) return;

            RemoveBorder(hwnd);
        }

        // ---------------- Startup registration ----------------

        private static void InstallStartup()
        {
            string exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            string command = $"\"{exePath}\" --watch";

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RunValueName, command, RegistryValueKind.String);

            Console.WriteLine("Installed. NoBorder will start in --watch mode automatically at your next login.");
            Console.WriteLine("To start watching right now without logging out, run: NoBorder.exe --watch");
        }

        private static void UninstallStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            Console.WriteLine("Removed NoBorder from startup.");
        }
    }
}
