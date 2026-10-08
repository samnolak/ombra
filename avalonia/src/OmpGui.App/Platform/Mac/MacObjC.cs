using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// The Objective-C runtime for the WebKit hooks, through typed function pointers to <c>objc_msgSend</c> (arm64 needs
/// each method's exact argument types, so every call site casts to its own shape). No Avalonia types: the windowless
/// harness (<c>tools/verify/webview-harness</c>) compiles this folder.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacObjC
{
    public const string LibObjC = "/usr/lib/libobjc.A.dylib";
    public const string LibSystem = "/usr/lib/libSystem.B.dylib";
    public const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    public static readonly nint MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(LibObjC), "objc_msgSend");

    /// <summary>dispatch_get_main_queue() is a macro for &amp;_dispatch_main_q.</summary>
    private static readonly nint MainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");

    private static readonly ConcurrentDictionary<string, nint> Selectors = new();
    private static readonly ConcurrentDictionary<string, nint> Classes = new();

    public static nint Sel(string name) => Selectors.GetOrAdd(name, static n => sel_registerName(n));

    /// <summary>The class, or 0 when it is not loaded.</summary>
    public static nint Cls(string name)
    {
        if (Classes.TryGetValue(name, out var cls)) return cls;
        cls = objc_getClass(name);
        if (cls != 0) Classes[name] = cls;
        return cls;
    }

    // ── objc_msgSend shapes ──

    public static nint Send(nint r, string sel) => ((delegate* unmanaged<nint, nint, nint>)MsgSend)(r, Sel(sel));
    public static nint Send(nint r, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(r, Sel(sel), a);
    public static nint Send(nint r, string sel, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(r, Sel(sel), a, b);
    public static nint Send(nint r, string sel, nint a, nint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(r, Sel(sel), a, b, c);
    public static nint SendNUInt(nint r, string sel, nint a, nuint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nuint, nint, nint>)MsgSend)(r, Sel(sel), a, b, c);
    public static nint SendIndex(nint r, string sel, nuint i) => ((delegate* unmanaged<nint, nint, nuint, nint>)MsgSend)(r, Sel(sel), i);

    public static void SendVoid(nint r, string sel) => ((delegate* unmanaged<nint, nint, void>)MsgSend)(r, Sel(sel));
    public static void SendVoid(nint r, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(r, Sel(sel), a);
    public static void SendVoid(nint r, string sel, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, void>)MsgSend)(r, Sel(sel), a, b);
    public static void SendVoid(nint r, string sel, nint a, nint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)MsgSend)(r, Sel(sel), a, b, c);
    public static void SendVoidLong(nint r, string sel, long a) => ((delegate* unmanaged<nint, nint, long, void>)MsgSend)(r, Sel(sel), a);

    public static bool SendBool(nint r, string sel) => ((delegate* unmanaged<nint, nint, byte>)MsgSend)(r, Sel(sel)) != 0;
    public static bool SendBool(nint r, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, byte>)MsgSend)(r, Sel(sel), a) != 0;
    public static long SendLong(nint r, string sel) => ((delegate* unmanaged<nint, nint, long>)MsgSend)(r, Sel(sel));
    public static nuint SendNUInt(nint r, string sel) => ((delegate* unmanaged<nint, nint, nuint>)MsgSend)(r, Sel(sel));

    public static bool RespondsTo(nint obj, string sel) => obj != 0 && SendBool(obj, "respondsToSelector:", Sel(sel));

    public static bool IsKindOf(nint obj, string cls) => obj != 0 && Cls(cls) is var c && c != 0 && SendBool(obj, "isKindOfClass:", c);

    public static nint Retain(nint obj) => obj == 0 ? 0 : Send(obj, "retain");

    public static void Release(nint obj)
    {
        if (obj != 0) SendVoid(obj, "release");
    }

    // ── Foundation values ──

    /// <summary>An autoreleased NSString (call inside an autorelease pool).</summary>
    public static nint Str(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try
        {
            return Send(Cls("NSString"), "stringWithUTF8String:", utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static string? ToStr(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    /// <summary>An <c>NSURL*</c> as a Uri (null when absent or not absolute).</summary>
    public static Uri? ToUri(nint nsUrl) =>
        nsUrl != 0 && ToStr(Send(nsUrl, "absoluteString")) is { Length: > 0 } s && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    /// <summary>An autoreleased NSURL.</summary>
    public static nint Url(Uri url) => Send(Cls("NSURL"), "URLWithString:", Str(url.AbsoluteUri));

    /// <summary>An autoreleased file NSURL.</summary>
    public static nint FileUrl(string path) => Send(Cls("NSURL"), "fileURLWithPath:", Str(path));

    /// <summary>The strings of an <c>NSArray&lt;NSString*&gt;*</c> (other elements are skipped).</summary>
    public static List<string> Strings(nint array)
    {
        var list = new List<string>();
        if (array == 0) return list;
        var count = SendNUInt(array, "count");
        for (nuint i = 0; i < count; i++)
        {
            var item = SendIndex(array, "objectAtIndex:", i);
            if (IsKindOf(item, "NSString") && ToStr(item) is { } s) list.Add(s);
        }
        return list;
    }

    /// <summary><c>-[NSError domain]</c>, <c>code</c> and <c>localizedDescription</c>.</summary>
    public static (string Domain, long Code, string Message) Error(nint error) =>
        error == 0 ? ("", 0, "") : (ToStr(Send(error, "domain")) ?? "", SendLong(error, "code"), ToStr(Send(error, "localizedDescription")) ?? "");

    // ── classes ──

    /// <summary>
    /// Registers an NSObject subclass conforming to <paramref name="protocol"/> with the given methods (each IMP an
    /// <c>[UnmanagedCallersOnly]</c> function taking <c>(self, _cmd, args…)</c>); the existing class when already registered.
    /// </summary>
    public static nint DefineClass(string name, string? protocol, params (string Selector, nint Imp, string Types)[] methods)
    {
        var existing = objc_getClass(name);
        if (existing != 0) return existing;
        var cls = objc_allocateClassPair(Cls("NSObject"), name, 0);
        if (cls == 0) throw new InvalidOperationException($"Could not allocate class {name}.");
        if (protocol is not null && objc_getProtocol(protocol) is var proto && proto != 0) class_addProtocol(cls, proto);
        foreach (var (selector, imp, types) in methods)
        {
            if (class_addMethod(cls, Sel(selector), imp, types) == 0)
                throw new InvalidOperationException($"Could not add {selector} to {name}.");
        }
        objc_registerClassPair(cls);
        Classes[name] = cls;
        return cls;
    }

    /// <summary>
    /// Adds <paramref name="imp"/> as <paramref name="selector"/> to <paramref name="cls"/>. True when the class now
    /// answers it with <paramref name="imp"/> (added now or earlier); false when it already has its own.
    /// </summary>
    public static bool AddMethod(nint cls, string selector, nint imp, string types)
    {
        var existing = class_getInstanceMethod(cls, Sel(selector));
        if (existing != 0) return method_getImplementation(existing) == imp;
        return class_addMethod(cls, Sel(selector), imp, types) != 0;
    }

    /// <summary>The implementation <paramref name="cls"/> has for <paramref name="selector"/> (0: none).</summary>
    public static nint ImplementationOf(nint cls, string selector) =>
        class_getInstanceMethod(cls, Sel(selector)) is var m && m != 0 ? method_getImplementation(m) : 0;

    public static nint ClassOf(nint obj) => object_getClass(obj);

    public static string? ClassName(nint cls) => cls == 0 ? null : Marshal.PtrToStringUTF8(class_getName(cls));

    public static nint PoolPush() => objc_autoreleasePoolPush();

    public static void PoolPop(nint pool) => objc_autoreleasePoolPop(pool);

    // ── main thread: WebKit calls back there and wants its handlers answered there ──

    public static bool IsMainThread => pthread_main_np() != 0;

    /// <summary>Runs <paramref name="action"/> on the main thread: now when already there, else on the next main-queue turn.</summary>
    public static void OnMainThread(Action action)
    {
        if (IsMainThread) Guard(action);
        else Post(action);
    }

    /// <summary>Runs <paramref name="action"/> on a later main-queue turn (never inside the current callback).</summary>
    public static void Post(Action action) =>
        dispatch_async_f(MainQueue, GCHandle.ToIntPtr(GCHandle.Alloc(action)), &RunPosted);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunPosted(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var action = handle.Target as Action;
        handle.Free();
        if (action is not null) Guard(action);
    }

    /// <summary>Runs <paramref name="action"/> in an autorelease pool; nothing may unwind into WebKit or libdispatch.</summary>
    public static void Guard(Action action)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            action();
        }
        catch (Exception)
        {
            // dropped: the native caller cannot take an exception
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    [DllImport(LibObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC)]
    private static extern nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

    [DllImport(LibObjC)]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport(LibObjC)]
    private static extern nint objc_getProtocol([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC)]
    private static extern byte class_addProtocol(nint cls, nint protocol);

    [DllImport(LibObjC)]
    private static extern byte class_addMethod(nint cls, nint name, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(LibObjC)]
    private static extern nint class_getInstanceMethod(nint cls, nint name);

    [DllImport(LibObjC)]
    private static extern nint method_getImplementation(nint method);

    [DllImport(LibObjC)]
    private static extern nint object_getClass(nint obj);

    [DllImport(LibObjC)]
    private static extern nint class_getName(nint cls);

    [DllImport(LibObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(LibObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(LibSystem)]
    private static extern int pthread_main_np();

    [DllImport(LibSystem)]
    private static extern void dispatch_async_f(nint queue, nint context, delegate* unmanaged[Cdecl]<nint, void> work);
}

/// <summary>
/// Apple's block ABI. Blocks we hand to WebKit are stack literals holding a GCHandle, copied to the heap with
/// <c>_Block_copy</c>; their invoke function takes the state once (<see cref="TakeState{T}"/>), so each is called exactly
/// once. Blocks WebKit hands us are called through the invoke pointer at offset 16 (after <c>isa</c>, flags, reserved).
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacBlocks
{
    private const int BlockHasSignature = 1 << 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct Literal
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public Descriptor* Desc;
        public nint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        public nuint Reserved;
        public nuint Size;
        public nint Signature;
    }

    private static readonly nint StackBlockClass =
        NativeLibrary.GetExport(NativeLibrary.Load(MacObjC.LibSystem), "_NSConcreteStackBlock");

    private static readonly ConcurrentDictionary<string, nint> Descriptors = new();

    /// <summary>A heap block calling <paramref name="invoke"/> with <paramref name="state"/>; <see cref="Release"/> it after handing it over.</summary>
    public static nint Create(nint invoke, object state, string signature)
    {
        var desc = Descriptors.GetOrAdd(signature, static sig =>
        {
            var d = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
            d->Size = (nuint)sizeof(Literal);
            d->Signature = Marshal.StringToCoTaskMemUTF8(sig);
            return (nint)d;
        });
        var handle = GCHandle.Alloc(state);
        var literal = new Literal
        {
            Isa = StackBlockClass,
            Flags = BlockHasSignature,
            Invoke = invoke,
            Desc = (Descriptor*)desc,
            State = GCHandle.ToIntPtr(handle),
        };
        var block = _Block_copy((nint)(&literal));
        if (block == 0)
        {
            handle.Free();
            throw new InvalidOperationException("Could not copy the block.");
        }
        return block;
    }

    /// <summary>The state of a block made by <see cref="Create"/>, taken once: later calls return null.</summary>
    public static T? TakeState<T>(nint block) where T : class
    {
        var slot = &((Literal*)block)->State;
        var raw = Interlocked.Exchange(ref *slot, 0);
        if (raw == 0) return null;
        var handle = GCHandle.FromIntPtr(raw);
        var target = handle.Target as T;
        handle.Free();
        return target;
    }

    public static nint InvokePointer(nint block) => *(nint*)(block + 16);

    public static nint Copy(nint block) => _Block_copy(block);

    public static void Release(nint block)
    {
        if (block != 0) _Block_release(block);
    }

    public static void InvokeVoid(nint b) => ((delegate* unmanaged[Cdecl]<nint, void>)InvokePointer(b))(b);
    public static void InvokeBool(nint b, bool v) => ((delegate* unmanaged[Cdecl]<nint, byte, void>)InvokePointer(b))(b, v ? (byte)1 : (byte)0);
    public static void InvokeObject(nint b, nint o) => ((delegate* unmanaged[Cdecl]<nint, nint, void>)InvokePointer(b))(b, o);
    public static void InvokeLong(nint b, long v) => ((delegate* unmanaged[Cdecl]<nint, long, void>)InvokePointer(b))(b, v);
    public static void InvokeLongObject(nint b, long v, nint o) => ((delegate* unmanaged[Cdecl]<nint, long, nint, void>)InvokePointer(b))(b, v, o);

    [DllImport(MacObjC.LibSystem)]
    private static extern nint _Block_copy(nint block);

    [DllImport(MacObjC.LibSystem)]
    private static extern void _Block_release(nint block);
}

/// <summary>
/// A completion handler WebKit gave us, copied so it can be answered later, and answered exactly once on the main
/// thread: the first <see cref="Complete"/> / <see cref="Default"/> wins, later calls do nothing. WebKit raises if a
/// handler is released without being called, so every owner answers what is left with <see cref="Default"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacPendingBlock(nint block, Action<nint> invokeDefault)
{
    private nint _block = MacBlocks.Copy(block);

    public bool IsDone => Volatile.Read(ref _block) == 0;

    /// <summary>Calls the handler through <paramref name="invoke"/> (on the main thread, now or next turn); false when it was answered already.</summary>
    public bool Complete(Action<nint> invoke)
    {
        var b = Interlocked.Exchange(ref _block, 0);
        if (b == 0) return false;
        MacObjC.OnMainThread(() =>
        {
            try
            {
                invoke(b);
            }
            finally
            {
                MacBlocks.Release(b);
            }
        });
        return true;
    }

    public bool Default() => Complete(invokeDefault);
}
