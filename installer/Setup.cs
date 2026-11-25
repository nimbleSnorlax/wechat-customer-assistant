using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

internal static class Setup
{
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
        Directory.CreateDirectory(staging);
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
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open())
                    using (var output = new FileStream(path, FileMode.CreateNew))
                        input.CopyTo(output);
                }
            }
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void CreateShortcut(string start)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;
        string path = Path.Combine(desktop, "微信客服助手.lnk");
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
