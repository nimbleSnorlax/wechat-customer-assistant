using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WeAutoCommon.Enums;
using WeAutoCommon.Models;
using WeChatAuto.Components;
using WeChatAuto.Services;

internal sealed class BridgeSession : IDisposable
{
    private readonly IServiceProvider services;
    internal WeChatClient Client { get; }

    private BridgeSession(IServiceProvider services, WeChatClient client)
    {
        this.services = services;
        Client = client;
    }

    internal static BridgeSession Open(int? processId = null)
    {
        var selected = processId ?? WeChatWindows.LoggedInProcess()
            ?? throw new InvalidOperationException("Open one authenticated WeChat client.");
        var services = WeAutomation.Initialize(options =>
        {
            options.ConfigureDesktopEnvironment = false;
            options.IsForceOpenUITree = false;
            options.IsOptimizeMainWindowLayout = true;
            options.EnableRecordVideo = false;
            options.EnableOCR = false;
            options.UseDatabaseRead = false;
        });
        try
        {
            var factory = services.GetRequiredService<WeChatClientFactory>();
            return new BridgeSession(services, factory.ConnectMainWindow(selected));
        }
        catch
        {
            (services as IDisposable)?.Dispose();
            throw;
        }
    }

    internal static async Task<FriendInfo?> FindContact(WeChatClient client, string name)
    {
        if (!await client.SearchFriend(name)) return null;
        var header = await client.GetTitle();
        if (header.HeaderType is not (ChatType.好友 or ChatType.企业微信) || header.Title != name)
            return null;
        return await client.MessageMonitor.ReadCurrentFriendInfo(header);
    }

    private static bool ValidAlias(string value) => Regex.IsMatch(value ?? "", @"\A[A-Za-z0-9_-]{1,60}\z");

    internal async Task<object> SearchContacts(string? query)
    {
        var owner = Client.Alias;
        if (!ValidAlias(owner)) throw new InvalidOperationException("The WeChat account could not be identified.");

        string? name = query;
        FriendInfo? match;
        if (query is null)
        {
            var header = await Client.GetTitle();
            name = header.Title?.Trim();
            match = header.HeaderType is ChatType.好友 or ChatType.企业微信 &&
                name is { Length: > 0 and <= 80 }
                    ? await Client.MessageMonitor.ReadCurrentFriendInfo(header)
                    : null;
        }
        else
        {
            match = await FindContact(Client, query);
        }

        var people = match is not null && ValidAlias(match.WxId) &&
            !match.WxId.Equals(owner, StringComparison.OrdinalIgnoreCase) && name is { Length: > 0 and <= 80 }
                ? new object[] { new { id = $"uia:{owner}:{match.WxId}", name } }
                : Array.Empty<object>();
        return new { name = Client.NickName, people };
    }

    internal async Task<object> ReadTagDirectory()
    {
        var owner = Client.Alias;
        if (!ValidAlias(owner)) throw new InvalidOperationException("The WeChat account could not be identified.");
        var labels = await ContactTagCatalog.Read(Client);
        var directory = labels.Length == 0 ? [] : await Client.GetAllFriends(false);
        var people = directory
            .Where(contact => contact.ChatType is ChatType.好友 or ChatType.企业微信)
            .Select(contact => (contact, Name: (string.IsNullOrWhiteSpace(contact.MemoName) ? contact.NickName : contact.MemoName).Trim()))
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Key.Length is > 0 and <= 80 && group.Count() == 1)
            .Select(group => group.Single())
            .Where(item => ValidAlias(item.contact.WxId) && !owner.Equals(item.contact.WxId, StringComparison.OrdinalIgnoreCase))
            .Select(item => new { id = $"uia:{owner}:{item.contact.WxId}", name = item.Name,
                tags = item.contact.Lable.Select(tag => tag.Trim()).Intersect(labels, StringComparer.Ordinal).ToArray() })
            .DistinctBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new { owner, labels, people, includesUntagged = true };
    }

    public void Dispose() => (services as IDisposable)?.Dispose();
}
