using Godot;

namespace ColdOrbit.SimCore;

// Scatters simple placeholder debris through a volume around the origin so
// translation is visible via parallax -- a starfield alone can't show that
// since stars are effectively at infinity. Placeholder quality: low-poly
// boxes, not art. Uses a single MultiMesh rather than individual
// MeshInstance3D nodes since instance count can get large.
public partial class DebrisField : MultiMeshInstance3D
{
    [Export] public int DebrisCount { get; set; } = 400;
    [Export] public float MinDistance { get; set; } = 200f;
    [Export] public float MaxDistance { get; set; } = 2000f;
    [Export] public float MinScale { get; set; } = 2f;
    [Export] public float MaxScale { get; set; } = 15f;

    public override void _Ready()
    {
        var mesh = new BoxMesh
        {
            Size = Vector3.One,
            Material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.4f, 0.4f, 0.45f),
                Roughness = 0.9f
            }
        };

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = DebrisCount
        };

        var rng = new RandomNumberGenerator();
        rng.Randomize();

        for (int i = 0; i < DebrisCount; i++)
        {
            Vector3 direction = new Vector3(
                rng.RandfRange(-1f, 1f),
                rng.RandfRange(-1f, 1f),
                rng.RandfRange(-1f, 1f)
            ).Normalized();
            float distance = rng.RandfRange(MinDistance, MaxDistance);
            Vector3 position = direction * distance;

            Vector3 axis = new Vector3(rng.Randf(), rng.Randf(), rng.Randf()).Normalized();
            var rotation = new Basis(axis, rng.RandfRange(0f, Mathf.Tau));
            float scale = rng.RandfRange(MinScale, MaxScale);

            multiMesh.SetInstanceTransform(i, new Transform3D(rotation.Scaled(Vector3.One * scale), position));
        }

        Multimesh = multiMesh;
    }
}
