using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// One chunk of the Minecraft world.
///
/// DRAW CALLS: the whole chunk renders in ONE draw call. Colours are baked into
/// the mesh as per-vertex colours and drawn with a single vertex-colour material
/// (the VoxelVertexColor ShaderGraph), instead of one submesh+material per block
/// type. That's a big reduction — a chunk with 6 block types went from 6 draw
/// calls to 1.
///
/// THREADING: parsing + geometry building run on a background thread, so a
/// rebuild doesn't hitch the framerate. Only the final mesh upload is on the
/// main thread (Unity objects are main-thread only).
///
/// Created and configured by WorldMapManager — you don't add this by hand.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class WorldChunk : MonoBehaviour
{
    [HideInInspector] public string serverIP;
    [HideInInspector] public int serverPort;
    [HideInInspector] public Vector3Int chunkCoord;
    [HideInInspector] public int chunkSize;
    [HideInInspector] public float blockSize;
    [HideInInspector] public Vector3Int worldOrigin;
    [HideInInspector] public float pollInterval;
    [HideInInspector] public bool livePolling;

    // ONE shared material (the VoxelVertexColor ShaderGraph). All chunks use the
    // same instance, so the SRP batcher can batch across chunks too.
    [HideInInspector] public Material vertexColorMaterial;

    [HideInInspector] public Vector3Int mapMin;
    [HideInInspector] public Vector3Int mapMax;

    private Vector3Int mcOrigin;
    private int lastHash = 0;

    // True once this chunk has successfully fetched at least once. Until then,
    // the initial-load loop retries promptly on failure so nothing is missed.
    private bool hasLoadedOnce = false;

    /// Finished geometry, computed off-thread. Plain data only — no Unity objects.
    private class MeshData
    {
        public List<Vector3> verts = new List<Vector3>();
        public List<Color32> colors = new List<Color32>();   // per-vertex colour
        public List<int> tris = new List<int>();              // single triangle list now
    }

    // Tracks whether we currently hold a throttle slot, so OnDisable can free
    // it if the chunk is destroyed mid-request (otherwise the slot leaks and
    // the throttle slowly starves).
    private bool holdingSlot = false;

    void OnDisable()
    {
        if (holdingSlot && LoadThrottle.Instance != null)
        {
            LoadThrottle.Instance.Release();
            holdingSlot = false;
        }
    }

    public void Initialise(float startDelay)
    {
        mcOrigin = worldOrigin + chunkCoord * chunkSize;
        StartCoroutine(PollLoop(startDelay));
    }

    IEnumerator PollLoop(float startDelay)
    {
        yield return new WaitForSeconds(startDelay);

        // INITIAL LOAD: keep trying until this chunk successfully loads once.
        // Unlike polling, a failed attempt here retries promptly (not on the
        // slow poll cycle), so startup is a GUARANTEED complete load — no chunk
        // is left blank because its one attempt happened to time out under load.
        while (!hasLoadedOnce)
        {
            yield return StartCoroutine(FetchOnce());
            if (!hasLoadedOnce)
                yield return new WaitForSeconds(0.5f);   // brief backoff, then retry
        }

        // Now settled — drop into the relaxed, distance-prioritised polling.
        while (livePolling)
        {
            float multiplier = UpdatePriority.IntervalMultiplier(ChunkDistanceFromPlayer());
            float wait = pollInterval * multiplier * Random.Range(0.85f, 1.15f);

            float waited = 0f;
            while (waited < wait)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
                float m2 = UpdatePriority.IntervalMultiplier(ChunkDistanceFromPlayer());
                if (m2 < multiplier) { multiplier = m2; wait = pollInterval * multiplier; }
            }

            yield return StartCoroutine(FetchOnce());
        }
    }

    float ChunkDistanceFromPlayer()
    {
        if (!UpdatePriority.hasFocus) return 99f;
        Vector3 chunkCentre = (Vector3)mcOrigin + Vector3.one * (chunkSize * 0.5f);
        Vector3 d = UpdatePriority.playerWorldPos - chunkCentre;
        d.y = 0f;
        return d.magnitude / chunkSize;
    }

    IEnumerator FetchOnce()
    {
        // Ask the global throttle for a slot before firing. This prevents the
        // startup stampede (all chunks requesting at once) that left blank
        // spots. Chunks are released nearest-first, so the world loads as an
        // expanding ring from the player outward.
        bool granted = false;
        if (LoadThrottle.Instance != null)
        {
            // Chunks that haven't loaded yet get absolute priority, so the
            // initial world load completes before polling can starve them.
            LoadThrottle.Instance.Request(
                !hasLoadedOnce,                    // initial load = top tier
                () => ChunkDistanceFromPlayer(),   // then nearer = sooner
                () => granted = true);
            while (!granted) yield return null;
            holdingSlot = true;
        }
        else
        {
            granted = true;   // no throttle present -> proceed unthrottled
        }

        // From here we hold a slot; make sure we Release it on EVERY exit path.
        bool needRelease = (LoadThrottle.Instance != null);

        int bs = chunkSize + 2;
        string url = "http://" + serverIP + ":" + serverPort + "/blocks"
                   + "?x=" + (mcOrigin.x - 1)
                   + "&y=" + (mcOrigin.y - 1)
                   + "&z=" + (mcOrigin.z - 1)
                   + "&dx=" + bs + "&dy=" + bs + "&dz=" + bs;

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (needRelease) { LoadThrottle.Instance.Release(); holdingSlot = false; }
                yield break;
            }

            string json = req.downloadHandler.text;
            int hash = json.GetHashCode();
            if (hash == lastHash)
            {
                // Valid response, data unchanged — still counts as a successful
                // load (the first fetch could legitimately match a prior hash of
                // 0 only if empty; in practice this marks us loaded either way).
                hasLoadedOnce = true;
                if (needRelease) { LoadThrottle.Instance.Release(); holdingSlot = false; }
                yield break;
            }
            lastHash = hash;

            Vector3Int origin = mcOrigin;
            int size = chunkSize;
            float bSize = blockSize;
            Vector3Int mn = mapMin;
            Vector3Int mx = mapMax;

            Task<MeshData> job = Task.Run(() => BuildMeshData(json, origin, size, bSize, mn, mx));
            while (!job.IsCompleted) yield return null;

            // Release the network slot now that the request is done. The mesh
            // build already ran on a worker thread; the upload below is cheap.
            if (needRelease) { LoadThrottle.Instance.Release(); holdingSlot = false; }

            if (job.Exception != null)
            {
                Debug.LogError("WorldChunk " + chunkCoord + " job failed: " + job.Exception.InnerException);
                yield break;
            }

            ApplyMesh(job.Result);
            hasLoadedOnce = true;   // this chunk has now loaded successfully
        }
    }

    // ===================== BACKGROUND THREAD — no Unity API =====================

    [System.Serializable] private struct Block { public string id; public int x, y, z; }
    [System.Serializable] private struct BlockList { public Block[] items; }

    private static MeshData BuildMeshData(string json, Vector3Int mcOrigin, int chunkSize,
                                          float blockSize, Vector3Int mapMin, Vector3Int mapMax)
    {
        Dictionary<Vector3Int, string> blocks = new Dictionary<Vector3Int, string>();
        BlockList list = JsonUtility.FromJson<BlockList>("{\"items\":" + json + "}");
        if (list.items != null)
        {
            foreach (Block b in list.items)
            {
                if (b.id == "minecraft:air" || b.id == "minecraft:void_air"
                    || b.id == "minecraft:cave_air") continue;
                blocks[new Vector3Int(b.x, b.y, b.z)] = b.id;
            }
        }

        MeshData data = new MeshData();

        foreach (KeyValuePair<Vector3Int, string> kv in blocks)
        {
            Vector3Int p = kv.Key;
            if (p.x < mcOrigin.x || p.x >= mcOrigin.x + chunkSize) continue;
            if (p.y < mcOrigin.y || p.y >= mcOrigin.y + chunkSize) continue;
            if (p.z < mcOrigin.z || p.z >= mcOrigin.z + chunkSize) continue;

            Color32 c = ColorFor(kv.Value);
            Vector3 lp = new Vector3(p.x - mcOrigin.x, p.y - mcOrigin.y, p.z - mcOrigin.z) * blockSize;

            if (Draw(blocks, p,  0,  1,  0, mapMin, mapMax)) AddFace(data, lp, Vector3.up,      c, blockSize);
            if (Draw(blocks, p,  0, -1,  0, mapMin, mapMax)) AddFace(data, lp, Vector3.down,    c, blockSize);
            if (Draw(blocks, p,  1,  0,  0, mapMin, mapMax)) AddFace(data, lp, Vector3.right,   c, blockSize);
            if (Draw(blocks, p, -1,  0,  0, mapMin, mapMax)) AddFace(data, lp, Vector3.left,    c, blockSize);
            if (Draw(blocks, p,  0,  0,  1, mapMin, mapMax)) AddFace(data, lp, Vector3.forward, c, blockSize);
            if (Draw(blocks, p,  0,  0, -1, mapMin, mapMax)) AddFace(data, lp, Vector3.back,    c, blockSize);
        }
        return data;
    }

    private static bool Draw(Dictionary<Vector3Int, string> blocks, Vector3Int p,
                             int dx, int dy, int dz, Vector3Int mapMin, Vector3Int mapMax)
    {
        Vector3Int n = new Vector3Int(p.x + dx, p.y + dy, p.z + dz);
        bool outsideMap = n.x < mapMin.x || n.x >= mapMax.x
                       || n.y < mapMin.y || n.y >= mapMax.y
                       || n.z < mapMin.z || n.z >= mapMax.z;
        if (outsideMap) return true;                 // wall off the whole-map boundary
        return !blocks.ContainsKey(n);               // else draw only against air
    }

    private static void AddFace(MeshData data, Vector3 p, Vector3 dir, Color32 col, float s)
    {
        Vector3 centre = p + Vector3.one * (s * 0.5f);
        Vector3 faceCentre = centre + dir * (s * 0.5f);

        Vector3 up = (dir == Vector3.up || dir == Vector3.down) ? Vector3.forward : Vector3.up;
        Vector3 right = Vector3.Cross(dir, up).normalized * (s * 0.5f);
        up = Vector3.Cross(right.normalized, dir).normalized * (s * 0.5f);

        int start = data.verts.Count;
        data.verts.Add(faceCentre - right - up);
        data.verts.Add(faceCentre - right + up);
        data.verts.Add(faceCentre + right + up);
        data.verts.Add(faceCentre + right - up);
        for (int i = 0; i < 4; i++) data.colors.Add(col);

        data.tris.Add(start + 0); data.tris.Add(start + 1); data.tris.Add(start + 2);
        data.tris.Add(start + 0); data.tris.Add(start + 2); data.tris.Add(start + 3);
    }

    private static Color32 ColorFor(string id)
    {
        switch (id)
        {
            case "minecraft:grass_block":  return new Color32( 89, 166, 76, 255);
            case "minecraft:dirt":         return new Color32(115, 82, 51, 255);
            case "minecraft:stone":        return new Color32(128,128,133, 255);
            case "minecraft:cobblestone":  return new Color32(115,115,120, 255);
            case "minecraft:gravel":       return new Color32(107,102,102, 255);
            case "minecraft:coal_ore":     return new Color32( 64, 64, 69, 255);
            case "minecraft:iron_ore":     return new Color32(179,153,128, 255);
            case "minecraft:sand":         return new Color32(217,204,153, 255);
            case "minecraft:sandstone":    return new Color32(204,191,148, 255);
            case "minecraft:water":        return new Color32( 64,102,217, 255);
            case "minecraft:lava":         return new Color32(230, 89, 26, 255);
            case "minecraft:oak_log":      return new Color32(102, 77, 46, 255);
            case "minecraft:birch_log":    return new Color32(204,191,158, 255);
            case "minecraft:oak_leaves":   return new Color32( 64,140, 64, 255);
            case "minecraft:birch_leaves": return new Color32( 89,153, 77, 255);
            case "minecraft:deepslate":    return new Color32( 77, 77, 84, 255);
            case "minecraft:tuff":         return new Color32(102,107,102, 255);
            case "minecraft:bedrock":      return new Color32( 38, 38, 38, 255);
            case "minecraft:snow_block":   return new Color32(242,242,247, 255);
            case "minecraft:oak_planks":   return new Color32(166,133, 84, 255);
            default:                       return new Color32(153,153,158, 255);
        }
    }

    // ===================== MAIN THREAD =====================

    private void ApplyMesh(MeshData data)
    {
        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(data.verts);
        mesh.SetColors(data.colors);           // <-- per-vertex colour
        mesh.SetTriangles(data.tris, 0);       // single submesh -> single draw call
        mesh.RecalculateNormals();
        GetComponent<MeshFilter>().mesh = mesh;

        // One material for the whole chunk.
        GetComponent<MeshRenderer>().sharedMaterial = vertexColorMaterial;
    }
}