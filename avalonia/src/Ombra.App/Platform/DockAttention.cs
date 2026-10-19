using System.Globalization;
using System.Runtime.Versioning;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Platform;

public interface IDockAttention
{
    void Update(int waitingChats, bool requestAttention);
}

/// <summary>UI-thread Dock state. Informational attention bounces briefly, never indefinitely.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDockAttention : IDockAttention
{
    public void Update(int waitingChats, bool requestAttention)
    {
        if (!MacObjC.IsMainThread) throw new InvalidOperationException("Dock updates require the main thread.");
        var pool = MacObjC.PoolPush();
        try
        {
            var app = MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
            var tile = MacObjC.Send(app, "dockTile");
            MacObjC.SendVoid(tile, "setBadgeLabel:", waitingChats > 0
                ? MacObjC.Str(waitingChats.ToString(CultureInfo.InvariantCulture)) : 0);
            if (requestAttention && waitingChats > 0)
                _ = MacObjC.Send(app, "requestUserAttention:", (nint)10); // NSInformationalRequest
        }
        finally { MacObjC.PoolPop(pool); }
    }
}
