using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Globalization;

/// <summary>
/// Renders live Minecraft MOBS/ENTITIES as markers on the tabletop map.
///
/// Same architecture as PlayerLayer (fast poll, move transforms, never rebuild
/// meshes) but with three differences:
///   1. Spatially scoped — only fetches entities inside the visible map region,
///      not every mob in the world.
///   2. Many types — each mob gets its own colour/size.
///   3. Markers are REUSED (pooled) rather than created/destroyed each poll,
///      since mobs spawn and despawn constantly.
///
/// Attach to the SAME GameObject as WorldMapManager.
/// </summary>
[RequireComponent(typeof(WorldMapManager))]
public class EntityLayer : MonoBehaviour
{
    [Header("Polling")]
    [Tooltip("Seconds between entity polls.")]
    public float pollInterval = 0.4f;

    [Tooltip("Skip item drops, arrows, XP orbs etc. Keeps the map readable.")]
    public bool ignoreClutter = true;

    [Header("Appearance")]
    [Tooltip("Smooth movement between polls.")]
    public bool smoothMovement = true;
    public float smoothSpeed = 6f;

    private WorldMapManager map;

    // uuid -> marker. Reused between polls so mobs don't flicker.
    private Dictionary<string, GameObject> markers = new Dictionary<string, GameObject>();
    private Dictionary<string, Vector3> targets = new Dictionary<string, Vector3>();

    // Cached materials per mob type.
    private Dictionary<string, Material> matCache = new Dictionary<string, Material>();

    void Start()
    {
        map = GetComponent<WorldMapManager>();
        StartCoroutine(PollLoop());
    }

    void Update()
    {
        if (!smoothMovement) return;
        foreach (KeyValuePair<string, GameObject> kv in markers)
        {
            if (!targets.TryGetValue(kv.Key, out Vector3 t)) continue;
            kv.Value.transform.localPosition = Vector3.Lerp(
                kv.Value.transform.localPosition, t, Time.deltaTime * smoothSpeed);
        }
    }

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return StartCoroutine(FetchEntities());
            yield return new WaitForSeconds(pollInterval);
        }
    }

    IEnumerator FetchEntities()
    {
        // Only ask for entities inside the region the map actually shows.
        // The selector is a Minecraft target selector, URL-encoded.
        int span = (map.renderRadius * 2 + 1) * map.chunkSize;
        int height = map.verticalChunks * map.chunkSize;

        Vector3Int origin = map.RegionCorner;   // MC coord of the map's corner

        string selector = "@e[x=" + origin.x + ",y=" + origin.y + ",z=" + origin.z
                        + ",dx=" + span + ",dy=" + height + ",dz=" + span + "]";

        string url = "http://" + map.serverIP + ":" + map.serverPort
                   + "/entities?includeData=true&selector="
                   + UnityWebRequest.EscapeURL(selector);

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success) yield break;

            ParseAndPlace(req.downloadHandler.text);
        }
    }

    [System.Serializable] private struct EntityEntry
    {
        public string uuid;
        public string data;   // SNBT blob: contains Pos, Rotation and id
    }
    [System.Serializable] private struct EntityList { public EntityEntry[] items; }

    private static readonly Regex posRegex = new Regex(
        @"Pos:\[\s*(-?[\d.]+)d?\s*,\s*(-?[\d.]+)d?\s*,\s*(-?[\d.]+)d?\s*\]");
    private static readonly Regex rotRegex = new Regex(
        @"Rotation:\[\s*(-?[\d.]+)f?\s*,\s*(-?[\d.]+)f?\s*\]");
    // The mob type, e.g.  id:"minecraft:sheep"
    private static readonly Regex idRegex = new Regex(
        "id:\"(minecraft:[a-z_]+)\"");

    void ParseAndPlace(string json)
    {
        EntityList list = JsonUtility.FromJson<EntityList>("{\"items\":" + json + "}");
        if (list.items == null) return;

        HashSet<string> seen = new HashSet<string>();

        foreach (EntityEntry e in list.items)
        {
            if (string.IsNullOrEmpty(e.data)) continue;

            Match idm = idRegex.Match(e.data);
            string type = idm.Success ? idm.Groups[1].Value : "unknown";

            if (ignoreClutter && IsClutter(type)) continue;

            Match m = posRegex.Match(e.data);
            if (!m.Success) continue;

            float wx = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            float wy = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            float wz = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

            seen.Add(e.uuid);

            GameObject marker = GetOrCreateMarker(e.uuid, type);
            Vector3 size = SizeFor(type);

            // Same coordinate conversion the chunks use, so mobs sit on the terrain.
            targets[e.uuid] = map.WorldToMapLocal(new Vector3(wx, wy, wz))
                            + Vector3.up * (size.y * 0.5f * map.blockSize);

            if (!smoothMovement)
                marker.transform.localPosition = targets[e.uuid];

            Match r = rotRegex.Match(e.data);
            if (r.Success)
            {
                float yaw = float.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                // Same MC->Unity yaw correction as players.
                marker.transform.localRotation = Quaternion.Euler(0f, -yaw, 0f);
            }
        }

        // Despawn markers for mobs that are gone (killed, wandered off, despawned).
        List<string> gone = new List<string>();
        foreach (string uuid in markers.Keys)
            if (!seen.Contains(uuid)) gone.Add(uuid);

        foreach (string uuid in gone)
        {
            Destroy(markers[uuid]);
            markers.Remove(uuid);
            targets.Remove(uuid);
        }
    }

    /// Things that would spam the map with noise.
    bool IsClutter(string type)
    {
        switch (type)
        {
            case "minecraft:item":
            case "minecraft:experience_orb":
            case "minecraft:arrow":
            case "minecraft:snowball":
            case "minecraft:item_frame":
            case "minecraft:painting":
            case "minecraft:armor_stand":
            case "minecraft:falling_block":
            case "minecraft:area_effect_cloud":
                return true;
            default:
                return false;
        }
    }

    GameObject GetOrCreateMarker(string uuid, string type)
    {
        if (markers.TryGetValue(uuid, out GameObject existing))
            return existing;

        Vector3 sizeBlocks = SizeFor(type);

        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = type;
        Destroy(marker.GetComponent<Collider>());
        marker.transform.SetParent(transform, false);
        marker.transform.localScale = sizeBlocks * map.blockSize;
        marker.GetComponent<Renderer>().material = MaterialFor(type);

        markers[uuid] = marker;
        return marker;
    }

    Material MaterialFor(string type)
    {
        if (matCache.TryGetValue(type, out Material cached)) return cached;

        Material m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.color = ColorFor(type);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        matCache[type] = m;
        return m;
    }

    /// Rough size in Minecraft blocks (width, height, depth).
    Vector3 SizeFor(string type)
    {
        switch (type)
        {
            // Hostile
            case "minecraft:zombie":
            case "minecraft:skeleton":
            case "minecraft:husk":
            case "minecraft:drowned":
            case "minecraft:stray":       return new Vector3(0.7f, 1.9f, 0.7f);
            case "minecraft:creeper":     return new Vector3(0.7f, 1.7f, 0.7f);
            case "minecraft:spider":      return new Vector3(1.3f, 0.9f, 1.3f);
            case "minecraft:enderman":    return new Vector3(0.7f, 2.9f, 0.7f);

            // Passive
            case "minecraft:cow":
            case "minecraft:sheep":       return new Vector3(0.9f, 1.3f, 1.3f);
            case "minecraft:pig":         return new Vector3(0.9f, 0.9f, 1.3f);
            case "minecraft:chicken":     return new Vector3(0.4f, 0.7f, 0.4f);
            case "minecraft:villager":    return new Vector3(0.6f, 1.9f, 0.6f);
            case "minecraft:horse":       return new Vector3(1.3f, 1.6f, 1.3f);
            case "minecraft:wolf":
            case "minecraft:cat":
            case "minecraft:fox":         return new Vector3(0.6f, 0.8f, 0.9f);
            case "minecraft:bat":         return new Vector3(0.5f, 0.9f, 0.5f);

            case "minecraft:boat":
            case "minecraft:minecart":    return new Vector3(1.3f, 0.6f, 1.3f);

            default:                      return new Vector3(0.8f, 1.2f, 0.8f);
        }
    }

    Color ColorFor(string type)
    {
        switch (type)
        {
            // Hostile mobs — reds
            case "minecraft:zombie":      return new Color(0.30f, 0.55f, 0.35f);
            case "minecraft:creeper":     return new Color(0.30f, 0.80f, 0.30f);
            case "minecraft:skeleton":
            case "minecraft:stray":       return new Color(0.90f, 0.90f, 0.85f);
            case "minecraft:spider":      return new Color(0.25f, 0.20f, 0.20f);
            case "minecraft:enderman":    return new Color(0.10f, 0.05f, 0.20f);

            // Passive mobs — natural tones
            case "minecraft:cow":         return new Color(0.35f, 0.25f, 0.18f);
            case "minecraft:sheep":       return new Color(0.95f, 0.95f, 0.92f);
            case "minecraft:pig":         return new Color(0.95f, 0.65f, 0.68f);
            case "minecraft:chicken":     return new Color(1.00f, 0.98f, 0.90f);
            case "minecraft:villager":    return new Color(0.60f, 0.45f, 0.35f);
            case "minecraft:horse":       return new Color(0.55f, 0.40f, 0.25f);
            case "minecraft:wolf":        return new Color(0.75f, 0.75f, 0.75f);
            case "minecraft:cat":         return new Color(0.85f, 0.70f, 0.45f);
            case "minecraft:fox":         return new Color(0.90f, 0.55f, 0.20f);
            case "minecraft:bat":         return new Color(0.35f, 0.28f, 0.25f);

            default:                      return new Color(0.85f, 0.30f, 0.85f); // unknown = magenta
        }
    }
}
