using UnityEngine;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Grid-snapped placement locked to the TABLE TOP.
/// The table surface height defines grid level 0. Every block's BOTTOM face
/// sits on a clean multiple of blockSize above that surface, so the first
/// layer rests flush on the table and stacks rise in exact cube increments.
/// Horizontal rows align to the table's yaw. No doubling: one block per cell.
/// </summary>
public class BlockPlacer : MonoBehaviour
{
    [Tooltip("Drag RightHandAnchor here (OVRCameraRig > TrackingSpace > RightHandAnchor).")]
    public Transform controllerAnchor;

    [Tooltip("Edge length of each block in metres. 0.05 = 5cm voxel.")]
    public float blockSize = 0.05f;

    [Tooltip("Max ray distance in metres.")]
    public float maxDistance = 5f;

    private Material blockMaterial;
    private HashSet<Vector3Int> filledCells = new HashSet<Vector3Int>();

    // Grid frame: yaw-only rotation for horizontal alignment, an origin at the
    // table, and the table-top height that defines vertical grid level 0.
    private Quaternion gridYaw = Quaternion.identity;
    private Vector3 gridOrigin = Vector3.zero;
    private float tableTopY = 0f;
    private bool frameReady = false;

    void Start()
    {
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        blockMaterial = urpLit != null ? new Material(urpLit) : null;
        if (blockMaterial != null)
            blockMaterial.color = new Color(0.3f, 0.7f, 0.3f); // grass green

        if (MRUK.Instance != null)
            MRUK.Instance.RegisterSceneLoadedCallback(CacheTableFrame);
    }

    void CacheTableFrame()
    {
        MRUKRoom room = MRUK.Instance.GetCurrentRoom();
        if (room == null) return;

        foreach (MRUKAnchor anchor in room.Anchors)
        {
            if (anchor.Label == MRUKAnchor.SceneLabels.TABLE)
            {
                gridOrigin = anchor.transform.position;
                tableTopY = anchor.transform.position.y; // table surface height

                Vector3 tableForward = anchor.transform.forward;
                tableForward.y = 0f;
                if (tableForward.sqrMagnitude < 0.001f)
                {
                    tableForward = anchor.transform.up;
                    tableForward.y = 0f;
                }
                if (tableForward.sqrMagnitude > 0.001f)
                    gridYaw = Quaternion.LookRotation(tableForward.normalized, Vector3.up);

                frameReady = true;
                Debug.Log("BlockPlacer: table frame cached. Top Y = " + tableTopY);
                return;
            }
        }
        Debug.LogWarning("BlockPlacer: no TABLE anchor found; using world axes.");
    }

    void Update()
    {
        if (controllerAnchor == null) return;

        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
        {
            Ray ray = new Ray(controllerAnchor.position, controllerAnchor.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance))
            {
                PlaceBlock(hit.point, hit.normal);
            }
        }
    }

    void PlaceBlock(Vector3 hitPoint, Vector3 surfaceNormal)
    {
        // --- HORIZONTAL: snap X/Z in the table's yaw-aligned frame ---
        Vector3 local = Quaternion.Inverse(gridYaw) * (hitPoint - gridOrigin);
        int cx = Mathf.RoundToInt(local.x / blockSize);
        int cz = Mathf.RoundToInt(local.z / blockSize);

        // --- VERTICAL: which stack level does the hit height correspond to? ---
        // Height above the table top, measured in whole blocks. Level 0 = the
        // layer resting directly on the table. We push the hit slightly up by
        // the surface normal first, so hitting the top of a block lands us on
        // the next level up rather than back on the same one.
        Vector3 nudged = hitPoint + surfaceNormal * (blockSize * 0.5f);
        int level = Mathf.FloorToInt((nudged.y - tableTopY) / blockSize);
        if (level < 0) level = 0; // never below the table

        Vector3Int cell = new Vector3Int(cx, level, cz);
        if (filledCells.Contains(cell)) return;   // one block per cell, no doubling
        filledCells.Add(cell);

        // --- Build the world position ---
        // Horizontal comes from the snapped X/Z in the yaw frame.
        Vector3 snappedLocalXZ = new Vector3(cx * blockSize, 0f, cz * blockSize);
        Vector3 worldXZ = gridYaw * snappedLocalXZ + gridOrigin;

        // Vertical: block CENTRE = table top + (level + 0.5) blocks, so the
        // block's BOTTOM face sits exactly on its grid level (level 0 flush
        // on the table).
        float centreY = tableTopY + (level + 0.5f) * blockSize;

        Vector3 worldPos = new Vector3(worldXZ.x, centreY, worldXZ.z);

        GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = "Block " + cell;
        block.transform.position = worldPos;
        block.transform.rotation = gridYaw;
        block.transform.localScale = Vector3.one * blockSize;

        if (blockMaterial != null)
            block.GetComponent<Renderer>().material = blockMaterial;
    }
}