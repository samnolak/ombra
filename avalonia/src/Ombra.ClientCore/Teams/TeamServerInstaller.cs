using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Ombra.ClientCore.Teams;

/// <summary>Where to install the team server. <paramref name="Address"/> is an IP or DNS name; SSH goes to the same host.</summary>
public sealed record ServerInstallRequest(string Address, string SshUser, int SshPort, string SshKeyPath, string? AcmeEmail = null);

public enum ServerInstallStep { Connecting, PreparingHost, Downloading, Verifying, Installing, Done }

public sealed record ServerInstallProgress(ServerInstallStep Step, string? Line);

/// <summary>What <see cref="TeamSession.ConnectAsync"/> needs: <paramref name="Server"/> as its address, <paramref name="Pin"/> (IP mode only).</summary>
public sealed record ServerInstallResult(string Server, string? Pin, string BootstrapCode, string ServerId);

/// <summary>Why an install failed; the App turns it into a localized sentence.</summary>
public enum ServerInstallFailure
{
    InvalidAddress, InvalidPort, InvalidUser, MissingKey, InvalidAcmeEmail, AcmeEmailNeedsDomain,
    SshNotStarted, SshFailed, NoSudo, UnsupportedOs, UnsupportedArch, AlreadyInstalled, LowDisk, PackagesFailed,
    DockerInstallFailed, DownloadFailed, ChecksumMismatch, SignatureInvalid, ReleaseInvalid, InstallFailed, MissingOutput,
}

/// <summary>
/// The install failed for <see cref="Reason"/>. <see cref="Detail"/> is the redacted reason line (for <see cref="ServerInstallFailure.MissingOutput"/>
/// one of <c>bootstrap-code</c>, <c>tls-pin</c>, <c>server-id</c>); <see cref="Log"/> holds the last lines of the output with the code and pin redacted.
/// <see cref="Exception.Message"/> is English, for logs only.
/// </summary>
public class ServerInstallException(ServerInstallFailure reason, string? detail, string? log)
    : Exception(detail is { Length: > 0 } ? $"Server install failed ({reason}): {detail}" : $"Server install failed ({reason}).")
{
    public ServerInstallFailure Reason { get; } = reason;
    public string? Detail { get; } = detail;
    public string? Log { get; } = log;
}

/// <summary>The host already runs an Ombra server: connect to it with its pin and a bootstrap code instead.</summary>
public sealed class ServerAlreadyInstalledException(string? log)
    : ServerInstallException(ServerInstallFailure.AlreadyInstalled, null, log);

/// <summary>
/// Installs <see cref="TeamServerRelease"/> on a Debian/Ubuntu x86_64 host over the system OpenSSH client: one bash
/// script on stdin, step markers (<c>##OMBRA-STEP name</c>) on stdout, then install.sh's summary parsed for the
/// bootstrap code, pin and server id.
/// </summary>
public sealed partial class TeamServerInstaller
{
    private const string StepMarker = "##OMBRA-STEP ";
    private const string AlreadyMarker = "##OMBRA-ALREADY-INSTALLED";
    private const int LogLines = 80;

    private readonly string _ssh;

    public TeamServerInstaller() : this(OperatingSystem.IsWindows() ? "ssh.exe" : "/usr/bin/ssh") { }

    /// <summary>Tests pass a fake ssh executable.</summary>
    internal TeamServerInstaller(string sshExecutable) => _ssh = sshExecutable;

    public async Task<ServerInstallResult> InstallAsync(ServerInstallRequest request, IProgress<ServerInstallProgress>? progress, CancellationToken ct)
    {
        var ipMode = Validate(request);
        var script = BuildScript(request, ipMode);

        progress?.Report(new ServerInstallProgress(ServerInstallStep.Connecting, null));
        var psi = new ProcessStartInfo(_ssh)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in SshArguments(request)) psi.ArgumentList.Add(a);

        var last = ServerInstallStep.Connecting;
        var log = new Queue<string>();
        var gate = new object();
        string? code = null, pin = null, serverId = null, endpoint = null;
        bool failed = false, already = false;
        string? failLine = null, errorLine = null;

        void OnLine(string? raw)
        {
            if (raw is null) return;
            var line = raw.TrimEnd('\r');
            lock (gate)
            {
                if (line.StartsWith(StepMarker, StringComparison.Ordinal))
                {
                    if (Enum.TryParse<ServerInstallStep>(line[StepMarker.Length..].Trim(), out var step))
                    {
                        last = step;
                        progress?.Report(new ServerInstallProgress(step, null));
                    }
                    return;
                }
                if (line.StartsWith(AlreadyMarker, StringComparison.Ordinal)) { already = true; return; }
                if (CodeRe().Match(line) is { Success: true } c) code = c.Groups[1].Value.Trim();
                if (PinRe().Match(line) is { Success: true } p) pin = p.Groups[1].Value.ToLowerInvariant();
                if (ServerIdRe().Match(line) is { Success: true } s) serverId = s.Groups[1].Value;
                if (EndpointRe().Match(line) is { Success: true } e) endpoint = e.Groups[1].Value;
                if (line.Contains(FailedMarker, StringComparison.Ordinal)) { failed = true; failLine ??= line; }
                if (AlreadyRe().IsMatch(line)) already = true;
                var safe = Redact(line);
                if (ErrorRe().IsMatch(safe)) errorLine = safe;
                log.Enqueue(safe);
                while (log.Count > LogLines) log.Dequeue();
                progress?.Report(new ServerInstallProgress(last, safe));
            }
        }

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) { throw new ServerInstallException(ServerInstallFailure.SshNotStarted, ex.Message, null); }

        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        var outTask = Pump(proc.StandardOutput, OnLine);
        var errTask = Pump(proc.StandardError, OnLine);
        try
        {
            await proc.StandardInput.WriteAsync(script.AsMemory(), ct);
            proc.StandardInput.Close();
        }
        catch (IOException) { /* ssh exited early; the exit code says why */ }
        await Task.WhenAll(outTask, errTask);
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        string Log() { lock (gate) return string.Join('\n', log); }
        if (already) throw new ServerAlreadyInstalledException(Log());
        if (failed || proc.ExitCode != 0)
        {
            string? tagged, untagged;
            lock (gate) { tagged = failLine is { } f ? Redact(f) : null; untagged = errorLine; }
            var reason = ServerInstallFailure.InstallFailed;
            string? detail = untagged;
            if (tagged is not null && FailRe().Match(tagged) is { Success: true } m)
            {
                reason = m.Groups[1].Success ? ReasonForTag(m.Groups[1].Value) : ServerInstallFailure.InstallFailed;
                detail = m.Groups[2].Value;
            }
            else if (proc.ExitCode == 255) reason = ServerInstallFailure.SshFailed;
            detail = detail?.Trim();
            if (detail is { Length: > 200 }) detail = detail[..200] + "…";
            if (detail is { Length: 0 }) detail = null;
            if (reason == ServerInstallFailure.InstallFailed && detail is null && proc.ExitCode != 0)
                detail = "exit " + proc.ExitCode.ToString(CultureInfo.InvariantCulture);
            throw new ServerInstallException(reason, detail, Log());
        }
        if (code is null) throw new ServerInstallException(ServerInstallFailure.MissingOutput, "bootstrap-code", Log());
        if (ipMode && pin is null) throw new ServerInstallException(ServerInstallFailure.MissingOutput, "tls-pin", Log());
        if (serverId is null) throw new ServerInstallException(ServerInstallFailure.MissingOutput, "server-id", Log());

        progress?.Report(new ServerInstallProgress(ServerInstallStep.Done, null));
        var server = endpoint ?? "https://" + (request.Address.Contains(':') ? "[" + request.Address + "]" : request.Address);
        return new ServerInstallResult(server, ipMode ? pin : null, code, serverId);

        string Redact(string l)
        {
            if (code is { Length: > 0 }) l = l.Replace(code, "[redacted]", StringComparison.Ordinal);
            if (pin is { Length: > 0 }) l = l.Replace(pin, "[redacted]", StringComparison.OrdinalIgnoreCase);
            l = CodeRe().Replace(l, m => m.Value[..(m.Groups[1].Index - m.Index)] + "[redacted]");
            return PinRe().Replace(l, m => m.Value[..(m.Groups[1].Index - m.Index)] + "[redacted]");
        }
    }

    private static async Task Pump(StreamReader reader, Action<string?> onLine)
    {
        while (await reader.ReadLineAsync() is { } line) onLine(line);
    }

    private const string FailedMarker = "install FAILED";

    /// <summary>The script's <c>fail &lt;tag&gt;</c> tags; an unknown tag is a plain <see cref="ServerInstallFailure.InstallFailed"/>.</summary>
    internal static ServerInstallFailure ReasonForTag(string tag) => tag switch
    {
        "no-sudo" => ServerInstallFailure.NoSudo,
        "unsupported-os" => ServerInstallFailure.UnsupportedOs,
        "unsupported-arch" => ServerInstallFailure.UnsupportedArch,
        "already-installed" => ServerInstallFailure.AlreadyInstalled,
        "low-disk" => ServerInstallFailure.LowDisk,
        "packages" => ServerInstallFailure.PackagesFailed,
        "docker" => ServerInstallFailure.DockerInstallFailed,
        "download" => ServerInstallFailure.DownloadFailed,
        "checksum" => ServerInstallFailure.ChecksumMismatch,
        "signature" => ServerInstallFailure.SignatureInvalid,
        "release" => ServerInstallFailure.ReleaseInvalid,
        _ => ServerInstallFailure.InstallFailed,
    };

    internal static IReadOnlyList<string> SshArguments(ServerInstallRequest r) =>
    [
        "-i", r.SshKeyPath, "-p", r.SshPort.ToString(CultureInfo.InvariantCulture),
        "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=accept-new",
        "-o", "ConnectTimeout=15", "-o", "ControlPath=none",
        "--", r.SshUser + "@" + r.Address, "bash -s",
    ];

    /// <summary>Checks the request; true for IP mode. Throws <see cref="ServerInstallException"/> before anything runs.</summary>
    internal static bool Validate(ServerInstallRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var ip = IPAddress.TryParse(r.Address, out var addr) && addr.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
        if (!ip && !DomainRe().IsMatch(r.Address ?? "")) throw Invalid(ServerInstallFailure.InvalidAddress);
        if (r.SshPort is < 1 or > 65535) throw Invalid(ServerInstallFailure.InvalidPort);
        if (!UserRe().IsMatch(r.SshUser ?? "")) throw Invalid(ServerInstallFailure.InvalidUser);
        if (string.IsNullOrEmpty(r.SshKeyPath) || !File.Exists(r.SshKeyPath)) throw Invalid(ServerInstallFailure.MissingKey);
        if (r.AcmeEmail is { Length: > 0 } email && (ip || !EmailRe().IsMatch(email)))
            throw Invalid(ip ? ServerInstallFailure.AcmeEmailNeedsDomain : ServerInstallFailure.InvalidAcmeEmail);
        return ip;

        static ServerInstallException Invalid(ServerInstallFailure reason) => new(reason, null, null);
    }

    /// <summary>Single-quotes <paramref name="s"/> for bash.</summary>
    internal static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static string BuildScript(ServerInstallRequest r, bool ipMode)
    {
        var mode = ipMode ? "--ip " + Quote(r.Address) : "--domain " + Quote(r.Address)
            + (r.AcmeEmail is { Length: > 0 } e ? " --acme-email " + Quote(e) : "");
        var sb = new StringBuilder();
        sb.Append("set -euo pipefail\n");
        sb.Append("OMBRA_URL=").Append(Quote(TeamServerRelease.Url)).Append('\n');
        sb.Append("OMBRA_SHA256=").Append(Quote(TeamServerRelease.Sha256)).Append('\n');
        sb.Append("OMBRA_VERSION=").Append(Quote(TeamServerRelease.Version)).Append('\n');
        sb.Append("OMBRA_PUBKEY=").Append(Quote(TeamServerRelease.PublicKeyPem)).Append('\n');
        sb.Append("OMBRA_MODE_ARGS=(").Append(mode).Append(")\n");
        sb.Append("OMBRA_NEED_DOCKER=").Append(ipMode ? "0" : "1").Append('\n');
        // The script comes on ssh's stdin (bash -s): wrapped in one function that bash reads whole before running it,
        // run with stdin closed, so a child that reads stdin (install.sh, apt, docker) cannot swallow the lines after it.
        // A live install showed it: the line removing the incomplete-install marker after install.sh never ran.
        sb.Append("ombra_install() {\n").Append(Script).Append("\n}\nombra_install </dev/null\n");
        return sb.ToString();
    }

    // Runs as the SSH user; root work goes through sudo -n. Step markers go to stdout; errors to stderr with exit 1.
    private const string Script = """
        fail() { local tag=$1; shift; echo "install FAILED [$tag]: $*" >&2; exit 1; }
        step() { echo "##OMBRA-STEP $1"; }
        if [ "$(id -u)" = 0 ]; then SUDO=(); else
          command -v sudo >/dev/null 2>&1 || fail no-sudo "user is not root and sudo is not installed"
          sudo -n true 2>/dev/null || fail no-sudo "user is not root and has no passwordless sudo (sudo -n)"
          SUDO=(sudo -n)
        fi
        step PreparingHost
        [ -r /etc/os-release ] || fail unsupported-os "/etc/os-release missing (Debian or Ubuntu required)"
        . /etc/os-release
        case " ${ID:-} ${ID_LIKE:-} " in *" debian "*|*" ubuntu "*) ;; *) fail unsupported-os "${PRETTY_NAME:-unknown} (Debian or Ubuntu required)";; esac
        [ "$(uname -m)" = x86_64 ] || fail unsupported-arch "$(uname -m) (x86_64 required)"
        MARK=/var/lib/ombra-ssh-install.incomplete
        if [ -e /etc/ombra ] && "${SUDO[@]}" test -e "$MARK"; then
          echo "removing the unfinished install left by an earlier attempt"
          if "${SUDO[@]}" test -f /opt/ombra/compose.yaml; then
            "${SUDO[@]}" docker compose --project-directory /opt/ombra -f /opt/ombra/compose.yaml down -v --remove-orphans || true
          fi
          if "${SUDO[@]}" systemctl cat ombra-server >/dev/null 2>&1; then "${SUDO[@]}" systemctl disable --now ombra-server || true; fi
          if command -v psql >/dev/null 2>&1; then
            "${SUDO[@]}" runuser -u postgres -- dropdb --if-exists ombra || true
            "${SUDO[@]}" runuser -u postgres -- dropuser --if-exists ombra || true
          fi
          "${SUDO[@]}" rm -rf /etc/ombra /etc/ombra-offline /opt/ombra /var/lib/ombra /etc/cron.daily/ombra-backup /etc/periodic/daily/ombra-backup "$MARK" || true
          echo "unfinished install removed"
        fi
        if [ -e /etc/ombra ]; then echo "##OMBRA-ALREADY-INSTALLED"; fail already-installed "an Ombra server is already installed (/etc/ombra exists)"; fi
        free_kb=$(df -Pk / | awk 'NR==2{print $4}')
        [ "${free_kb:-0}" -ge 4194304 ] || fail low-disk "$((${free_kb:-0} / 1048576)) GB free on / (4 GB required)"
        export DEBIAN_FRONTEND=noninteractive
        pkgs=()
        for t in curl openssl; do command -v "$t" >/dev/null 2>&1 || pkgs+=("$t"); done
        [ -d /etc/ssl/certs ] && [ -n "$(ls -A /etc/ssl/certs 2>/dev/null)" ] || pkgs+=(ca-certificates)
        if [ ${#pkgs[@]} -gt 0 ] || ! command -v docker >/dev/null 2>&1 || ! "${SUDO[@]}" docker compose version >/dev/null 2>&1; then
          "${SUDO[@]}" apt-get update -q || fail packages "apt-get update failed"
        fi
        if [ ${#pkgs[@]} -gt 0 ]; then "${SUDO[@]}" apt-get install -y -q "${pkgs[@]}" || fail packages "could not install ${pkgs[*]}"; fi
        if ! command -v docker >/dev/null 2>&1 || ! "${SUDO[@]}" docker compose version >/dev/null 2>&1; then
          "${SUDO[@]}" apt-get install -y -q docker.io docker-compose-v2 \
            || "${SUDO[@]}" apt-get install -y -q docker.io docker-compose-plugin \
            || { [ "$OMBRA_NEED_DOCKER" = 0 ] || fail docker "could not install Docker Engine and Compose v2"; }
          "${SUDO[@]}" systemctl enable --now docker >/dev/null 2>&1 || true
        fi
        SYSTEMD=()
        if ! "${SUDO[@]}" docker compose version >/dev/null 2>&1; then
          [ "$OMBRA_NEED_DOCKER" = 0 ] || fail docker "Docker Compose v2 is not available"
          SYSTEMD=(--systemd)
        fi
        step Downloading
        work=$("${SUDO[@]}" mktemp -d /root/ombra-install.XXXXXX) || fail host "could not create a work directory"
        "${SUDO[@]}" chmod 700 "$work"
        cleanup() { case "$work" in /root/ombra-install.?*) "${SUDO[@]}" rm -rf -- "$work";; esac; }
        trap cleanup EXIT
        "${SUDO[@]}" curl -fsSL --proto '=https' --retry 3 -o "$work/release.tar.gz" "$OMBRA_URL" || fail download "$OMBRA_URL"
        step Verifying
        echo "$OMBRA_SHA256  $work/release.tar.gz" | "${SUDO[@]}" sha256sum -c --quiet - || fail checksum "release checksum mismatch"
        printf '%s' "$OMBRA_PUBKEY" | "${SUDO[@]}" tee "$work/release.pub" >/dev/null
        "${SUDO[@]}" tar -xzf "$work/release.tar.gz" -C "$work" || fail release "could not unpack the release"
        rel="$work/ombra-server-$OMBRA_VERSION"
        "${SUDO[@]}" test -f "$rel/SHA256SUMS" || fail release "release layout unexpected"
        "${SUDO[@]}" openssl dgst -sha256 -verify "$work/release.pub" -signature "$rel/SHA256SUMS.sig" "$rel/SHA256SUMS" >/dev/null \
          || fail signature "release signature does not verify"
        "${SUDO[@]}" bash -c 'cd "$1" && sha256sum -c --quiet SHA256SUMS' _ "$rel" || fail checksum "release files do not match SHA256SUMS"
        step Installing
        "${SUDO[@]}" install -d -m 755 /var/lib
        ( umask 077; "${SUDO[@]}" touch "$MARK" ) && "${SUDO[@]}" chmod 600 "$MARK"
        "${SUDO[@]}" bash -c 'cd "$1" && shift && exec ./deploy/install.sh "$@"' _ "$rel" "${OMBRA_MODE_ARGS[@]}" --release-key "$work/release.pub" "${SYSTEMD[@]}" 2>&1
        "${SUDO[@]}" rm -f "$MARK"
        """;

    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRe();

    [GeneratedRegex(@"^[a-z_][a-z0-9_-]*$")]
    private static partial Regex UserRe();

    [GeneratedRegex(@"^[^\s@'""\\`$;&|<>()]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,63}$")]
    private static partial Regex EmailRe();

    // The server's alphabet (BootstrapCodes.Alphabet: no I, O, 0, 1), 26 characters in groups of 5 joined by '-'
    [GeneratedRegex(@"bootstrap code:\s*([A-HJ-NP-Z2-9]{1,5}(?:-[A-HJ-NP-Z2-9]{1,5})*)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRe();

    [GeneratedRegex(@"(?:TLS pin \(current\)|pin \(SPKI SHA-256\)):\s*([0-9a-fA-F]{64})")]
    private static partial Regex PinRe();

    [GeneratedRegex(@"^\s*serverId:\s*(\S+)")]
    private static partial Regex ServerIdRe();

    [GeneratedRegex(@"^\s*endpoint:\s*(https://\S+)")]
    private static partial Regex EndpointRe();

    [GeneratedRegex(@"error|failed|denied", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRe();

    [GeneratedRegex(@"already installed|/etc/ombra (?:already )?exists", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyRe();

    // "install FAILED [tag]: detail"; the tag is optional (install.sh's own failures have none)
    [GeneratedRegex(@"install FAILED(?: \[([a-z-]+)\])?:?\s*(.*)$")]
    private static partial Regex FailRe();
}
