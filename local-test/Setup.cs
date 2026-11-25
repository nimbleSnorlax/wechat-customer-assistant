using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

internal static class Setup
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool interactive = args.Length == 0;
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WeChatAssistant-LocalTest");
            string instance = interactive ? "Test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") : args[0];
            if (String.IsNullOrWhiteSpace(instance) || instance == "." || instance == ".." ||
                instance.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("测试安装目录名无效。");
            int port = args.Length < 2 ? 18766 : Int32.Parse(args[1]);
            if (port < 1024 || port > 65534)
                throw new ArgumentOutOfRangeException("port");
            string target = Path.Combine(root, instance);
            Directory.CreateDirectory(root);
            using (var installLock = new FileStream(Path.Combine(root, ".setup.lock"), FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None))
            {
                if (Directory.Exists(target) || File.Exists(target))
                    throw new IOException("这份测试版已存在，请使用现有启动文件或选一个新的目录。");
                Directory.CreateDirectory(target);
                using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkbenchPayload"))
                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    string prefix = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string path = Path.GetFullPath(Path.Combine(target, entry.FullName));
                        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("安装包内的路径无效。");
                        using (var file = new FileStream(path, FileMode.CreateNew))
                        using (var contents = entry.Open())
                            contents.CopyTo(file);
                    }
                }
                File.WriteAllText(Path.Combine(target, "port.txt"), port.ToString());
            }
            if (interactive)
                MessageBox.Show("测试版已安装。运行以下文件打开工作台：" + Environment.NewLine +
                    Path.Combine(target, "Start.cmd"), "微信助手本地测试");
            return 0;
        }
        catch (Exception error)
        {
            if (interactive)
                MessageBox.Show(error.Message, "微信助手安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                Console.Error.WriteLine(error);
            return 1;
        }
    }
}
