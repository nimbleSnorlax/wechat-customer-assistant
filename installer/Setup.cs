using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Setup
{
    private const string LauncherName = "WeChatAssistant.exe";
    private const string InstallKey = @"Software\WeChatAssistant";
    private static readonly int[] FileRetryDelays = { 250, 500, 1000, 2000, 3000, 5000, 8000 };

    [STAThread]
    private static int Main(string[] args)
    {
        bool verify = args.Length == 2 && args[0] == "--verify";
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (verify)
            {
                int port = Int32.Parse(args[1]);
                if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port");
                InstallTo(Path.Combine(root, "WeChatAssistant-Verify-" + port), true, null);
                return 0;
            }

            string sessionPath = Path.Combine(root, "WeChatAssistant.setup.lock");
            using (OpenLease(sessionPath))
            {
                string defaultTarget = Path.Combine(root, "WeChatAssistant");
                string target = RememberedTarget() ?? defaultTarget;
                if (!IsInstalled(target) && IsInstalled(defaultTarget))
                    target = defaultTarget;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var wizard = new SetupWizard(target, IsInstalled(target), InstallAndStart))
                    wizard.ShowDialog();
            }
            return 0;
        }
        catch (Exception error)
        {
            var failure = SetupFailure.Record(null, error);
            if (!verify) MessageBox.Show(failure.Summary, "微信助手安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static bool IsInstalled(string target)
    {
        return File.Exists(Path.Combine(target, LauncherName)) && File.Exists(Path.Combine(target, "app_version.txt"));
    }

    private static string RememberedTarget()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(InstallKey))
            return key == null ? null : key.GetValue("InstallDirectory") as string;
    }

    private static void RememberTarget(string target)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(InstallKey))
            key.SetValue("InstallDirectory", target);
    }

    private static string NormalizeTarget(string input)
    {
        string value = input.Trim();
        if (!Regex.IsMatch(value, @"\A[A-Za-z]:\\") || value.IndexOf(':', 2) >= 0)
            throw new IOException("请输入完整的安装路径，例如 D:\\Apps\\WeChatAssistant。");
        string target = Path.GetFullPath(value);
        string root = Path.GetPathRoot(target);
        target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target.Length < root.Length)
            throw new IOException("请选择磁盘下的专用文件夹。");
        return target;
    }

    private static FileStream OpenLease(string path)
    {
        FileStream lease = null;
        RunInstallStep("打开安装锁", path, () =>
            lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None), retryLimit: 0);
        return lease;
    }

    private static void InstallAndStart(string input, IProgress<SetupProgress> progress)
    {
        string target = NormalizeTarget(input);
        string defaultTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            "WeChatAssistant");
        bool known = String.Equals(target, defaultTarget, StringComparison.OrdinalIgnoreCase) ||
                     String.Equals(target, RememberedTarget(), StringComparison.OrdinalIgnoreCase);
        InstallTo(target, known, RememberTarget, progress);
        string start = Path.Combine(target, LauncherName);
        Report(progress, "正在打开微信客服助手…", 100);
        CreateShortcut(start);
        RunInstallStep("打开微信客服助手", start, () =>
            Process.Start(new ProcessStartInfo(start) { WorkingDirectory = target, UseShellExecute = false }),
            retryLimit: 0);
    }

    private static void InstallTo(string target, bool known, Action<string> remember,
                                  IProgress<SetupProgress> progress = null)
    {
        if (!known) RequireEmpty(target);
        RunInstallStep("准备安装目录", target, () => Directory.CreateDirectory(Path.GetDirectoryName(target)), retryLimit: 0);
        using (var lease = OpenLease(target + ".lock"))
        {
            if (!known && lease.Length != 0)
                throw new IOException("此目录有未完成的安装记录，请选择其他目录。" + Environment.NewLine + target);
            bool installed = known && lease.Length == 0 && IsInstalled(target);
            if (!installed && lease.Length == 0) RequireEmpty(target);
            RunInstallStep("检查安装目录", target, () => CheckWritable(target), retryLimit: 3, progress: progress);
            if (remember != null)
                RunInstallStep("记录安装位置", target, () => remember(target), retryLimit: 0);
            if (installed)
            {
                Report(progress, "正在检查已安装版本…");
                StageUpgrade(target);
            }
            else Install(target, lease, progress);
            if (!File.Exists(Path.Combine(target, LauncherName)))
                throw new IOException("安装目录缺少启动程序。" + Environment.NewLine + target);
        }
    }

    private static void RequireEmpty(string target)
    {
        RunInstallStep("检查安装目录", target, () =>
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new IOException("请选择一个空文件夹或输入新文件夹路径。");
        }, retryLimit: 0);
    }

    private static void CheckWritable(string target)
    {
        Directory.CreateDirectory(target);
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("请选择实际安装目录。当前文件夹指向另一个位置。");
        string probe = Path.Combine(target, ".setup-" + Guid.NewGuid().ToString("N") + ".tmp");
        string renamed = probe + ".check";
        bool created = false;
        bool moved = false;
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.WriteByte(0);
            }
            File.Move(probe, renamed);
            moved = true;
            File.Delete(renamed);
        }
        finally
        {
            if (moved) DeleteTemporary(renamed);
            else if (created) DeleteTemporary(probe);
        }
    }

    private static void Install(string target, FileStream lease, IProgress<SetupProgress> progress)
    {
        // A nonempty lease marks a first install that can be resumed.
        bool resume = lease.Length != 0;
        RunInstallStep("记录安装状态", lease.Name, () =>
        {
            lease.SetLength(1);
            lease.Flush(true);
        });
        string temporary = Path.Combine(target, ".setup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkbenchPayload"))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                var launcher = zip.GetEntry(LauncherName);
                if (launcher == null) throw new IOException("安装包缺少启动程序。");
                int total = zip.Entries.Count;
                int completed = 0;
                int percent = 0;
                Report(progress, "正在解包安装文件…", percent);
                foreach (var entry in zip.Entries)
                {
                    if (entry == launcher) continue;
                    ExtractInstallFile(entry, InstallPath(target, entry), temporary, resume, progress);
                    int next = ++completed * 100 / total;
                    if (next != percent)
                    {
                        percent = next;
                        Report(progress, "正在解包安装文件（" + completed + " / " + total + "）", percent);
                    }
                }
                Report(progress, "正在核对安装文件…", percent);
                var missing = zip.Entries.FirstOrDefault(entry => entry != launcher &&
                    !File.Exists(Path.Combine(target, entry.FullName)));
                if (missing != null)
                    throw new IOException("解包后找不到安装文件。" + Environment.NewLine + Path.Combine(target, missing.FullName));
                // A fresh install cannot be launched before all its dependencies have been written.
                ExtractInstallFile(launcher, InstallPath(target, launcher), temporary, resume, progress);
            }
            RunInstallStep("保存安装结果", lease.Name, () =>
            {
                lease.SetLength(0);
                lease.Flush(true);
            });
        }
        finally { DeleteTemporary(temporary); }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string InstallPath(string target, ZipArchiveEntry entry)
    {
        string path = Path.GetFullPath(Path.Combine(target, entry.FullName));
        string prefix = target + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("安装包内的路径无效。");
        // An interrupted install may have left directories in the destination.
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path));
             parent != null && parent.FullName.Length >= target.Length; parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装目录指向其他位置。" + Environment.NewLine + parent.FullName);
        }
        return path;
    }

    private static void ExtractInstallFile(ZipArchiveEntry entry, string path, string temporary, bool resume,
                                           IProgress<SetupProgress> progress)
    {
        bool created = false;
        bool copied = false;
        RunInstallStep("解包安装文件", path, () =>
        {
            if (!copied)
            {
                if (Directory.Exists(path))
                    throw new IOException("安装文件位置被同名文件夹占用。");
                if (resume && FileMatchesEntry(path, entry)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var input = entry.Open())
                using (var output = new FileStream(temporary, created ? FileMode.Create : FileMode.CreateNew,
                                                   FileAccess.Write, FileShare.Read))
                {
                    created = true;
                    input.CopyTo(output);
                }
                copied = true;
            }
            if (resume && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }, temporary: temporary, progress: progress);
    }

    private static bool FileMatchesEntry(string path, ZipArchiveEntry entry)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return false;
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("安装文件指向其他位置。");
        if (file.Length != entry.Length) return false;
        using (var installed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var bundled = entry.Open())
        using (var hash = SHA256.Create())
            return hash.ComputeHash(installed).SequenceEqual(hash.ComputeHash(bundled));
    }

    private static void Report(IProgress<SetupProgress> progress, string message, int? percentage = null)
    {
        if (progress != null) progress.Report(new SetupProgress(message, percentage));
    }

    private static void RunInstallStep(string step, string path, Action action, int? retryLimit = null,
                                       string temporary = null, IProgress<SetupProgress> progress = null)
    {
        int retries = retryLimit ?? FileRetryDelays.Length;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error)
            {
                uint hresult = unchecked((uint)error.HResult);
                uint code = hresult & 0xffff;
                if (hresult >> 16 == 0x8007 && (code == 5 || code == 32 || code == 33) && attempt < retries)
                {
                    Report(progress, "暂时无法访问 " + Path.GetFileName(path) + "，正在自动重试…");
                    Thread.Sleep(FileRetryDelays[attempt]);
                    continue;
                }
                throw new InstallStepException(step, path, temporary, error);
            }
        }
    }

    private static void StageUpgrade(string target)
    {
        var update = Path.Combine(target, ".run", "update");
        var releases = Path.Combine(update, "releases");
        var pending = Path.Combine(update, "pending.json");
        var installedFile = Path.Combine(target, "app_version.txt");
        Version installed;
        if (!File.Exists(installedFile) || !Version.TryParse(File.ReadAllText(installedFile).Trim(), out installed))
            throw new IOException("无法核对已安装版本。");
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkbenchPayload"))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            string number;
            using (var reader = new StreamReader(zip.GetEntry("app_version.txt").Open()))
                number = reader.ReadToEnd().Trim();
            Version candidate;
            if (!Regex.IsMatch(number, @"\A\d{1,6}\.\d{1,6}\.\d{1,6}\z") || !Version.TryParse(number, out candidate))
                throw new IOException("安装包版本无效。");
            foreach (string record in new[] { "active.json", "pending.json" })
            {
                string path = Path.Combine(update, record);
                if (!File.Exists(path)) continue;
                var match = Regex.Match(File.ReadAllText(path), @"""version""\s*:\s*""([0-9.]+)""");
                Version recorded;
                if (Version.TryParse(match.Groups[1].Value, out recorded) && recorded > installed)
                    installed = recorded;
            }
            if (candidate <= installed) return;
            string id = Guid.NewGuid().ToString("N");
            string folder = number + "-" + id.Substring(0, 12) + "-" + id.Substring(12, 8);
            var stage = Path.Combine(releases, folder);
            RunInstallStep("创建升级目录", stage, () => Directory.CreateDirectory(stage));
            foreach (string name in new[] { "server.py", "style_history.py", "index.html", "release_update.py", "app_version.txt" })
            {
                string path = Path.Combine(stage, name);
                RunInstallStep("解包升级文件", path, () => zip.GetEntry(name).ExtractToFile(path, true));
            }
            string receipt = pending + "." + id;
            try
            {
                RunInstallStep("记录升级版本", pending, () =>
                {
                    File.WriteAllText(receipt, "{\"folder\":\"" + folder + "\",\"version\":\"" + number + "\"}");
                    if (File.Exists(pending)) File.Replace(receipt, pending, null);
                    else File.Move(receipt, pending);
                }, temporary: receipt);
            }
            finally { DeleteTemporary(receipt); }
        }
    }

    private static void CreateShortcut(string start)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" &&
            TrySaveShortcut(start, Path.Combine(desktop, "微信客服助手.lnk")))
            return;
        TrySaveShortcut(start, Path.Combine(desktop, "WeChat Assistant.lnk"));
    }

    private static bool TrySaveShortcut(string start, string path)
    {
        try
        {
            SaveShortcut(start, path);
            return true;
        }
        catch (COMException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void SaveShortcut(string start, string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic shortcut = shell.CreateShortcut(path);
        if (File.Exists(path) && !String.Equals((string)shortcut.TargetPath, start, StringComparison.OrdinalIgnoreCase))
            return;
        shortcut.TargetPath = start;
        shortcut.WorkingDirectory = Path.GetDirectoryName(start);
        string icon = Path.Combine(Path.GetDirectoryName(start), "WeChatAssistant.ico");
        if (File.Exists(icon))
            shortcut.IconLocation = icon + ",0";
        shortcut.Save();
    }
}
