using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using WeAutoCommon.Enums;
using WeChatAuto.Components;
using WeChatAuto.Utils;

internal static class ContactTagCatalog
{
    private const string ManagerClass = "mmui::ContactsManagerWindow";
    private const string SidebarClass = "mmui::ContactsManagerControlView";
    private const string FolderClass = "mmui::ContactsManagerControlFolderCell";
    private const string CreateTagClass = "mmui::ContactsManagerControlCreateLabelCell";
    private static readonly string[] TagFolderNames = ["标签", "標籤", "Tags"];
    private static readonly string[] ManageContactsNames = ["通讯录管理", "通訊錄管理", "Manage Contacts"];

    internal static async Task<string[]> Read(WeChatClient client)
    {
        await client.Navigation.SwitchNavigation(NavigationType.通讯录);
        try { return await WeChatInvoker.Call(automation => ReadManager(automation, client)); }
        finally { await client.Navigation.SwitchNavigation(NavigationType.微信); }
    }

    private static string[] ReadManager(UIA3Automation automation, WeChatClient client)
    {
        var desktop = automation.GetDesktop();
        var processId = client.MainWindow.Properties.ProcessId.Value;
        AutomationElement? FindManager() => desktop.FindFirstChild(condition =>
            condition.ByClassName(ManagerClass).And(condition.ByProcessId(processId)));
        var existing = FindManager();
        var manager = existing;
        if (manager is null)
        {
            var entry = client.MainWindow.FindAllDescendants(condition => condition.ByControlType(ControlType.Button)
                .Or(condition.ByControlType(ControlType.ListItem)))
                .FirstOrDefault(item => ManageContactsNames.Contains(item.Name?.Trim()));
            if (entry is null) throw new InvalidOperationException("找不到微信的通讯录管理入口。");
            entry.Click();
            manager = Retry.WhileNull(FindManager, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(120)).Result;
        }
        if (manager is null) throw new InvalidOperationException("无法打开微信通讯录管理窗口。");
        try { return ReadSidebar(manager); }
        finally { if (existing is null) manager.AsWindow().Close(); }
    }

    private static string[] ReadSidebar(AutomationElement manager)
    {
        var list = manager.FindFirstDescendant(condition =>
            condition.ByClassName(SidebarClass).And(condition.ByControlType(ControlType.List)));
        if (list is null) throw new InvalidOperationException("找不到微信标签列表。");
        AutomationElement[] Rows() => list.FindAllChildren(condition => condition.ByControlType(ControlType.ListItem));
        var scroll = list.Patterns.Scroll.PatternOrDefault;
        if (scroll?.VerticallyScrollable.ValueOrDefault == true)
            scroll.SetScrollPercent(-1, 0);
        var rows = Rows();
        var folder = Array.FindIndex(rows, IsTagFolder);
        if (folder < 0) throw new InvalidOperationException("微信通讯录里没有找到标签分区。");
        if (folder + 1 == rows.Length || rows[folder + 1].ClassName == FolderClass)
        {
            rows[folder].Click();
            var expanded = Retry.WhileFalse(() =>
            {
                var expandedRows = Rows();
                var index = Array.FindIndex(expandedRows, IsTagFolder);
                return index >= 0 && index + 1 < expandedRows.Length && expandedRows[index + 1].ClassName != FolderClass;
            }, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
            if (!expanded.Success) throw new InvalidOperationException("微信标签分区还没有展开。");
        }
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var started = false;
        var createFound = false;
        for (var page = 0; page < 200; page++)
        {
            var finished = false;
            foreach (var row in Rows())
            {
                if (row.ClassName == FolderClass)
                {
                    if (IsTagFolder(row)) started = true;
                    else if (started && createFound) { finished = true; break; }
                    else if (started) break;
                    continue;
                }
                if (!started) continue;
                if (row.ClassName == CreateTagClass) { createFound = true; continue; }
                var name = row.Name?.Split('\n')[0].Trim();
                if (!string.IsNullOrEmpty(name)) labels.Add(name);
            }
            if (finished || scroll?.VerticallyScrollable.ValueOrDefault != true || scroll.VerticalScrollPercent.ValueOrDefault >= 99.9)
            {
                if (!createFound) throw new InvalidOperationException("微信标签列表没有完整读取。");
                return labels.Order(StringComparer.CurrentCulture).ToArray();
            }
            var before = scroll.VerticalScrollPercent.ValueOrDefault;
            scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            var moved = Retry.WhileFalse(() => scroll.VerticalScrollPercent.ValueOrDefault > before,
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(70));
            if (!moved.Success) throw new InvalidOperationException("微信标签列表滚动没有完成。");
        }
        throw new InvalidOperationException("微信标签列表没有完整读取。");
    }

    private static bool IsTagFolder(AutomationElement item) =>
        item.ClassName == FolderClass && TagFolderNames.Contains(item.Name?.Trim());
}
