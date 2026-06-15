namespace WinIsland;

/// <summary>A simple damped spring integrator for one scalar value.</summary>
internal sealed class Spring
{
    public double Value;
    public double Target;
    private double _vel;

    private readonly double _stiffness;
    private readonly double _damping;

    public Spring(double stiffness = 200, double damping = 24, double initial = 0)
    {
        _stiffness = stiffness;
        _damping = damping;
        Value = initial;
        Target = initial;
    }

    /// <summary>Advance the spring. Returns true while still moving.</summary>
    public bool Step(double dt)
    {
        double force = -_stiffness * (Value - Target) - _damping * _vel;
        _vel += force * dt;
        Value += _vel * dt;

        if (Math.Abs(Value - Target) < 0.0008 && Math.Abs(_vel) < 0.0008)
        {
            Value = Target;
            _vel = 0;
            return false;
        }
        return true;
    }
}
