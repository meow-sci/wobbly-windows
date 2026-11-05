using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;

namespace WobblyWindows.Rendering;

/// <summary>Owns output-only memory. Its ImVector views must never be freed by ImGui.</summary>
internal unsafe sealed class NativeBuffer<T> : IDisposable where T : unmanaged
{
    public T* Pointer { get; private set; }
    public int Count { get; private set; }
    public int Capacity { get; private set; }
    public Span<T> Span => new(Pointer, Count);

    public void Reserve(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity <= Capacity) return;
        int grown = Math.Max(capacity, Math.Max(32, checked(Capacity * 2)));
        T* next = (T*)NativeMemory.Alloc(checked((nuint)grown * (nuint)sizeof(T)));
        if (next == null) throw new OutOfMemoryException();
        Span.CopyTo(new Span<T>(next, Count));
        NativeMemory.Free(Pointer);
        Pointer = next; Capacity = grown;
    }

    public void Add(T value) { Reserve(checked(Count + 1)); Pointer[Count++] = value; }
    public void AddRange(ReadOnlySpan<T> values)
    {
        Reserve(checked(Count + values.Length));
        values.CopyTo(new Span<T>(Pointer + Count, values.Length)); Count += values.Length;
    }
    public void Reset() => Count = 0;

    public ImVector<T> View
    {
        get
        {
            var header = new VectorHeader { Size = Count, Capacity = Capacity, Data = (nint)Pointer };
            return Unsafe.As<VectorHeader, ImVector<T>>(ref header);
        }
    }

    public void Dispose() { NativeMemory.Free(Pointer); Pointer = null; Count = Capacity = 0; }

    [StructLayout(LayoutKind.Sequential)]
    private struct VectorHeader { public int Size; public int Capacity; public nint Data; }
}
