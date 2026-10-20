using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>One connector in the chat's connectors list (the message box's footer): omp's <c>get_mcp_servers</c> state.</summary>
public sealed partial class ChatConnectorViewModel : ObservableObject
{
    private readonly ConnectorsViewModel _page;

    internal ChatConnectorViewModel(ConnectorsViewModel page, OmpMcpServer server)
    {
        _page = page;
        _server = server;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(IsRemote), nameof(State), nameof(StatusText), nameof(IsConnected), nameof(IsFailed), nameof(IsWorking),
        nameof(IsStopped), nameof(NeedsSignIn), nameof(SignInLabel), nameof(Error), nameof(HasError), nameof(NeedsAttention), nameof(CanSignIn), nameof(CanReconnect))]
    private OmpMcpServer _server;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(CanReconnect))]
    private bool _isSigningIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignIn), nameof(CanReconnect))]
    private bool _isReconnecting;

    /// <summary>Why the last sign-in or reconnect of this server failed (omp's words).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    private string _actionError = "";

    public string Name => Server.Name;
    public bool IsRemote => Server.Transport is "http" or "sse";
    public ConnectorState State => ConnectorsViewModel.Describe(Server).State;
    public string StatusText => ConnectorsViewModel.Describe(Server).Text;
    public bool IsConnected => State == ConnectorState.Connected;
    public bool IsFailed => State == ConnectorState.Failed;
    public bool IsWorking => State == ConnectorState.Pending;
    /// <summary>Connected without tools, or waiting for a sign-in: the warning dot.</summary>
    public bool IsStopped => State == ConnectorState.NoTools || NeedsSignIn;
    public bool NeedsSignIn => Server.NeedsSignIn;
    public string SignInLabel => Server.Auth == "expired" ? Loc.T("Sign in again") : Loc.T("Sign in");

    /// <summary>omp's reason the server did not connect (a sign-in-needed server says that instead).</summary>
    public string Error => NeedsSignIn ? "" : Server.Error ?? "";
    public bool HasError => Error.Length > 0;
    public bool HasActionError => ActionError.Length > 0;

    /// <summary>The chip in the message box's footer names it: a sign-in to do, or a server that failed.</summary>
    public bool NeedsAttention => NeedsSignIn || Server.Status == "failed";

    public bool CanSignIn => NeedsSignIn && !IsSigningIn && !IsReconnecting;
    public bool CanReconnect => !NeedsSignIn && Server.Status is "failed" or "disconnected" && !IsSigningIn && !IsReconnecting;

    [RelayCommand] private Task SignIn() => _page.SignInAsync(Name);
    [RelayCommand] private Task CancelSignIn() => _page.CancelSignInAsync(Name);
    [RelayCommand] private Task Reconnect() => _page.ReconnectAsync(Name);
}

/// <summary>
/// The chat's connectors list: a chip under the message box opens each server's state in this chat (connected, sign-in
/// needed, omp's error), with Sign in, Reconnect, Reload config and the way to Settings → Plugins → MCP. It reads omp
/// only when asked (the list opens, an action answers) or when omp says a server needs a sign-in, and keeps what it
/// learned per chat: the sign-in stays reachable after the conversation's notice and the page's prompt are gone.
/// </summary>
public sealed partial class ConnectorsViewModel
{
    private sealed class ChatCache
    {
        public List<OmpMcpServer> Servers = [];
        /// <summary>Servers of omp's sign-in prompts already seen (<c>mcp_auth_required</c>).</summary>
        public HashSet<string> Prompted = new(StringComparer.Ordinal);
    }

    private readonly ConditionalWeakTable<SessionController, ChatCache> _chats = new();
    private SessionController? _chatSession;

    public ObservableCollection<ChatConnectorViewModel> ChatServers { get; } = [];

    /// <summary>The chat's omp runs: the chip shows.</summary>
    [ObservableProperty] private bool _isChatAvailable;

    [ObservableProperty] private bool _isChatOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatEmpty))]
    private bool _isChatLoading;

    /// <summary>What the last action said (the same words as the settings page's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChatNotice))]
    private string _chatNotice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChatError), nameof(ShowChatEmpty))]
    private string _chatError = "";

    private bool _chatLoaded;

    public bool HasChatNotice => ChatNotice.Length > 0;
    public bool HasChatError => ChatError.Length > 0;
    public bool HasChatServers => ChatServers.Count > 0;
    public bool ShowChatEmpty => _chatLoaded && !IsChatLoading && !HasChatError && ChatServers.Count == 0;

    public int ChatAttention => ChatServers.Count(c => c.NeedsAttention);
    public bool HasChatAttention => ChatAttention > 0;

    /// <summary>The chip's words when a server needs the user: "Sign in to figma", "figma isn't connected", "2 connectors need attention".</summary>
    public string ChipLabel
    {
        get
        {
            var needing = ChatServers.Where(c => c.NeedsAttention).ToList();
            return needing switch
            {
                [] => "",
                [{ NeedsSignIn: true } one] => Loc.F("Sign in to {0}", one.Name),
                [var one] => Loc.F("{0} isn't connected", one.Name),
                _ => Loc.Plural(needing.Count, "{0} connector needs attention", "{0} connectors need attention"),
            };
        }
    }

    public string ChipName => HasChatAttention ? Loc.F("Connectors: {0}", ChipLabel) : Loc.T("Connectors");

    /// <summary>A server's badge from omp's state of it in a chat.</summary>
    internal static (ConnectorState State, string Text) Describe(OmpMcpServer s) => s switch
    {
        { NeedsSignIn: true } => (ConnectorState.NotConnected, s.Auth == "expired" ? Loc.T("Sign-in expired") : Loc.T("Sign-in needed")),
        { Status: "connected", ToolCount: > 0 } => (ConnectorState.Connected, Loc.Plural(s.ToolCount, "Connected · {0} tool", "Connected · {0} tools")),
        { Status: "connected" } => (ConnectorState.NoTools, Loc.T("Connected · 0 tools")),
        { Status: "failed" } => (ConnectorState.Failed, Loc.T("Couldn't connect")),
        { Status: "connecting" } => (ConnectorState.Pending, Loc.T("Connecting…")),
        _ => (ConnectorState.NotConnected, Loc.T("Not connected")),
    };

    /// <summary>
    /// From each applied snapshot of the shown chat (MainViewModel.Session.cs): the chip while omp runs, the chat's own
    /// list when another chat is shown, and omp's new sign-in prompts, which mark the server and read the states.
    /// </summary>
    internal void ApplyChat(SessionController session, SessionSnapshot s)
    {
        var available = s.Phase is SessionPhase.Ready or SessionPhase.Running;
        if (IsChatAvailable != available) IsChatAvailable = available;
        if (!available) IsChatOpen = false;
        var cache = _chats.GetValue(session, _ => new ChatCache());
        if (!ReferenceEquals(session, _chatSession))
        {
            _chatSession = session;
            _chatLoaded = false;
            IsChatLoading = false;
            ChatNotice = ChatError = "";
            ShowChat(cache, reuse: false);
        }
        var prompted = s.McpAuthRequests.Select(r => r.Server).ToHashSet(StringComparer.Ordinal);
        var fresh = s.McpAuthRequests.Where(r => !cache.Prompted.Contains(r.Server)).ToList();
        cache.Prompted = prompted;
        if (fresh.Count == 0) return;
        // The server needs a sign-in now, whatever omp said of it before; its full state follows from omp
        UpdateChat(session, [.. fresh.Select(r => cache.Servers.Find(x => x.Name == r.Server) is { } known
            ? known with { Auth = r.Expired ? "expired" : "signed_out" }
            : new OmpMcpServer(r.Server, "http", null, "disconnected", 0, null, r.Expired ? "expired" : "signed_out"))], complete: false);
        if (available) _ = LoadChatAsync();
    }

    partial void OnIsChatOpenChanged(bool value)
    {
        if (!value) return;
        ChatNotice = ChatError = "";
        _ = LoadChatAsync();
    }

    [RelayCommand]
    private void ToggleChat() => IsChatOpen = !IsChatOpen;

    /// <summary>Settings → Plugins → MCP: every server, the switches, details and the add form.</summary>
    [RelayCommand]
    private Task ManageConnectorsAsync()
    {
        IsChatOpen = false;
        return _owner.OpenConnectorsPageCommand.ExecuteAsync(null);
    }

    /// <summary>omp's servers of the shown chat now (<c>get_mcp_servers</c>).</summary>
    [RelayCommand]
    internal async Task LoadChatAsync()
    {
        var session = _owner.Session;
        if (!IsChatAvailable) return;
        IsChatLoading = true;
        try
        {
            var servers = await session.GetMcpServersAsync(_owner.Closing);
            if (ReferenceEquals(session, _chatSession)) ChatError = "";
            UpdateChat(session, servers, complete: true);
        }
        catch (RpcCommandException e) when (ReferenceEquals(session, _chatSession))
        {
            ChatError = e.Code == "unknown_command" || e.Error.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
                ? Loc.T("This version of omp can't report its connectors. Update omp to see them here.")
                : e.Error;
        }
        catch (Exception e) when (IsOmpFailure(e) && ReferenceEquals(session, _chatSession))
        {
            ChatError = WhyFailed(e);
        }
        catch (Exception e) when (IsOmpFailure(e)) { }
        finally
        {
            if (ReferenceEquals(session, _chatSession))
            {
                _chatLoaded = true;
                IsChatLoading = false;
            }
        }
    }

    /// <summary>omp's states for a chat: the whole list (<paramref name="complete"/>) or the servers an action answered for.</summary>
    private void UpdateChat(SessionController session, IReadOnlyCollection<OmpMcpServer> servers, bool complete)
    {
        var cache = _chats.GetValue(session, _ => new ChatCache());
        if (complete) cache.Servers = [.. servers];
        else
            foreach (var server in servers)
            {
                var i = cache.Servers.FindIndex(x => x.Name == server.Name);
                if (i >= 0) cache.Servers[i] = server;
                else cache.Servers.Add(server);
            }
        _chatSession ??= _owner.Session;
        if (ReferenceEquals(session, _chatSession)) ShowChat(cache, reuse: true);
    }

    /// <summary>The list as the chat's cache has it; a row kept by name keeps its sign-in or reconnect under way.</summary>
    private void ShowChat(ChatCache cache, bool reuse)
    {
        var rows = reuse ? ChatServers.ToDictionary(r => r.Name, StringComparer.Ordinal) : [];
        ChatServers.Clear();
        foreach (var server in cache.Servers)
        {
            if (rows.TryGetValue(server.Name, out var row)) row.Server = server;
            else row = new ChatConnectorViewModel(this, server);
            ChatServers.Add(row);
        }
        foreach (var p in new[] { nameof(HasChatServers), nameof(ShowChatEmpty), nameof(ChatAttention), nameof(HasChatAttention), nameof(ChipLabel), nameof(ChipName) })
            OnPropertyChanged(p);
    }

    private ChatConnectorViewModel? ChatRow(string name) =>
        ReferenceEquals(_chatSession, _owner.Session) ? ChatServers.FirstOrDefault(r => r.Name == name) : null;
}
