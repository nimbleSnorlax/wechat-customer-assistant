using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class InstallStepException : IOException
{
    internal readonly string Step;
    internal readonly string Path;
    internal readonly string Temporary;

    internal InstallStepException(string step, string path, string temporary, Exception error)
        : base(step + "失败。", error)
    {
        Step = step;
        Path = path;
        Temporary = temporary;
    }
}

internal sealed class SetupFailure
{
    internal string Advice { get; private set; }
    internal string Summary { get; private set; }
    internal string Details { get; private set; }

    internal static SetupFailure Record(string target, Exception error)
    {
        var step = error as InstallStepException;
        var cause = step == null ? error : step.InnerException;
        var win32 = cause as Win32Exception;
        uint hresult = unchecked((uint)cause.HResult);
        if (win32 != null && win32.NativeErrorCode > 0 && win32.NativeErrorCode <= 65535)
            hresult = 0x80070000 | (uint)win32.NativeErrorCode;
        string[] apps = new string[0];
        if (step != null && (hresult == 0x80070005 || hresult == 0x80070020 || hresult == 0x80070021))
        {
            try { apps = FileOccupancy.Find(step.Path, step.Temporary); }
            catch (ExternalException) { }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        var summary = new StringBuilder();
        if (step != null) summary.AppendLine(step.Message).AppendLine(step.Path);
        summary.AppendLine(cause.Message).AppendLine("错误码 0x" + hresult.ToString("X8"));
        if (apps.Length > 0) summary.AppendLine("正在使用文件的程序").AppendLine(String.Join(Environment.NewLine, apps));
        string advice = "安装未完成。查看下方详情，处理后可以点「重试」。";
        if (step != null && step.Step == "打开微信客服助手")
            advice = "安装文件已就绪，工作台暂时没打开。处理下方错误后，可以点「重试」。";
        else if (apps.Length > 0)
            advice = "有程序正在使用安装文件。保存工作并退出可以正常关闭的程序，再点「重试」。";
        else if (hresult == 0x80070020 || hresult == 0x80070021)
            advice = "文件正在被占用。关闭正在使用该文件的程序，或稍后再点「重试」。";
        else if (hresult == 0x80070070 || hresult == 0x80070027)
            advice = "磁盘空间不足。释放空间后点「重试」，首次安装也可以换到空间充足的目录。";
        else if (step != null && (step.Step == "检查安装目录" || step.Step == "准备安装目录"))
            advice = "这个目录没有通过写入检查。可以选择可写的目录，或处理下方错误后重试。";
        else if (hresult == 0x80070005)
            advice = "Windows 拒绝了文件访问。可以检查下方路径的写入权限和安全软件的拦截记录。";

        string log = DateTimeOffset.Now.ToString("o") + Environment.NewLine +
                     "安装包 " + AppDomain.CurrentDomain.FriendlyName + Environment.NewLine +
                     "安装目录 " + (target ?? "未选定") + Environment.NewLine +
                     summary + Environment.NewLine + error;
        string logPath = SaveLog(log);
        if (logPath != null) summary.AppendLine().AppendLine("诊断日志").AppendLine(logPath);
        else summary.AppendLine().AppendLine("日志未能写入磁盘，可以复制本窗口的诊断信息。");
        return new SetupFailure { Advice = advice, Summary = summary.ToString(), Details = log };
    }

    private static string SaveLog(string contents)
    {
        string name = "setup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                      Guid.NewGuid().ToString("N") + ".log";
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       Path.GetTempPath() })
        {
            if (String.IsNullOrEmpty(root)) continue;
            try
            {
                string folder = Path.Combine(root, "WeChatAssistant-Setup-Logs");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name);
                File.WriteAllText(path, contents, Encoding.UTF8);
                return path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}

internal static class FileOccupancy
{
    private const int MoreData = 234;

    internal static string[] Find(params string[] paths)
    {
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return new string[0];
        uint session;
        if (RmStartSession(out session, 0, new StringBuilder(33)) != 0) return new string[0];
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                return new string[0];
            uint count = 0, needed, reboot;
            ProcessInfo[] processes = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int result = RmGetList(session, out needed, ref count, processes, out reboot);
                if (result == 0)
                {
                    if (count == 0) return new string[0];
                    int current;
                    using (var process = Process.GetCurrentProcess()) current = process.Id;
                    return processes.Take((int)count).Where(info => info.Process.Id != current)
                        .Select(Describe).ToArray();
                }
                if (result != MoreData) break;
                count = needed;
                processes = new ProcessInfo[count];
            }
            return new string[0];
        }
        finally { RmEndSession(session); }
    }

    private static string Describe(ProcessInfo info)
    {
        string name = info.Name;
        if (String.IsNullOrWhiteSpace(name))
        {
            try
            {
                var start = info.Process.Start;
                long startedAt = ((long)start.dwHighDateTime << 32) | (uint)start.dwLowDateTime;
                using (var process = Process.GetProcessById(info.Process.Id))
                {
                    if (process.StartTime.ToFileTimeUtc() == startedAt) name = process.ProcessName;
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
        return (String.IsNullOrWhiteSpace(name) ? "进程" : name) + " (PID " + info.Process.Id + ")";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int Id;
        public System.Runtime.InteropServices.ComTypes.FILETIME Start;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
                                                  uint appCount, IntPtr apps, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
                                       [In, Out] ProcessInfo[] apps, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
