using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class Setup
{
    private static readonly int[] FileRetryDelays = { 250, 500, 1000, 2000, 3000 };

    [STAThread]
    private static int Main(string[] args)
    {
        bool verify = args.Length == 2 && args[0] == "--verify";
        try
        {
            string folder = "WeChatAssistant";
            if (verify)
            {
                int port = Int32.Parse(args[1]);
                if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port");
                folder += "-Verify-" + port;
            }
            string root = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            string target = Path.Combine(root, folder);
            string start = Path.Combine(target, "WeChatAssistant.exe");
            using (var lease = new FileStream(target + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                if (!Directory.Exists(target)) Install(root, target);
                else StageUpgrade(target);
                if (!File.Exists(start)) throw new IOException("安装目录不完整，请联系安装包提供者。");
            }
            if (verify) return 0;

            CreateShortcut(start);
            Process.Start(new ProcessStartInfo(start) { WorkingDirectory = target, UseShellExecute = false });
            return 0;
        }
        catch (Exception error)
        {
            if (!verify) MessageBox.Show(error.Message, "微信助手安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Install(string root, string target)
    {
        string staging = Path.GetFullPath(target + ".installing-" + Guid.NewGuid().ToString("N"));
        if (Path.GetDirectoryName(staging) != root || Path.GetDirectoryName(target) != root)
            throw new IOException("安装目录无效。");
        RunInstallStep("创建安装临时目录", staging, () => Directory.CreateDirectory(staging));
        try
        {
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkbenchPayload"))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                string prefix = staging + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("安装包内的路径无效。");
                    ExtractInstallFile(entry, path);
                }
            }
            RunInstallStep("保存安装结果", target, () => Directory.Move(staging, target));
        }
        finally
        {
            // A locked file in the staging directory must not hide the installation error.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ExtractInstallFile(ZipArchiveEntry entry, string path)
    {
        bool created = false;
        RunInstallStep("解包安装文件", path, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var input = entry.Open())
            using (var output = new FileStream(path, created ? FileMode.Create : FileMode.CreateNew,
                                               FileAccess.Write, FileShare.None))
            {
                created = true;
                input.CopyTo(output);
            }
        });
    }

    private static void RunInstallStep(string step, string path, Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException))
                    throw;
                uint hresult = unchecked((uint)error.HResult);
                uint code = hresult & 0xffff;
                if (hresult >> 16 == 0x8007 && (code == 5 || code == 32 || code == 33)
                    && attempt < FileRetryDelays.Length)
                {
                    Thread.Sleep(FileRetryDelays[attempt]);
                    continue;
                }
                throw new IOException(step + "失败。" + Environment.NewLine + path + Environment.NewLine
                                      + error.Message + " (0x" + hresult.ToString("X8") + ")", error);
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
            Directory.CreateDirectory(stage);
            foreach (string name in new[] { "server.py", "style_history.py", "index.html", "release_update.py", "app_version.txt" })
                zip.GetEntry(name).ExtractToFile(Path.Combine(stage, name));
            string receipt = pending + "." + id;
            try
            {
                File.WriteAllText(receipt, "{\"folder\":\"" + folder + "\",\"version\":\"" + number + "\"}");
                if (File.Exists(pending)) File.Replace(receipt, pending, null);
                else File.Move(receipt, pending);
            }
            finally { if (File.Exists(receipt)) File.Delete(receipt); }
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
