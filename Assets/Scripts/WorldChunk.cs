using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// One chunk of the Minecraft world. Owns its own mesh, polls its own small
/// slice of the world, and rebuilds ONLY itself when its data changes.
///
/// This is the unit of work that makes updates cheap: breaking one block in
/// Minecraft dirties one chunk, so we regenerate a small mesh instead of the
/// entire world.
///
/// Created and configured by WorldMapManager — you don't add this by hand.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class WorldChunk : MonoBehaviour
{
    // --- Set by the manager when the chunk is spawned ---
    [HideInInspector] public string serverIP;
    [HideInInspector] public int serverPort;
    [HideInInspector] public Vector3Int chunkCoord;   // which chunk in the grid
    [HideInInspector] public int chunkSize;           // blocks per side
    [HideInInspector] public float blockSize;         // metres per block
    [HideInInspector] public Vector3Int worldOrigin;  // MC coord of chunk (0,0,0)
    [HideInInspector] public float pollInterval;
    [HideInInspector] public bool livePolling;
    [HideInInspector] public MaterialLibrary materials;

    // Minecraft-space origin of THIS chunk.
    private Vector3Int mcOrigin;

    private Dictionary<Vector3Int, string> blocks = new Dictionary<Vector3Int, string>();
    private int lastHash = 0;

    private List<Vector3> verts = new List<Vector3>();
    private Dictionary<string, List<int>> trisByType = new Dictionary<string, List<int>>();

    /// Called by the manager after it sets the fields above.
    public void Initialise(float startDelay)
    {
        mcOrigin = worldOrigin + chunkCoord * chunkSize;
        StartCoroutine(PollLoop(startDelay));
    }

    IEnumerator PollLoop(float startDelay)
    {
        // Stagger the first request so chunks don't all hit the server at once.
        yield return new WaitForSeconds(startDelay);

        while (true)
        {
            yield return StartCoroutine(FetchOnce());
            if (!livePolling) yield break;
            yield return new WaitForSeconds(pollInterval);
        }
    }

    IEnumerator FetchOnce()
    {
        string url = "http://" + serverIP + ":" + serverPort + "/blocks"
                   + "?x=" + mcOrigin.x + "&y=" + mcOrigin.y + "&z=" + mcOrigin.z
                   + "&dx=" + chunkSize + "&dy=" + chunkSize + "&dz=" + chunkSize;

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                // Keep the old mesh; try again next cycle.
                yield break;
            }

            string json = req.downloadHandler.text;

            // Per-chunk change detection: only this chunk rebuilds, and only
            // if ITS data actually changed.
            int hash = json.GetHashCode();
            if (hash == lastHash) yield break;
            lastHash = hash;

            Parse(json);
            BuildMesh();
        }
    }

    [System.Serializable] private struct Block { public string id; public int x, y, z; }
    [System.Serializable] private struct BlockList { public Block[] items; }

    void Parse(string json)
    {
        blocks.Clear();
        BlockList list = JsonUtility.FromJson<BlockList>("{\"items\":" + json + "}");
        if (list.items == null) return;
        foreach (Block b in list.items)
        {
            if (b.id == "minecraft:air" || b.id == "minecraft:void_air"
                || b.id == "minecraft:cave_air") continue;
            blocks[new Vector3Int(b.x, b.y, b.z)] = b.id;
        }
    }

    bool IsAir(int x, int y, int z)
    {
        // NOTE: blocks just outside this chunk aren't in our dictionary, so the
        // faces on a chunk's outer boundary get generated even if a neighbouring
        // chunk has a solid block there. That's a small amount of hidden
        // geometry at chunk seams — a known trade-off. It can be removed later
        // by having chunks query their neighbours.
        return !blocks.ContainsKey(new Vector3Int(x, y, z));
    }

    void BuildMesh()
    {
        verts.Clear();
        trisByType.Clear();

        foreach (KeyValuePair<Vector3Int, string> kv in blocks)
        {
            Vector3Int p = kv.Key;
            string type = kv.Value;

            // Position local to this chunk's GameObject.
            Vector3 lp = new Vector3(p.x - mcOrigin.x, p.y - mcOrigin.y, p.z - mcOrigin.z)
                         * blockSize;

            if (IsAir(p.x, p.y + 1, p.z)) AddFace(lp, Vector3.up, type);
            if (IsAir(p.x, p.y - 1, p.z)) AddFace(lp, Vector3.down, type);
            if (IsAir(p.x + 1, p.y, p.z)) AddFace(lp, Vector3.right, type);
            if (IsAir(p.x - 1, p.y, p.z)) AddFace(lp, Vector3.left, type);
            if (IsAir(p.x, p.y, p.z + 1)) AddFace(lp, Vector3.forward, type);
            if (IsAir(p.x, p.y, p.z - 1)) AddFace(lp, Vector3.back, type);
        }

        List<string> types = new List<string>(trisByType.Keys);

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.subMeshCount = types.Count;
        for (int i = 0; i < types.Count; i++)
            mesh.SetTriangles(trisByType[types[i]], i);
        mesh.RecalculateNormals();
        GetComponent<MeshFilter>().mesh = mesh;

        Material[] mats = new Material[types.Count];
        for (int i = 0; i < types.Count; i++)
            mats[i] = materials.Get(types[i]);
        GetComponent<MeshRenderer>().materials = mats;
    }

    void AddFace(Vector3 p, Vector3 dir, string type)
    {
        if (!trisByType.ContainsKey(type))
            trisByType[type] = new List<int>();
        List<int> tris = trisByType[type];

        float s = blockSize;
        Vector3 centre = p + Vector3.one * (s * 0.5f);
        Vector3 faceCentre = centre + dir * (s * 0.5f);

        Vector3 up = (dir == Vector3.up || dir == Vector3.down) ? Vector3.forward : Vector3.up;
        Vector3 right = Vector3.Cross(dir, up).normalized * (s * 0.5f);
        up = Vector3.Cross(right.normalized, dir).normalized * (s * 0.5f);

        int start = verts.Count;
        verts.Add(faceCentre - right - up);
        verts.Add(faceCentre - right + up);
        verts.Add(faceCentre + right + up);
        verts.Add(faceCentre + right - up);

        tris.Add(start + 0); tris.Add(start + 1); tris.Add(start + 2);
        tris.Add(start + 0); tris.Add(start + 2); tris.Add(start + 3);
    }
}
