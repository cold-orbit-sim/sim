using Godot;
using System.Collections.Generic;

namespace ColdOrbit.SimCore;

// Scatters tumbling asteroid-shaped debris through a volume around the origin.
// Uses a small pool of pre-generated meshes so only PoolSize icosphere builds
// happen at startup rather than one per piece.
//
// Each piece is a RigidBody3D (GravityScale=0) with a matching ConvexPolygonShape3D
// so the ship can physically collide with and scatter them. Hull-impact alerts
// will only fire if the impact impulse exceeds CollisionAlertThresholdN (5000 N
// default) — slow-drifting debris at these speeds won't reach it.
public partial class DebrisField : Node3D
{
    [Export] public int DebrisCount { get; set; } = 100;
    [Export] public int PoolSize { get; set; } = 5;
    [Export] public float MinDistance { get; set; } = 200f;
    [Export] public float MaxDistance { get; set; } = 2000f;
    [Export] public float MinScale { get; set; } = 100f;
    [Export] public float MaxScale { get; set; } = 700f;
    [Export] public float MaxDriftSpeed { get; set; } = 40f;   // m/s
    [Export] public float MaxTumbleSpeed { get; set; } = 0.5f; // rad/s

    public override void _Ready()
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        // Seeds 200–204: distinct from test-obstacle seeds (1, 2, 3) so the
        // debris shapes don't repeat the named asteroids.
        // RigidBody3D ignores node Scale (the physics engine requires unit-scale
        // transforms), so we keep unit-radius pool meshes and scale the
        // MeshInstance3D child + pre-multiply the collision points per instance.
        var poolMeshes    = new ArrayMesh[PoolSize];
        var poolPoints    = new Vector3[PoolSize][];  // unit-radius displaced verts
        var poolMaterials = new StandardMaterial3D[PoolSize];

        for (int p = 0; p < PoolSize; p++)
        {
            int seed = 200 + p;
            var (displaced, mesh) = GenerateMesh(seed, 1f, 0.35f, 3);
            poolMeshes[p]    = mesh;
            poolPoints[p]    = displaced;
            poolMaterials[p] = BuildMaterial(seed);
        }

        for (int i = 0; i < DebrisCount; i++)
        {
            int p = i % PoolSize;
            float scale = rng.RandfRange(MinScale, MaxScale);

            var body = new RigidBody3D();
            body.GravityScale  = 0f;
            body.LinearDamp    = 0f;
            body.AngularDamp   = 0f;

            var dir = new Vector3(
                rng.RandfRange(-1f, 1f),
                rng.RandfRange(-1f, 1f),
                rng.RandfRange(-1f, 1f)
            ).Normalized();
            body.Position = dir * rng.RandfRange(MinDistance, MaxDistance);
            body.Basis    = new Basis(
                new Vector3(rng.Randf(), rng.Randf(), rng.Randf()).Normalized(),
                rng.RandfRange(0f, Mathf.Tau)
            );

            // Scale the mesh child — RigidBody3D must stay at unit scale for
            // the physics engine, so size is applied here instead.
            var meshInst = new MeshInstance3D();
            meshInst.Mesh             = poolMeshes[p];
            meshInst.MaterialOverride = poolMaterials[p];
            meshInst.Scale            = Vector3.One * scale;
            body.AddChild(meshInst);

            // Per-instance collision shape with points pre-multiplied by scale.
            var scaledPoints = new Vector3[poolPoints[p].Length];
            for (int j = 0; j < scaledPoints.Length; j++)
                scaledPoints[j] = poolPoints[p][j] * scale;
            var instanceShape = new ConvexPolygonShape3D();
            instanceShape.Points = scaledPoints;

            var col = new CollisionShape3D();
            col.Shape = instanceShape;
            body.AddChild(col);

            AddChild(body);

            // Set after entering the tree so the physics body is initialised.
            body.LinearVelocity = new Vector3(
                rng.RandfRange(-MaxDriftSpeed, MaxDriftSpeed),
                rng.RandfRange(-MaxDriftSpeed, MaxDriftSpeed),
                rng.RandfRange(-MaxDriftSpeed, MaxDriftSpeed)
            );
            body.AngularVelocity = new Vector3(
                rng.RandfRange(-MaxTumbleSpeed, MaxTumbleSpeed),
                rng.RandfRange(-MaxTumbleSpeed, MaxTumbleSpeed),
                rng.RandfRange(-MaxTumbleSpeed, MaxTumbleSpeed)
            );
        }
    }

    private static (Vector3[] displaced, ArrayMesh mesh) GenerateMesh(
        int seed, float radius, float roughness, int subdivisions)
    {
        var (unitVerts, faces) = BuildIcosphere(subdivisions);
        var displaced          = DisplaceVertices(unitVerts, seed, radius, roughness);
        return (displaced, BuildFlatShadedMesh(displaced, faces));
    }

    private static (List<Vector3> verts, List<(int a, int b, int c)> faces) BuildIcosphere(int subdivisions)
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;

        var verts = new List<Vector3>
        {
            new(-1,  t,  0), new( 1,  t,  0), new(-1, -t,  0), new( 1, -t,  0),
            new( 0, -1,  t), new( 0,  1,  t), new( 0, -1, -t), new( 0,  1, -t),
            new( t,  0, -1), new( t,  0,  1), new(-t,  0, -1), new(-t,  0,  1),
        };
        for (int i = 0; i < verts.Count; i++)
            verts[i] = verts[i].Normalized();

        var faces = new List<(int, int, int)>
        {
            (0,11,5),(0,5,1),(0,1,7),(0,7,10),(0,10,11),
            (1,5,9),(5,11,4),(11,10,2),(10,7,6),(7,1,8),
            (3,9,4),(3,4,2),(3,2,6),(3,6,8),(3,8,9),
            (4,9,5),(2,4,11),(6,2,10),(8,6,7),(9,8,1),
        };

        var midCache = new Dictionary<long, int>();

        for (int s = 0; s < subdivisions; s++)
        {
            var next = new List<(int, int, int)>(faces.Count * 4);
            foreach (var (a, b, c) in faces)
            {
                int ab = Midpoint(a, b, verts, midCache);
                int bc = Midpoint(b, c, verts, midCache);
                int ca = Midpoint(c, a, verts, midCache);
                next.Add((a, ab, ca));
                next.Add((b, bc, ab));
                next.Add((c, ca, bc));
                next.Add((ab, bc, ca));
            }
            faces = next;
        }

        return (verts, faces);
    }

    private static int Midpoint(int a, int b, List<Vector3> verts, Dictionary<long, int> cache)
    {
        long key = a < b ? ((long)a << 32 | (uint)b) : ((long)b << 32 | (uint)a);
        if (cache.TryGetValue(key, out int idx)) return idx;
        idx = verts.Count;
        verts.Add(((verts[a] + verts[b]) * 0.5f).Normalized());
        cache[key] = idx;
        return idx;
    }

    private static Vector3[] DisplaceVertices(List<Vector3> unitVerts, int seed, float radius, float roughness)
    {
        var noise = new FastNoiseLite
        {
            Seed      = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = 1.2f,
        };

        var result = new Vector3[unitVerts.Count];
        for (int i = 0; i < unitVerts.Count; i++)
        {
            var dir = unitVerts[i];
            float n = noise.GetNoise3D(dir.X, dir.Y, dir.Z);
            result[i] = dir * (radius * (1f + n * roughness));
        }
        return result;
    }

    private static ArrayMesh BuildFlatShadedMesh(Vector3[] displaced, List<(int a, int b, int c)> faces)
    {
        int triCount  = faces.Count;
        var positions = new Vector3[triCount * 3];
        var normals   = new Vector3[triCount * 3];

        for (int i = 0; i < triCount; i++)
        {
            var (a, b, c) = faces[i];
            var p0 = displaced[a];
            var p1 = displaced[b];
            var p2 = displaced[c];
            var n  = (p2 - p0).Cross(p1 - p0).Normalized();

            int idx = i * 3;
            positions[idx] = p0; normals[idx] = n;
            positions[idx + 1] = p1; normals[idx + 1] = n;
            positions[idx + 2] = p2; normals[idx + 2] = n;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Normal]  = normals;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static StandardMaterial3D BuildMaterial(int seed)
    {
        const int size = 256;
        var texNoise = new FastNoiseLite
        {
            Seed        = seed + 1000,
            NoiseType   = FastNoiseLite.NoiseTypeEnum.Perlin,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency   = 3f,
            FractalOctaves = 4,
        };

        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgb8);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float n = (texNoise.GetNoise2D(x, y) + 1f) * 0.5f;
                float v = 0.10f + n * 0.12f;
                img.SetPixel(x, y, new Color(v, v, v));
            }
        }

        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(img),
            Roughness     = 0.9f,
            Metallic      = 0.0f,
            CullMode      = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }
}
