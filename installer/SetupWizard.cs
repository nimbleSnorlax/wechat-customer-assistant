using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class SetupProgress
{
    internal readonly string Message;
    internal readonly int? Percentage;

    internal SetupProgress(string message, int? percentage)
    {
        Message = message;
        Percentage = percentage;
    }
}

internal sealed class SetupWizard : Form
{
    private readonly TextBox directory = new TextBox();
    private readonly Button browse = new Button { Text = "浏览…" };
    private readonly Button install = new Button { Text = "安装" };
    private readonly Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
    private readonly Button copy = new Button { Text = "复制诊断", Enabled = false, AutoSize = true };
    private readonly TextBox details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label status = new Label();
    private readonly ProgressBar progressBar = new ProgressBar { Dock = DockStyle.Fill, Height = 16, Visible = false };
    private readonly Action<string, IProgress<SetupProgress>> apply;
    private readonly bool existing;
    private bool busy;
    private bool expanded;

    internal SetupWizard(string target, bool existing, Action<string, IProgress<SetupProgress>> apply)
    {
        this.apply = apply;
        this.existing = existing;
        Text = "微信客服助手安装向导";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(570, 195);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int row = 0; row < 4; row++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label { Text = "安装目录", AutoSize = true, Margin = new Padding(3, 3, 3, 5) };
        body.Controls.Add(label, 0, 0);
        directory.Text = target;
        directory.ReadOnly = existing;
        directory.Dock = DockStyle.Fill;
        body.Controls.Add(directory, 0, 1);
        browse.AutoSize = true;
        browse.Enabled = !existing;
        browse.Click += Browse;
        body.Controls.Add(browse, 1, 1);
        status.Dock = DockStyle.Fill;
        status.AutoSize = true;
        status.MaximumSize = new Size(526, 0);
        status.Margin = new Padding(3, 10, 3, 8);
        status.Text = existing ? "已找到原来的安装目录，继续时会保留现有配置和数据。"
                               : "直接点「安装」即可使用默认目录。也可以输入新路径或点「浏览」。";
        body.Controls.Add(status, 0, 2);
        body.SetColumnSpan(status, 2);
        body.Controls.Add(progressBar, 0, 3);
        body.SetColumnSpan(progressBar, 2);
        details.Dock = DockStyle.Fill;
        details.Visible = false;
        body.Controls.Add(details, 0, 4);
        body.SetColumnSpan(details, 2);
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(0, 9, 0, 0), WrapContents = false
        };
        cancel.AutoSize = true;
        install.AutoSize = true;
        install.Text = existing ? "继续" : "安装";
        install.Click += Apply;
        copy.Click += Copy;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(install);
        buttons.Controls.Add(copy);
        body.Controls.Add(buttons, 0, 5);
        body.SetColumnSpan(buttons, 2);
        Controls.Add(body);
        AcceptButton = install;
        CancelButton = cancel;
        ActiveControl = install;
    }

    private void Browse(object sender, EventArgs args)
    {
        using (var picker = new FolderBrowserDialog())
        {
            picker.Description = "选择上一级文件夹，安装器会使用其中的 WeChatAssistant 文件夹。";
            if (Directory.Exists(directory.Text))
                picker.SelectedPath = Path.GetDirectoryName(directory.Text.Trim()) ?? directory.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
                directory.Text = Path.Combine(picker.SelectedPath, "WeChatAssistant");
        }
    }

    private async void Apply(object sender, EventArgs args)
    {
        string selected = directory.Text;
        SetBusy(true);
        details.Visible = false;
        copy.Enabled = false;
        status.Text = "正在检查安装目录…";
        progressBar.Style = ProgressBarStyle.Marquee;
        progressBar.Value = 0;
        var progress = new Progress<SetupProgress>(ShowProgress);
        var failure = await Task.Run(() =>
        {
            try
            {
                apply(selected, progress);
                return null;
            }
            catch (Exception error) { return SetupFailure.Record(selected, error); }
        });
        SetBusy(false);
        if (failure == null)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        install.Text = "重试";
        status.Text = failure.Advice;
        details.Text = failure.Summary;
        if (!expanded)
        {
            Height += Font.Height * 10;
            expanded = true;
        }
        details.Visible = true;
        copy.Tag = failure.Details;
        copy.Enabled = true;
    }

    private void ShowProgress(SetupProgress progress)
    {
        if (!busy) return;
        status.Text = progress.Message;
        if (progress.Percentage.HasValue)
        {
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = progress.Percentage.Value;
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        directory.ReadOnly = existing || busy;
        browse.Enabled = !existing && !busy;
        install.Enabled = !busy;
        cancel.Enabled = !busy;
        progressBar.Visible = busy;
        UseWaitCursor = busy;
    }

    private void Copy(object sender, EventArgs args)
    {
        try { Clipboard.SetText((string)copy.Tag); }
        catch (ExternalException)
        {
            MessageBox.Show(this, "剪贴板暂时不可用，可以选中诊断文字后再复制。", Text);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (busy) args.Cancel = true;
        base.OnFormClosing(args);
    }
}
