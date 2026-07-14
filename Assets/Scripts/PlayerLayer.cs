using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Globalization;

/// <summary>
/// Renders live Minecraft PLAYERS as markers on the tabletop map.
/// Uses SmoothedMarker (velocity extrapolation) so movement is fluid between
/// polls rather than lagging or stepping.
///
/// Attach to the SAME GameObject as WorldMapManager.
/// </summary>
[RequireComponent(typeof(WorldMapManager))]
public class PlayerLayer : MonoBehaviour
{
    [Header("Polling")]
    [Tooltip("Seconds between position polls. Players are a tiny payload, so " +
             "polling fast is cheap. 0.1 = very responsive.")]
    public float pollInterval = 0.1f;

    [Header("Marker")]
    public float markerHeightBlocks = 2f;
    public float markerWidthBlocks = 0.8f;
    public Color markerColor = new Color(1f, 0.85f, 0.1f);

    [Tooltip("Fine-tunes facing. Try 0, 90, 180 or -90.")]
    public float yawOffset = 0f;

    [Header("Motion smoothing")]
    public bool smoothMovement = true;
    [Tooltip("How tightly the marker tracks the predicted position.")]
    public float correctionSpeed = 12f;
    [Tooltip("How fast the marker turns to match the player's facing.")]
    public float rotationSpeed = 10f;

    private WorldMapManager map;
    private Material markerMat;

    private Dictionary<string, GameObject> markers = new Dictionary<string, GameObject>();
    private Dictionary<string, SmoothedMarker> motion = new Dictionary<string, SmoothedMarker>();

    void Start()
    {
        map = GetComponent<WorldMapManager>();
        markerMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        markerMat.color = markerColor;
        StartCoroutine(PollLoop());
    }

    void Update()
    {
        if (!smoothMovement) return;
        foreach (KeyValuePair<string, GameObject> kv in markers)
            if (motion.TryGetValue(kv.Key, out SmoothedMarker sm))
                sm.Tick(kv.Value.transform);
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
        string url = "http://" + map.serverIP + ":" + map.serverPort
                   + "/players?includeData=true";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Accept", "application/json");
            req.timeout = 10;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;
            ParseAndPlace(req.downloadHandler.text);
        }
    }

    [System.Serializable] private struct PlayerEntry
    { public string name; public string uuid; public string data; }
    [System.Serializable] private struct PlayerList { public PlayerEntry[] items; }

    private static readonly Regex posRegex = new Regex(
        @"Pos:\[\s*(-?[\d.]+)d?\s*,\s*(-?[\d.]+)d?\s*,\s*(-?[\d.]+)d?\s*\]");
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

            Vector3 pos = map.WorldToMapLocal(new Vector3(wx, wy, wz))
                        + Vector3.up * (markerHeightBlocks * 0.5f * map.blockSize);

            Quaternion rot = marker.transform.localRotation;
            Match r = rotRegex.Match(p.data);
            if (r.Success)
            {
                float yaw = float.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                rot = Quaternion.Euler(0f, -yaw + yawOffset, 0f);
            }

            SmoothedMarker sm = motion[p.uuid];
            if (smoothMovement) sm.PushUpdate(pos, rot);
            else                sm.SnapTo(marker.transform, pos, rot);
        }

        List<string> gone = new List<string>();
        foreach (string uuid in markers.Keys)
            if (!seen.Contains(uuid)) gone.Add(uuid);
        foreach (string uuid in gone)
        {
            Destroy(markers[uuid]);
            markers.Remove(uuid);
            motion.Remove(uuid);
        }
    }

    GameObject GetOrCreateMarker(string uuid, string name)
    {
        if (markers.TryGetValue(uuid, out GameObject existing)) return existing;

        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "Player: " + name;
        Destroy(marker.GetComponent<Collider>());
        marker.transform.SetParent(transform, false);
        marker.transform.localScale = new Vector3(
            markerWidthBlocks * map.blockSize,
            markerHeightBlocks * map.blockSize,
            markerWidthBlocks * map.blockSize);
        marker.GetComponent<Renderer>().material = markerMat;

        GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(nose.GetComponent<Collider>());
        nose.transform.SetParent(marker.transform, false);
        nose.transform.localScale = new Vector3(0.35f, 0.25f, 0.6f);
        nose.transform.localPosition = new Vector3(0f, 0.25f, 0.6f);
        nose.GetComponent<Renderer>().material = markerMat;

        markers[uuid] = marker;
        SmoothedMarker sm = new SmoothedMarker();
        sm.correctionSpeed = correctionSpeed;
        sm.rotationSpeed = rotationSpeed;
        motion[uuid] = sm;

        Debug.Log("PlayerLayer: tracking '" + name + "'");
        return marker;
    }
}
