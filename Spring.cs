namespace DynaIsland;

/// <summary>
/// Damped spring used for the island's size / corner animations.
/// Slightly under-damped so the island gets the subtle "bounce" Apple's has.
/// </summary>
internal sealed class Spring(double value, double stiffness = 320, double damping = 28)
{
    public double Value { get; private set; } = value;
    public double Velocity { get; private set; }
    public double Target { get; set; } = value;
    public double Stiffness { get; set; } = stiffness;
    public double Damping { get; set; } = damping;

    public bool IsSettled => Math.Abs(Value - Target) < 0.05 && Math.Abs(Velocity) < 0.5;

    public void Step(double dt)
    {
        double force = -Stiffness * (Value - Target) - Damping * Velocity;
        Velocity += force * dt;
        Value += Velocity * dt;
    }

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}
