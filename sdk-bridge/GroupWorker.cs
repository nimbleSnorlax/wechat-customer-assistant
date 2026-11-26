using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WeAutoCommon.Enums;
using WeAutoCommon.Models;
using WeChatAuto.Components;
using WeChatAuto.Models;
using WeChatAuto.Options;
using WeChatAuto.Services;
using WeChatAuto.Utils;

internal static class GroupWorker
{
    private sealed record Contact(string Id, string Name, string Alias);
    private sealed record Assignment(string Id, string Name, string Alias, Contact Adviser)
    {
        public Contact Customer => new(Id, Name, Alias);
    }

    private sealed record Config(bool Enabled, bool Ready, bool GroupReady, bool ReplyReady,
        string Revision, string OwnerAlias, Assignment[] Customers, Contact[] ReplyTargets,
        string[]? ReplyTags, string? ReplyTagRule, bool BackgroundReceive);
    private sealed record Candidate(string CustomerId, string EventId, string Message, string LastTextHash,
        string? GroupCustomerId = null, string? ReplyCustomerId = null, bool FocusAttempted = false,
        string? HoldText = null, bool ResumeReply = false);
    private sealed record FocusDecision(bool Allowed);
    private sealed record DeliveryDecision(bool Allowed, string? ImagePath);
    private sealed record ReplyRecovery(string ContactId, string TextSha256, long ExpiresAt);
    private sealed record ReplyPlan(string Id, string Text);
    private sealed record ReplyDecision(ReplyPlan? Plan);
    private sealed record HoldDecision(string? Text);
    private sealed record Plan(string Id, string GroupName, string Welcome, string AdviserId);
    private sealed record Decision(Plan? Plan);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private const int ContextMessages = 4;
    private static readonly TimeSpan LookupNoticeDelay = TimeSpan.FromSeconds(6);

    public static async Task<int> Run(int port, bool background = false)
    {
        var token = Environment.GetEnvironmentVariable("WECHAT_GROUP_WORKER_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || port is < 1 or > 65535) return 1;
        using var api = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/api/group-worker/"),
            Timeout = TimeSpan.FromSeconds(85)
        };
        api.DefaultRequestHeaders.Add("X-WeChat-Worker", token);
        using var stopping = new CancellationTokenSource();
        Config? config = null;
        var stage = "read-config";
        try
        {
            config = await api.GetFromJsonAsync<Config>("config");
            if (config is not { Ready: true } || config.BackgroundReceive != background) return 0;
            if (background)
            {
                stage = "background-receive";
                return await RunBackground(api, config, stopping.Token, step => stage = step);
            }
            stage = "resolve-config";
            config = await api.GetFromJsonAsync<Config>("config?resolve=1");
            if (config is null || !config.Ready || config.BackgroundReceive) return 0;
            stage = "connect-wechat";
            if (WeChatWindows.CountLoggedIn() != 1)
            {
                await SetStates(api, config, "disconnected");
                return 1;
            }
            if (!WeChatWindows.IsForeground())
            {
                await SetStates(api, config, "paused");
                return 0;
            }
            SupperMouseKey.CanUseDesktopInput = WeChatWindows.IsForeground;
            using var session = BridgeSession.Open();
            var client = session.Client;
            if (!string.Equals(client.Alias, config.OwnerAlias, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The local history account and current WeChat account do not match.");
            stage = "verify-contacts";

            foreach (var assignment in config.Customers)
                await VerifyContact(client, assignment.Adviser);

            var replyTargets = config.ReplyTargets.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var repliesByName = config.ReplyTargets.ToDictionary(item => item.Name, StringComparer.Ordinal);
            var groupsByName = config.Customers.ToDictionary(item => item.Name, StringComparer.Ordinal);
            var clients = config.Customers.Select(item => item.Customer).Concat(replyTargets.Values)
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .Where(items => items.Select(item => item.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                .ToDictionary(items => items.Key, items => items.First(), StringComparer.Ordinal);
            if (clients.Count == 0) throw new InvalidOperationException("No configured contact can be uniquely verified.");
            var namesById = clients.Values.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
            var seen = new HashSet<string>();
            var baselineDates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var recovery = ReadRecovery();
            Candidate? missed = null;
            foreach (var contact in clients.Values)
            {
                await VerifyContact(client, contact);
                stage = "baseline-messages";
                var recent = await ReadLatest(client, contact.Name);
                baselineDates[contact.Id] = recent.Select(message => message.SendDate).DefaultIfEmpty().Max();
                foreach (var previous in recent)
                {
                    if (Incoming(previous))
                        seen.Add(MessageFingerprint(config.OwnerAlias, contact.Id, previous));
                }
                if (recovery?.ContactId == contact.Id && replyTargets.ContainsKey(contact.Id)
                    && recent.LastOrDefault() is { MessageType: MessageType.文字 } last && last.Who == contact.Name
                    && TextHash(last.Message) == recovery.TextSha256)
                    missed = new Candidate(contact.Id, MessageFingerprint(config.OwnerAlias, contact.Id, last),
                        last.Message, recovery.TextSha256);
            }
            var pending = Channel.CreateBounded<Candidate>(new BoundedChannelOptions(24)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            var options = new MessageMonitorOptions
            {
                FetchImage = false, FetchVideo = false, FetchAttachment = false, FetchFriendInfo = false,
                ClickRedEnvelope = false, ContextFetchNumber = ContextMessages, IsRiskPrevention = false,
                MonitorSelectedConversation = true
            };
            stage = "register-listener";
            await client.AddMessageListener(clients.Keys.ToArray(), context =>
            {
                // The SDK invokes this callback on its UI worker. Only queue work here.
                if (clients.TryGetValue(context.Who, out var contact))
                {
                    // WeChat timestamps can be inherited from an earlier bubble in the same chat.
                    var fresh = context.NewMessages.Where(message => Incoming(message)
                            && (message.SendDate == default || message.SendDate >= baselineDates[contact.Id]))
                        .Select(message => (Id: MessageFingerprint(config.OwnerAlias, contact.Id, message), Text: message.Message.Trim()))
                        .Where(message => seen.Add(message.Id)).TakeLast(ContextMessages).ToArray();
                    if (fresh.Length > 0)
                    {
                        var content = string.Join("\n", fresh.Select(message => message.Text));
                        pending.Writer.TryWrite(new Candidate(contact.Id, fresh[^1].Id,
                            content.Length > 2800 ? content[^2800..] : content, TextHash(fresh[^1].Text)));
                        TraceWorker("New incoming text queued.");
                    }
                }
                return Task.CompletedTask;
            }, IsOpenMonitor: false, userToken: stopping.Token, options: options);
            TraceWorker("Listener registered.");
            if (recovery is not null) TraceWorker(missed is null ? "Recovered incoming message could not be verified." : "Verified incoming message queued for recovery.");
            if (missed is not null) pending.Writer.TryWrite(missed);

            var handling = Task.Run(async () =>
            {
                await foreach (var item in pending.Reader.ReadAllAsync(stopping.Token))
                {
                    var name = namesById[item.CustomerId];
                    var grouped = config.GroupReady && groupsByName.TryGetValue(name, out var assignment)
                        && await HandleCandidate(api, client, config, item with { CustomerId = assignment.Id }, stopping.Token);
                    if (!grouped && repliesByName.TryGetValue(name, out var contact))
                    {
                        stage = "reply";
                        await HandleReply(api, client, config, contact, item with { CustomerId = contact.Id }, stopping.Token);
                    }
                }
            });
            try
            {
                int missingWindowChecks = 0;
                while (!handling.IsCompleted)
                {
                    missingWindowChecks = WeChatWindows.CountLoggedIn() == 1 ? 0 : missingWindowChecks + 1;
                    if (missingWindowChecks >= 3)
                    {
                        await SetStates(api, config, "disconnected");
                        return 1;
                    }
                    if (!await StillCurrent(api, config.Revision))
                    {
                        await SetStates(api, config, "stopped");
                        return 0;
                    }
                    if (!WeChatWindows.IsForeground())
                    {
                        await SetStates(api, config, "paused");
                        return 0;
                    }
                    await SetStates(api, config, "running");
                    await Task.WhenAny(handling, Task.Delay(8000));
                }
                await handling;
                return 0;
            }
            finally
            {
                await stopping.CancelAsync();
                try { await handling; }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception error)
        {
            var method = new System.Diagnostics.StackTrace(error).GetFrame(0)?.GetMethod()?.Name;
            var status = error is HttpRequestException { StatusCode: { } code } ? $" HTTP {(int)code}" : "";
            TraceWorker($"{stage}: {error.GetType().Name}{status} ({method})");
            try
            {
                if (config is not null)
                    await SetStates(api, config, error is OperationCanceledException ? "paused" : "error");
            }
            catch (Exception) { }
            return 1;
        }
    }

    private static async Task<int> RunBackground(HttpClient api, Config config, CancellationToken stopping,
        Action<string> setStage)
    {
        while (await StillCurrent(api, config.Revision))
        {
            setStage("detect-wechat");
            var processId = WeChatWindows.LoggedInProcess();
            if (processId is not int wechatProcess)
            {
                await SetStates(api, config, "disconnected");
                return 0;
            }

            setStage("read-background-message");
            var incoming = await api.GetFromJsonAsync<Candidate>(
                "background-message?revision=" + config.Revision, stopping);
            if (incoming is { FocusAttempted: false } && !WeChatWindows.IsForeground(wechatProcess))
            {
                using var response = await SendJson(api, "background-focus",
                    new { incoming.CustomerId, incoming.EventId, config.Revision }, stopping);
                response.EnsureSuccessStatusCode();
                var decision = await response.Content.ReadFromJsonAsync<FocusDecision>(cancellationToken: stopping);
                if (decision?.Allowed == true && !WeChatWindows.IsForeground(wechatProcess)
                    && !WeChatWindows.TryFocusLoggedIn(wechatProcess))
                    TraceWorker("Could not bring WeChat to the foreground; keeping the message pending.");
            }
            if (incoming is null)
                await SetStates(api, config, "background");
            else if (!WeChatWindows.IsForeground(wechatProcess))
            {
                if (config.ReplyReady)
                    await Post(api, "state", new { kind = "reply", state = incoming.ReplyCustomerId is null
                        && incoming.GroupCustomerId is not null ? "background" : "reply_pending" });
                if (config.GroupReady)
                    await Post(api, "state", new { kind = "group", state = incoming.GroupCustomerId is null
                        ? "background" : "reply_pending" });
            }
            else
            {
                setStage("resolve-contacts");
                var resolved = await api.GetFromJsonAsync<Config>("config?resolve=1", stopping);
                if (resolved is not { Ready: true, BackgroundReceive: true } || resolved.Revision != config.Revision)
                    break;
                if (!WeChatWindows.IsForeground(wechatProcess))
                    continue;
                var groupId = incoming.GroupCustomerId;
                var replyId = incoming.ReplyCustomerId ?? (groupId is null ? incoming.CustomerId : null);
                var assignment = resolved.Customers.SingleOrDefault(target => target.Id == groupId);
                var contact = resolved.ReplyTargets.SingleOrDefault(target => target.Id == replyId);
                if (assignment is null && contact is null) break;

                SupperMouseKey.CanUseDesktopInput = () => WeChatWindows.IsForeground(wechatProcess);
                setStage("connect-wechat");
                using var session = BridgeSession.Open(wechatProcess);
                setStage("verify-wechat-account");
                if (!string.Equals(session.Client.Alias, resolved.OwnerAlias, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The local history account and current WeChat account do not match.");
                var grouped = assignment is not null && await HandleCandidate(api, session.Client, resolved,
                    incoming with { CustomerId = assignment.Id }, stopping);
                setStage("verify-and-reply");
                if (grouped || contact is null || await HandleReply(api, session.Client, resolved, contact,
                        incoming with { CustomerId = contact.Id }, stopping, wechatProcess))
                    await Post(api, "background-ack", new { incoming.CustomerId, incoming.EventId });
            }

            await Task.Delay(TimeSpan.FromSeconds(4), stopping);
        }
        await SetStates(api, config, "stopped");
        return 0;
    }

    private static async Task<FriendInfo> VerifyContact(WeChatClient client, Contact expected)
    {
        SupperMouseKey.CheckDesktopInput();
        var selected = await BridgeSession.FindContact(client, expected.Name);
        if (selected is null || !string.Equals(selected.WxId, expected.Alias, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured contact is not uniquely verifiable.");
        return selected;
    }

    private static bool Incoming(SimpleMessageBubble message) =>
        message.MessageType == MessageType.文字
        && !string.IsNullOrWhiteSpace(message.Who) && message.Who is not ("我" or "系统")
        && !string.IsNullOrWhiteSpace(message.Message);

    private static async Task<bool> StillCurrent(HttpClient api, string revision)
    {
        var state = await api.GetFromJsonAsync<Config>("config");
        return state is { Ready: true } && state.Revision == revision;
    }

    private static async Task Post(HttpClient api, string path, object data)
    {
        using var response = await SendJson(api, path, data);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> SendJson(HttpClient api, string path, object data,
        CancellationToken stopping = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(data, WebJson), Encoding.UTF8, "application/json");
        return await api.PostAsync(path, content, stopping);
    }

    private static async Task SetStates(HttpClient api, Config config, string state)
    {
        if (config.ReplyReady) await Post(api, "state", new { kind = "reply", state });
        if (config.GroupReady) await Post(api, "state", new { kind = "group", state });
    }

    private static Task<List<SimpleMessageBubble>> ReadLatest(WeChatClient client, string who, int count = ContextMessages) =>
        client.PullMessages(who, count, IsIncludeSystemMessage: false, options: new PullMessagesOptions
        {
            FetchImage = false, FetchAttachment = false, FetchVideo = false, ReadWhilePaused = true,
            BottomRetryNumber = 2, usingMessageCacheFill = false,
        });

    private static string MessageFingerprint(string account, string contact, SimpleMessageBubble message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{account}\n{contact}\n{message.SendDate:O}\n{message.Message}"));
        return Convert.ToHexStringLower(bytes);
    }

    private static string TextHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim())));

    private static ReplyRecovery? ReadRecovery()
    {
        var value = Environment.GetEnvironmentVariable("WECHAT_ASSISTANT_RETRY_REPLY");
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var recovery = JsonSerializer.Deserialize<ReplyRecovery>(value, WebJson);
            return recovery?.ExpiresAt >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? recovery : null;
        }
        catch (JsonException) { return null; }
    }

    private static void TraceWorker(string message)
    {
        Console.Error.WriteLine($"WeChat worker: {message}");
        if (Environment.GetEnvironmentVariable("WECHAT_ASSISTANT_WORKER_TRACE") is not { Length: > 0 } path)
            return;
        try { File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static Task Result(HttpClient api, Plan plan, string state, string[]? members = null, string? actualGroupName = null) =>
        Post(api, "result", new { plan.Id, state, members, actualGroupName, completedAt = DateTimeOffset.Now.ToString("O") });

    internal static string[]? VerifyThreeMembers(IReadOnlyCollection<string> actual, string customer, string adviser, string owner)
    {
        if (actual.Count != 3 || customer == adviser || string.IsNullOrWhiteSpace(owner) ||
            actual.Count(name => name == customer) != 1 ||
            actual.Count(name => name == adviser) != 1 || owner == customer || owner == adviser)
            return null;
        var remaining = actual.Single(name => name != customer && name != adviser);
        return remaining == owner || remaining == "我" ? [owner, customer, adviser] : null;
    }

    private static async Task<bool> HandleReply(HttpClient api, WeChatClient client, Config config, Contact target,
        Candidate incoming, CancellationToken stopping, int? wechatProcess = null)
    {
        if (config.BackgroundReceive)
        {
            var contact = await VerifyContact(client, target);
            if (!AllowsReplyTags(config, contact) || !await IsReplyAnchor(client, target, incoming))
            {
                if (incoming.ResumeReply && WeChatWindows.IsForeground())
                    await AcknowledgeHold(api, incoming.EventId, "cancelled");
                return true;
            }
        }
        var planTask = RequestReplyPlan(api, config, incoming, stopping);
        if (config.BackgroundReceive && !incoming.ResumeReply
            && await Task.WhenAny(planTask, Task.Delay(LookupNoticeDelay, stopping)) != planTask)
        {
            try
            {
                var waiting = await TrySendLookupHold(api, client, config, target, incoming, planTask, stopping);
                if (waiting is not null) incoming = incoming with { HoldText = waiting };
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                TraceWorker("Lookup notice deferred because the WeChat window lost focus.");
            }
        }
        var plan = await planTask;
        if (plan is null) return true;
        if (wechatProcess is int processId && !WeChatWindows.IsForeground(processId)
            && !WeChatWindows.TryFocusLoggedIn(processId))
        {
            await SetStates(api, config, "reply_pending");
            return false;
        }

        Task Acknowledge(string state, string? imageStatus = null) => Post(api, "reply-result",
            new { plan.Id, state, imageStatus, completedAt = DateTimeOffset.Now.ToString("O") });

        var started = false;
        var listenerPaused = false;
        try
        {
            if (!config.BackgroundReceive)
            {
                await client.PauseMessageListener();
                listenerPaused = true;
            }
            if (stopping.IsCancellationRequested || !await StillCurrent(api, config.Revision))
            {
                await Acknowledge("cancelled");
                return true;
            }
            var selected = await VerifyContact(client, target);
            if (!AllowsReplyTags(config, selected))
            {
                await Acknowledge("cancelled");
                return true;
            }
            var matchesIncoming = await IsReplyAnchor(client, target, incoming);
            if (!matchesIncoming || stopping.IsCancellationRequested
                || !await StillCurrent(api, config.Revision))
            {
                TraceWorker($"Reply skipped (same incoming={matchesIncoming}, stopping={stopping.IsCancellationRequested}).");
                if (config.BackgroundReceive && !WeChatWindows.IsForeground(wechatProcess))
                    return false;
                await Acknowledge("cancelled");
                return true;
            }
            SupperMouseKey.CheckDesktopInput();
            using var delivery = await SendJson(api, "reply-start", new { plan.Id, config.Revision }, stopping);
            delivery.EnsureSuccessStatusCode();
            var decision = await delivery.Content.ReadFromJsonAsync<DeliveryDecision>(cancellationToken: stopping);
            if (decision?.Allowed != true)
                return false;
            started = true;
            TraceWorker("Verified current incoming text; submitting reply.");
            await client.SendMessage(target.Name, plan.Text);
            var sent = (await ReadLatest(client, target.Name, 1)).LastOrDefault();
            if (sent is not { MessageType: MessageType.文字, Who: "我" } || sent.Message.Trim() != plan.Text)
            {
                var recent = await ReadLatest(client, target.Name);
                var triggerIndex = recent.FindLastIndex(message => message.MessageType == MessageType.文字
                    && message.Who == target.Name && TextHash(message.Message) == incoming.LastTextHash);
                if (triggerIndex < 0 || !recent.Skip(triggerIndex + 1).Any(message =>
                    message.MessageType == MessageType.文字 && message.Who == "我" && message.Message.Trim() == plan.Text))
                    throw new InvalidOperationException("The automatic reply could not be confirmed in the conversation.");
            }
            string? imageStatus = null;
            if (decision.ImagePath is { Length: > 0 } imagePath)
            {
                imageStatus = "skipped";
                if (File.Exists(imagePath) && Path.GetFileName(Path.GetDirectoryName(imagePath)) == "knowledge-images"
                    && Path.GetExtension(imagePath) is (".jpg" or ".jpeg" or ".png")
                    && sent is { MessageType: MessageType.文字, Who: "我" } && sent.Message.Trim() == plan.Text
                    && !stopping.IsCancellationRequested && await StillCurrent(api, config.Revision)
                    && await client.GetOnlyTitle() == target.Name)
                {
                    try
                    {
                        SupperMouseKey.CheckDesktopInput();
                        await client.SendFile(target.Name, [imagePath]);
                        var latest = (await ReadLatest(client, target.Name, 1)).LastOrDefault();
                        imageStatus = latest is { Who: "我" } && (latest.MessageType == MessageType.图片
                            || latest.MessageType == MessageType.文件 &&
                            latest.Message.Contains(Path.GetFileName(imagePath), StringComparison.OrdinalIgnoreCase))
                            ? "sent" : "unconfirmed";
                    }
                    catch (Exception error)
                    {
                        imageStatus = "unconfirmed";
                        TraceWorker("Image delivery could not be confirmed: " + error.GetType().Name);
                    }
                }
            }
            await Acknowledge("sent", imageStatus);
            TraceWorker("Reply delivery confirmed.");
            return true;
        }
        catch
        {
            if (started)
                await Acknowledge("uncertain");
            throw;
        }
        finally
        {
            if (listenerPaused) await client.ResumeMessageListener();
        }
    }

    private static async Task<ReplyPlan?> RequestReplyPlan(HttpClient api, Config config, Candidate incoming,
        CancellationToken stopping)
    {
        using var response = await SendJson(api, "reply-plan",
            new { incoming.CustomerId, incoming.EventId, incoming.Message, incoming.ResumeReply, config.Revision }, stopping);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReplyDecision>(cancellationToken: stopping))?.Plan;
    }

    private static Task AcknowledgeHold(HttpClient api, string id, string state) =>
        Post(api, "reply-hold-result", new { id, state, completedAt = DateTimeOffset.Now.ToString("O") });

    private static async Task<string?> TrySendLookupHold(HttpClient api, WeChatClient client, Config config,
        Contact target, Candidate incoming, Task<ReplyPlan?> planTask, CancellationToken stopping)
    {
        if (stopping.IsCancellationRequested || !WeChatWindows.IsForeground()) return null;
        using var response = await SendJson(api, "reply-hold",
            new { incoming.CustomerId, incoming.EventId, config.Revision }, stopping);
        response.EnsureSuccessStatusCode();
        var decision = await response.Content.ReadFromJsonAsync<HoldDecision>(cancellationToken: stopping);
        if (decision?.Text is not { Length: > 0 } text) return null;

        var started = false;
        var paused = false;
        try
        {
            if (planTask.IsCompleted || stopping.IsCancellationRequested || !WeChatWindows.IsForeground())
                return null;
            await client.PauseMessageListener();
            paused = true;
            var selected = await VerifyContact(client, target);
            if (!AllowsReplyTags(config, selected) || !await IsLatestIncoming(client, target, incoming)
                || planTask.IsCompleted || !await StillCurrent(api, config.Revision))
                return null;
            SupperMouseKey.CheckDesktopInput();
            started = true;
            await client.SendMessage(target.Name, text);
            if (!await IsReplyAnchor(client, target, incoming with { HoldText = text }))
                throw new InvalidOperationException("The lookup notice could not be confirmed.");
            await AcknowledgeHold(api, incoming.EventId, "sent");
            return text;
        }
        finally
        {
            if (!started) await AcknowledgeHold(api, incoming.EventId, "cancelled");
            if (paused) await client.ResumeMessageListener();
        }
    }

    private static bool AllowsReplyTags(Config config, FriendInfo contact)
    {
        if (config.ReplyTags is not { Length: > 0 } tags) return true;
        var matches = contact.Lable.Select(tag => tag.Trim()).Intersect(tags, StringComparer.Ordinal).Any();
        return config.ReplyTagRule == "exclude" ? contact.LabelsRead && !matches : matches;
    }

    private static async Task<bool> IsLatestIncoming(WeChatClient client, Contact target, Candidate incoming)
    {
        var latest = (await ReadLatest(client, target.Name, 1)).LastOrDefault();
        return latest is { MessageType: MessageType.文字 } && latest.Who == target.Name &&
            TextHash(latest.Message) == incoming.LastTextHash;
    }

    private static async Task<bool> IsReplyAnchor(WeChatClient client, Contact target, Candidate incoming)
    {
        if (incoming.HoldText is not { Length: > 0 } waiting)
            return await IsLatestIncoming(client, target, incoming);
        var recent = await ReadLatest(client, target.Name, 2);
        return recent.Count >= 2
            && recent[^1] is { MessageType: MessageType.文字, Who: "我" }
            && recent[^1].Message.Trim() == waiting
            && recent[^2] is { MessageType: MessageType.文字 }
            && recent[^2].Who == target.Name && TextHash(recent[^2].Message) == incoming.LastTextHash;
    }

    private static async Task<bool> HandleCandidate(HttpClient api, WeChatClient client, Config config, Candidate incoming, CancellationToken stopping)
    {
        var assignment = config.Customers.Single(person => person.Id == incoming.CustomerId);
        var target = assignment.Customer;
        var adviser = assignment.Adviser;
        if (config.BackgroundReceive)
        {
            await VerifyContact(client, target);
            if (!await IsLatestIncoming(client, target, incoming)) return true;
        }
        using var response = await SendJson(api, "plan", new
        {
            incoming.CustomerId, incoming.EventId, incoming.Message, config.Revision
        }, stopping);
        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<Decision>(cancellationToken: stopping))?.Plan;
        if (plan is null) return false;
        if (stopping.IsCancellationRequested || !await StillCurrent(api, config.Revision))
        {
            await Result(api, plan, "cancelled");
            return true;
        }

        if (plan.AdviserId != adviser.Id)
        {
            await Result(api, plan, "cancelled");
            return true;
        }
        await client.PauseMessageListener();
        var actionStarted = false;
        var created = false;
        try
        {
            await VerifyContact(client, adviser);
            await VerifyContact(client, target);
            if (stopping.IsCancellationRequested || !await StillCurrent(api, config.Revision))
            {
                await Result(api, plan, "cancelled");
                return true;
            }
            if (!await client.SearchFriend(target.Name) || await client.GetOnlyTitle() != target.Name)
                throw new InvalidOperationException("The active conversation could not be verified.");
            if ((config.BackgroundReceive && !await IsLatestIncoming(client, target, incoming))
                || stopping.IsCancellationRequested || !await StillCurrent(api, config.Revision))
            {
                await Result(api, plan, "cancelled");
                return true;
            }
            SupperMouseKey.CheckDesktopInput();
            actionStarted = true;
            var create = await client.CreateOwnerChatGroup("", "", [adviser.Name]);
            if (!create.Success) throw new InvalidOperationException("Group creation not confirmed.");
            var group = await client.GetTitle();
            if (group.HeaderType != ChatType.群聊 || string.IsNullOrWhiteSpace(group.Title))
                throw new InvalidOperationException("The newly created group could not be identified.");
            var actual = await client.GetChatGroupMemberList("");
            var verified = VerifyThreeMembers(actual, target.Name, adviser.Name, client.NickName);
            if (verified is null || await client.GetOnlyTitle() != group.Title)
                throw new InvalidOperationException("The exact three-person membership could not be verified.");

            var rename = await client.RenameActiveGroup(group.Title, plan.GroupName);
            if (!rename.Success || await client.GetOnlyTitle() != rename.Value)
                throw new InvalidOperationException("The group's current name could not be verified.");
            await Result(api, plan, "created", verified, rename.Value);
            created = true;

            if (stopping.IsCancellationRequested || !await StillCurrent(api, config.Revision)
                || !await client.SendToActiveGroup(rename.Value, plan.Welcome))
                throw new InvalidOperationException("The group's welcome could not be safely delivered.");
            var recent = await ReadLatest(client, rename.Value);
            var sent = recent.Any(message => message.Who == "我" && message.Message == plan.Welcome);
            await Result(api, plan, sent ? "completed" : "welcomeFailed");
            if (!sent) throw new InvalidOperationException("Welcome message delivery could not be confirmed.");
            return true;
        }
        catch
        {
            await Result(api, plan, created ? "welcomeFailed" : actionStarted ? "uncertain" : "cancelled");
            throw;
        }
        finally
        {
            await client.ResumeMessageListener();
        }
    }
}

