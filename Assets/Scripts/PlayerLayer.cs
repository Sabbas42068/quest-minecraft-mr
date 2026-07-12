using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Globalization;

/// <summary>
/// Renders live Minecraft PLAYERS as markers on the tabletop map.
///
/// This is deliberately a SEPARATE system from the chunk/block rendering:
///   - Blocks change rarely  -> chunks poll slowly, rebuild meshes (expensive)
///   - Players move constantly -> this polls fast, just moves transforms (cheap)
///
/// Attach to the SAME GameObject as WorldMapManager (it reads the map's
/// settings so the markers land in the right place at the right scale).
/// </summary>
[RequireComponent(typeof(WorldMapManager))]
public class PlayerLayer : MonoBehaviour
{
    [Header("Player markers")]
    [Tooltip("Seconds between position polls. Low = smooth movement.")]
    public float pollInterval = 0.25f;

    [Tooltip("Marker height in Minecraft blocks (a player is ~2 blocks tall).")]
    public float markerHeightBlocks = 2f;

    [Tooltip("Marker width in Minecraft blocks.")]
    public float markerWidthBlocks = 0.8f;

    public Color markerColor = new Color(1f, 0.85f, 0.1f); // gold

    [Tooltip("Corrects the Minecraft->Unity yaw mismatch. -90 is usually right; " +
             "adjust in 90 steps if the facing is off.")]
    public float yawOffset = -90f;

    [Tooltip("Smooth the marker's movement between polls instead of snapping.")]
    public bool smoothMovement = true;
    public float smoothSpeed = 8f;

    private WorldMapManager map;

    // One marker GameObject per player uuid.
    private Dictionary<string, GameObject> markers = new Dictionary<string, GameObject>();
    // Where each marker is heading (for smoothing).
    private Dictionary<string, Vector3> targets = new Dictionary<string, Vector3>();

    private Material markerMat;

    void Start()
    {
        map = GetComponent<WorldMapManager>();

        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        markerMat = new Material(urpLit);
        markerMat.color = markerColor;

        StartCoroutine(PollLoop());
    }

    void Update()
    {
        if (!smoothMovement) return;

        // Glide each marker toward its latest known position, so movement looks
        // continuous instead of teleporting once per poll.
        foreach (KeyValuePair<string, GameObject> kv in markers)
        {
            if (!targets.TryGetValue(kv.Key, out Vector3 target)) continue;
            kv.Value.transform.localPosition = Vector3.Lerp(
                kv.Value.transform.localPosition, target, Time.deltaTime * smoothSpeed);
        }
    }

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return StartCoroutine(FetchPlayers());
            yield return new WaitForSeconds(pollInterval);
        }
    }

    IEnumerator FetchPlayers()
    {
        // includeData=true is REQUIRED — without it we get name/uuid but no Pos.
        string url = "http://" + map.serverIP + ":" + map.serverPort
                   + "/players?includeData=true";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                yield break;   // silently retry next tick

            ParseAndPlace(req.downloadHandler.text);
        }
    }

    [System.Serializable] private struct PlayerEntry
    {
        public string name;
        public string uuid;
        public string data;   // SNBT blob containing Pos and Rotation
    }
    [System.Serializable] private struct PlayerList { public PlayerEntry[] items; }

    // Pull "Pos:[1.23d,45.6d,7.89d]" out of the SNBT data string.
    private static readonly Regex posRegex = new Regex(
        @"Pos:\[\s*(-?[\d.]+)d?\s*,\s*(-?[\d.]+)d?\s*,\s*(-?[\d.]+)d?\s*\]");
    // Pull "Rotation:[yaw,pitch]" out.
    private static readonly Regex rotRegex = new Regex(
        @"Rotation:\[\s*(-?[\d.]+)f?\s*,\s*(-?[\d.]+)f?\s*\]");

    void ParseAndPlace(string json)
    {
        PlayerList list = JsonUtility.FromJson<PlayerList>("{\"items\":" + json + "}");
        if (list.items == null) return;

        HashSet<string> seen = new HashSet<string>();

        foreach (PlayerEntry p in list.items)
        {
            if (string.IsNullOrEmpty(p.data)) continue;

            Match m = posRegex.Match(p.data);
            if (!m.Success) continue;

            float wx = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            float wy = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            float wz = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

            seen.Add(p.uuid);

            GameObject marker = GetOrCreateMarker(p.uuid, p.name);

            // Convert Minecraft world coords -> local position on the map,
            // using the SAME transform the chunks use so it lines up exactly.
            targets[p.uuid] = map.WorldToMapLocal(new Vector3(wx, wy, wz))
                            + Vector3.up * (markerHeightBlocks * 0.5f * map.blockSize);

            if (!smoothMovement)
                marker.transform.localPosition = targets[p.uuid];

            // Face the direction the player is looking (yaw only).
            Match r = rotRegex.Match(p.data);
            if (r.Success)
            {
                float yaw = float.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                // Minecraft yaw increases CLOCKWISE; Unity's Y-rotation increases
                // COUNTER-clockwise. So we negate it, then apply an offset to line
                // up the zero point. Tune yawOffset in the Inspector if needed.
                marker.transform.localRotation = Quaternion.Euler(0f, -yaw + yawOffset, 0f);
            }
        }

        // Remove markers for players who logged off / left.
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

    GameObject GetOrCreateMarker(string uuid, string name)
    {
        if (markers.TryGetValue(uuid, out GameObject existing))
            return existing;

        // Body: a simple upright box, scaled to a player's rough size.
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "Player: " + name;
        Destroy(marker.GetComponent<Collider>());   // no physics needed
        marker.transform.SetParent(transform, false);
        marker.transform.localScale = new Vector3(
            markerWidthBlocks  * map.blockSize,
            markerHeightBlocks * map.blockSize,
            markerWidthBlocks  * map.blockSize);
        marker.GetComponent<Renderer>().material = markerMat;

        // A small "nose" so you can see which way they're facing.
        GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(nose.GetComponent<Collider>());
        nose.transform.SetParent(marker.transform, false);
        nose.transform.localScale = new Vector3(0.35f, 0.25f, 0.6f);
        nose.transform.localPosition = new Vector3(0f, 0.25f, 0.6f);
        nose.GetComponent<Renderer>().material = markerMat;

        markers[uuid] = marker;
        Debug.Log("PlayerLayer: tracking player '" + name + "'");
        return marker;
    }
}