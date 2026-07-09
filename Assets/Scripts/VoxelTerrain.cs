using UnityEngine;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Scalable voxel terrain generated as a SINGLE MESH (not per-cube GameObjects).
///
/// How it works:
///  1. Wait for MRUK so we know where the table is.
///  2. Build a 3D grid of solid/empty cells: for each (x,z) column, sample
///     Perlin noise to get a height, fill cells below that height as solid.
///  3. Walk every solid cell and emit only the FACES that are exposed to air
///     (hidden faces between adjacent solid blocks are skipped).
///  4. Hand the combined vertices/triangles to one Mesh -> one draw, fast.
///
/// The whole terrain sits on the table top, aligned to the table's yaw.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class VoxelTerrain : MonoBehaviour
{
    [Header("Grid size (in blocks)")]
    [Tooltip("How many blocks wide/deep the terrain is.")]
    public int width = 24;
    public int depth = 24;
    [Tooltip("Max possible height in blocks. Keep small for 'flat-ish'.")]
    public int maxHeight = 5;

    [Header("Block scale")]
    [Tooltip("Edge length of each voxel in metres. 0.03 = 3cm, good for tabletop.")]
    public float blockSize = 0.03f;

    [Header("Terrain shape")]
    [Tooltip("Lower = broader, smoother bumps. Higher = choppier.")]
    public float noiseScale = 0.12f;
    [Tooltip("0..1 fraction of maxHeight the bumps actually use. Small = flat-ish.")]
    [Range(0f, 1f)] public float bumpiness = 0.4f;
    [Tooltip("Change this to get a different random landscape.")]
    public int seed = 1;

    // solid[x,y,z] = is this cell filled?
    private bool[,,] solid;

    // Mesh build buffers.
    private List<Vector3> verts = new List<Vector3>();
    private List<int> tris = new List<int>();

    void Start()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.RegisterSceneLoadedCallback(OnSceneReady);
        else
            Build(Vector3.zero, Quaternion.identity); // editor/no-MRUK fallback
    }

    void OnSceneReady()
    {
        MRUKRoom room = MRUK.Instance.GetCurrentRoom();
        Vector3 origin = Vector3.zero;
        Quaternion yaw = Quaternion.identity;

        if (room != null)
        {
            foreach (MRUKAnchor a in room.Anchors)
            {
                if (a.Label == MRUKAnchor.SceneLabels.TABLE)
                {
                    // Centre the terrain on the table, resting on its top.
                    origin = a.transform.position;

                    Vector3 fwd = a.transform.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.001f) { fwd = a.transform.up; fwd.y = 0f; }
                    if (fwd.sqrMagnitude > 0.001f)
                        yaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                    break;
                }
            }
        }
        Build(origin, yaw);
    }

    void Build(Vector3 tableCentre, Quaternion yaw)
    {
        GenerateSolidGrid();
        BuildMesh();

        // Position/orient the whole terrain object: centre it on the table,
        // rest it on the table top, align to table yaw.
        transform.position = tableCentre;
        transform.rotation = yaw;

        // Shift so the grid is centred horizontally on the table and its base
        // sits on the surface. We do this by offsetting the mesh origin.
        // (Handled in BuildMesh via the centring offset.)
    }

    void GenerateSolidGrid()
    {
        solid = new bool[width, maxHeight, depth];

        // Offset the noise sample by the seed so different seeds -> different land.
        float ox = seed * 13.13f;
        float oz = seed * 7.77f;

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                // Perlin returns 0..1. Scale it by how bumpy we want things.
                float n = Mathf.PerlinNoise(ox + x * noiseScale, oz + z * noiseScale);
                int columnHeight = 1 + Mathf.FloorToInt(n * bumpiness * (maxHeight - 1));
                columnHeight = Mathf.Clamp(columnHeight, 1, maxHeight);

                for (int y = 0; y < columnHeight; y++)
                    solid[x, y, z] = true;
            }
        }
    }

    void BuildMesh()
    {
        verts.Clear();
        tris.Clear();

        // Centre the grid horizontally on the table, base on the surface.
        Vector3 offset = new Vector3(-width * 0.5f * blockSize, 0f, -depth * 0.5f * blockSize);

        for (int x = 0; x < width; x++)
        for (int y = 0; y < maxHeight; y++)
        for (int z = 0; z < depth; z++)
        {
            if (!solid[x, y, z]) continue;

            Vector3 p = new Vector3(x, y, z) * blockSize + offset;

            // Emit each face only if the neighbour in that direction is air.
            if (IsAir(x, y + 1, z)) AddFace(p, Vector3.up, blockSize);
            if (IsAir(x, y - 1, z)) AddFace(p, Vector3.down, blockSize);
            if (IsAir(x + 1, y, z)) AddFace(p, Vector3.right, blockSize);
            if (IsAir(x - 1, y, z)) AddFace(p, Vector3.left, blockSize);
            if (IsAir(x, y, z + 1)) AddFace(p, Vector3.forward, blockSize);
            if (IsAir(x, y, z - 1)) AddFace(p, Vector3.back, blockSize);
        }

        Mesh mesh = new Mesh();
        // Allow big meshes (>65k verts) as terrain grows.
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();

        GetComponent<MeshFilter>().mesh = mesh;

        // Give it a green URP material if none is set.
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr.sharedMaterial == null)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit != null)
            {
                Material m = new Material(urpLit);
                m.color = new Color(0.35f, 0.65f, 0.3f);
                mr.material = m;
            }
        }

        Debug.Log("VoxelTerrain: built mesh with " + verts.Count + " verts, "
                  + (tris.Count / 3) + " triangles.");
    }

    // True if the cell is outside the grid or empty (i.e. exposed to air).
    bool IsAir(int x, int y, int z)
    {
        if (x < 0 || x >= width || y < 0 || y >= maxHeight || z < 0 || z >= depth)
            return true;
        return !solid[x, y, z];
    }

    // Adds one square face (two triangles) at block position p, facing 'dir'.
    void AddFace(Vector3 p, Vector3 dir, float s)
    {
        // Centre of the block; face sits half a block out along dir.
        Vector3 c = p + Vector3.one * (s * 0.5f);
        Vector3 faceCentre = c + dir * (s * 0.5f);

        // Build two in-plane axes perpendicular to dir.
        Vector3 up = (dir == Vector3.up || dir == Vector3.down) ? Vector3.forward : Vector3.up;
        Vector3 right = Vector3.Cross(dir, up).normalized * (s * 0.5f);
        up = Vector3.Cross(right.normalized, dir).normalized * (s * 0.5f);

        int start = verts.Count;
        verts.Add(faceCentre - right - up);
        verts.Add(faceCentre - right + up);
        verts.Add(faceCentre + right + up);
        verts.Add(faceCentre + right - up);

        // Two triangles, wound so the face points outward along dir.
        tris.Add(start + 0); tris.Add(start + 1); tris.Add(start + 2);
        tris.Add(start + 0); tris.Add(start + 2); tris.Add(start + 3);
    }
}
