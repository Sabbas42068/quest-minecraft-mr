using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Spawns and positions a grid of WorldChunks that together render a window
/// onto the Minecraft world.
///
/// Structured as centre + radius + scale so growing toward the full-world /
/// floor-map vision is a matter of changing numbers, not rewriting:
///   - worldCentre : where in Minecraft the view is focused
///   - renderRadius: how many chunks out from the centre to show
///   - blockSize   : the scale (metres per Minecraft block) — your zoom level
///
/// Later additions that slot in naturally on top of this:
///   - dynamic load/unload as the viewer pans
///   - LOD (bigger blockSize + coarser chunks for distant areas)
///   - a separate entity layer for players/mobs (they update far more often
///     than blocks, so they want their own fast poll path, NOT chunks)
/// </summary>
public class WorldMapManager : MonoBehaviour
{
    [Header("Minecraft server")]
    public string serverIP = "192.168.0.112";
    public int serverPort = 8000;

    [Header("What part of the world to show")]
    [Tooltip("Minecraft coords the view is centred on.")]
    public Vector3Int worldCentre = new Vector3Int(100, 70, 200);

    [Tooltip("How many chunks out from centre, horizontally. 1 = 3x3, 2 = 5x5...")]
    public int renderRadius = 2;

    [Tooltip("How many chunks tall. 1 keeps it a thin slab (good for a map).")]
    public int verticalChunks = 2;

    [Header("Chunk settings")]
    [Tooltip("Blocks per chunk side. Smaller = cheaper individual updates.")]
    public int chunkSize = 8;

    [Tooltip("Metres per Minecraft block. This is your zoom/scale.")]
    public float blockSize = 0.015f;

    [Header("Live updates")]
    public float pollInterval = 1.5f;
    public bool livePolling = true;

    [Header("Placement")]
    [Tooltip("Anchor the map to the detected table. Off = anchor to this object.")]
    public bool anchorToTable = true;

    private MaterialLibrary materials;

    void Start()
    {
        materials = new MaterialLibrary();

        if (anchorToTable && MRUK.Instance != null)
            MRUK.Instance.RegisterSceneLoadedCallback(OnSceneReady);
        else
            SpawnChunks();
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
                    transform.position = a.transform.position;

                    Vector3 fwd = a.transform.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 0.001f) { fwd = a.transform.up; fwd.y = 0f; }
                    if (fwd.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                    break;
                }
            }
        }
        SpawnChunks();
    }

    void SpawnChunks()
    {
        // Snap the centre to the chunk grid, so chunks tile cleanly.
        Vector3Int gridOrigin = new Vector3Int(
            Mathf.FloorToInt(worldCentre.x / (float)chunkSize) * chunkSize,
            Mathf.FloorToInt(worldCentre.y / (float)chunkSize) * chunkSize,
            Mathf.FloorToInt(worldCentre.z / (float)chunkSize) * chunkSize
        );

        int spawned = 0;
        int total = (renderRadius * 2 + 1) * (renderRadius * 2 + 1) * verticalChunks;

        // Centre the whole map on the anchor point.
        float span = (renderRadius * 2 + 1) * chunkSize * blockSize;
        Vector3 centringOffset = new Vector3(-span * 0.5f, 0f, -span * 0.5f);

        for (int cx = -renderRadius; cx <= renderRadius; cx++)
        for (int cz = -renderRadius; cz <= renderRadius; cz++)
        for (int cy = 0; cy < verticalChunks; cy++)
        {
            Vector3Int coord = new Vector3Int(cx, cy, cz);

            GameObject go = new GameObject("Chunk " + coord);
            go.transform.SetParent(transform, false);

            // Local position of this chunk within the map.
            go.transform.localPosition = new Vector3(cx * chunkSize, cy * chunkSize, cz * chunkSize)
                                         * blockSize + centringOffset;
            go.transform.localRotation = Quaternion.identity;

            WorldChunk chunk = go.AddComponent<WorldChunk>();
            chunk.serverIP     = serverIP;
            chunk.serverPort   = serverPort;
            chunk.chunkCoord   = coord;
            chunk.chunkSize    = chunkSize;
            chunk.blockSize    = blockSize;
            chunk.worldOrigin  = gridOrigin;
            chunk.pollInterval = pollInterval;
            chunk.livePolling  = livePolling;
            chunk.materials    = materials;

            // Stagger start times across the poll interval so all chunks don't
            // hammer the server simultaneously.
            float delay = (spawned / (float)total) * pollInterval;
            chunk.Initialise(delay);

            spawned++;
        }

        Debug.Log("WorldMapManager: spawned " + spawned + " chunks ("
                  + chunkSize + " blocks each).");
    }
}
