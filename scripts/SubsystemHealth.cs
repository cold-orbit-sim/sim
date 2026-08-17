using System;
using System.Collections.Generic;
using Godot;

namespace ColdOrbit.SimCore;

// Mutable health record for one subsystem.
public sealed class SubsystemRecord
{
    private int _health = 100;

    public string Id { get; }
    public int Health
    {
        get => _health;
        set => _health = System.Math.Clamp(value, 0, 100);
    }

    public bool Disabled => _health <= 0;

    // 0.0 at health ≥ 70 (free zone); 1.0 at health 0 (fully degraded).
    public float DegradationFactor => _health >= 70 ? 0f : (70f - _health) / 70f;

    // null for reactor and hull (no power allocation slot in the model).
    public int? PowerAllocatedKW { get; set; }
    public int? PowerMaxKW { get; }

    // Seconds until fully repaired at the current rate; null when rate = 0.
    public float? RepairEtaSeconds { get; set; }

    public SubsystemRecord(string id, int? powerAllocatedKW = null, int? powerMaxKW = null)
    {
        Id = id;
        PowerAllocatedKW = powerAllocatedKW;
        PowerMaxKW = powerMaxKW;
    }
}

// Holds all nine subsystem health records, damage distribution logic, repair
// queue management, and derived multipliers. Accessed only from the Godot
// main thread (PlayerShip._PhysicsProcess and SimBus._Process) — no locks needed.
public sealed class EngineeringState
{
    // ── Subsystems ────────────────────────────────────────────────────────
    public SubsystemRecord Weapons  { get; } = new("weapons",   200, 500);
    public SubsystemRecord Engines  { get; } = new("engines",   200, 500);
    public SubsystemRecord Ftl      { get; } = new("ftl",       200, 500);
    public SubsystemRecord Reactor  { get; } = new("reactor");
    public SubsystemRecord Utility1 { get; } = new("utility_1", 200, 500);
    public SubsystemRecord Utility2 { get; } = new("utility_2", 200, 500);
    public SubsystemRecord Utility3 { get; } = new("utility_3", 200, 500);
    public SubsystemRecord Utility4 { get; } = new("utility_4", 200, 500);
    public SubsystemRecord Hull     { get; } = new("hull");

    // Iteration order matches the §3.1b MQTT publish order.
    public SubsystemRecord[] AllSystems { get; }

    // Ordered repair queue: index-0 is being actively repaired ("in_progress"),
    // all others are "queued". Auto-populated on damage; systems at 100 HP are
    // silently removed each repair tick.
    public List<string> RepairQueue { get; } = new();

    // Set by PlayerShip._Ready so BuildEffects can format the threshold string.
    public float MaxEngineTempC { get; set; } = 900f;

    // ── Derived multipliers ───────────────────────────────────────────────
    // Linear, driven by DegradationFactor d (0 at health ≥ 70, 1 at health 0).
    public float ThrustMultiplier            => 1f - Engines.DegradationFactor;
    public float OverheatThresholdMultiplier => 1f - Engines.DegradationFactor * 0.5f;
    public float FtlChargeRateMultiplier     => 1f - Ftl.DegradationFactor;
    public float ReactorPowerMultiplier      => 1f - Reactor.DegradationFactor * 0.5f;

    // Fractional HP accumulator ensures slow repair rates still heal over time.
    private float _hpAccumulator;

    public EngineeringState()
    {
        AllSystems = new[]
        {
            Weapons, Engines, Ftl, Reactor,
            Utility1, Utility2, Utility3, Utility4,
            Hull,
        };
    }

    public SubsystemRecord GetById(string id) => id switch
    {
        "weapons"   => Weapons,
        "engines"   => Engines,
        "ftl"       => Ftl,
        "reactor"   => Reactor,
        "utility_1" => Utility1,
        "utility_2" => Utility2,
        "utility_3" => Utility3,
        "utility_4" => Utility4,
        "hull"      => Hull,
        _           => throw new ArgumentException($"Unknown subsystem id: {id}"),
    };

    // Apply collision or weapon-hit damage. worldNormal is the contact normal in
    // world space; shipBasis is the ship's GlobalTransform.Basis at impact time.
    // Always returns true (hull always takes ≥ 1 HP on any above-threshold hit).
    public bool ApplyDamage(
        float impulseN, Vector3 worldNormal, Basis shipBasis,
        float damageScaleN, float zoneThreshold)
    {
        int totalDmg = Math.Max(1, Mathf.FloorToInt(impulseN / damageScaleN));
        int hullDmg  = Math.Max(1, Mathf.FloorToInt(totalDmg * 0.6f));
        int subDmg   = totalDmg - hullDmg;

        DamageSubsystem(Hull, hullDmg);

        if (subDmg > 0)
        {
            // Dot contact normal (world space) with ship's world-space Z axis.
            // Z+ is the aft direction in Godot (-Z is forward), so dot > 0 = rear hit.
            float dot = worldNormal.Dot(shipBasis.Z);
            if (dot > zoneThreshold)
            {
                DamageSubsystem(Engines, subDmg);
            }
            else if (dot < -zoneThreshold)
            {
                DamageSubsystem(Weapons, subDmg);
            }
            else
            {
                // Side hit: cross product Y-sign determines port vs starboard.
                // crossY < 0 → port (-X side); crossY ≥ 0 → starboard (+X side).
                float crossY = worldNormal.Cross(shipBasis.Z).Y;
                int half = subDmg / 2;
                if (crossY < 0f)
                {
                    DamageSubsystem(Utility1, half);
                    DamageSubsystem(Utility2, subDmg - half);
                }
                else
                {
                    DamageSubsystem(Utility3, half);
                    DamageSubsystem(Utility4, subDmg - half);
                }
            }
        }

        return true;
    }

    private void DamageSubsystem(SubsystemRecord sys, int hp)
    {
        if (hp <= 0) return;
        sys.Health -= hp;
        if (sys.Health < 100 && !RepairQueue.Contains(sys.Id))
            RepairQueue.Add(sys.Id);
    }

    // Advance repair by dt seconds. Returns true when any health or ETA changed
    // (caller should publish MQTT state when true).
    public bool UpdateRepair(float dt, float reactorOutputKW, float repairKWPerHPPerS)
    {
        // Purge fully-healed systems first.
        bool changed = RepairQueue.RemoveAll(id => GetById(id).Health >= 100) > 0;
        if (RepairQueue.Count == 0) return changed;

        // Headroom = effective reactor output minus all allocated power (hull
        // and reactor excluded — no allocation slots for those systems).
        float powerUsed = Weapons.PowerAllocatedKW.GetValueOrDefault()
                        + Engines.PowerAllocatedKW.GetValueOrDefault()
                        + Ftl.PowerAllocatedKW.GetValueOrDefault()
                        + Utility1.PowerAllocatedKW.GetValueOrDefault()
                        + Utility2.PowerAllocatedKW.GetValueOrDefault()
                        + Utility3.PowerAllocatedKW.GetValueOrDefault()
                        + Utility4.PowerAllocatedKW.GetValueOrDefault();
        float headroom = reactorOutputKW * ReactorPowerMultiplier - powerUsed;
        float ratePerS = headroom > 0f ? headroom / repairKWPerHPPerS : 0f;

        // Update ETA for all queued systems (null when rate = 0).
        foreach (string sysId in RepairQueue)
        {
            var sys = GetById(sysId);
            sys.RepairEtaSeconds = ratePerS > 0f
                ? (100f - sys.Health) / ratePerS
                : (float?)null;
        }

        if (ratePerS <= 0f) return changed;

        // Repair index-0 entry only.
        var target = GetById(RepairQueue[0]);
        _hpAccumulator += ratePerS * dt;
        int hpGain = Mathf.FloorToInt(_hpAccumulator);
        if (hpGain > 0)
        {
            _hpAccumulator -= hpGain;
            int before = target.Health;
            target.Health = Math.Min(100, target.Health + hpGain);
            if (target.Health != before) changed = true;
        }

        return changed;
    }

    // Generate human-readable degradation effect strings for MQTT payload.
    public string[] BuildEffects(SubsystemRecord sys)
    {
        float d = sys.DegradationFactor;
        if (d <= 0f) return Array.Empty<string>();
        int pct = (int)(d * 100f + 0.5f);

        return sys.Id switch
        {
            "engines" => new[]
            {
                $"Max thrust reduced by {pct}%",
                $"Overheat threshold lowered to {(int)(MaxEngineTempC * (1f - d * 0.5f))}°C",
            },
            "ftl"     => new[] { $"Charge rate reduced by {pct}%" },
            "weapons" => new[] { $"Fire rate reduced by {pct}%" },
            "reactor" => new[] { $"Power output at {(int)((1f - d * 0.5f) * 100f + 0.5f)}%" },
            "utility_1" or "utility_2" or "utility_3" or "utility_4"
                      => new[] { $"Intensity max reduced by {pct}%" },
            _         => Array.Empty<string>(),
        };
    }
}
