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

    // The bounds of the WHOLE rendered map, in Minecraft coords. Used to tell
    // "chunk seam" (cull against neighbour) apart from "outer map edge" (always
    // draw a wall, so the diorama isn't sliced open).
    [HideInInspector] public Vector3Int mapMin;
    [HideInInspector] public Vector3Int mapMax;

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

        // FIRST PASS: every chunk fetches once immediately, so the whole world
        // appears complete. Prioritisation only kicks in after this.
        yield return StartCoroutine(FetchOnce());

        while (livePolling)
        {
            float dist = ChunkDistanceFromPlayer();
            float multiplier = UpdatePriority.IntervalMultiplier(dist);

            // Chunks near the player poll at the base rate; distant ones poll
            // proportionally less often. Small random jitter spreads requests
            // out so they don't all fire on the same frame.
            float wait = pollInterval * multiplier * Random.Range(0.85f, 1.15f);

            // Wait in short slices rather than one long sleep, re-checking the
            // player's distance as we go. Without this, a far-away chunk on a
            // 16x wait wouldn't notice the player walking toward it until that
            // whole wait expired — so approaching a distant area would be slow
            // to refresh. This lets a chunk "wake up" as soon as it becomes
            // high-priority.
            float waited = 0f;
            while (waited < wait)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;

                // Player got closer? Shorten the wait accordingly.
                float newMultiplier = UpdatePriority.IntervalMultiplier(ChunkDistanceFromPlayer());
                if (newMultiplier < multiplier)
                {
                    multiplier = newMultiplier;
                    wait = pollInterval * multiplier;
                }
            }

            yield return StartCoroutine(FetchOnce());
        }
    }

    /// Distance from this chunk's centre to the player, measured in chunks.
    float ChunkDistanceFromPlayer()
    {
        if (!UpdatePriority.hasFocus) return 99f;

        // Centre of this chunk in Minecraft coords.
        Vector3 chunkCentre = (Vector3)mcOrigin + Vector3.one * (chunkSize * 0.5f);

        // Horizontal distance only — vertical rings aren't meaningful for a map
        // (a chunk directly below the player is just as relevant as their own).
        Vector3 d = UpdatePriority.playerWorldPos - chunkCentre;
        d.y = 0f;

        return d.magnitude / chunkSize;
    }

    IEnumerator FetchOnce()
    {
        // Fetch this chunk's region PLUS a 1-block border on every side.
        //
        // Why: a chunk can't tell whether a face on its outer boundary is
        // exposed without knowing what its NEIGHBOUR has there. Without the
        // border, every chunk generates faces all over its outer shell — even
        // where the adjacent chunk is solid rock right against it. That's a
        // huge amount of invisible geometry buried at every chunk seam, which
        // costs us BOTH mesh-build time and render time.
        //
        // The border blocks are used ONLY for culling decisions. They are never
        // rendered (the neighbouring chunk renders them), so there's no overlap.
        int bx = mcOrigin.x - 1;
        int by = mcOrigin.y - 1;
        int bz = mcOrigin.z - 1;
        int bs = chunkSize + 2;

        string url = "http://" + serverIP + ":" + serverPort + "/blocks"
                   + "?x=" + bx + "&y=" + by + "&z=" + bz
                   + "&dx=" + bs + "&dy=" + bs + "&dz=" + bs;

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
        // Thanks to the 1-block border we fetch, this correctly answers "is
        // there air here?" even just OUTSIDE the chunk — which is what lets us
        // cull the hidden faces at chunk seams.
        return !blocks.ContainsKey(new Vector3Int(x, y, z));
    }

    /// <summary>
    /// Should we draw a face between block 'p' and its neighbour in direction
    /// (dx,dy,dz)?
    ///
    /// Two different rules, and the distinction matters:
    ///
    ///  - At a CHUNK seam (inside the map): cull against the neighbour. If the
    ///    next chunk has solid rock there, the face is invisible — skip it.
    ///    This is the optimisation.
    ///
    ///  - At the MAP boundary (the outer edge of the whole rendered region):
    ///    ALWAYS draw. The Minecraft world continues past our region, so those
    ///    neighbour blocks are solid — which would cull the face and leave the
    ///    diorama sliced open with no walls. We want a clean wall capping the
    ///    map instead.
    /// </summary>
    /// <summary>
    /// Should we draw a face between block 'p' and its neighbour?
    ///
    /// Two rules:
    ///  - At a CHUNK seam (inside the map): cull against the neighbour. If the
    ///    next chunk is solid there, the face is invisible — skip it. This is
    ///    the optimisation that removes the hidden geometry at seams.
    ///  - At the MAP boundary (any of the six outer sides, INCLUDING the top):
    ///    always draw. The Minecraft world continues past our region, so those
    ///    neighbours are solid and would cull the face, leaving the diorama
    ///    sliced open. Walling all six sides gives a clean, fully-enclosed
    ///    cross-section — a solid block of world sitting on the table.
    /// </summary>
    bool ShouldDrawFace(Vector3Int p, int dx, int dy, int dz)
    {
        Vector3Int n = new Vector3Int(p.x + dx, p.y + dy, p.z + dz);

        // Outside the rendered map on ANY side (including top)? Wall it off.
        if (IsOutsideMap(n)) return true;

        // Otherwise — including at chunk seams — cull normally.
        return IsAir(n.x, n.y, n.z);
    }

    /// Is this Minecraft position outside the whole rendered map region?
    bool IsOutsideMap(Vector3Int p)
    {
        return p.x < mapMin.x || p.x >= mapMax.x
            || p.y < mapMin.y || p.y >= mapMax.y
            || p.z < mapMin.z || p.z >= mapMax.z;
    }

    /// Is this block inside the chunk proper (as opposed to the border we
    /// fetched purely for culling)?
    bool IsInterior(Vector3Int p)
    {
        return p.x >= mcOrigin.x && p.x < mcOrigin.x + chunkSize
            && p.y >= mcOrigin.y && p.y < mcOrigin.y + chunkSize
            && p.z >= mcOrigin.z && p.z < mcOrigin.z + chunkSize;
    }

    void BuildMesh()
    {
        verts.Clear();
        trisByType.Clear();

        foreach (KeyValuePair<Vector3Int, string> kv in blocks)
        {
            Vector3Int p = kv.Key;

            // Skip the border blocks — they exist only so face-culling can make
            // correct decisions. The neighbouring chunk renders them.
            if (!IsInterior(p)) continue;

            string type = kv.Value;

            Vector3 lp = new Vector3(p.x - mcOrigin.x, p.y - mcOrigin.y, p.z - mcOrigin.z)
                         * blockSize;

            if (ShouldDrawFace(p,  0,  1,  0)) AddFace(lp, Vector3.up,      type);
            if (ShouldDrawFace(p,  0, -1,  0)) AddFace(lp, Vector3.down,    type);
            if (ShouldDrawFace(p,  1,  0,  0)) AddFace(lp, Vector3.right,   type);
            if (ShouldDrawFace(p, -1,  0,  0)) AddFace(lp, Vector3.left,    type);
            if (ShouldDrawFace(p,  0,  0,  1)) AddFace(lp, Vector3.forward, type);
            if (ShouldDrawFace(p,  0,  0, -1)) AddFace(lp, Vector3.back,    type);
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

        // Uncomment to measure the seam fix: compare the face count before and
        // after. Boundary culling typically removes a large fraction of the
        // geometry, since every chunk used to render its entire outer shell.
        // Debug.Log("Chunk " + chunkCoord + ": " + (verts.Count / 4) + " faces");
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
