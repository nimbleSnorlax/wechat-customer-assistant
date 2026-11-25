using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.Win32;
using WeAutoCommon.Utils;
using WeChatAuto.Utils;

if (args is ["--group-worker", var port] && int.TryParse(port, out var portNumber))
{
    return await GroupWorker.Run(portNumber);
}
if (args is ["--background-worker", var backgroundPort] && int.TryParse(backgroundPort, out var backgroundPortNumber))
{
    return await GroupWorker.Run(backgroundPortNumber, background: true);
}
try
{
    if (args is ["--open-wechat"])
    {
        Console.WriteLine("WECHAT_RESULT " + JsonSerializer.Serialize(new { opened = WeChatWindows.Open() }));
        return 0;
    }
    if (args is ["--account"])
    {
        using var session = BridgeSession.Open();
        Console.WriteLine("WECHAT_RESULT " + JsonSerializer.Serialize(new
        { name = session.Client.NickName, alias = session.Client.Alias, processId = session.Client.MainWindow.Properties.ProcessId.Value }));
        return 0;
    }
    if (args is ["--tag-directory"])
    {
        using var session = BridgeSession.Open();
        Console.WriteLine("WECHAT_RESULT " + JsonSerializer.Serialize(await session.ReadTagDirectory()));
        return 0;
    }
    if (args is ["--contacts", _] or ["--current-contact"])
    {
        using var session = BridgeSession.Open();
        Console.WriteLine("WECHAT_RESULT " + JsonSerializer.Serialize(
            await session.SearchContacts(args.Length == 2 ? args[1] : null)));
        return 0;
    }
    var processes = WeChatWindows.LoggedInProcesses();
    Console.WriteLine("WECHAT_RESULT " + JsonSerializer.Serialize(new
    {
        windows = processes.Length,
        processId = processes.Length == 1 ? (int?)processes[0] : null
    }));
    return 0;
}
catch (Exception error)
{
    var frame = new StackTrace(error).GetFrame(0)?.GetMethod()?.Name;
    Console.WriteLine("WECHAT_RESULT " + JsonSerializer.Serialize(new { error = error.GetType().Name, stage = frame }));
    return 1;
}

internal static class WeChatWindows
{
    private static readonly string[] ClientNames = ["Weixin", "WeChat"];

    private static bool IsClientName(string name) =>
        ClientNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<Process> ClientProcesses() =>
        ClientNames.SelectMany(name => Process.GetProcessesByName(name));

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    internal static bool IsForeground() => IsForeground(null);

    internal static bool IsForeground(int? expectedProcess)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var processId) == 0)
            return false;
        if (expectedProcess is int expected && processId != expected)
            return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return IsClientName(process.ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool Open()
    {
        using var automation = new UIA3Automation();
        string? installed = null;
        foreach (var process in ClientProcesses())
        {
            using (process)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    try
                    {
                        Focus(automation.FromHandle(process.MainWindowHandle).AsWindow());
                        return true;
                    }
                    catch (Exception) { /* Launch the installed client if the window cannot be restored. */ }
                }
                try { installed ??= process.MainModule?.FileName; }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }

        IEnumerable<string?> Locations()
        {
            yield return installed;
            foreach (var registry in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var name in ClientNames)
                {
                    using var app = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name + ".exe");
                    yield return app?.GetValue(null) as string;
                }
                foreach (var key in new[] { @"SOFTWARE\Tencent\Weixin", @"SOFTWARE\Tencent\WeChat",
                    @"SOFTWARE\WOW6432Node\Tencent\Weixin", @"SOFTWARE\WOW6432Node\Tencent\WeChat" })
                {
                    using var item = registry.OpenSubKey(key);
                    var folder = item?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(folder))
                        foreach (var name in ClientNames)
                            yield return Path.Combine(folder, name + ".exe");
                }
            }
            foreach (var location in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData })
            {
                var root = Environment.GetFolderPath(location);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    foreach (var name in ClientNames)
                        yield return Path.Combine(root, "Tencent", name, name + ".exe");
                    yield return Path.Combine(root, "Tencent", "Weixin", "WeChat", "Weixin.exe");
                }
            }
        }
        var path = Locations().Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Environment.ExpandEnvironmentVariables(item!.Trim('"')))
            .FirstOrDefault(item => Path.IsPathFullyQualified(item) &&
                string.Equals(Path.GetExtension(item), ".exe", StringComparison.OrdinalIgnoreCase)
                && IsClientName(Path.GetFileNameWithoutExtension(item)) && File.Exists(item));
        if (path is null) return false;
        using var launched = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return launched is not null;
    }

    private static void Focus(Window window)
    {
        var pattern = window.Patterns.Window.PatternOrDefault;
        if (pattern?.WindowVisualState.Value == WindowVisualState.Minimized)
            pattern.SetWindowVisualState(WindowVisualState.Normal);
        window.Focus();
    }

    internal static bool TryFocusLoggedIn(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!IsClientName(process.ProcessName))
                return false;
            using var automation = new UIA3Automation();
            var window = WeChatAction.FindSignedInWindow(automation, processId, process.MainWindowHandle)?.AsWindow();
            if (window is null)
                return false;
            var handle = window.Properties.NativeWindowHandle.Value;
            if (IsIconic(handle) || !IsWindowVisible(handle))
            {
                const int restoreWindow = 9;
                ShowWindowAsync(handle, restoreWindow);
            }
            SetForegroundWindow(handle);
            if (IsForeground(processId)) return true;
            Focus(window);
            return IsForeground(processId);
        }
        catch (Exception) { return false; }
    }

    internal static int CountLoggedIn() => LoggedInProcesses().Length;

    internal static int? LoggedInProcess()
    {
        var processes = LoggedInProcesses();
        return processes.Length == 1 ? processes[0] : null;
    }

    internal static int[] LoggedInProcesses()
    {
        using var automation = new UIA3Automation();
        var processes = new List<int>();
        foreach (var process in ClientProcesses())
        {
            using (process)
            {
                try
                {
                    if (WeChatAction.FindSignedInWindow(automation, process.Id, process.MainWindowHandle) is not null)
                        processes.Add(process.Id);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return processes.ToArray();
    }
}


