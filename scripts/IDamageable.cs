using Godot;

namespace ColdOrbit.SimCore;

public interface IDamageable
{
    // Applies weapon damage. impulseEquivalentN feeds the same
    // damage_hp = impulse / DamageScaleN formula used for collisions (batch 19).
    // nonLethal routes damage per the disable-only non-lethal spec — implementer
    // decides how to honor it.
    void ApplyWeaponDamage(float impulseEquivalentN, Vector3 hitPointGlobal, bool nonLethal);
}
