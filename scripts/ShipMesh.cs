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

    // Duplicated engine_glow materials (one per nozzle), brightened with throttle.
    private readonly List<StandardMaterial3D> _engineGlowMaterials = new();

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

        BuildGlowOverlays();
        ApplyWeatheredHull();
        ApplyEngineExhaustEffects();
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

    // Spawns an EngineExhaust node (plume + ember particles) at each engine_glow
    // nozzle surface, and gives each nozzle its own mutable emissive material so
    // brightness can be driven per-frame without touching the shared GLB resource.
    private void ApplyEngineExhaustEffects()
    {
        if (!ApplyEngineExhaust) return;

        var nozzleTransforms = new List<Transform3D>();
        FindEngineGlowSurfaces(this, nozzleTransforms);

        if (nozzleTransforms.Count == 0)
        {
            GD.PrintErr("ShipMesh: no engine_glow surfaces found — exhaust effects not spawned");
            return;
        }

        foreach (var xf in nozzleTransforms)
        {
            var exhaust = new EngineExhaust();
            AddChild(exhaust);
            exhaust.GlobalTransform = xf;
        }
    }

    // Only "engine_core"-named nodes carry a real nozzle in cruiser.glb. The GLB
    // also reuses the "engine_glow" material name on bridge_viewport's glass (a
    // quirk in the source asset, confirmed by inspecting the GLB JSON directly) —
    // matching by material name alone would spawn a spurious plume at the bridge
    // dome, so bridge nodes are excluded explicitly (reusing BridgeNodes).
    private void FindEngineGlowSurfaces(Node node, List<Transform3D> transforms)
    {
        if (System.Array.IndexOf(BridgeNodes, node.Name.ToString()) >= 0)
            return;

        if (node is MeshInstance3D mi && mi.Mesh is ArrayMesh am)
        {
            for (int s = 0; s < am.GetSurfaceCount(); s++)
            {
                if (am.SurfaceGetName(s) != "engine_glow") continue;

                // Duplicate before mutating — StandardMaterial3D from a GLB import is a
                // shared resource; setting properties on it directly would affect every
                // instance of this mesh, not just this ship (same caution as the hull
                // weathering pass above).
                var mat = mi.GetActiveMaterial(s);
                if (mat is StandardMaterial3D std)
                {
                    var unique = (StandardMaterial3D)std.Duplicate();
                    mi.SetSurfaceOverrideMaterial(s, unique);
                    _engineGlowMaterials.Add(unique);
                }

                // Each engine_glow-surfaced mesh in cruiser.glb (engine_core x3) is its
                // own small node, confirmed against the GLB's node list — but the node
                // carries its own baked local rotation (a -90° Z rotation in the source
                // matrix), independent of the ship's ModelRotationDeg. Using the node's
                // own GlobalTransform (not just its position) is load-bearing: reusing
                // ShipMesh's basis instead dropped that local rotation and sent the
                // plume 90° off (caught in play-test after batch 22).
                transforms.Add(mi.GlobalTransform);
            }
        }

        foreach (Node child in node.GetChildren())
            FindEngineGlowSurfaces(child, transforms);
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

            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BlendMode   = BaseMaterial3D.BlendModeEnum.Add,
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

    public override void _Process(double delta)
    {
        if (_glowMaterials.Count > 0)
        {
            float temp = SimBus.Instance?.Propulsion.EngineTemp ?? 0f;
            Color glow = GlowColor(temp);
            foreach (var mat in _glowMaterials)
                mat.AlbedoColor = glow;
        }

        if (_engineGlowMaterials.Count > 0)
        {
            var prop = SimBus.Instance?.Propulsion;
            float t = (prop != null && !prop.IsPropulsionDisabled) ? prop.ThrottleInput : 0f;
            float energy = Mathf.Lerp(1.0f, 4.5f, t); // 1.0 matches GLB's baked emissiveStrength floor
            foreach (var mat in _engineGlowMaterials)
                mat.EmissionEnergyMultiplier = energy;
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
