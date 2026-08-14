using Godot;
using System.Collections.Generic;

namespace ColdOrbit.SimCore;

public partial class Asteroid : StaticBody3D
{
    [Export] public float Radius { get; set; } = 8f;
    [Export] public float RoughnessAmount { get; set; } = 0.35f;
    [Export] public int Subdivisions { get; set; } = 3;
    [Export] public int NoiseSeed { get; set; } = 0;

    public override void _Ready()
    {
        var (sphereVerts, faces) = BuildIcosphere();
        var displaced = DisplaceVertices(sphereVerts);
        var mesh = BuildFlatShadedMesh(displaced, faces);

        var meshInst = new MeshInstance3D { Name = "AsteroidMesh" };
        meshInst.Mesh = mesh;
        meshInst.MaterialOverride = BuildMaterial();
        AddChild(meshInst);

        var colShape = new ConvexPolygonShape3D();
        colShape.Points = displaced;

        var col = new CollisionShape3D { Name = "AsteroidCollision" };
        col.Shape = colShape;
        AddChild(col);
    }

    // Mirror collision-enabled state to visibility so ShowTestObstacles
    // disabling the parent propagates all the way through to the physics shape.
    public override void _Notification(int what)
    {
        if (what != NotificationVisibilityChanged) return;
        var col = GetNodeOrNull<CollisionShape3D>("AsteroidCollision");
        if (col != null)
            col.Disabled = !IsVisibleInTree();
    }

    // Returns unit-sphere vertices and triangle face index triples.
    private (List<Vector3> verts, List<(int a, int b, int c)> faces) BuildIcosphere()
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

        for (int s = 0; s < Subdivisions; s++)
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

    // Sample simplex noise on the unit-sphere position of each vertex to get
    // the lumpy irregular silhouette. Returns the displaced vertex positions.
    private Vector3[] DisplaceVertices(List<Vector3> unitVerts)
    {
        var noise = new FastNoiseLite
        {
            Seed = NoiseSeed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = 1.2f,
        };

        var result = new Vector3[unitVerts.Count];
        for (int i = 0; i < unitVerts.Count; i++)
        {
            var dir = unitVerts[i]; // already normalised
            float n = noise.GetNoise3D(dir.X, dir.Y, dir.Z); // -1..1
            result[i] = dir * (Radius * (1f + n * RoughnessAmount));
        }
        return result;
    }

    // Unindexed mesh so each triangle has its own vertices and flat-shaded
    // normals, giving the craggy rocky look rather than a smooth sphere.
    private static ArrayMesh BuildFlatShadedMesh(Vector3[] displaced, List<(int a, int b, int c)> faces)
    {
        int triCount = faces.Count;
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
            positions[idx]     = p0; normals[idx]     = n;
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

    // Dark charcoal rock look: noise-textured albedo, high roughness.
    // Follows the same bake-at-startup pattern as Planet.cs.
    private StandardMaterial3D BuildMaterial()
    {
        const int size = 256;
        var texNoise = new FastNoiseLite
        {
            Seed = NoiseSeed + 1000,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            Frequency = 3f,
            FractalOctaves = 4,
        };

        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgb8);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float n = (texNoise.GetNoise2D(x, y) + 1f) * 0.5f; // 0..1
                float v = 0.10f + n * 0.12f; // #1a1a1a .. #2e2e2e range
                img.SetPixel(x, y, new Color(v, v, v));
            }
        }

        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(img),
            Roughness     = 0.9f,
            Metallic      = 0.0f,
        };
    }
}
