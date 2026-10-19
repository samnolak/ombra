using Tmds.DBus.Protocol;

namespace Ombra.App.Platform.Linux;

/// <summary>logind's PrepareForSleep on the system bus; a missing bus/service is an explicit unsupported prerequisite.</summary>
internal sealed class LinuxPowerNotifications : IDisposable
{
    private readonly DBusConnection _connection = new(DBusAddress.System
        ?? throw new PlatformNotSupportedException("The system D-Bus address is unavailable."));
    private IDisposable? _match;
    private int _disposed;

    public static async Task<LinuxPowerNotifications> SubscribeAsync(Action<bool> changed, Action<string> failed)
    {
        var owner = new LinuxPowerNotifications();
        try
        {
            await owner._connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await owner._connection.CallMethodAsync(owner.PingLogind()).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            owner._match = await owner._connection.AddMatchAsync<bool>(new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.freedesktop.login1", Path = "/org/freedesktop/login1",
                Interface = "org.freedesktop.login1.Manager", Member = "PrepareForSleep",
            }, static (message, _) => message.GetBodyReader().ReadBool(), notification =>
            {
                if (Volatile.Read(ref owner._disposed) != 0) return;
                if (notification.HasValue) changed(notification.Value);
                else failed("Linux logind power notifications disconnected: " + notification.Exception?.Message);
            }, emitOnCapturedContext: false,
                flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed
                    | ObserverFlags.EmitOnOwnerChanged | ObserverFlags.EmitOnReaderFailed)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private MessageBuffer PingLogind()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.login1", path: "/org/freedesktop/login1",
            @interface: "org.freedesktop.DBus.Peer", member: "Ping");
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _match?.Dispose();
        _connection.Dispose();
    }
}
