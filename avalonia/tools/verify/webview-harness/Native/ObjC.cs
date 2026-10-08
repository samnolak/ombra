using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OmpGui.WebViewHarness.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint(double x, double y)
{
    public double X = x;
    public double Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize(double width, double height)
{
    public double Width = width;
    public double Height = height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect(double x, double y, double width, double height)
{
    public CGPoint Origin = new(x, y);
    public CGSize Size = new(width, height);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSRange(nuint location, nuint length)
{
    public nuint Location = location;
    public nuint Length = length;
}

/// <summary>
/// The Objective-C runtime through typed function pointers to <c>objc_msgSend</c> (arm64 needs the exact argument
/// types of every method, so each call site casts to its own signature).
/// </summary>
internal static unsafe class ObjC
{
    public const string LibObjC = "/usr/lib/libobjc.A.dylib";
    public const string LibSystem = "/usr/lib/libSystem.B.dylib";
    public const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string WebKit = "/System/Library/Frameworks/WebKit.framework/WebKit";
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public static readonly nint MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(LibObjC), "objc_msgSend");

    private static readonly ConcurrentDictionary<string, nint> Selectors = new();
    private static readonly ConcurrentDictionary<string, nint> Classes = new();

    /// <summary>Loads AppKit and WebKit so their classes are registered with the runtime.</summary>
    public static void LoadFrameworks()
    {
        NativeLibrary.Load(Foundation);
        NativeLibrary.Load(AppKit);
        NativeLibrary.Load(WebKit);
    }

    public static nint Sel(string name) => Selectors.GetOrAdd(name, static n => sel_registerName(n));

    public static nint Cls(string name)
    {
        var cls = Classes.GetOrAdd(name, static n => objc_getClass(n));
        if (cls == 0) throw new InvalidOperationException($"Objective-C class {name} is not loaded.");
        return cls;
    }

    // ── objc_msgSend shapes ──

    public static nint Send(nint r, string sel) => ((delegate* unmanaged<nint, nint, nint>)MsgSend)(r, Sel(sel));
    public static nint Send(nint r, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(r, Sel(sel), a);
    public static nint Send(nint r, string sel, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(r, Sel(sel), a, b);
    public static nint Send(nint r, string sel, nint a, nint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(r, Sel(sel), a, b, c);
    public static nint Send(nint r, string sel, nint a, nint b, nint c, nint d) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)MsgSend)(r, Sel(sel), a, b, c, d);

    public static void SendVoid(nint r, string sel) => ((delegate* unmanaged<nint, nint, void>)MsgSend)(r, Sel(sel));
    public static void SendVoid(nint r, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(r, Sel(sel), a);
    public static void SendVoid(nint r, string sel, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, void>)MsgSend)(r, Sel(sel), a, b);
    public static void SendVoid(nint r, string sel, nint a, nint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)MsgSend)(r, Sel(sel), a, b, c);
    public static void SendVoid(nint r, string sel, nint a, nint b, nint c, nint d) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)MsgSend)(r, Sel(sel), a, b, c, d);
    public static void SendVoid(nint r, string sel, nint a, nint b, nint c, nint d, nint e) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, void>)MsgSend)(r, Sel(sel), a, b, c, d, e);

    public static bool SendBool(nint r, string sel) => ((delegate* unmanaged<nint, nint, byte>)MsgSend)(r, Sel(sel)) != 0;
    public static bool SendBool(nint r, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, byte>)MsgSend)(r, Sel(sel), a) != 0;
    public static void SendVoidBool(nint r, string sel, bool a) => ((delegate* unmanaged<nint, nint, byte, void>)MsgSend)(r, Sel(sel), a ? (byte)1 : (byte)0);
    public static long SendLong(nint r, string sel) => ((delegate* unmanaged<nint, nint, long>)MsgSend)(r, Sel(sel));
    public static int SendInt(nint r, string sel) => ((delegate* unmanaged<nint, nint, int>)MsgSend)(r, Sel(sel));
    public static void SendVoidLong(nint r, string sel, long a) => ((delegate* unmanaged<nint, nint, long, void>)MsgSend)(r, Sel(sel), a);
    public static double SendDouble(nint r, string sel) => ((delegate* unmanaged<nint, nint, double>)MsgSend)(r, Sel(sel));
    public static CGRect SendRect(nint r, string sel) => ((delegate* unmanaged<nint, nint, CGRect>)MsgSend)(r, Sel(sel));
    public static CGPoint SendPointToView(nint r, CGPoint p, nint view) =>
        ((delegate* unmanaged<nint, nint, CGPoint, nint, CGPoint>)MsgSend)(r, Sel("convertPoint:toView:"), p, view);

    public static bool RespondsTo(nint obj, string sel) => obj != 0 && SendBool(obj, "respondsToSelector:", Sel(sel));

    public static nint Retain(nint obj) => obj == 0 ? 0 : Send(obj, "retain");
    public static void Release(nint obj)
    {
        if (obj != 0) SendVoid(obj, "release");
    }

    public static nint New(string cls) => Send(Send(Cls(cls), "alloc"), "init");

    // ── Foundation values ──

    /// <summary>An autoreleased NSString.</summary>
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

    /// <summary>An autoreleased NSURL.</summary>
    public static nint Url(string url) => Send(Cls("NSURL"), "URLWithString:", Str(url));

    public static nint FileUrl(string path) => Send(Cls("NSURL"), "fileURLWithPath:", Str(path));

    public static string? Describe(nint obj) => obj == 0 ? null : ToStr(Send(obj, "description"));

    /// <summary><c>-[NSError localizedDescription]</c> plus domain and code.</summary>
    public static (string? Domain, long Code, string? Message) Error(nint error) =>
        error == 0 ? (null, 0, null) : (ToStr(Send(error, "domain")), SendLong(error, "code"), ToStr(Send(error, "localizedDescription")));

    /// <summary>Reads an exported Objective-C constant (e.g. <c>NSURLAuthenticationMethodHTTPBasic</c>).</summary>
    public static nint Constant(string library, string symbol) =>
        *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(library), symbol);

    // ── class building ──

    /// <summary>
    /// Registers an NSObject subclass with the given methods (each IMP is an <c>[UnmanagedCallersOnly]</c> function
    /// taking <c>(self, _cmd, args…)</c>). Returns the existing class if it was already registered in this process.
    /// </summary>
    public static nint DefineClass(string name, string? protocol, params (string Selector, nint Imp, string Types)[] methods)
    {
        var existing = objc_getClass(name);
        if (existing != 0) return existing;
        var cls = objc_allocateClassPair(Cls("NSObject"), name, 0);
        if (cls == 0) throw new InvalidOperationException($"Could not allocate class {name}.");
        if (protocol is not null)
        {
            var proto = objc_getProtocol(protocol);
            if (proto != 0) class_addProtocol(cls, proto);
        }
        foreach (var (selector, imp, types) in methods)
        {
            if (class_addMethod(cls, Sel(selector), imp, types) == 0)
                throw new InvalidOperationException($"Could not add {selector} to {name}.");
        }
        objc_registerClassPair(cls);
        Classes[name] = cls;
        return cls;
    }

    /// <summary>Adds a method to an existing class unless the class already responds to it. True if added.</summary>
    public static bool AddMethodIfMissing(nint cls, string selector, nint imp, string types)
    {
        if (class_getInstanceMethod(cls, Sel(selector)) != 0) return false;
        return class_addMethod(cls, Sel(selector), imp, types) != 0;
    }

    public static nint ClassOf(nint obj) => object_getClass(obj);

    public static string? ClassName(nint obj) => obj == 0 ? null : Marshal.PtrToStringUTF8(class_getName(object_getClass(obj)));

    public static nint PoolPush() => objc_autoreleasePoolPush();
    public static void PoolPop(nint pool) => objc_autoreleasePoolPop(pool);

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
    private static extern nint object_getClass(nint obj);

    [DllImport(LibObjC)]
    private static extern nint class_getName(nint cls);

    [DllImport(LibObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(LibObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);
}
