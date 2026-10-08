using System.Globalization;
using System.Text.RegularExpressions;

namespace OmpGui.ClientCore;

/// <summary>The stages of an SSH connection, in the order OpenSSH goes through them.</summary>
public enum SshStage { Resolve, Tcp, Banner, Kex, HostKey, Auth }

/// <summary>How ssh reached the server, as its own log says (ssh_config ProxyJump / ProxyCommand or neither).</summary>
public enum SshRoute { Unknown, Direct, ProxyJump, ProxyCommand }

/// <summary>Why a check failed. Each value is a code without addresses, users or paths.</summary>
public enum SshFailure
{
    None,
    ResolveFailed,
    TcpRefused,
    TcpTimeout,
    TcpUnreachable,
    TcpFailed,
    BannerTimeout,
    ClosedBeforeBanner,
    NotSsh,
    ProxyFailed,
    KexNoMatch,
    HostKeyChanged,
    HostKeyRejected,
    KeyUnusable,
    AuthDenied,
    ConnectionClosed,
    NoAnswer,
    RemoteCommandFailed,
    Unknown,
}

/// <summary>
/// What one <c>ssh</c> run proved, read from its debug log. <see cref="FailedAt"/> is the first stage the log does not
/// show as passed; every stage before it passed (OpenSSH only moves on after the previous one), except
/// <see cref="SshStage.Resolve"/> and <see cref="SshStage.Tcp"/> on a proxied route: the jump host or the proxy
/// command makes that connection, out of this ssh's sight. The raw log is not kept.
/// </summary>
public sealed record SshDiagnosis(SshRoute Route, int? Port, SshStage? FailedAt, SshFailure Failure, string? ProxyMessage)
{
    public bool Connected => Failure == SshFailure.None;

    public bool Proxied => Route is SshRoute.ProxyJump or SshRoute.ProxyCommand;

    /// <summary>A stable code for the outcome, such as <c>banner-timeout</c>, safe to paste into a report.</summary>
    public string Code => Failure switch
    {
        SshFailure.None => "ok",
        SshFailure.RemoteCommandFailed => "command-failed",
        SshFailure.Unknown => "unknown",
        _ => StageCode(FailedAt) + "-" + FailureCode(Failure),
    };

    private static string StageCode(SshStage? stage) => stage switch
    {
        SshStage.Resolve => "resolve",
        SshStage.Tcp => "tcp",
        SshStage.Banner => "banner",
        SshStage.Kex => "kex",
        SshStage.HostKey => "hostkey",
        SshStage.Auth => "auth",
        _ => "ssh",
    };

    private static string FailureCode(SshFailure f) => f switch
    {
        SshFailure.ResolveFailed => "failed",
        SshFailure.TcpRefused => "refused",
        SshFailure.TcpTimeout or SshFailure.BannerTimeout => "timeout",
        SshFailure.TcpUnreachable => "unreachable",
        SshFailure.TcpFailed => "failed",
        SshFailure.ClosedBeforeBanner or SshFailure.ConnectionClosed => "closed",
        SshFailure.NotSsh => "not-ssh",
        SshFailure.ProxyFailed => "proxy-failed",
        SshFailure.KexNoMatch => "no-match",
        SshFailure.HostKeyChanged => "changed",
        SshFailure.HostKeyRejected => "rejected",
        SshFailure.KeyUnusable => "key-unusable",
        SshFailure.AuthDenied => "denied",
        SshFailure.NoAnswer => "no-answer",
        _ => "unknown",
    };
}

/// <summary>
/// The SSH test of Settings → SSH hosts: the arguments omp's ssh tool uses (connection-manager.ts buildCommonArgs:
/// BatchMode, new host keys accepted, the entry's port and key; no <c>-F</c>, so the user's ssh_config with its
/// HostName, Port, ProxyJump and ProxyCommand applies) plus a connect timeout, a fresh connection instead of a shared
/// master, and ssh's debug log, which <see cref="Diagnose"/> reads stage by stage.
/// </summary>
public static partial class SshCheck
{
    public static IReadOnlyList<string> Arguments(SshHostEntry host, string? keyPath, TimeSpan connectTimeout)
    {
        // LogLevel instead of -v: ssh hands -v on to the ssh it starts for ProxyJump, whose debug lines would mix
        // with these; with LogLevel only that ssh's errors show up.
        var args = new List<string>
        {
            "-n", "-o", "LogLevel=DEBUG1", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
            "-o", "ConnectTimeout=" + ((int)Math.Ceiling(connectTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
            "-o", "ControlPath=none",
        };
        if (host.Port != 22) args.AddRange(["-p", host.Port.ToString(CultureInfo.InvariantCulture)]);
        if (keyPath is { Length: > 0 }) args.AddRange(["-i", keyPath]);
        args.Add(host.User is { Length: > 0 } u ? $"{u}@{host.Host}" : host.Host);
        args.Add("exit");
        return args;
    }

    /// <summary>Long enough for ssh to give up by itself: the connect timeout covers TCP and the greeting separately.</summary>
    public static TimeSpan ToolTimeout(TimeSpan connectTimeout) => connectTimeout * 2 + TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"^debug1: Connecting to .* port (\d+)\.$")]
    private static partial Regex ConnectingRe();

    [GeneratedRegex(@"^debug1: Remote protocol version (\d+\.\d+),")]
    private static partial Regex RemoteVersionRe();

    [GeneratedRegex(@"^Load key "".*"": (bad permissions|invalid format|error in libcrypto|incorrect passphrase)", RegexOptions.IgnoreCase)]
    private static partial Regex LoadKeyRe();

    /// <summary>What ssh's debug log (<see cref="Arguments"/>) proves about the connection.</summary>
    public static SshDiagnosis Diagnose(ToolResult r)
    {
        var lines = r.Stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int Find(Func<string, bool> match, int from = 0)
        {
            for (var i = Math.Max(0, from); i < lines.Count; i++) if (match(lines[i])) return i;
            return -1;
        }
        bool Has(string text) => Find(l => l.Contains(text, StringComparison.Ordinal)) >= 0;

        var proxyAt = Find(l => l.StartsWith("debug1: Executing proxy command:", StringComparison.Ordinal));
        var connectAt = Find(l => ConnectingRe().IsMatch(l));
        var route = Has("debug1: Setting implicit ProxyCommand from ProxyJump") ? SshRoute.ProxyJump
            : proxyAt >= 0 ? SshRoute.ProxyCommand
            : connectAt >= 0 || Has("Could not resolve hostname") ? SshRoute.Direct
            : SshRoute.Unknown;
        int? port = route == SshRoute.Direct && connectAt >= 0
            ? int.Parse(ConnectingRe().Match(lines.Last(l => ConnectingRe().IsMatch(l))).Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
        var proxied = route is SshRoute.ProxyJump or SshRoute.ProxyCommand;

        // Each stage counts only after the one before it: the jump host's own ssh prints errors and warnings too.
        var tcpAt = proxied ? proxyAt : Find(l => l == "debug1: Connection established.", connectAt);
        var bannerAt = tcpAt < 0 ? -1 : Find(l => RemoteVersionRe().Match(l) is { Success: true } m && m.Groups[1].Value is "2.0" or "1.99", tcpAt);
        var kexAt = bannerAt < 0 ? -1 : Find(l => l.StartsWith("debug1: Server host key:", StringComparison.Ordinal), bannerAt);
        var hostKeyAt = kexAt < 0 ? -1 : Find(l => l.Contains("is known and matches", StringComparison.Ordinal)
            || l.StartsWith("Warning: Permanently added", StringComparison.Ordinal) || l == "debug1: SSH2_MSG_NEWKEYS received", kexAt);
        var authAt = hostKeyAt < 0 ? -1 : Find(l => l.StartsWith("Authenticated to ", StringComparison.Ordinal), hostKeyAt);

        SshDiagnosis At(SshStage stage, SshFailure failure, string? proxyMessage = null) => new(route, port, stage, failure, proxyMessage);

        if (r.Ok) return new(route, port, null, SshFailure.None, null);
        if (authAt >= 0) return new(route, port, null, r.TimedOut ? SshFailure.NoAnswer : SshFailure.RemoteCommandFailed, null);
        if (route == SshRoute.Unknown) return new(route, port, null, r.TimedOut ? SshFailure.NoAnswer : SshFailure.Unknown, null);

        if (!proxied && connectAt < 0) return At(SshStage.Resolve, r.TimedOut ? SshFailure.NoAnswer : SshFailure.ResolveFailed);
        if (tcpAt < 0)
        {
            if (r.TimedOut) return At(SshStage.Tcp, SshFailure.NoAnswer);
            var reason = lines.LastOrDefault(l => l.StartsWith("ssh: connect to host ", StringComparison.Ordinal)) ?? "";
            return At(SshStage.Tcp,
                reason.Contains("refused", StringComparison.OrdinalIgnoreCase) ? SshFailure.TcpRefused
                : reason.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? SshFailure.TcpTimeout
                : reason.Contains("unreachable", StringComparison.OrdinalIgnoreCase) || reason.Contains("No route to host", StringComparison.OrdinalIgnoreCase)
                  || reason.Contains("is down", StringComparison.OrdinalIgnoreCase) ? SshFailure.TcpUnreachable
                : SshFailure.TcpFailed);
        }
        if (bannerAt < 0)
        {
            var closedAt = Find(l => l.StartsWith("kex_exchange_identification: Connection closed", StringComparison.Ordinal)
                || l.StartsWith("kex_exchange_identification: read:", StringComparison.Ordinal), tcpAt);
            var notSsh = Find(l => l.StartsWith("debug1: kex_exchange_identification: banner line", StringComparison.Ordinal)
                || l.StartsWith("Protocol major versions differ", StringComparison.Ordinal)
                || l.StartsWith("Bad remote protocol version identification", StringComparison.Ordinal)
                || l.Contains("banner line contains invalid characters", StringComparison.Ordinal)
                || RemoteVersionRe().IsMatch(l), tcpAt) >= 0;
            if (notSsh) return At(SshStage.Banner, SshFailure.NotSsh);
            // The proxy (the jump host's ssh or the user's command) quit before any greeting came through: its own
            // last words are the only evidence of why.
            if (proxied && closedAt >= 0)
                return At(SshStage.Banner, SshFailure.ProxyFailed, lines.Skip(tcpAt + 1).Take(closedAt - tcpAt - 1).LastOrDefault(IsProxyWords)?.Trim());
            if (closedAt >= 0) return At(SshStage.Banner, SshFailure.ClosedBeforeBanner);
            if (Has("Connection timed out during banner exchange")) return At(SshStage.Banner, SshFailure.BannerTimeout);
            return At(SshStage.Banner, r.TimedOut ? SshFailure.NoAnswer : SshFailure.ConnectionClosed);
        }
        if (kexAt < 0)
            return At(SshStage.Kex, Find(l => l.StartsWith("Unable to negotiate with ", StringComparison.Ordinal), bannerAt) >= 0 ? SshFailure.KexNoMatch
                : r.TimedOut ? SshFailure.NoAnswer : SshFailure.ConnectionClosed);
        if (hostKeyAt < 0)
            return At(SshStage.HostKey, Find(l => l.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal), kexAt) >= 0 ? SshFailure.HostKeyChanged
                : Find(l => l.StartsWith("Host key verification failed", StringComparison.Ordinal), kexAt) >= 0 ? SshFailure.HostKeyRejected
                : r.TimedOut ? SshFailure.NoAnswer : SshFailure.ConnectionClosed);
        var denied = Find(l => l.Contains("Permission denied (", StringComparison.Ordinal) || l.Contains("Too many authentication failures", StringComparison.Ordinal), hostKeyAt) >= 0;
        return At(SshStage.Auth, Find(l => LoadKeyRe().IsMatch(l), hostKeyAt) >= 0 ? SshFailure.KeyUnusable
            : denied ? SshFailure.AuthDenied
            : r.TimedOut ? SshFailure.NoAnswer : SshFailure.ConnectionClosed);
    }

    private static bool IsProxyWords(string line) =>
        line.Trim().Length > 0 && !line.StartsWith("debug", StringComparison.Ordinal)
        && !line.StartsWith("Warning: Permanently added", StringComparison.Ordinal)
        && !line.StartsWith("Pseudo-terminal will not be allocated", StringComparison.Ordinal);
}
