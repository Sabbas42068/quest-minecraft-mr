using UnityEngine;

/// <summary>
/// Spawns a single cube in front of the player when the scene starts.
/// This is the "hello world" of placing your own virtual content into
/// the passthrough scene. Later this gets replaced/extended to place
/// cubes on a detected table surface and then in a full voxel grid.
/// </summary>
public class CubeSpawner : MonoBehaviour
{
    [Tooltip("How far in front of the headset the cube spawns, in metres.")]
    public float distanceInFront = 1.0f;

    [Tooltip("Height offset from the headset, in metres. Negative = lower, roughly table height.")]
    public float heightOffset = -0.4f;

    [Tooltip("Edge length of the cube, in metres. 0.1 = a 10cm block.")]
    public float cubeSize = 0.1f;

    void Start()
    {
        // Find the headset's eye camera so we can spawn relative to where the player looks.
        Camera headset = Camera.main;
        if (headset == null)
        {
            Debug.LogError("CubeSpawner: No camera tagged 'MainCamera' found. " +
                           "Make sure CenterEyeAnchor's camera is tagged MainCamera.");
            return;
        }

        // Work out a spawn position: in front of the headset, dropped down to table-ish height.
        Vector3 spawnPos = headset.transform.position
                         + headset.transform.forward * distanceInFront
                         + Vector3.up * heightOffset;

        // Create a cube primitive (comes with a mesh + collider already).
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "MyFirstBlock";
        cube.transform.position = spawnPos;
        cube.transform.localScale = Vector3.one * cubeSize;

        // Give it a URP-compatible material. Spawned primitives default to a
        // material that can render incorrectly in stereo + passthrough (showing
        // up as two flat cubes, one per eye). Explicitly using the URP/Lit
        // shader fixes per-eye world-space rendering.
        Renderer r = cube.GetComponent<Renderer>();
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit != null)
        {
            r.material = new Material(urpLit);
        }
        else
        {
            Debug.LogWarning("CubeSpawner: URP/Lit shader not found, using default material.");
        }
        r.material.color = new Color(0.3f, 0.7f, 0.3f); // grass-ish green

        Debug.Log("CubeSpawner: spawned a block at " + spawnPos);
    }
}
