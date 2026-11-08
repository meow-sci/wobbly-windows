using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using WobblyWindows.Core;

namespace WobblyWindows.Rendering;

/// <summary>Preclips in source space, then maps through one shared triangulated root-space grid.</summary>
internal static unsafe class GeometryProcessor
{
    public const int MaximumVertices = 240_000;
    private const int ChunkVertices = 65_532; // Multiple of 3, safely inside ushort addressing.

    public static bool Build(ImDrawList* source, OwnedDrawList output, SpringSurface surface,
        Vector4 viewport, float framebufferScale, ref int vertexBudget, ref int workBudget)
    {
        output.Reset();
        int cells = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(12 * surface.MaximumOffset * framebufferScale / 0.5f)), 8, 64);
        Vector2 cell = surface.Size / cells;
        // Evaluate the smooth surface only at shared grid nodes. Within each grid triangle the
        // mapping must be affine: bending each source vertex independently can invert a long,
        // thin bezel/AA triangle even when the underlying surface has no fold at all.
        int stride = cells + 1;
        Span<Vector2> offsets = stackalloc Vector2[stride * stride];
        for (int y = 0; y <= cells; ++y)
        for (int x = 0; x <= cells; ++x)
        {
            Vector2 p = surface.Position + new Vector2(x * cell.X, y * cell.Y);
            offsets[y * stride + x] = surface.Warp(p) - p;
            if (!SpringSurface.Finite(offsets[y * stride + x])) return false;
        }
        Span<ClipVertex> polygon = stackalloc ClipVertex[12];
        Span<ClipVertex> scratch = stackalloc ClipVertex[12];
        Span<ClipVertex> clipped = stackalloc ClipVertex[12];
        Span<ClipVertex> cellScratch = stackalloc ClipVertex[12];
        Span<ClipVertex> half = stackalloc ClipVertex[12];
        foreach (ImDrawCmd original in source->CmdBuffer.Span)
        {
            if (original.UserCallback != 0 || original.ElemCount % 3 != 0
                || (ulong)original.IdxOffset + original.ElemCount > (ulong)source->IdxBuffer.Count) return false;
            Vector4 clip = new(Math.Max(original.ClipRect.X, viewport.X), Math.Max(original.ClipRect.Y, viewport.Y),
                Math.Min(original.ClipRect.Z, viewport.Z), Math.Min(original.ClipRect.W, viewport.W));
            if (!Finite(clip)) return false;
            if (clip.X >= clip.Z || clip.Y >= clip.W) continue;
            ImDrawCmd command = original;
            command.IdxOffset = (uint)output.Indices.Count; command.VtxOffset = (uint)output.Vertices.Count;
            command.ElemCount = 0;
            Vector4 bounds = EmptyBounds;
            for (uint index = original.IdxOffset; index < original.IdxOffset + original.ElemCount; index += 3)
            {
                if (--workBudget < 0) return false;
                for (int i = 0; i < 3; ++i)
                {
                    ulong vertex = (ulong)source->IdxBuffer.DataRaw[index + i] + original.VtxOffset;
                    if (vertex >= (ulong)source->VtxBuffer.Count) return false;
                    polygon[i] = ClipVertex.From(source->VtxBuffer.DataRaw[vertex]);
                    if (!SpringSurface.Finite(polygon[i].Position) || !SpringSurface.Finite(polygon[i].UV)) return false;
                }
                if (Math.Abs(SpringSurface.Cross(polygon[1].Position - polygon[0].Position,
                    polygon[2].Position - polygon[0].Position)) < 0.00001f) continue;
                int count = Clip(polygon, 3, scratch, clip);
                if (count < 3) continue;
                Vector2 min = polygon[0].Position, max = min;
                for (int i = 1; i < count; ++i) { min = Vector2.Min(min, polygon[i].Position); max = Vector2.Max(max, polygon[i].Position); }
                int x0 = Cell(min.X, surface.Position.X, cell.X, cells), x1 = Cell(max.X, surface.Position.X, cell.X, cells);
                int y0 = Cell(min.Y, surface.Position.Y, cell.Y, cells), y1 = Cell(max.Y, surface.Position.Y, cell.Y, cells);
                for (int y = y0; y <= y1; ++y)
                for (int x = x0; x <= x1; ++x)
                {
                    if (--workBudget < 0) return false;
                    polygon[..count].CopyTo(clipped);
                    var tile = new Vector4(x == 0 ? min.X : surface.Position.X + x * cell.X,
                        y == 0 ? min.Y : surface.Position.Y + y * cell.Y,
                        x == cells - 1 ? max.X : surface.Position.X + (x + 1) * cell.X,
                        y == cells - 1 ? max.Y : surface.Position.Y + (y + 1) * cell.Y);
                    int n = Clip(clipped, count, cellScratch, tile);
                    if (n < 3) continue;
                    Vector2 origin = surface.Position + new Vector2(x * cell.X, y * cell.Y);
                    Vector2 d00 = offsets[y * stride + x], d10 = offsets[y * stride + x + 1],
                        d01 = offsets[(y + 1) * stride + x], d11 = offsets[(y + 1) * stride + x + 1];
                    // Both halves must preserve orientation. This validates the actual piecewise
                    // affine map rather than mistaking curved chords of skinny input for folds.
                    float area = cell.X * cell.Y;
                    if (SpringSurface.Cross(new Vector2(cell.X, 0) + d10 - d00, cell + d11 - d00) < area * 0.05f
                        || SpringSurface.Cross(cell + d11 - d00, new Vector2(0, cell.Y) + d01 - d00) < area * 0.05f) return false;
                    for (int side = 0; side < 2; ++side)
                    {
                        clipped[..n].CopyTo(half);
                        int halfCount = ClipDiagonal(half, n, cellScratch, origin, cell, side);
                        for (int t = 1; t + 1 < halfCount; ++t)
                        {
                            ClipVertex a = half[0], b = half[t], c = half[t + 1];
                            float sourceArea = SpringSurface.Cross(b.Position - a.Position, c.Position - a.Position);
                            if (Math.Abs(sourceArea) < 0.00001f) continue;
                            Vector2 pa = Warp(a.Position), pb = Warp(b.Position), pc = Warp(c.Position);
                            if (vertexBudget < 3) return false;
                            if (output.Vertices.Count - command.VtxOffset + 3 > ChunkVertices)
                            {
                                Flush(output, ref command, bounds, viewport);
                                command.IdxOffset = (uint)output.Indices.Count; command.VtxOffset = (uint)output.Vertices.Count;
                                bounds = EmptyBounds;
                            }
                            Emit(a, pa, output, command.VtxOffset, ref bounds);
                            Emit(b, pb, output, command.VtxOffset, ref bounds);
                            Emit(c, pc, output, command.VtxOffset, ref bounds);
                            command.ElemCount += 3; vertexBudget -= 3;
                        }

                        Vector2 Warp(Vector2 p)
                        {
                            Vector2 uv = (p - origin) / cell;
                            return p + (side == 0
                                ? d00 * (1 - uv.X) + d10 * (uv.X - uv.Y) + d11 * uv.Y
                                : d00 * (1 - uv.Y) + d11 * uv.X + d01 * (uv.Y - uv.X));
                        }
                    }
                }
            }
            Flush(output, ref command, bounds, viewport);

        }
        output.Complete(source->Flags);
        return true;
    }

    private static void Emit(ClipVertex vertex, Vector2 position, OwnedDrawList output, uint vertexOffset, ref Vector4 bounds)
    {
        if (!SpringSurface.Finite(position)) throw new InvalidOperationException("Non-finite deformed vertex.");
        bounds = new Vector4(Math.Min(bounds.X, position.X), Math.Min(bounds.Y, position.Y),
            Math.Max(bounds.Z, position.X), Math.Max(bounds.W, position.Y));
        output.Indices.Add(checked((ushort)(output.Vertices.Count - vertexOffset)));
        output.Vertices.Add(vertex.ToNative(position));
    }

    private static int Cell(float value, float origin, float step, int cells) =>
        Math.Clamp((int)MathF.Floor((value - origin) / step), 0, cells - 1);
    private static Vector4 EmptyBounds => new(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
    private static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);

    private static int ClipDiagonal(Span<ClipVertex> polygon, int count, Span<ClipVertex> scratch,
        Vector2 origin, Vector2 cell, int side)
    {
        int written = 0;
        ClipVertex previous = polygon[count - 1];
        float pd = Distance(previous.Position);
        for (int i = 0; i < count; ++i)
        {
            ClipVertex current = polygon[i];
            float cd = Distance(current.Position);
            if ((pd >= 0) != (cd >= 0)) scratch[written++] = ClipVertex.Lerp(previous, current, pd / (pd - cd));
            if (cd >= 0) scratch[written++] = current;
            previous = current; pd = cd;
        }
        scratch[..written].CopyTo(polygon);
        return written;

        float Distance(Vector2 p)
        {
            Vector2 relative = (p - origin) / cell;
            return side == 0 ? relative.X - relative.Y : relative.Y - relative.X;
        }
    }

    private static void Flush(OwnedDrawList output, ref ImDrawCmd command, Vector4 bounds, Vector4 viewport)
    {
        if (command.ElemCount == 0) return;
        // Preclipped triangles carry the curved clip boundary. Scissor only bounds their output.
        command.ClipRect = new float4(Math.Max(viewport.X, bounds.X - 1), Math.Max(viewport.Y, bounds.Y - 1),
            Math.Min(viewport.Z, bounds.Z + 1), Math.Min(viewport.W, bounds.W + 1));
        output.Commands.Add(command); command.ElemCount = 0;
    }

    internal static int Clip(Span<ClipVertex> polygon, int count, Span<ClipVertex> scratch, Vector4 rect)
    {
        for (int edge = 0; edge < 4 && count > 0; ++edge)
        {
            int written = 0;
            ClipVertex previous = polygon[count - 1];
            float pd = Distance(previous.Position, rect, edge);
            for (int i = 0; i < count; ++i)
            {
                ClipVertex current = polygon[i];
                float cd = Distance(current.Position, rect, edge);
                if ((pd >= 0) != (cd >= 0)) scratch[written++] = ClipVertex.Lerp(previous, current, pd / (pd - cd));
                if (cd >= 0) scratch[written++] = current;
                previous = current; pd = cd;
            }
            scratch[..written].CopyTo(polygon); count = written;
        }
        return count;
    }

    private static float Distance(Vector2 p, Vector4 r, int edge) => edge switch
    { 0 => p.X - r.X, 1 => r.Z - p.X, 2 => p.Y - r.Y, _ => r.W - p.Y };

    internal readonly record struct ClipVertex(Vector2 Position, Vector2 UV, Vector4 Color)
    {
        public static ClipVertex From(ImDrawVert v) => new(new(v.pos.X, v.pos.Y), new(v.uv.X, v.uv.Y),
            new(v.col & 255, (v.col >> 8) & 255, (v.col >> 16) & 255, v.col >> 24));
        public static ClipVertex Lerp(ClipVertex a, ClipVertex b, float t) => new(
            Vector2.Lerp(a.Position, b.Position, t), Vector2.Lerp(a.UV, b.UV, t), Vector4.Lerp(a.Color, b.Color, t));
        public ImDrawVert ToNative(Vector2 position) => new()
        {
            pos = new float2(position.X, position.Y), uv = new float2(UV.X, UV.Y),
            col = Pack(Color.X) | (Pack(Color.Y) << 8) | (Pack(Color.Z) << 16) | (Pack(Color.W) << 24)
        };
        private static uint Pack(float value) => (uint)Math.Clamp((int)MathF.Round(value), 0, 255);
    }
}
