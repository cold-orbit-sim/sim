using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ColdOrbit.SimCore;

// Hides crewed-bridge geometry that conflicts with the drone-ship premise,
// and applies the model-axis rotation to align GLB +X → Godot −Z (forward).
// Also manages hull-glow overlays driven by ship temperature (atmospheric
// friction + engine heat) — each visible MeshInstance3D in the GLB gets a
// slightly scaled-up sibling with an additive unshaded material.
public partial class ShipMesh : Node3D
{
    // Rotate 90° around Y so the model's +X fore aligns with Godot's −Z forward.
    // Tunable in the editor without a rebuild.
    [Export] public Vector3 ModelRotationDeg { get; set; } = new Vector3(0, 90, 0);

    [Export] public bool ApplyHullShader { get; set; } = true;
    [Export] public bool ApplyEngineExhaust { get; set; } = true;

    private static readonly string[] BridgeNodes = { "bridge", "bridge_dome", "bridge_viewport" };

    // One material per glow overlay; updated each _Process frame.
    private readonly List<StandardMaterial3D> _glowMaterials = new();

    // One entry per nozzle's duplicated engine_glow material, each with its own
    // smoothed brightness so a differential turn (see EngineExhaust) makes only
    // the firing nozzle glow, not all three uniformly.
    private sealed class EngineGlowEntry
    {
        public BaseMaterial3D Material;
        public EngineSide Side;
        public float Energy;
    }
    private readonly List<EngineGlowEntry> _engineGlowEntries = new();

    private TurretController _turretDorsal;
    private TurretController _turretVentral;

    // How fast the nozzle glow eases toward its target brightness, in 1/s. Shared by
    // spool-up and the fade-to-off on disable, so cutting propulsion reads as the
    // glow dying down naturally rather than snapping off.
    [Export] public float EngineGlowResponseRate = 2.5f;

    public override void _Ready()
    {
        RotationDegrees = ModelRotationDeg;

        foreach (var nodeName in BridgeNodes)
        {
            var node = FindChild(nodeName, owned: false);
            if (node is Node3D n3d)
                n3d.Visible = false;
            else
                GD.PushWarning($"ShipMesh: could not find bridge node '{nodeName}' to hide");
        }

        // Turret controllers FIRST: they reparent the ring/mantle/barrel meshes
        // under new pivot nodes. BuildGlowOverlays clones each mesh as a sibling
        // for the temperature glow, so it has to run after the reparenting —
        // otherwise the clones are created under the old (non-rotating) parent
        // and stay behind as static ghost turrets when the real one traverses.
        ApplyTurretControllers();

        BuildGlowOverlays();
        ApplyWeatheredHull();
        ApplyEngineExhaustEffects();
    }

    // Spawns a TurretController pivot for each turret rig found in the GLB.
    // See TurretController's class doc for why it reparents the ring/mantle/
    // barrels instead of attaching a script to the GLB-imported root node.
    private void ApplyTurretControllers()
    {
        _turretDorsal = AttachTurretController("turret_dorsal_fwd", "dorsal");
        _turretVentral = AttachTurretController("turret_ventral_fwd", "ventral");
    }

    private TurretController AttachTurretController(string nodeName, string turretId)
    {
        var turretRoot = FindChild(nodeName, recursive: true, owned: false) as Node3D;
        if (turretRoot == null)
        {
            GD.PrintErr($"ShipMesh: turret root '{nodeName}' not found — turret controller not attached");
            return null;
        }

        var ring = turretRoot.FindChild($"{nodeName}_ring", recursive: false, owned: false) as Node3D;
        var mantle = turretRoot.FindChild($"{nodeName}_mantle", recursive: false, owned: false) as Node3D;
        var barrels = new List<Node3D>();
        foreach (Node child in turretRoot.GetChildren())
            if (child is Node3D n3d && child.Name.ToString().Contains("_barrel"))
                barrels.Add(n3d);

        if (ring == null || mantle == null || barrels.Count < 2)
        {
            GD.PrintErr($"ShipMesh: turret '{nodeName}' missing ring, mantle, or barrels — controller not attached");
            return null;
        }

        var controller = new TurretController { Name = $"{turretId}_TurretController", TurretId = turretId };
        turretRoot.AddChild(controller);
        controller.GlobalTransform = turretRoot.GlobalTransform;
        // `this` is the hull reference frame for reported bearing/elevation —
        // ShipMesh's local space is the GLB model frame (+X nose, +Y up,
        // +Z starboard). See TurretController.CurrentBearingDeg.
        controller.Setup(this, ring, mantle, barrels[0], barrels[1]);
        return controller;
    }

    private void ApplyWeatheredHull()
    {
        if (!ApplyHullShader) return;

        var shaderRes = GD.Load<Shader>("res://shaders/ship_hull.gdshader");
        if (shaderRes == null)
        {
            GD.PrintErr("ShipMesh: ship_hull.gdshader not found");
            return;
        }

        ShaderMaterial MakeMat(
            Color baseCol, Color rustCol, Color grimeCol, Color scratchCol,
            float rustCov, float grimeInt, float scratchInt, float uvScale,
            float metalBase, float metalScratch, float roughBase, float roughScratch,
            float normalStr)
        {
            var m = new ShaderMaterial { Shader = shaderRes };
            m.SetShaderParameter("base_color",        baseCol);
            m.SetShaderParameter("rust_color",        rustCol);
            m.SetShaderParameter("grime_color",       grimeCol);
            m.SetShaderParameter("scratch_color",     scratchCol);
            m.SetShaderParameter("rust_coverage",     rustCov);
            m.SetShaderParameter("grime_intensity",   grimeInt);
            m.SetShaderParameter("scratch_intensity", scratchInt);
            m.SetShaderParameter("uv_scale",          uvScale);
            m.SetShaderParameter("metallic_base",     metalBase);
            m.SetShaderParameter("metallic_scratch",  metalScratch);
            m.SetShaderParameter("roughness_base",    roughBase);
            m.SetShaderParameter("roughness_scratch", roughScratch);
            m.SetShaderParameter("normal_strength",   normalStr);
            return m;
        }

        var matHullPlate = MakeMat(
            new Color(0.18f, 0.11f, 0.06f), new Color(0.52f, 0.22f, 0.06f),
            new Color(0.07f, 0.05f, 0.03f), new Color(0.30f, 0.28f, 0.26f),
            0.45f, 0.35f, 0.55f, 3.0f,
            0.15f, 0.60f, 0.88f, 0.55f, 0.4f);

        var matHullDark = MakeMat(
            new Color(0.08f, 0.05f, 0.03f), new Color(0.40f, 0.16f, 0.04f),
            new Color(0.04f, 0.03f, 0.02f), new Color(0.22f, 0.20f, 0.18f),
            0.30f, 0.55f, 0.35f, 3.5f,
            0.10f, 0.45f, 0.92f, 0.60f, 0.3f);

        var matTrimMetal = MakeMat(
            new Color(0.22f, 0.15f, 0.09f), new Color(0.45f, 0.18f, 0.05f),
            new Color(0.07f, 0.05f, 0.03f), new Color(0.42f, 0.40f, 0.38f),
            0.25f, 0.20f, 0.70f, 4.0f,
            0.28f, 0.72f, 0.80f, 0.42f, 0.3f);

        var matEtchLine = MakeMat(
            new Color(0.06f, 0.04f, 0.02f), new Color(0.22f, 0.09f, 0.02f),
            new Color(0.04f, 0.03f, 0.01f), new Color(0.15f, 0.14f, 0.13f),
            0.15f, 0.40f, 0.20f, 2.0f,
            0.08f, 0.30f, 0.95f, 0.75f, 0.2f);

        var matFadedStripe = MakeMat(
            new Color(0.28f, 0.10f, 0.05f), new Color(0.48f, 0.18f, 0.05f),
            new Color(0.07f, 0.05f, 0.03f), new Color(0.32f, 0.28f, 0.24f),
            0.30f, 0.30f, 0.60f, 3.0f,
            0.12f, 0.55f, 0.90f, 0.58f, 0.35f);

        ApplyHullRecursive(this, matHullPlate, matHullDark, matTrimMetal, matEtchLine, matFadedStripe);
    }

    private void ApplyHullRecursive(
        Node node,
        ShaderMaterial hull, ShaderMaterial dark, ShaderMaterial trim,
        ShaderMaterial etch, ShaderMaterial stripe)
    {
        if (node is MeshInstance3D mi && mi.Mesh is ArrayMesh am)
        {
            int surfCount = am.GetSurfaceCount();
            for (int s = 0; s < surfCount; s++)
            {
                string surfName = am.SurfaceGetName(s);
                if (surfName == "engine_glow") continue;

                ShaderMaterial mat = surfName switch
                {
                    "hull_dark"    => dark,
                    "trim_metal"   => trim,
                    "etch_line"    => etch,
                    "faded_stripe" => stripe,
                    _              => hull,
                };
                mi.SetSurfaceOverrideMaterial(s, mat);
            }
        }

        foreach (Node child in node.GetChildren())
            ApplyHullRecursive(child, hull, dark, trim, etch, stripe);
    }

    // Spawns an EngineExhaust node (particles) at each engine_glow nozzle surface,
    // and gives each nozzle its own mutable emissive material so brightness can be
    // driven per-frame without touching the shared GLB resource.
    private void ApplyEngineExhaustEffects()
    {
        if (!ApplyEngineExhaust) return;

        var nozzles = new List<(Transform3D xf, EngineSide side)>();
        FindEngineGlowSurfaces(this, nozzles);

        if (nozzles.Count == 0)
        {
            GD.PrintErr("ShipMesh: no engine_glow surfaces found — exhaust effects not spawned");
            return;
        }

        // engine_core's mesh geometry is a flat disc (x/z span ~2.7, y span only 0.6,
        // confirmed by inspecting the GLB's raw vertex data) — its face normal, i.e.
        // the actual exhaust direction, is the node's local +Y axis, not +Z. EngineExhaust
        // assumes its parent's local +Z is aft (true at the ship level per batch 17's
        // invariant), so remap nozzle-local +Y onto exhaust-local +Z here: Rx(-90) sends
        // local Z -> +Y, so composing xf with its inverse (Rx(+90) is its own use here as
        // the *source* axis, applied before xf) makes the exhaust's +Z point where the
        // nozzle's +Y actually points. (Root cause of the "sideways particles" bug in
        // batch 22 — using the nozzle's Z axis directly pointed the plume 90° off, since
        // the nozzle disc's real facing direction was always Y.)
        var nozzleYToExhaustZ = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(-90)), Vector3.Zero);

        foreach (var (xf, side) in nozzles)
        {
            var exhaust = new EngineExhaust { Side = side };
            AddChild(exhaust);
            exhaust.GlobalTransform = xf * nozzleYToExhaustZ;
        }
    }

    // Only "engine_core"-named nodes carry a real nozzle in cruiser.glb. The GLB
    // also reuses the "engine_glow" material name on bridge_viewport's glass (a
    // quirk in the source asset, confirmed by inspecting the GLB JSON directly) —
    // matching by material name alone would spawn a spurious plume at the bridge
    // dome, so bridge nodes are excluded explicitly (reusing BridgeNodes).
    private void FindEngineGlowSurfaces(Node node, List<(Transform3D xf, EngineSide side)> nozzles)
    {
        if (System.Array.IndexOf(BridgeNodes, node.Name.ToString()) >= 0)
            return;

        if (node is MeshInstance3D mi && mi.Mesh is ArrayMesh am)
        {
            for (int s = 0; s < am.GetSurfaceCount(); s++)
            {
                if (am.SurfaceGetName(s) != "engine_glow") continue;

                // Duplicate before mutating — a GLB-imported material is a shared
                // resource; setting properties on it directly would affect every
                // instance of this mesh, not just this ship (same caution as the hull
                // weathering pass above).
                // Side (Left/Center/Right) determined from the nozzle's position in
                // ShipMesh's own local frame — z<0 = Left, z>0 = Right, per the raw
                // GLB node translations (engine_core z=-4, engine_core2 z=0,
                // engine_core3 z=+4) and a standard right-handed forward=+X/up=+Y
                // convention (right = forward × up = +Z). UNVERIFIED in-editor —
                // confirm left/right actually match visually.
                Vector3 localPos = GlobalTransform.AffineInverse() * mi.GlobalPosition;
                EngineSide side = localPos.Z < -0.5f ? EngineSide.Left
                                 : localPos.Z > 0.5f  ? EngineSide.Right
                                 : EngineSide.Center;

                var mat = mi.GetActiveMaterial(s);
                if (mat is BaseMaterial3D std)
                {
                    var unique = (BaseMaterial3D)std.Duplicate();
                    // Set emission ourselves rather than trusting the GLB's
                    // KHR_materials_emissive_strength import to have come through —
                    // makes the throttle-driven glow self-contained either way.
                    unique.EmissionEnabled = true;
                    unique.Emission = new Color(1.0f, 0.45f, 0.12f);
                    // Disable culling: if this disc's winding puts its "front" face
                    // into the hull rather than outward (plausible given how the node's
                    // own baked rotation already turned out backwards once before — see
                    // the nozzle-orientation fix above), a camera behind the ship would
                    // be looking at the culled back face and see nothing at all, which
                    // would look identical to "the glow isn't rendering" regardless of
                    // any emission setting. Costs nothing on three small discs.
                    unique.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                    mi.SetSurfaceOverrideMaterial(s, unique);
                    _engineGlowEntries.Add(new EngineGlowEntry { Material = unique, Side = side });
                }
                else
                {
                    GD.PrintErr($"ShipMesh: engine_glow surface material is {mat?.GetType().Name ?? "null"}, not a BaseMaterial3D — nozzle glow not wired for this surface");
                }

                nozzles.Add((mi.GlobalTransform, side));
            }
        }

        foreach (Node child in node.GetChildren())
            FindEngineGlowSurfaces(child, nozzles);
    }

    // Creates one additive glow MeshInstance3D (sibling) per visible GLB mesh.
    // Each overlay uses the same Mesh but is scaled up by 1.002× so it
    // wraps the hull without z-fighting.
    private void BuildGlowOverlays()
    {
        var meshNodes = FindChildren("*", "MeshInstance3D", recursive: true, owned: false);
        foreach (var node in meshNodes)
        {
            if (node is not MeshInstance3D src || !src.Visible || src.Mesh == null)
                continue;

            // engine_core nozzles already get a dedicated throttle-driven glow material
            // (see FindEngineGlowSurfaces below) — skip them here so the temperature
            // overlay doesn't layer a second, conflicting glow on top of it.
            if (src.Mesh is ArrayMesh srcAm && HasSurfaceNamed(srcAm, "engine_glow"))
                continue;

            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BlendMode   = BaseMaterial3D.BlendModeEnum.Add,
                // BlendMode only takes effect in the transparent pass — without this,
                // the overlay renders as solid opaque black and occludes whatever it's
                // wrapped around. Confirmed as the cause of the engine nozzle glow being
                // completely invisible: this 1.002x-scaled opaque black disc sat directly
                // in front of engine_core's actual emissive surface.
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode    = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = Colors.Black,
            };

            var glow = new MeshInstance3D
            {
                Mesh      = src.Mesh,
                Transform = src.Transform,
                Scale     = src.Scale * 1.002f,
            };
            glow.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            glow.SetSurfaceOverrideMaterial(0, mat);

            src.GetParent().AddChild(glow);
            _glowMaterials.Add(mat);
        }
    }

    private static bool HasSurfaceNamed(ArrayMesh mesh, string name)
    {
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            if (mesh.SurfaceGetName(s) == name)
                return true;
        return false;
    }

    public override void _Process(double delta)
    {
        if (_glowMaterials.Count > 0)
        {
            float temp = SimBus.Instance?.Propulsion.EngineTemp ?? 0f;
            Color glow = GlowColor(temp);
            foreach (var mat in _glowMaterials)
                mat.AlbedoColor = glow;
        }

        if (_engineGlowEntries.Count > 0)
        {
            var prop = SimBus.Instance?.Propulsion;
            bool running = prop != null && !prop.IsPropulsionDisabled;
            // Same firing gate as EngineExhaust._Process: nothing during reverse, only
            // the opposite-side nozzle during an active turn, so the glow matches which
            // nozzle is actually shown firing rather than all three brightening uniformly.
            bool fires = running && prop != null && !prop.ReverseEnabled;
            bool yawing = fires && (prop.YawLeftActive || prop.YawRightActive);

            float k = 1f - Mathf.Exp(-EngineGlowResponseRate * (float)delta);

            foreach (var entry in _engineGlowEntries)
            {
                bool entryFires = fires;
                float throttle = fires ? prop.ThrottleInput : 0f;
                if (yawing)
                {
                    entryFires = (prop.YawLeftActive && entry.Side == EngineSide.Right)
                              || (prop.YawRightActive && entry.Side == EngineSide.Left);
                    if (entryFires) throttle = Mathf.Max(throttle, 0.6f);
                }

                // Slight orange glow at idle, brightening toward the throttle-driven peak;
                // zero (fully off) is the target when not firing — the smoothing below
                // turns that into a natural fade rather than a hard cut. NOTE: "unarmed"
                // per user feedback maps to IsPropulsionDisabled — Propulsion has no
                // separate Armed flag (see EngineExhaust.cs / batch 22 handback).
                // Idle floor and peak both raised well past "slight" as a visibility
                // sanity check — three previous structural fixes (transparency, material
                // cast, culling) haven't resolved this, so ruling out plain dimness
                // against the near-black space background before hunting further.
                float targetEnergy = entryFires ? Mathf.Lerp(2.5f, 9.0f, throttle) : 0f;
                entry.Energy = Mathf.Lerp(entry.Energy, targetEnergy, k);
                entry.Material.EmissionEnergyMultiplier = entry.Energy;
            }
        }

        SyncTurretWithSimBus(_turretDorsal, SimBus.Instance?.TurretDorsal);
        SyncTurretWithSimBus(_turretVentral, SimBus.Instance?.TurretVentral);

        if (SimBus.Instance != null)
        {
            SyncMissileWithSimBus(SimBus.Instance.MissileForePort,      (float)delta);
            SyncMissileWithSimBus(SimBus.Instance.MissileForeStarboard, (float)delta);
            SyncMissileWithSimBus(SimBus.Instance.MissileAftPort,       (float)delta);
            SyncMissileWithSimBus(SimBus.Instance.MissileAftStarboard,  (float)delta);
        }
    }

    // Two-way bridge between a TurretController (pure mechanism, no SimBus
    // knowledge) and its SimBus state: commands in, telemetry out.
    private static void SyncTurretWithSimBus(TurretController controller, SimBus.TurretState state)
    {
        if (controller == null || state == null) return;

        controller.Armed = state.Armed;

        if (state.PendingTargetCycle != 0)
        {
            controller.CycleTarget(state.PendingTargetCycle);
            state.PendingTargetCycle = 0;
        }

        // Fire control commands (batch 26): FiringRequested is held state (mirrored
        // every frame, not consumed); PendingReloadRequest is one-shot like
        // PendingTargetCycle above.
        controller.SetFiring(state.FiringRequested);
        if (state.PendingReloadRequest)
        {
            controller.StartReload();
            state.PendingReloadRequest = false;
        }

        if (state.PendingAmmoOverride.Count > 0)
        {
            foreach (var kv in state.PendingAmmoOverride)
                if (controller.AmmoMaxCapacity.TryGetValue(kv.Key, out int cap))
                    controller.AmmoRemaining[kv.Key] = Mathf.Clamp(kv.Value, 0, cap);
            state.PendingAmmoOverride.Clear();
        }

        state.LockState = controller.LockState;
        state.LockProgress = controller.LockProgress;
        state.Overheated = controller.Overheated;
        state.Heat = controller.Heat;
        state.Reloading = controller.Reloading;
        state.ReloadProgress = controller.ReloadProgress;
        state.AmmoLoaded = controller.AmmoLoaded;
        state.AmmoRemaining.Clear();
        foreach (var kv in controller.AmmoRemaining) state.AmmoRemaining[kv.Key] = kv.Value;
        if (state.AmmoMaxCapacity.Count == 0) // capacities never change post-init — copy once
            foreach (var kv in controller.AmmoMaxCapacity) state.AmmoMaxCapacity[kv.Key] = kv.Value;

        if (controller.HasTarget)
        {
            state.BearingDeg = controller.CurrentBearingDeg;
            state.ElevationDeg = controller.CurrentElevationDeg;
            state.TargetName = controller.TargetName;
            state.TargetClass = controller.TargetClass;
            state.TargetAlliance = controller.TargetAlliance;
            state.TargetRangeM = Mathf.RoundToInt(controller.CurrentRangeM);
            state.TargetVelocityMs = controller.CurrentTargetVelocityMs;
        }
        else
        {
            state.BearingDeg = null;
            state.ElevationDeg = null;
            state.TargetName = null;
            state.TargetClass = null;
            state.TargetAlliance = null;
            state.TargetRangeM = null;
            state.TargetVelocityMs = null;
        }
    }

    // Called by CameraController when switching views. Hides the hull on
    // internal (fore/aft) views so the camera doesn't clip through geometry.
    public void SetHullVisible(bool visible) => Visible = visible;

    // Maps ship temperature to an additive glow colour. With BlendMode=Add,
    // black is invisible (0 contribution) and brighter colours burn through.
    // Keyframes are: 400°C (barely visible red) → 600°C (orange-red) →
    // 750°C (bright orange) → 900°C (orange-yellow) → 1000°C (yellow-white).
    private static Color GlowColor(float tempC)
    {
        if (tempC < 400f) return Colors.Black;

        var c400  = new Color(0.08f, 0.01f, 0.00f);
        var c600  = new Color(0.25f, 0.05f, 0.00f);
        var c750  = new Color(0.45f, 0.12f, 0.00f);
        var c900  = new Color(0.70f, 0.25f, 0.02f);
        var c1000 = new Color(1.00f, 0.50f, 0.10f);

        if (tempC < 600f) return c400.Lerp(c600,  Mathf.InverseLerp(400f, 600f,  tempC));
        if (tempC < 750f) return c600.Lerp(c750,  Mathf.InverseLerp(600f, 750f,  tempC));
        if (tempC < 900f) return c750.Lerp(c900,  Mathf.InverseLerp(750f, 900f,  tempC));
        return c900.Lerp(c1000, Mathf.Clamp(Mathf.InverseLerp(900f, 1000f, tempC), 0f, 1f));
    }

    // ── Missile system ────────────────────────────────────────────────────────

    // Per-type specs: impulse (N), turn rate (°/s), non-lethal flag.
    // Impulse formula: damage_hp = impulseN / DamageScaleN (50 000) → same path as turret ammo.
    // EMP Burst is non-lethal: bypasses hull's 60% share, hits subsystems only, reactor excluded.
    // Fragmentation blast radius is wider (future splash) but per-hit damage is lower.
    // Armour Piercing hits harder; "piercing" framing is thin until an armour system exists.
    // All values are first-pass tuning, not final balance.
    private static readonly Dictionary<string, (float impulseN, float turnRateDegPerSec, bool nonLethal)> MissileTypeTable = new()
    {
        ["Seeking"]          = (200_000f, 120f, false),
        ["EMP Burst"]        = (150_000f,  90f, true),
        ["Fragmentation"]    = (100_000f, 100f, false),
        ["Armour Piercing"]  = (400_000f, 130f, false),
    };

    // Tube positions in PlayerShip local space (−Z = forward, +X = starboard, +Y = up).
    // Approximate — tune in editor once hull geometry is measured.
    private static readonly Dictionary<string, Vector3> TubeLocalOffsets = new()
    {
        ["fore_port"]       = new Vector3(-1f, 0f, -4f),
        ["fore_starboard"]  = new Vector3( 1f, 0f, -4f),
        ["aft_port"]        = new Vector3(-1f, 0f,  4f),
        ["aft_starboard"]   = new Vector3( 1f, 0f,  4f),
    };

    // Updates one missile tube's state machine each frame and spawns missiles on fire.
    // Handles: type advance, load/reload, target cycle, lock acquisition timer, fire.
    private void SyncMissileWithSimBus(SimBus.MissileState state, float dt)
    {
        if (state == null) return;

        bool changed = false;

        // Type advance: armed only (prevents mid-flight type change, though tube is
        // empty after fire anyway).
        if (state.PendingTypeAdvance)
        {
            state.PendingTypeAdvance = false;
            if (state.Armed)
            {
                state.AdvanceType();
                // Type change always requires a rearm cycle regardless of current status.
                state.Status = "loading";
                state.LoadTimer = 0f;
                state.LockState = TurretLockState.None;
                state.LockProgress = 0f;
                changed = true;
            }
        }

        // Loading timer: ticks while tube is being reloaded after a type change.
        if (state.Status == "loading")
        {
            state.LoadTimer += dt;
            if (state.LoadTimer >= SimBus.MissileState.LoadDurationS)
            {
                state.Status = "loaded";
                state.LoadTimer = 0f;
                changed = true;
            }
        }

        // Load / reload: tube goes from empty → loading (same timer as type change).
        // No auto-reload; player must press Load each time.
        if (state.PendingLoad)
        {
            state.PendingLoad = false;
            if (state.Armed && state.Status == "empty")
            {
                state.Status = "loading";
                state.LoadTimer = 0f;
                changed = true;
            }
        }

        // Target cycle: scene-tree lookup, same group as turrets.
        if (state.PendingTargetCycle != 0)
        {
            int dir = state.PendingTargetCycle;
            state.PendingTargetCycle = 0;
            CycleMissileTarget(state, dir);
            state.LockState = TurretLockState.None;
            state.LockTimer = 0f;
            state.LockProgress = 0f;
            changed = true;
        }

        // Lock initiation: starts acquisition timer. Must be armed, loaded, and have
        // a valid target. Pressing Lock while already acquiring or locked is a no-op.
        if (state.PendingLock)
        {
            state.PendingLock = false;
            if (state.Armed && state.Status == "loaded"
                && state.SelectedTarget != null && IsInstanceValid(state.SelectedTarget)
                && state.LockState == TurretLockState.None)
            {
                state.LockState = TurretLockState.Acquiring;
                state.LockTimer = 0f;
                changed = true;
            }
            else
            {
                GD.Print($"[missile {state.TubeId}] lock rejected — armed={state.Armed} " +
                    $"status={state.Status} target={state.SelectedTarget?.Name ?? "null"} " +
                    $"lockState={state.LockState}");
            }
        }

        // Acquisition timer.
        if (state.LockState == TurretLockState.Acquiring)
        {
            if (state.SelectedTarget == null || !IsInstanceValid(state.SelectedTarget))
            {
                state.LockState = TurretLockState.None;
                state.LockTimer = 0f;
                changed = true;
            }
            else
            {
                state.LockTimer += dt;
                if (state.LockTimer >= SimBus.MissileState.LockDurationS)
                {
                    state.LockState = TurretLockState.Locked;
                    changed = true;
                }
            }
        }

        state.LockProgress = state.LockState switch
        {
            TurretLockState.Locked    => 1f,
            TurretLockState.Acquiring => Mathf.Clamp(state.LockTimer / SimBus.MissileState.LockDurationS, 0f, 1f),
            _                         => 0f,
        };

        // Update target telemetry from the live Node3D reference.
        if (state.SelectedTarget != null && IsInstanceValid(state.SelectedTarget))
        {
            var t = state.SelectedTarget as Target;
            state.TargetName    = t?.TargetDisplayName ?? state.SelectedTarget.Name;
            state.TargetClass   = t?.TargetClass;
            state.TargetAlliance = t?.TargetAlliance;
            state.TargetRangeM  = Mathf.RoundToInt(GlobalPosition.DistanceTo(state.SelectedTarget.GlobalPosition));
        }
        else if (state.SelectedTarget != null)
        {
            // Target node was freed.
            state.SelectedTarget = null;
            state.TargetName = state.TargetClass = state.TargetAlliance = null;
            state.TargetRangeM = null;
            if (state.LockState != TurretLockState.None)
            {
                state.LockState = TurretLockState.None;
                state.LockTimer = 0f;
                state.LockProgress = 0f;
            }
            changed = true;
        }

        // Fire: must be armed, loaded, and locked.
        if (state.PendingFire)
        {
            state.PendingFire = false;
            if (state.Armed && state.Status == "loaded" && state.LockState == TurretLockState.Locked)
            {
                LaunchMissile(state);
                state.Status = "empty";
                state.LockState = TurretLockState.None;
                state.LockTimer = 0f;
                state.LockProgress = 0f;
                changed = true;
            }
        }

        if (changed) SimBus.Instance.PublishMissileState(state);
    }

    // Cycles to the next/previous member of the "lockable_targets" group for a
    // missile tube. Same group and ordering as TurretController.CycleTarget.
    private void CycleMissileTarget(SimBus.MissileState state, int direction)
    {
        var targets = GetTree().GetNodesInGroup("lockable_targets")
            .OfType<Node3D>().OrderBy(n => n.Name.ToString()).ToList();
        if (targets.Count == 0) return;

        int cur = state.SelectedTarget != null ? targets.IndexOf(state.SelectedTarget) : -1;
        int next = cur < 0
            ? (direction > 0 ? 0 : targets.Count - 1)
            : (cur + direction + targets.Count) % targets.Count;
        state.SelectedTarget = targets[next];
    }

    // Spawns a Missile node and launches it from the tube's approximate position.
    private void LaunchMissile(SimBus.MissileState state)
    {
        if (!MissileTypeTable.TryGetValue(state.MissileType, out var spec))
        {
            GD.PrintErr($"ShipMesh: unknown missile type '{state.MissileType}' — launch aborted");
            return;
        }

        var ship = SimBus.Instance?.PlayerShipNode;
        if (ship == null) { GD.PrintErr("ShipMesh: PlayerShipNode null — missile launch aborted"); return; }

        TubeLocalOffsets.TryGetValue(state.TubeId, out Vector3 localOffset);
        Vector3 launchPos = ship.GlobalTransform * localOffset;

        // Aft tubes kick backward, fore tubes kick forward — missile steers toward target.
        bool isAft = state.TubeId.StartsWith("aft");
        Vector3 kickDir = isAft ? ship.GlobalTransform.Basis.Z : -ship.GlobalTransform.Basis.Z;
        Vector3 initialVel = ship.LinearVelocity + kickDir * 50f;

        var missile = new Missile
        {
            ThrustMs2          = 200f,
            MaxSpeedMs         = 400f,
            TurnRateDegPerSec  = spec.turnRateDegPerSec,
            ImpulseEquivalentN = spec.impulseN,
            NonLethal          = spec.nonLethal,
        };
        GetTree().CurrentScene.AddChild(missile);
        missile.Launch(launchPos, initialVel, state.SelectedTarget);
    }
}
