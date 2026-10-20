using Avalonia.Controls;
using Ombra.App.Platform.Mac;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        private static nint sFrameTraceWindow, sFrameTraceImp, sAnimatedFrameTraceImp;
        private static Action<string>? sFrameTraceLog;
        private bool _frameTraceAttached;

        private unsafe void TraceRestoreFrames()
        {
            if (_frameTraceAttached || Environment.GetEnvironmentVariable("OMBRA_TRACE_WINDOW_FRAME") != "1") return;
            _frameTraceAttached = true;
            sFrameTraceWindow = NsWindow;
            sFrameTraceLog = Log;
            var cls = MacObjC.Send(NsWindow, "class");
            sFrameTraceImp = SetImplementation(InstanceMethod(cls, "setFrame:display:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGRect, byte, void>)&TraceSetFrame);
            sAnimatedFrameTraceImp = SetImplementation(InstanceMethod(cls, "setFrame:display:animate:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGRect, byte, byte, void>)&TraceAnimatedSetFrame);
            window.PositionChanged += (_, e) => TraceFrameChange("PositionChanged " + e.Point);
            window.SizeChanged += (_, e) => TraceFrameChange("SizeChanged " + e.NewSize);
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WidthProperty || e.Property == Window.HeightProperty || e.Property == Window.WindowStateProperty)
                    TraceFrameChange($"{e.Property.Name}: {e.OldValue} -> {e.NewValue}");
            };
            TraceFrameChange("attached " + RestoreState(out _));
        }

        private static void TraceFrameChange(string reason)
        {
            try
            {
                var symbols = MacObjC.Send(MacObjC.Cls("NSThread"), "callStackSymbols");
                var currentEvent = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication"), "currentEvent");
                sFrameTraceLog?.Invoke($"FRAME-TRACE {reason}\nevent={MacObjC.ToStr(MacObjC.Send(currentEvent, "description"))}\n{Environment.StackTrace}\n{MacObjC.ToStr(MacObjC.Send(symbols, "description"))}");
            }
            catch { /* Diagnostic code must not interrupt AppKit's window lifecycle. */ }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void TraceSetFrame(nint self, nint cmd, CGRect frame, byte display)
        {
            if (self == sFrameTraceWindow) TraceFrameChange("setFrame:display: " + frame);
            ((delegate* unmanaged<nint, nint, CGRect, byte, void>)sFrameTraceImp)(self, cmd, frame, display);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void TraceAnimatedSetFrame(nint self, nint cmd, CGRect frame, byte display, byte animate)
        {
            if (self == sFrameTraceWindow) TraceFrameChange("setFrame:display:animate: " + frame);
            ((delegate* unmanaged<nint, nint, CGRect, byte, byte, void>)sAnimatedFrameTraceImp)(self, cmd, frame, display, animate);
        }

        // Deliberately not Activate(): its makeKeyAndOrderFront would repair the failure under test.
        private static unsafe void RestoreApplication(bool reopen)
        {
            var app = MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
            if (reopen)
                ((delegate* unmanaged<nint, nint, nint, byte, byte>)MacObjC.MsgSend)(
                    MacObjC.Send(app, "delegate"), MacObjC.Sel("applicationShouldHandleReopen:hasVisibleWindows:"), app, 0);
            else
                // Exercise the notification forwarded by AppKit, not a physical application switch.
                MacObjC.SendVoid(MacObjC.Send(app, "delegate"), "applicationDidBecomeActive:", 0);
            ((delegate* unmanaged<nint, nint, ulong, byte>)MacObjC.MsgSend)(
                MacObjC.Send(MacObjC.Cls("NSRunningApplication"), "currentApplication"), MacObjC.Sel("activateWithOptions:"), 2);
        }

        private unsafe CGRect RestoreFrame() =>
            ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("frame"));

        private unsafe string RestoreState(out bool restored)
        {
            var native = RestoreFrame();
            var mini = MacObjC.SendBool(NsWindow, "isMiniaturized");
            var visible = MacObjC.SendBool(NsWindow, "isVisible");
            var number = MacObjC.SendLong(NsWindow, "windowNumber");
            var list = CGWindowListCopyWindowInfo(1, 0);
            var onScreen = false;
            try
            {
                using var key = new NSStr("kCGWindowNumber");
                onScreen = Items(list).Any(info => Int(CFDictionaryGetValue(info, key.Handle)) == number);
            }
            finally { NightlyMach.CFRelease(list); }
            restored = !mini && visible && onScreen && native.Width > 0 && native.Height > 0;
            var app = MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
            return $"mini={mini} visible={visible} cg={onScreen} frame={native} avalonia={window.WindowState} "
                + $"style=0x{MacObjC.SendNUInt(NsWindow, "styleMask"):x} zoomed={MacObjC.SendBool(NsWindow, "isZoomed")} "
                + $"position={window.Position} active={window.IsActive} appActive={MacObjC.SendBool(app, "isActive")}";
        }

        private static bool FullScreenMenu(nint menu)
        {
            for (long i = 0, count = MacObjC.SendLong(menu, "numberOfItems"); i < count; i++)
            {
                var item = MacObjC.SendIndex(menu, "itemAtIndex:", (nuint)i);
                if (MacObjC.ToStr(MacObjC.Send(item, "title")) == Localization.Loc.T("Exit Full Screen"))
                {
                    MacObjC.SendVoidLong(menu, "performActionForItemAtIndex:", i);
                    return true;
                }
                if (MacObjC.Send(item, "submenu") is var submenu and not 0 && FullScreenMenu(submenu)) return true;
            }
            return false;
        }

        private async Task RestoreChecks(string mode)
        {
            TraceRestoreFrames();
            if (window.Position.X >= 0) throw new InvalidOperationException("Restore checks require a window on the left display.");
            var popup = mode.StartsWith("popup-", StringComparison.Ordinal);
            var starting = mode.StartsWith("starting-", StringComparison.Ordinal);
            var label = mode;
            if (popup || starting) mode = mode[(mode.IndexOf('-') + 1)..];
            if (mode is not ("dock" or "activate" or "hide" or "fullscreen" or "green" or "green-key" or "green-menu" or "green-minimize" or "green-activate" or "zoom" or "doubleclick" or "close"))
                throw new ArgumentException("Unknown restore mode: " + mode);
            for (var i = 0; i < 10; i++)
            {
                if (window.Position.X >= 0) throw new InvalidOperationException("Window left the test display.");
                Activate();
                await Task.Delay(150);
                if (starting)
                {
                    vm.SetAgentBrowserEngineCommand.Execute("preview");
                    vm.SetAgentBrowserEngineCommand.Execute("nightly");
                    vm.OpenBrowserPageCommand.Execute(null);
                    Log($"restore-{label}-{i}-starting frames={BrowserWindow()?.FramesShown} pid={BrowserWindow()?.RuntimePid}");
                }
                if (popup && BrowserWindow() is { } pane)
                {
                    RightClick(pane, pane.Bounds.Width * .65, pane.Bounds.Height * .6);
                    Check($"restore-{label}-{i}-popup", await Until(() => NightlyWindowRects(pane.RuntimePid).Count > 0, 5),
                        $"pid={pane.RuntimePid}");
                }
                if (mode == "close")
                {
                    KeyPress("w", 0x0D, 1UL << 20);
                    await Task.Delay(500);
                    RestoreApplication(true);
                    RestoreState(out var open);
                    Check($"restore-{label}-{i}", open && await Until(() => BrowserWindow() is { FramesShown: > 0 }, 10),
                        RestoreState(out _));
                    continue;
                }
                if (mode is "fullscreen" or "green" or "green-key" or "green-menu" or "green-minimize" or "green-activate" or "zoom" or "doubleclick")
                {
                    var originalFrame = RestoreFrame();
                    var wasZoomed = MacObjC.SendBool(NsWindow, "isZoomed");
                    var fullScreen = mode == "fullscreen" || mode.StartsWith("green", StringComparison.Ordinal);
                    void Toggle()
                    {
                        if (mode == "doubleclick") MouseClick(ClientToWindow(new Avalonia.Point(window.Bounds.Width / 2, 20)), false, 2);
                        else if (mode.StartsWith("green", StringComparison.Ordinal))
                            MacObjC.SendVoid(MacObjC.SendIndex(NsWindow, "standardWindowButton:", 2), "performClick:", 0);
                        else MacObjC.SendVoid(NsWindow, mode == "fullscreen" ? "toggleFullScreen:" : "performZoom:", 0);
                    }
                    Toggle();
                    await Task.Delay(fullScreen ? 2200 : 500);
                    Check($"restore-{label}-{i}-entered", fullScreen ? window.WindowState == WindowState.FullScreen
                        && (MacObjC.SendNUInt(NsWindow, "styleMask") & (1 << 14)) != 0
                        : MacObjC.SendBool(NsWindow, "isZoomed") != wasZoomed,
                        RestoreState(out _));
                    if (mode == "green-activate")
                    {
                        AnotherAppTakesFocus();
                        await Task.Delay(300);
                        RestoreApplication(false);
                        await Task.Delay(300);
                        Check($"restore-{label}-{i}-preserved", window.WindowState == WindowState.FullScreen
                            && (MacObjC.SendNUInt(NsWindow, "styleMask") & (1 << 14)) != 0, RestoreState(out _));
                    }
                    if (mode == "green-key") KeyPress("f", 0x03, (1UL << 20) | (1UL << 18));
                    else if (mode == "green-menu")
                        Check($"restore-{label}-{i}-menu", FullScreenMenu(MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication"), "mainMenu")), "");
                    else if (mode == "green-minimize")
                    {
                        KeyPress("m", 0x2E, 1UL << 20);
                        await Task.Delay(2200);
                        Check($"restore-{label}-{i}-minimized", MacObjC.SendBool(NsWindow, "isMiniaturized"), RestoreState(out _));
                        RestoreApplication(true);
                    }
                    else Toggle();
                    await Task.Delay(fullScreen ? 2200 : 500);
                    RestoreState(out var visible);
                    Check($"restore-{label}-{i}-exited", visible && window.WindowState != WindowState.FullScreen
                        && (MacObjC.SendNUInt(NsWindow, "styleMask") & (1 << 14)) == 0
                        && (fullScreen || MacObjC.SendBool(NsWindow, "isZoomed") == wasZoomed), RestoreState(out _));
                    Check($"restore-{label}-{i}-frame", RestoreFrame().Equals(originalFrame),
                        $"before={originalFrame} after={RestoreFrame()}");
                    if ((MacObjC.SendNUInt(NsWindow, "styleMask") & (1 << 14)) != 0)
                    {
                        MacObjC.SendVoid(NsWindow, "toggleFullScreen:", 0);
                        await Task.Delay(2200);
                    }
                    continue;
                }
                if (mode == "hide") KeyPress("h", 0x04, 1UL << 20);
                else KeyPress("m", 0x2E, 1UL << 20);
                await Task.Delay(700);
                var before = RestoreState(out _);
                Check($"restore-{label}-{i}-prepared",
                    mode == "hide" ? !MacObjC.SendBool(NsWindow, "isVisible") : MacObjC.SendBool(NsWindow, "isMiniaturized"), before);
                if (mode == "activate")
                {
                    AnotherAppTakesFocus();
                    await Task.Delay(200);
                }
                RestoreApplication(mode != "activate");
                var ok = await Until(() => { RestoreState(out var restored); return restored; }, 3);
                Check($"restore-{label}-{i}", ok, RestoreState(out _));
                // Recovery is after recording the result, never part of the assertion.
                if (!ok) MacObjC.SendVoid(NsWindow, "deminiaturize:", 0);
                await Task.Delay(150);
                if (starting) Check($"restore-{label}-{i}-loaded",
                    await Until(() => BrowserWindow() is { FramesShown: > 0 }, 30), $"frames={BrowserWindow()?.FramesShown}");
            }
        }
    }
}
