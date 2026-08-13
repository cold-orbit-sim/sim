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

    private static readonly string[] BridgeNodes = { "bridge", "bridge_dome", "bridge_viewport" };

    // One material per glow overlay; updated each _Process frame.
    private readonly List<StandardMaterial3D> _glowMaterials = new();

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
        if (_glowMaterials.Count == 0) return;
        float temp = SimBus.Instance?.Propulsion.EngineTemp ?? 0f;
        Color glow = GlowColor(temp);
        foreach (var mat in _glowMaterials)
            mat.AlbedoColor = glow;
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
