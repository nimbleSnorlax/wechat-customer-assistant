using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Launcher
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var home = AppDomain.CurrentDomain.BaseDirectory;
            bool verify = args.Length == 2 && args[0] == "--verify";
            bool testWindow = args.Length == 2 && args[0] == "--window";
            int port = verify || testWindow ? Int32.Parse(args[1]) : 8766;
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port");
            var start = new ProcessStartInfo(
                Path.Combine(home, "runtime", "python", verify ? "python.exe" : "pythonw.exe"),
                "-X utf8 -u launcher.py");
            start.WorkingDirectory = home;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.EnvironmentVariables["WECHAT_ASSISTANT_PORT"] = port.ToString();
            start.EnvironmentVariables["WECHAT_ASSISTANT_DATA"] = Path.Combine(home, "data");
            start.EnvironmentVariables["WECHAT_ASSISTANT_DOTNET"] = Path.Combine(home, ".dotnet", "dotnet.exe");
            start.EnvironmentVariables["WECHAT_ASSISTANT_SDK"] = Path.Combine(home, ".run", "sdk", "WeChatBridge.dll");
            start.EnvironmentVariables["WECHAT_ASSISTANT_NATIVE_CONTACTS"] = "1";
            start.EnvironmentVariables.Remove("WECHAT_UPDATE_GITHUB_REPO");
            if (verify)
            {
                using (var child = Process.Start(start))
                {
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            string instance;
            using (var hash = SHA256.Create())
                instance = @"Local\WeChatAssistant-" + Convert.ToBase64String(
                    hash.ComputeHash(Encoding.UTF8.GetBytes(home.ToUpperInvariant() + "/" + port))).Replace('/', '_');
            bool first;
            using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-open"))
            using (var mutex = new Mutex(true, instance, out first))
            {
                if (!first)
                {
                    activation.Set();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var child = Process.Start(start))
                using (var window = new WorkbenchWindow(home, port, child, activation))
                    Application.Run(window);
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "微信助手启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class WorkbenchWindow : Form
{
    private const string AppName = "微信客服助手";
    private readonly Uri address;
    private readonly string profileDirectory;
    private readonly Process launcher;
    private readonly WebView2 view = new WebView2();
    private readonly Label status = new Label();
    private readonly Button action = new Button();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    private readonly System.Windows.Forms.Timer activationTimer = new System.Windows.Forms.Timer();
    private readonly ToolStripMenuItem exitItem;
    private bool installingRuntime;
    private bool exiting;
    private bool allowClose;

    internal WorkbenchWindow(string home, int port, Process launcher, EventWaitHandle activation)
    {
        address = new Uri("http://127.0.0.1:" + port + "/");
        profileDirectory = Path.Combine(home, "data", "desktop-webview");
        this.launcher = launcher;
        Text = AppName;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(420, 400);
        ClientSize = new Size(1200, 800);
        AutoScaleMode = AutoScaleMode.Dpi;

        view.Dock = DockStyle.Fill;
        view.Visible = false;
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.Dock = DockStyle.Fill;
        action.Dock = DockStyle.Bottom;
        action.AutoSize = true;
        action.Visible = false;
        action.Click += OnAction;
        Controls.Add(status);
        Controls.Add(action);
        Controls.Add(view);
        trayMenu.Items.Add("打开", null, (sender, args) => RestoreWindow());
        exitItem = (ToolStripMenuItem)trayMenu.Items.Add("退出", null, ExitFromTray);
        tray.Text = AppName;
        tray.Icon = Icon ?? SystemIcons.Application;
        tray.ContextMenuStrip = trayMenu;
        tray.MouseClick += (sender, args) =>
        {
            if (args.Button == MouseButtons.Left) RestoreWindow();
        };
        tray.Visible = true;
        activationTimer.Interval = 200;
        activationTimer.Tick += (sender, args) =>
        {
            if (activation.WaitOne(0)) RestoreWindow();
        };
        activationTimer.Start();
        Shown += async (sender, args) => await OpenWorkbench();
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        base.OnFormClosing(args);
        if (args.CloseReason == CloseReason.UserClosing && !allowClose)
        {
            args.Cancel = true;
            Hide();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            activationTimer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private async void ExitFromTray(object sender, EventArgs args)
    {
        if (exiting) return;
        exiting = true;
        exitItem.Enabled = false;
        try
        {
            using (var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
            using (var body = new StringContent("{}", Encoding.UTF8, "application/json"))
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                if (!launcher.HasExited) await WaitForServer();
                using (var response = await client.PostAsync(new Uri(address, "/api/shutdown"), body))
                    response.EnsureSuccessStatusCode();
            }
            if (!await Task.Run(() => launcher.WaitForExit(15000)))
                throw new TimeoutException();
        }
        catch (Exception)
        {
            if (!launcher.HasExited)
            {
                tray.ShowBalloonTip(5000, AppName, "本机服务还在停止，请稍后再试。", ToolTipIcon.Warning);
                exitItem.Enabled = true;
                exiting = false;
                return;
            }
        }
        allowClose = true;
        Close();
    }

    private async void OnAction(object sender, EventArgs args)
    {
        if (installingRuntime)
        {
            Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/#download-section")
            {
                UseShellExecute = true,
            });
            return;
        }
        await OpenWorkbench();
    }

    private async Task OpenWorkbench()
    {
        action.Visible = false;
        view.Visible = false;
        status.Visible = true;
        status.Text = "正在打开" + AppName + "…";
        installingRuntime = false;
        try
        {
            if (view.CoreWebView2 == null)
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory);
                if (IsDisposed) return;
                await view.EnsureCoreWebView2Async(environment);
                if (IsDisposed) return;
                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                view.CoreWebView2.Settings.IsWebMessageEnabled = false;
                view.CoreWebView2.NavigationStarting += (sender, args) => args.Cancel = !IsWorkbenchUrl(args.Uri);
                view.CoreWebView2.NewWindowRequested += (sender, args) =>
                {
                    args.Handled = true;
                    if (IsWorkbenchUrl(args.Uri)) view.CoreWebView2.Navigate(args.Uri);
                };
                view.CoreWebView2.NavigationCompleted += (sender, args) =>
                {
                    if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                        ShowError("本机连接中断，请重新连接。");
                };
            }
            await WaitForServer();
            if (IsDisposed) return;
            status.Visible = false;
            view.Visible = true;
            view.CoreWebView2.Navigate(address.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            installingRuntime = true;
            ShowError("请安装 Microsoft Edge WebView2 运行时，然后重新打开微信客服助手。");
        }
        catch (Exception)
        {
            if (!IsDisposed) ShowError("暂时无法连接本机服务，请重试。");
        }
    }

    private async Task WaitForServer()
    {
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
        {
            client.Timeout = TimeSpan.FromSeconds(2);
            var deadline = Stopwatch.StartNew();
            while (!IsDisposed && deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                try
                {
                    using (var response = await client.GetAsync(new Uri(address, "/api/update/status")))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            var payload = await response.Content.ReadAsStringAsync();
                            if (payload.Contains("\"current\"") && payload.Contains("\"canInstall\"")) return;
                        }
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(350);
            }
        }
        throw new TimeoutException();
    }

    private bool IsWorkbenchUrl(string url)
    {
        Uri destination;
        return Uri.TryCreate(url, UriKind.Absolute, out destination)
            && destination.Scheme == address.Scheme && destination.Host == address.Host
            && destination.Port == address.Port && destination.UserInfo.Length == 0;
    }

    private void ShowError(string text)
    {
        view.Visible = false;
        status.Visible = true;
        status.Text = text;
        action.Text = installingRuntime ? "下载 WebView2 运行时" : "重新连接";
        action.Visible = true;
    }
}
