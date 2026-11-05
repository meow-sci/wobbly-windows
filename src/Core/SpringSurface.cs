using System.Numerics;

namespace WobblyWindows.Core;

/// <summary>A damped 4x4 displacement lattice evaluated as a bicubic Bezier surface.</summary>
internal sealed class SpringSurface
{
    private const float Step = 1f / 120;
    private readonly Vector2[] _offset = new Vector2[16], _displayOffset = new Vector2[16], _velocity = new Vector2[16], _force = new Vector2[16];
    private Vector2 _position, _size, _grab, _pendingMovement;
    private float _accumulator;
    private bool _initialized, _dragging;
    public bool IsActive { get; private set; }
    public float MaximumOffset { get; private set; }
    public Vector2 Position => _position;
    public Vector2 Size => _size;

    public void Reset(Vector2 position, Vector2 size)
    {
        Array.Clear(_offset); Array.Clear(_displayOffset); Array.Clear(_velocity);
        _position = position; _size = size; _accumulator = 0;
        _pendingMovement = Vector2.Zero;
        _initialized = true; _dragging = false; IsActive = false; MaximumOffset = 0;
    }

    public void Advance(Vector2 position, Vector2 size, Vector2 mouse, bool dragging, float elapsed, WobbleSettings settings)
    {
        if (!_initialized || !Finite(position) || !Finite(size) || size.X < 40 || size.Y < 30
            || Vector2.DistanceSquared(size, _size) > 0.25f || !float.IsFinite(elapsed) || elapsed <= 0 || elapsed > 0.1f)
        { Reset(position, size); return; }
        Vector2 movement = position - _position;
        if (movement.Length() > Math.Min(size.X, size.Y) * 0.6f || (!dragging && !_dragging && movement.LengthSquared() > 1))
        { Reset(position, size); return; }
        if (dragging && !_dragging)
            _grab = Vector2.Clamp((mouse - position) / size, Vector2.Zero, Vector2.One);
        _position = position; _size = size;
        if (dragging)
        {
            _pendingMovement += movement * settings.Strength;
            IsActive |= movement.LengthSquared() > 0.001f;
        }
        _dragging = dragging;
        if (!IsActive) return;
        float omega = 2 * MathF.PI * settings.Frequency;
        float stiffness = omega * omega, damping = 2 * settings.Damping * omega;
        _accumulator = Math.Min(_accumulator + elapsed, 8 * Step);
        while (_accumulator >= Step)
        {
            // Resample the frame's moving rest position across fixed steps. Applying all movement
            // before the first step would over-damp low-FPS dragging and change the feel with FPS.
            Vector2 stepMovement = _pendingMovement * (Step / _accumulator);
            _pendingMovement -= stepMovement;
            for (int i = 0; i < 16; ++i) _offset[i] -= stepMovement;
            for (int y = 0; y < 4; ++y)
            for (int x = 0; x < 4; ++x)
            {
                int i = y * 4 + x;
                Vector2 force = -stiffness * _offset[i] - damping * _velocity[i];
                if (x > 0) force += 90 * (_offset[i - 1] - _offset[i]);
                if (x < 3) force += 90 * (_offset[i + 1] - _offset[i]);
                if (y > 0) force += 90 * (_offset[i - 4] - _offset[i]);
                if (y < 3) force += 90 * (_offset[i + 4] - _offset[i]);
                _force[i] = force;
            }
            for (int i = 0; i < 16; ++i)
            { _velocity[i] += _force[i] * Step; _offset[i] += _velocity[i] * Step; }
            if (dragging) PinGrab(_offset, stepMovement / Step);
            _accumulator -= Step;
        }
        // Extrapolate only the substep remainder to the current logical rectangle. The displayed
        // constraint is separate so a high-refresh frame cannot perturb the fixed-step solver.
        for (int i = 0; i < 16; ++i) _displayOffset[i] = _offset[i] + _velocity[i] * _accumulator - _pendingMovement;
        if (dragging) PinGrab(_displayOffset, null);
        MaximumOffset = 0;
        float maximumVelocity = 0;
        for (int i = 0; i < 16; ++i)
        {
            if (!Finite(_displayOffset[i]) || !Finite(_velocity[i])) { Reset(position, size); return; }
            MaximumOffset = Math.Max(MaximumOffset, _displayOffset[i].Length());
            maximumVelocity = Math.Max(maximumVelocity, _velocity[i].Length());
        }
        float limit = Math.Min(64, Math.Min(size.X, size.Y) * 0.08f) * Math.Min(1.5f, settings.Strength);
        if (MaximumOffset > limit)
        {
            float scale = limit / MaximumOffset;
            for (int i = 0; i < 16; ++i) { _offset[i] *= scale; _displayOffset[i] *= scale; _velocity[i] *= scale; }
            MaximumOffset = limit;
        }
        if (MaximumOffset < 0.08f && maximumVelocity < 0.4f) { Reset(position, size); return; }
        if (!HasPositiveJacobian()) Reset(position, size);
    }

    private void PinGrab(Vector2[] offsets, Vector2? targetVelocity)
    {
        Span<float> weights = stackalloc float[16];
        Weights(_grab, weights);
        Vector2 error = Vector2.Zero, speed = Vector2.Zero;
        float sum = 0;
        for (int i = 0; i < 16; ++i)
        { error += weights[i] * offsets[i]; speed += weights[i] * _velocity[i]; sum += weights[i] * weights[i]; }
        for (int i = 0; i < 16; ++i)
        {
            offsets[i] -= error * (weights[i] / sum);
            if (targetVelocity.HasValue) _velocity[i] += (targetVelocity.Value - speed) * (weights[i] / sum);
        }
    }

    public Vector2 Warp(Vector2 position)
    {
        if (!IsActive) return position;
        Span<float> weights = stackalloc float[16];
        Weights(Vector2.Clamp((position - _position) / _size, Vector2.Zero, Vector2.One), weights);
        Vector2 displacement = Vector2.Zero;
        for (int i = 0; i < 16; ++i) displacement += weights[i] * _displayOffset[i];
        return position + displacement;
    }

    private bool HasPositiveJacobian()
    {
        Vector2 h = _size / 8;
        for (int y = 0; y < 8; ++y)
        for (int x = 0; x < 8; ++x)
        {
            Vector2 p = _position + new Vector2(x * h.X, y * h.Y);
            Vector2 a = Warp(p), b = Warp(p + new Vector2(h.X, 0)), c = Warp(p + new Vector2(0, h.Y));
            if (Cross(b - a, c - a) < h.X * h.Y * 0.25f) return false;
        }
        return true;
    }

    private static void Weights(Vector2 uv, Span<float> weights)
    {
        float u = uv.X, v = uv.Y, a = 1 - u, b = 1 - v;
        Span<float> x = stackalloc float[] { a * a * a, 3 * u * a * a, 3 * u * u * a, u * u * u };
        Span<float> y = stackalloc float[] { b * b * b, 3 * v * b * b, 3 * v * v * b, v * v * v };
        for (int j = 0; j < 4; ++j) for (int i = 0; i < 4; ++i) weights[j * 4 + i] = x[i] * y[j];
    }

    public static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
