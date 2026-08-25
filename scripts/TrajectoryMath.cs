using Godot;

namespace ColdOrbit.SimCore;

public static class TrajectoryMath
{
    // Solves for the intercept point: where a projectile fired now, at speed
    // projectileSpeed from shooterPos, would meet a target currently at
    // targetPos moving at targetVelocity (assumed constant for the flight).
    // Returns null if no valid positive-time solution exists (e.g. target
    // outrunning the projectile) — caller should fall back to aiming at
    // the target's raw current position in that case.
    public static Vector3? SolveIntercept(Vector3 shooterPos, Vector3 targetPos, Vector3 targetVelocity, float projectileSpeed)
    {
        Vector3 toTarget = targetPos - shooterPos;
        // |toTarget + targetVelocity * t| = projectileSpeed * t
        // Expand to a t^2 + b t + c = 0
        float a = targetVelocity.LengthSquared() - projectileSpeed * projectileSpeed;
        float b = 2f * toTarget.Dot(targetVelocity);
        float c = toTarget.LengthSquared();
        float t;
        if (Mathf.Abs(a) < 0.0001f)
        {
            // Degenerate case: target speed ≈ projectile speed. Linear instead of quadratic.
            if (Mathf.Abs(b) < 0.0001f) return null;
            t = -c / b;
        }
        else
        {
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f) return null; // no real solution — target unreachable
            float sqrtDisc = Mathf.Sqrt(discriminant);
            float t1 = (-b + sqrtDisc) / (2f * a);
            float t2 = (-b - sqrtDisc) / (2f * a);
            // Take the smallest positive root.
            if (t1 > 0f && t2 > 0f) t = Mathf.Min(t1, t2);
            else if (t1 > 0f) t = t1;
            else if (t2 > 0f) t = t2;
            else return null; // both roots negative — target already unreachable in forward time
        }
        if (t <= 0f || float.IsNaN(t) || float.IsInfinity(t)) return null;
        return targetPos + targetVelocity * t;
    }
}
