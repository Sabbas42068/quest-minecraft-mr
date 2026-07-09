using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Milestone 1 (Quest 2): places a cube on the real table detected during
/// Space Setup. Waits for MRUK's scene data to load, finds the TABLE anchor,
/// and drops a cube on its surface.
///
/// Setup:
///  - MRUK.prefab must be in the scene (Scene Data Source = Device).
///  - Attach this script to an empty GameObject (e.g. GameManager).
///  - MRUK fires SceneLoadedEvent when the room model is ready; we hook it
///    in the Inspector OR via code (this script does it via code in Start).
/// </summary>
public class TableCubePlacer : MonoBehaviour
{
    [Tooltip("Edge length of the cube in metres. 0.1 = a 10cm block.")]
    public float cubeSize = 0.1f;

    void Start()
    {
        // Register for MRUK's scene-loaded event. This fires once the room
        // model (walls, floor, furniture from your scan) is available.
        MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
    }

    void OnSceneLoaded()
    {
        MRUKRoom room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogError("TableCubePlacer: No room found. Did Space Setup run, " +
                           "and is MRUK's data source set to Device?");
            return;
        }

        // Look through every anchor in the room for one labelled TABLE.
        MRUKAnchor tableAnchor = null;
        foreach (MRUKAnchor anchor in room.Anchors)
        {
            if (anchor.Label == MRUKAnchor.SceneLabels.TABLE)
            {
                tableAnchor = anchor;
                break;
            }
        }

        if (tableAnchor == null)
        {
            Debug.LogError("TableCubePlacer: No TABLE found in the scan. " +
                           "Re-run Space Setup and make sure you trace the table.");
            return;
        }

        // The anchor's transform sits at the centre of the table's top surface,
        // with 'up' pointing away from the surface. Spawn the cube slightly
        // above it so it rests on top rather than intersecting.
        Vector3 surfaceCentre = tableAnchor.transform.position;
        Vector3 spawnPos = surfaceCentre + tableAnchor.transform.up * (cubeSize * 0.5f);

        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "TableBlock";
        cube.transform.position = spawnPos;
        cube.transform.rotation = tableAnchor.transform.rotation;
        cube.transform.localScale = Vector3.one * cubeSize;

        // URP material so it renders correctly in stereo passthrough
        // (same fix as the first cube).
        Renderer r = cube.GetComponent<Renderer>();
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit != null) r.material = new Material(urpLit);
        r.material.color = new Color(0.3f, 0.7f, 0.3f); // grass green

        Debug.Log("TableCubePlacer: placed a cube on the table at " + spawnPos);
    }
}
