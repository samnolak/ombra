using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OmpGui.WebViewHarness.Native;

/// <summary>
/// Apple's block ABI. Outgoing blocks (completion handlers we pass to WebKit) are stack literals holding a GCHandle,
/// copied to the heap with <c>_Block_copy</c>; their invoke function frees the handle, so each must be called exactly
/// once. Incoming blocks (handlers WebKit passes us) are called through the invoke pointer at offset 16.
/// </summary>
internal static unsafe class Blocks
{
    private const int BlockHasSignature = 1 << 30;

    [StructLayout(LayoutKind.Sequential)]
    public struct Literal
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public Descriptor* Desc;
        public nint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Descriptor
    {
        public nuint Reserved;
        public nuint Size;
        public nint Signature;
    }

    private static readonly nint StackBlockClass =
        NativeLibrary.GetExport(NativeLibrary.Load(ObjC.LibSystem), "_NSConcreteStackBlock");

    private static readonly ConcurrentDictionary<string, nint> Descriptors = new();

    /// <summary>A heap block calling <paramref name="invoke"/> with <paramref name="state"/>; release after handing it over.</summary>
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
        var block = _Block_copy(&literal);
        if (block == 0)
        {
            handle.Free();
            throw new InvalidOperationException("Could not copy the block.");
        }
        return block;
    }

    /// <summary>The state of a block made by <see cref="Create"/>; frees its handle (call once, in the invoke).</summary>
    public static T? TakeState<T>(nint block) where T : class
    {
        var handle = GCHandle.FromIntPtr(((Literal*)block)->State);
        var target = handle.Target as T;
        handle.Free();
        return target;
    }

    public static nint InvokePointer(nint block) => *(nint*)(block + 16);

    public static nint Copy(nint block) => _Block_copy((Literal*)block);

    public static void Release(nint block)
    {
        if (block != 0) _Block_release(block);
    }

    [DllImport(ObjC.LibSystem)]
    private static extern nint _Block_copy(Literal* block);

    [DllImport(ObjC.LibSystem)]
    private static extern void _Block_release(nint block);
}

/// <summary>
/// A completion handler WebKit gave us, copied so it can be answered later, and answered exactly once
/// (<see cref="Complete"/> is idempotent; <see cref="Default"/> answers with the handler's default).
/// </summary>
internal sealed class PendingBlock(nint block, Action<nint> invokeDefault)
{
    private nint _block = Blocks.Copy(block);

    /// <summary>Calls the handler through <paramref name="invoke"/> once; later calls do nothing. Main thread only.</summary>
    public bool Complete(Action<nint> invoke)
    {
        var b = _block;
        if (b == 0) return false;
        _block = 0;
        try
        {
            invoke(b);
        }
        finally
        {
            Blocks.Release(b);
        }
        return true;
    }

    public bool Default() => Complete(invokeDefault);
}
