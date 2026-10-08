using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// What <see cref="NativeInput"/> and <see cref="IsolatedWorld"/> need beyond <see cref="MacObjC"/> /
/// <see cref="MacBlocks"/>: geometry structs, owned strings, main-thread calls with a result, completion blocks
/// for <c>void (^)(id, NSError*)</c>, associated objects and method replacement.
/// </summary>
/// <remarks>No Avalonia types: the windowless harness compiles this folder.</remarks>
[SupportedOSPlatform("macos")]
internal static unsafe class MacRuntime
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CGPoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct CGRect(double X, double Y, double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct NSRange(nuint Location, nuint Length);

    /// <summary>A new <c>NSString</c> (+1: release it).</summary>
    public static nint NewString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSString"), "alloc"), "initWithUTF8String:", utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>An <c>NSString</c> released at the end of a <c>using</c> scope (no autorelease pool needed).</summary>
    public readonly struct NSStr(string value) : IDisposable
    {
        public nint Handle { get; } = NewString(value);

        public void Dispose() => MacObjC.Release(Handle);
    }

    public static double SendDouble(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, double>)MacObjC.MsgSend)(receiver, MacObjC.Sel(selector));

    /// <summary>An <c>NSError</c>'s domain, code and message; WebKit's JavaScript exception text when it carries one.</summary>
    public static (string Domain, long Code, string Message) ScriptError(nint error)
    {
        var (domain, code, message) = MacObjC.Error(error);
        var info = error == 0 ? 0 : MacObjC.Send(error, "userInfo");
        if (info != 0)
        {
            using var key = new NSStr("WKJavaScriptExceptionMessage");
            var js = MacObjC.Send(info, "objectForKey:", key.Handle);
            if (js != 0 && MacObjC.IsKindOf(js, "NSString") && MacObjC.ToStr(js) is { Length: > 0 } text) message = text;
        }
        return (domain, code, message);
    }

    /// <summary><paramref name="work"/> on the main thread (in an autorelease pool), its result or exception back to the caller.</summary>
    public static Task<T> OnMainAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        MacObjC.OnMainThread(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task;
    }

    public static Task OnMainAsync(Action work) => OnMainAsync(() =>
    {
        work();
        return true;
    });

    // ── blocks ──

    /// <summary>A block for <c>void (^)(id result, NSError *error)</c>, called at most once (+1: <see cref="MacBlocks.Release"/> it after handing it over).</summary>
    public static nint ResultBlock(Action<nint, nint> handler) =>
        MacBlocks.Create((nint)(delegate* unmanaged<nint, nint, nint, void>)&InvokeResult, handler, "v24@?0@8@16");

    [UnmanagedCallersOnly]
    private static void InvokeResult(nint block, nint result, nint error)
    {
        try
        {
            MacBlocks.TakeState<Action<nint, nint>>(block)?.Invoke(result, error);
        }
        catch
        {
            // Nothing may unwind into WebKit
        }
    }

    /// <summary>A block for <c>void (^)(void)</c> (<c>dispatch_block_t</c>), called at most once (+1: <see cref="MacBlocks.Release"/> it after handing it over).</summary>
    public static nint VoidBlock(Action handler) =>
        MacBlocks.Create((nint)(delegate* unmanaged<nint, void>)&InvokeVoid, handler, "v8@?0");

    [UnmanagedCallersOnly]
    private static void InvokeVoid(nint block)
    {
        try
        {
            MacBlocks.TakeState<Action>(block)?.Invoke();
        }
        catch
        {
            // Nothing may unwind into WebKit
        }
    }

    /// <summary>The state of a block made by <see cref="MacBlocks.Create"/> without taking it (blocks called many times).</summary>
    public static T? PeekState<T>(nint block) where T : class
    {
        var raw = *(nint*)(block + 32); // isa, flags + reserved, invoke, descriptor, then the state
        return raw == 0 ? null : GCHandle.FromIntPtr(raw).Target as T;
    }

    // ── associated objects (marks on our NSEvents) ──

    public static void SetAssociated(nint obj, nint key, nint value) => objc_setAssociatedObject(obj, key, value, 1 /* RETAIN_NONATOMIC */);

    public static nint GetAssociated(nint obj, nint key) => objc_getAssociatedObject(obj, key);

    // ── methods ──

    public static nint InstanceMethod(nint cls, string selector) => class_getInstanceMethod(cls, MacObjC.Sel(selector));

    public static nint ClassMethod(nint cls, string selector) => class_getClassMethod(cls, MacObjC.Sel(selector));

    public static nint Implementation(nint method) => method_getImplementation(method);

    public static nint SetImplementation(nint method, nint imp) => method_setImplementation(method, imp);

    /// <summary><c>class_addMethod</c>: false when <paramref name="cls"/> itself already has the method (an inherited one is overridden).</summary>
    public static bool AddOwnMethod(nint cls, string selector, nint imp, string types) =>
        class_addMethod(cls, MacObjC.Sel(selector), imp, types) != 0;

    [DllImport(MacObjC.LibObjC)]
    private static extern void objc_setAssociatedObject(nint obj, nint key, nint value, nuint policy);

    [DllImport(MacObjC.LibObjC)]
    private static extern nint objc_getAssociatedObject(nint obj, nint key);

    [DllImport(MacObjC.LibObjC)]
    private static extern byte class_addMethod(nint cls, nint name, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(MacObjC.LibObjC)]
    private static extern nint class_getInstanceMethod(nint cls, nint selector);

    [DllImport(MacObjC.LibObjC)]
    private static extern nint class_getClassMethod(nint cls, nint selector);

    [DllImport(MacObjC.LibObjC)]
    private static extern nint method_getImplementation(nint method);

    [DllImport(MacObjC.LibObjC)]
    private static extern nint method_setImplementation(nint method, nint imp);
}
