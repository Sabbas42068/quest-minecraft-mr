using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;

/// <summary>
/// LIVE view of a real Minecraft world on the table.
/// Polls the GDMC-HTTP mod over Wi-Fi on a timer, and rebuilds the mesh only
/// when the world data actually changed — so breaking/placing a block in
/// Minecraft on the PC shows up on the table a moment later, without needless
/// mesh rebuilds when nothing has happened.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class MinecraftWorldView : MonoBehaviour
{
    [Header("Minecraft server (Wi-Fi: PC's IP + GDMC port)")]
    public string serverIP = "192.168.0.112";
    public int serverPort = 8000;

    [Header("Region to fetch (Minecraft world coords)")]
    public int originX = 100;
    public int originY = 64;
    public int originZ = 200;
    public int sizeX = 32;
    public int sizeY = 24;
    public int sizeZ = 32;

    [Header("Tabletop rendering")]
    [Tooltip("Size of each voxel on the table, in metres. 0.015 = 1.5cm.")]
    public float blockSize = 0.015f;

    [Header("Live updates")]
    [Tooltip("Seconds between polls. Lower = more responsive, more network traffic.")]
    public float pollInterval = 1.5f;
    [Tooltip("Turn off to load once and stay static.")]
    public bool livePolling = true;

    private Dictionary<Vector3Int, string> world = new Dictionary<Vector3Int, string>();

    // Hash of the last response, so we skip rebuilding when nothing changed.
    private int lastDataHash = 0;

    private Vector3 tableOrigin = Vector3.zero;
    private Quaternion tableYaw = Quaternion.identity;

    private List<Vector3> verts = new List<Vector3>();
    private Dictionary<string, List<int>> trisByType = new Dictionary<string, List<int>>();

    // Materials are cached per block type so we don't recreate them every poll.
    private Dictionary<string, Material> materialCache = new Dictionary<string, Material>();

    void Start()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.RegisterSceneLoadedCallback(OnSceneReady);
        else
            StartCoroutine(PollLoop());
    }

    void OnSceneReady()
    {
        MRUKRoom room = MRUK.Instance.GetCurrentRoom();
        if (room != null)
        {
            foreach (MRUKAnchor a in room.Anchors)
            {
                if (a.Label == MRUKAnchor.SceneLabels.TABLE)
                {
                    tableOrigin = a.transform.position;
                    Vector3 fwd = a.transform.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.001f) { fwd = a.transform.up; fwd.y = 0f; }
                    if (fwd.sqrMagnitude > 0.001f)
                        tableYaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                    break;
                }
            }
        }
        // Place the object once; the mesh gets swapped in on each rebuild.
        transform.position = tableOrigin;
        transform.rotation = tableYaw;

        StartCoroutine(PollLoop());
    }

    /// Repeatedly fetch the region. Rebuilds only when the data changed.
    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return StartCoroutine(FetchOnce());

            if (!livePolling) yield break;          // one-shot mode
            yield return new WaitForSeconds(pollInterval);
        }
    }

    IEnumerator FetchOnce()
    {
        string url = "http://" + serverIP + ":" + serverPort + "/blocks"
                   + "?x=" + originX + "&y=" + originY + "&z=" + originZ
                   + "&dx=" + sizeX + "&dy=" + sizeY + "&dz=" + sizeZ;

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("MinecraftWorldView: poll failed - " + req.error
                               + " (will retry)");
                yield break;   // keep the old mesh, try again next poll
            }

            string json = req.downloadHandler.text;

            // Cheap change-detection: if the payload is identical, skip the
            // expensive parse + mesh rebuild entirely.
            int hash = json.GetHashCode();
            if (hash == lastDataHash)
                yield break;   // nothing changed in the world
            lastDataHash = hash;

            ParseBlocks(json);
            BuildMesh();
            Debug.Log("MinecraftWorldView: world updated (" + world.Count + " blocks).");
        }
    }

    [System.Serializable] private struct Block { public string id; public int x, y, z; }
    [System.Serializable] private struct BlockList { public Block[] items; }

    void ParseBlocks(string json)
    {
        world.Clear();
        string wrapped = "{\"items\":" + json + "}";
        BlockList list = JsonUtility.FromJson<BlockList>(wrapped);
        if (list.items == null) return;
        foreach (Block b in list.items)
        {
            if (b.id == "minecraft:air" || b.id == "minecraft:void_air"
                || b.id == "minecraft:cave_air") continue;
            world[new Vector3Int(b.x, b.y, b.z)] = b.id;
        }
    }

    bool IsAir(int x, int y, int z)
    {
        return !world.ContainsKey(new Vector3Int(x, y, z));
    }

    void BuildMesh()
    {
        verts.Clear();
        trisByType.Clear();

        Vector3 centreOffset = new Vector3(-sizeX * 0.5f * blockSize, 0f, -sizeZ * 0.5f * blockSize);

        foreach (KeyValuePair<Vector3Int, string> kv in world)
        {
            Vector3Int p = kv.Key;
            string type = kv.Value;
            Vector3 lp = new Vector3(p.x - originX, p.y - originY, p.z - originZ) * blockSize + centreOffset;

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

        // Reuse cached materials so we're not allocating new ones every poll.
        Material[] mats = new Material[types.Count];
        for (int i = 0; i < types.Count; i++)
            mats[i] = GetMaterial(types[i]);
        GetComponent<MeshRenderer>().materials = mats;
    }

    Material GetMaterial(string type)
    {
        if (materialCache.TryGetValue(type, out Material cached))
            return cached;

        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        Material m = new Material(urpLit);
        m.color = ColorFor(type);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        materialCache[type] = m;
        return m;
    }

    Color ColorFor(string id)
    {
        switch (id)
        {
            case "minecraft:grass_block": return new Color(0.35f, 0.65f, 0.30f);
            case "minecraft:dirt":        return new Color(0.45f, 0.32f, 0.20f);
            case "minecraft:stone":       return new Color(0.50f, 0.50f, 0.52f);
            case "minecraft:gravel":      return new Color(0.42f, 0.40f, 0.40f);
            case "minecraft:coal_ore":    return new Color(0.25f, 0.25f, 0.27f);
            case "minecraft:iron_ore":    return new Color(0.70f, 0.60f, 0.50f);
            case "minecraft:sand":        return new Color(0.85f, 0.80f, 0.60f);
            case "minecraft:water":       return new Color(0.25f, 0.40f, 0.85f);
            case "minecraft:oak_log":     return new Color(0.40f, 0.30f, 0.18f);
            case "minecraft:oak_leaves":  return new Color(0.25f, 0.55f, 0.25f);
            case "minecraft:deepslate":   return new Color(0.30f, 0.30f, 0.33f);
            case "minecraft:tuff":        return new Color(0.40f, 0.42f, 0.40f);
            case "minecraft:bedrock":     return new Color(0.15f, 0.15f, 0.15f);
            default:                      return new Color(0.6f, 0.6f, 0.62f);
        }
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