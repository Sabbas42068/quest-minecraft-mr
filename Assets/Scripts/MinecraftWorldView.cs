using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Fetches a region of REAL Minecraft world data from the GDMC-HTTP mod and
/// renders it as ONE mesh with submeshes, one solid URP/Lit material per block
/// type. No custom shader (avoids VR shader-compile issues) — URP/Lit renders
/// correctly in stereo.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class MinecraftWorldView : MonoBehaviour
{
    [Header("Minecraft server (USB tunnel = 127.0.0.1, port 9001)")]
    public string serverIP = "127.0.0.1";
    public int serverPort = 9001;

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

    private Dictionary<Vector3Int, string> world = new Dictionary<Vector3Int, string>();

    private Vector3 tableOrigin = Vector3.zero;
    private Quaternion tableYaw = Quaternion.identity;

    // Shared vertex list; triangles are grouped per block type (submesh).
    private List<Vector3> verts = new List<Vector3>();
    private Dictionary<string, List<int>> trisByType = new Dictionary<string, List<int>>();

    void Start()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.RegisterSceneLoadedCallback(OnSceneReady);
        else
            StartCoroutine(FetchAndBuild());
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
        StartCoroutine(FetchAndBuild());
    }

    IEnumerator FetchAndBuild()
    {
        string url = "http://" + serverIP + ":" + serverPort + "/blocks"
                   + "?x=" + originX + "&y=" + originY + "&z=" + originZ
                   + "&dx=" + sizeX + "&dy=" + sizeY + "&dz=" + sizeZ;

        Debug.Log("MinecraftWorldView: requesting " + url);

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 20;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("MinecraftWorldView: request FAILED - " + req.result + " : " + req.error);
                yield break;
            }

            string json = req.downloadHandler.text;
            Debug.Log("MinecraftWorldView: received " + json.Length + " chars.");
            ParseBlocks(json);
            Debug.Log("MinecraftWorldView: parsed " + world.Count + " solid blocks.");
            BuildMesh();
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

        // Build the mesh: one submesh per block type, in a stable order.
        List<string> types = new List<string>(trisByType.Keys);

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.subMeshCount = types.Count;
        for (int i = 0; i < types.Count; i++)
            mesh.SetTriangles(trisByType[types[i]], i);
        mesh.RecalculateNormals();
        GetComponent<MeshFilter>().mesh = mesh;

        // One solid URP/Lit material per submesh, matching block type order.
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        Material[] mats = new Material[types.Count];
        for (int i = 0; i < types.Count; i++)
        {
            Material m = new Material(urpLit);
            m.color = ColorFor(types[i]);
            // Make it matte, not shiny.
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
            mats[i] = m;
        }
        GetComponent<MeshRenderer>().materials = mats;

        transform.position = tableOrigin;
        transform.rotation = tableYaw;

        Debug.Log("MinecraftWorldView: mesh built - " + verts.Count + " verts, "
                  + types.Count + " block types.");
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