using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using WobblyWindows.Core;
using WobblyWindows.Rendering;
using WobblyWindows.Runtime;

namespace WobblyWindows.Checks;

internal static unsafe class Checks
{
    private static int _passed;
    public static int Main()
    {
        try
        {
            Run("host binding layouts and renderer capabilities", CompatibilityChecks);
            Run("native views and transactional restoration", Publication);
            Run("corner anchoring, bounded motion and settling", Motion);
            Run("consistent motion across frame rates", FrameRates);
            Run("stall, resize, teleport and strength resets", Transitions);
            Run("source clipping preserves UV and color", Clipping);
            Run("identity raster coverage matches original clipping", RasterIdentity);
            Run("thin themed geometry preserves continuous output", ThinThemedGeometry);
            Run("shared warp preserves coverage across source triangulations", SharedWarpCoverage);
            Run("16-bit command segmentation and offsets", Segmentation);
            Run("callbacks, malformed input and budget fallback", Rejection);
            Run("tile work cap and locally folded geometry fallback", WorkAndFolds);
            Run("warm solver and mesh path allocate no managed memory", Allocation);
            Console.WriteLine($"PASS: {_passed} check groups. Native ImGui and KSA were not launched.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string name, Action test) { test(); ++_passed; Console.WriteLine("PASS " + name); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Near(float actual, float expected, float tolerance, string message) =>
        Require(Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} != {expected}");

    private static void Publication()
    {
        Require(sizeof(ImVector<ImDrawListPtr>) == 16, "Unexpected vector header ABI");
        using var original = new OwnedDrawList();
        original.Vertices.Add(Vertex(1, 2)); original.Complete(default);
        using var originalLists = new NativeBuffer<ImDrawListPtr>();
        originalLists.Add(original.Pointer);
        ImDrawData data = new() { CmdLists = originalLists.View, CmdListsCount = 1, TotalVtxCount = 1, TotalIdxCount = 0 };
        using var replacement = new OwnedDrawList();
        replacement.CopyFrom(original.Pointer);
        using var publication = new DrawPublication();
        publication.Lists.Add(replacement.Pointer);
        nint before = (nint)data.CmdLists.DataRaw;
        publication.Publish(&data, 1, 0);
        Require(data.CmdLists.DataRaw[0].Ptr == replacement.Pointer, "Replacement not visible");
        Require(data.CmdLists.Span[0].VtxBuffer.Span[0].pos.X == 1, "Native view layout incorrect");
        bool duplicateRejected = false;
        try { publication.Publish(&data, 1, 0); } catch (InvalidOperationException) { duplicateRejected = true; }
        Require(duplicateRejected, "Duplicate publication accepted");
        publication.Restore(); publication.Restore();
        Require((nint)data.CmdLists.DataRaw == before && data.CmdListsCount == 1 && data.TotalVtxCount == 1, "Original header not restored");
        publication.Publish(&data, 999, 777); publication.Dispose();
        Require((nint)data.CmdLists.DataRaw == before && data.TotalVtxCount == 1 && data.TotalIdxCount == 0, "Dispose did not restore");
        Require(original.Vertices.Span[0].pos.X == 1, "Original buffer altered/freed");
    }

    private static void CompatibilityChecks()
    {
        Compatibility.ValidateBindings();
        Compatibility.ValidateRenderer(ImGuiBackendFlags.RendererHasVtxOffset);
        Compatibility.ValidateRenderer(ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures);
        try { Compatibility.ValidateRenderer(ImGuiBackendFlags.RendererHasTextures); }
        catch (NotSupportedException error)
        {
            Require(error.Message.Contains("RendererHasVtxOffset"), "Missing capability diagnostic is not actionable");
            return;
        }
        throw new InvalidOperationException("Renderer without vertex offsets was accepted");
    }

    private static SpringSurface MovingSurface(Vector2? grab = null)
    {
        var settings = new WobbleSettings();
        var surface = new SpringSurface();
        Vector2 size = new(400, 300), offset = grab ?? new Vector2(92, 12);
        surface.Reset(Vector2.Zero, size);
        for (int i = 1; i <= 30; ++i)
        {
            Vector2 position = new(i * 3, i * 0.8f);
            surface.Advance(position, size, position + offset, true, 1f / 60, settings);
        }
        return surface;
    }

    private static void Motion()
    {
        foreach (Vector2 grab in new[] { new Vector2(4, 4), new Vector2(92, 12), new Vector2(396, 4), new Vector2(200, 150) })
        {
            SpringSurface surface = MovingSurface(grab);
            Require(surface.IsActive && surface.MaximumOffset > 1, "Drag failed to excite the spring lattice");
            Vector2 point = surface.Position + grab;
            Require(Vector2.Distance(surface.Warp(point), point) < 0.0002f, "Displayed grab point slipped");
            Require(surface.MaximumOffset <= 24.001f, "Displacement cap exceeded");
            Vector2 corner = surface.Warp(surface.Position + surface.Size);
            surface.Advance(surface.Position, surface.Size, point, false, 1f / 120, new());
            Require(surface.IsActive, "Release snapped to identity");
            Require(Vector2.Distance(corner, surface.Warp(surface.Position + surface.Size)) < 8, "Release jumped discontinuously");
            for (int i = 0; i < 600; ++i) surface.Advance(surface.Position, surface.Size, point, false, 1f / 120, new());
            Require(!surface.IsActive && surface.Warp(point) == point, "Surface did not sleep at exact identity");
        }
    }

    private static void FrameRates()
    {
        Vector2 reference = Replay(120);
        foreach (int hz in new[] { 30, 60, 144, 240 })
        {
            Vector2 sample = Replay(hz);
            float error = Vector2.Distance(reference, sample);
            Console.WriteLine($"  {hz} Hz release comparison: {error:F3} px");
            Require(error < 3, "Frame-rate response diverged");
        }
        Vector2 dragReference = ConstantDrag(120);
        foreach (int hz in new[] { 30, 60, 144, 240 })
        {
            float error = Vector2.Distance(dragReference, ConstantDrag(hz));
            Console.WriteLine($"  {hz} Hz continuous-drag comparison: {error:F3} px");
            Require(error < 0.5f, "Continuous dragging depends on frame rate");
        }
        static Vector2 ConstantDrag(int hz)
        {
            var surface = new SpringSurface();
            var size = new Vector2(800, 600);
            var settings = new WobbleSettings();
            surface.Reset(Vector2.Zero, size);
            for (int i = 1; i <= hz / 2; ++i)
            {
                var pos = new Vector2(600f * i / hz, 0);
                surface.Advance(pos, size, pos + new Vector2(184, 21), true, 1f / hz, settings);
            }
            return surface.Warp(surface.Position + size) - (surface.Position + size);
        }
        static Vector2 Replay(int hz)
        {
            var surface = new SpringSurface();
            var size = new Vector2(400, 300);
            surface.Reset(Vector2.Zero, size);
            for (int i = 1; i <= hz; ++i)
            {
                float t = i / (float)hz;
                var pos = new Vector2(220 * Math.Min(t, 0.75f), 30 * Math.Min(t, 0.75f));
                surface.Advance(pos, size, pos + new Vector2(80, 10), t <= 0.75f, 1f / hz, new());
            }
            return surface.Warp(surface.Position + size) - (surface.Position + size);
        }
    }

    private static void Transitions()
    {
        SpringSurface surface = MovingSurface();
        surface.Advance(surface.Position, surface.Size, Vector2.Zero, false, 0.5f, new());
        Require(!surface.IsActive, "Long stall did not reset");
        surface = MovingSurface();
        surface.Advance(surface.Position, surface.Size + new Vector2(10, 0), Vector2.Zero, true, 1f / 60, new());
        Require(!surface.IsActive, "Resize did not reset");
        surface = MovingSurface();
        surface.Advance(surface.Position + new Vector2(1000), surface.Size, Vector2.Zero, true, 1f / 60, new());
        Require(!surface.IsActive, "Teleport did not reset");
        surface = new(); surface.Reset(Vector2.Zero, new(400, 300));
        for (int i = 1; i < 60; ++i) surface.Advance(new(i, 0), new(400, 300), new(i + 10, 10), true, 1f / 60, new() { Strength = 0 });
        Require(surface.MaximumOffset == 0, "Zero strength deforms");
        var settings = new WobbleSettings { Frequency = float.NaN, Damping = float.PositiveInfinity, Strength = -10 };
        settings.Normalize();
        Require(float.IsFinite(settings.Frequency) && float.IsFinite(settings.Damping) && settings.Strength == 0, "Settings not normalized");
    }

    private static OwnedDrawList TriangleSource(int repetitions = 1)
    {
        var source = new OwnedDrawList();
        source.Vertices.Add(Vertex(-999, -999)); // deliberately unused prefix vertex
        source.Vertices.Add(Vertex(-5, 2)); source.Vertices.Add(Vertex(15, 2)); source.Vertices.Add(Vertex(5, 18));
        source.Indices.Add(0); // deliberately unused prefix index
        for (int i = 0; i < repetitions; ++i) { source.Indices.Add(0); source.Indices.Add(1); source.Indices.Add(2); }
        source.Commands.Add(new ImDrawCmd { ClipRect = new float4(0, 0, 10, 10), VtxOffset = 1, IdxOffset = 1, ElemCount = (uint)(repetitions * 3) });
        source.Complete(default); return source;
    }

    private static ImDrawVert Vertex(float x, float y) => new()
    {
        pos = new float2(x, y), uv = new float2(x / 20, y / 20), col = 0x80402010
    };

    private static bool Build(OwnedDrawList source, OwnedDrawList output, SpringSurface surface, int budget = GeometryProcessor.MaximumVertices)
    {
        int triangles = 120_000;
        return GeometryProcessor.Build(source.Pointer, output, surface, new Vector4(-2000, -2000, 3000, 3000), 1, ref budget, ref triangles);
    }

    private static void Clipping()
    {
        using var source = TriangleSource();
        using var output = new OwnedDrawList();
        var surface = new SpringSurface(); surface.Reset(Vector2.Zero, new(400, 300));
        byte[] before = MemoryMarshal.AsBytes(source.Vertices.Span).ToArray();
        Require(Build(source, output, surface), "Clipped mesh rejected");
        Require(output.Vertices.Count > 0, "Clipped mesh empty");
        foreach (ImDrawVert vertex in output.Vertices.Span)
        {
            Require(vertex.pos.X >= -0.0001f && vertex.pos.X <= 10.0001f && vertex.pos.Y >= -0.0001f && vertex.pos.Y <= 10.0001f, "Source clip leaked");
            Near(vertex.uv.X * 20, vertex.pos.X, 0.0001f, "Clipped U drift");
            Near(vertex.uv.Y * 20, vertex.pos.Y, 0.0001f, "Clipped V drift");
            Require(vertex.col == 0x80402010, "Alpha/color changed");
        }
        Require(before.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(source.Vertices.Span)), "Source vertices modified");
        Span<GeometryProcessor.ClipVertex> polygon = stackalloc GeometryProcessor.ClipVertex[12];
        Span<GeometryProcessor.ClipVertex> scratch = stackalloc GeometryProcessor.ClipVertex[12];
        polygon[0] = new(new(-10, 0), Vector2.Zero, Vector4.Zero);
        polygon[1] = new(new(10, 0), Vector2.One, new(200));
        polygon[2] = new(new(0, 10), new(0.5f), new(100));
        int count = GeometryProcessor.Clip(polygon, 3, scratch, new(0, -10, 20, 20));
        Require(count >= 3, "Clipping removed visible polygon");
        foreach (var vertex in polygon[..count])
            if (Math.Abs(vertex.Position.X) < 0.0001f) { Near(vertex.UV.X, 0.5f, 0.0001f, "Interpolated UV"); Near(vertex.Color.X, 100, 0.001f, "Interpolated color"); }
        // A nonlinear warp must still discard source content outside its original clip.
        surface = MovingSurface();
        Require(Build(source, output, surface), "Warped clipped mesh rejected");
        foreach (ImDrawVert vertex in output.Vertices.Span)
            Require(vertex.uv.X >= -0.0001f && vertex.uv.X <= 0.5001f && vertex.uv.Y >= -0.0001f && vertex.uv.Y <= 0.5001f, "Warp exposed originally clipped content");
    }

    private static void RasterIdentity()
    {
        using var source = TriangleSource(); using var output = new OwnedDrawList();
        var surface = new SpringSurface(); surface.Reset(Vector2.Zero, new(40, 30));
        Require(Build(source, output, surface), "Identity tessellation failed");
        for (int y = -2; y < 20; ++y)
        for (int x = -8; x < 18; ++x)
        {
            Vector2 point = new(x + 0.37f, y + 0.61f);
            bool expected = point.X >= 0 && point.X < 10 && point.Y >= 0 && point.Y < 10
                && Inside(point, new(-5, 2), new(15, 2), new(5, 18));
            bool actual = false;
            foreach (ImDrawCmd command in output.Commands.Span)
            for (uint i = command.IdxOffset; i < command.IdxOffset + command.ElemCount; i += 3)
            {
                Vector2 a = Pos(output, command, i), b = Pos(output, command, i + 1), c = Pos(output, command, i + 2);
                actual |= Inside(point, a, b, c);
            }
            Require(actual == expected, $"Identity coverage differs at {point}");
        }
    }

    private static Vector2 Pos(OwnedDrawList list, ImDrawCmd command, uint index)
    { var p = list.Vertices.Pointer[list.Indices.Pointer[index] + command.VtxOffset].pos; return new(p.X, p.Y); }
    private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
        SpringSurface.Cross(b - a, p - a) >= -0.00001f && SpringSurface.Cross(c - b, p - b) >= -0.00001f && SpringSurface.Cross(a - c, p - c) >= -0.00001f;

    private static void Segmentation()
    {
        using var source = new OwnedDrawList(); using var output = new OwnedDrawList();
        // Keep this triangle within one grid half so this check isolates index segmentation.
        source.Vertices.Add(Vertex(10, 1)); source.Vertices.Add(Vertex(11, 1)); source.Vertices.Add(Vertex(10, 2));
        for (int i = 0; i < 23_000; ++i) { source.Indices.Add(0); source.Indices.Add(1); source.Indices.Add(2); }
        source.Commands.Add(new ImDrawCmd { ClipRect = new float4(0, 0, 20, 20), ElemCount = 69_000 }); source.Complete(default);
        var surface = new SpringSurface(); surface.Reset(Vector2.Zero, new(400, 300));
        Require(Build(source, output, surface), "Large mesh failed");
        Require(output.Vertices.Count == 69_000 && output.Commands.Count == 2, "Large mesh not segmented");
        int drawn = 0;
        foreach (ImDrawCmd command in output.Commands.Span)
        {
            Require(command.ElemCount % 3 == 0 && command.VtxOffset + command.ElemCount <= output.Vertices.Count, "Invalid chunk offset");
            for (uint i = command.IdxOffset; i < command.IdxOffset + command.ElemCount; ++i)
            {
                int vertex = checked((int)(output.Indices.Pointer[i] + command.VtxOffset));
                Require(vertex == drawn++, "Chunk lost vertex indexing");
            }
        }
        Require(drawn == 69_000, "Triangles lost across chunks");
    }

    private static void ThinThemedGeometry()
    {
        var surface = new SpringSurface();
        var size = new Vector2(483, 367);
        var settings = new WobbleSettings();
        surface.Reset(new(600, 400), size);
        for (int frame = 0; frame <= 174; ++frame)
        {
            var pos = new Vector2(2600 + MathF.Sin(frame * 0.013f) * 500, 1400 + MathF.Sin(frame * 0.1f) * 300);
            surface.Advance(pos, size, pos + new Vector2(100, 15), true, 1f / 60, settings);
        }
        Require(surface.IsActive, "Replay did not excite the surface");
        using var source = new OwnedDrawList(); using var output = new OwnedDrawList();
        // Captured from a native ImGui rounded console bezel replay. Direct bicubic evaluation
        // reverses this skinny triangle's winding despite a healthy, unfolded spring surface.
        source.Vertices.Add(Vertex(2986.028f, 1293.6804f));
        source.Vertices.Add(Vertex(2986, 1283.923f));
        source.Vertices.Add(Vertex(2986, 1277.7239f));
        source.Indices.Add(0); source.Indices.Add(1); source.Indices.Add(2);
        source.Commands.Add(new ImDrawCmd { ClipRect = new(0, 0, 7680, 4320), ElemCount = 3 });
        source.Complete(default);
        int vertices = GeometryProcessor.MaximumVertices, work = 120_000;
        Require(GeometryProcessor.Build(source.Pointer, output, surface, new(0, 0, 7680, 4320), 1,
            ref vertices, ref work), "Thin console triangle discarded the warped frame");
        Require(output.Vertices.Count > 0, "Thin console geometry disappeared");
        foreach (ImDrawCmd command in output.Commands.Span)
        for (uint i = command.IdxOffset; i < command.IdxOffset + command.ElemCount; i += 3)
            Require(SpringSurface.Cross(Pos(output, command, i + 1) - Pos(output, command, i),
                Pos(output, command, i + 2) - Pos(output, command, i)) > 0, "Thin console triangle inverted");
    }

    private static void SharedWarpCoverage()
    {
        SpringSurface surface = MovingSurface();
        using var quad = new OwnedDrawList(); using var fan = new OwnedDrawList();
        using var quadOutput = new OwnedDrawList(); using var fanOutput = new OwnedDrawList();
        Vector2 pos = surface.Position, size = surface.Size;
        foreach (Vector2 offset in new[] { Vector2.Zero, new Vector2(size.X, 0), size, new Vector2(0, size.Y) })
        {
            quad.Vertices.Add(Vertex(pos.X + offset.X, pos.Y + offset.Y));
            fan.Vertices.Add(Vertex(pos.X + offset.X, pos.Y + offset.Y));
        }
        // The same rectangle as a coarse quad and a fan with a very thin left-hand triangle.
        foreach (ushort index in new ushort[] { 0, 1, 2, 0, 2, 3 }) quad.Indices.Add(index);
        fan.Vertices.Add(Vertex(pos.X + 0.005f, pos.Y + size.Y * 0.6f));
        for (ushort i = 0; i < 4; ++i)
        { fan.Indices.Add(i); fan.Indices.Add((ushort)((i + 1) % 4)); fan.Indices.Add(4); }
        quad.Commands.Add(new ImDrawCmd { ClipRect = new(-2000, -2000, 3000, 3000), ElemCount = 6 });
        fan.Commands.Add(new ImDrawCmd { ClipRect = new(-2000, -2000, 3000, 3000), ElemCount = 12 });
        quad.Complete(default); fan.Complete(default);
        Require(Build(quad, quadOutput, surface) && Build(fan, fanOutput, surface), "Equivalent meshes rejected");
        for (int y = -2; y < 26; ++y)
        for (int x = -2; x < 34; ++x)
        {
            Vector2 sample = pos + new Vector2((x + 0.37f) * size.X / 32, (y + 0.61f) * size.Y / 24);
            int first = Coverage(quadOutput, sample), second = Coverage(fanOutput, sample);
            Require(first <= 1 && first == second, $"Warped mesh has a seam, overlap or triangulation-dependent coverage at {sample}");
        }

        static int Coverage(OwnedDrawList mesh, Vector2 sample)
        {
            int count = 0;
            foreach (ImDrawCmd command in mesh.Commands.Span)
            for (uint i = command.IdxOffset; i < command.IdxOffset + command.ElemCount; i += 3)
                if (Inside(sample, Pos(mesh, command, i), Pos(mesh, command, i + 1), Pos(mesh, command, i + 2))) ++count;
            return count;
        }
    }

    private static void Rejection()
    {
        using var source = TriangleSource(); using var output = new OwnedDrawList();
        var surface = new SpringSurface(); surface.Reset(Vector2.Zero, new(400, 300));
        Require(!Build(source, output, surface, 2), "Vertex budget ignored");
        source.Commands.Pointer[0].UserCallback = 123;
        Require(!Build(source, output, surface), "Callback accepted");
        source.Commands.Pointer[0].UserCallback = 0;
        source.Commands.Pointer[0].VtxOffset = 999;
        Require(!Build(source, output, surface), "Out-of-bounds vertex accepted");
        source.Commands.Pointer[0].VtxOffset = 1;
        source.Vertices.Pointer[1].pos.X = float.NaN;
        Require(!Build(source, output, surface), "Non-finite vertex accepted");
    }

    private static void WorkAndFolds()
    {
        using var source = TriangleSource(); using var output = new OwnedDrawList();
        var surface = new SpringSurface(); surface.Reset(Vector2.Zero, new(40, 30));
        int vertices = 1000, work = 1;
        Require(!GeometryProcessor.Build(source.Pointer, output, surface, new(-100, -100, 500, 500), 1,
            ref vertices, ref work), "Tile clipping ignored work budget");

        surface.Reset(Vector2.Zero, new(400, 400));
        // Adversarial bounded field: a coarse 8x8 guard misses the top-right local inversion.
        var offsets = (Vector2[])typeof(SpringSurface).GetField("_displayOffset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(surface)!;
        float q = 48 / MathF.Sqrt(2);
        Array.Fill(offsets, new Vector2(-q, q)); offsets[2] = offsets[7] = new Vector2(q, -q);
        typeof(SpringSurface).GetProperty(nameof(SpringSurface.IsActive))!.SetValue(surface, true);
        typeof(SpringSurface).GetProperty(nameof(SpringSurface.MaximumOffset))!.SetValue(surface, 48f);
        using var corner = new OwnedDrawList();
        corner.Vertices.Add(Vertex(399.9f, 0.1f)); corner.Vertices.Add(Vertex(399.99f, 0.1f)); corner.Vertices.Add(Vertex(399.9f, 0.19f));
        corner.Indices.Add(0); corner.Indices.Add(1); corner.Indices.Add(2);
        corner.Commands.Add(new ImDrawCmd { ClipRect = new float4(0, 0, 400, 400), ElemCount = 3 }); corner.Complete(default);
        Require(!Build(corner, output, surface), "Locally flipped output was accepted");
    }

    private static void Allocation()
    {
        using var source = TriangleSource(); using var output = new OwnedDrawList();
        SpringSurface surface = MovingSurface();
        for (int i = 0; i < 20; ++i) Require(Build(source, output, surface), "Warmup failed");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; ++i)
        {
            if (!Build(source, output, surface)) throw new InvalidOperationException("Build failed");
            surface.Warp(new Vector2(100, 100));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0, $"Warm mesh path allocated {allocated} bytes");
    }
}
