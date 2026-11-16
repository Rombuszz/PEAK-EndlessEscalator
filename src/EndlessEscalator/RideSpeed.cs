using System;

namespace EndlessEscalator;

internal sealed class RideSpeed
{
    // Used until an unobstructed ride provides a measured metres/second per force unit.
    private float perForce = 1.4f / 20f;

    internal float AtForce(float force) => float.IsNaN(force) || float.IsInfinity(force)
        ? 0 : perForce * Math.Abs(force);

    internal void Observe(float speed, float force, float deltaTime)
    {
        force = Math.Abs(force);
        if (force <= 0 || float.IsNaN(force) || float.IsInfinity(force) ||
            float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0 ||
            deltaTime <= 0 || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return;
        float ratio = speed / force;
        // Ignore a blocked rider and one-off impulses; retain the last usable calibration.
        if (ratio < 0.01f || ratio > 0.2f) return;
        perForce += (ratio - perForce) * (1f - (float)Math.Exp(-deltaTime / 0.4f));
    }
}
