using System.Collections.Generic;
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

        state.LockState = controller.LockState;
        state.LockProgress = controller.LockProgress;
        if (controller.HasTarget)
        {
            state.BearingDeg = controller.CurrentBearingDeg;
            state.ElevationDeg = controller.CurrentElevationDeg;
            state.TargetName = controller.TargetName;
            state.TargetClass = controller.TargetClass;
            state.TargetAlliance = controller.TargetAlliance;
            state.TargetRangeM = Mathf.RoundToInt(controller.CurrentRangeM);
        }
        else
        {
            state.BearingDeg = null;
            state.ElevationDeg = null;
            state.TargetName = null;
            state.TargetClass = null;
            state.TargetAlliance = null;
            state.TargetRangeM = null;
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
}
