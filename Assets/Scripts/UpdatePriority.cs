using UnityEngine;

/// <summary>
/// Shared focus point for distance-based update prioritisation.
///
/// The idea: changes to the world almost always happen NEAR THE PLAYER (they're
/// mining, building, mobs are active around them). A chunk five rings away is
/// almost certainly static, so polling it as often as the player's own chunk
/// wastes a network request every cycle.
///
/// So chunks near the player poll fast; distant chunks poll slowly. The "hot
/// zone" follows the player as they walk. This cuts total request volume
/// dramatically, which is what makes rendering a LARGE world affordable.
///
/// PlayerLayer writes the focus here; WorldChunk reads it.
/// </summary>
public static class UpdatePriority
{
    /// The player's current position in Minecraft world coords.
    /// Null-ish (hasFocus == false) until the first player is seen.
    public static Vector3 playerWorldPos;
    public static bool hasFocus = false;

    public static void SetFocus(Vector3 minecraftPos)
    {
        playerWorldPos = minecraftPos;
        hasFocus = true;
    }

    /// <summary>
    /// How much to multiply a chunk's base poll interval by, based on how many
    /// chunks away it is from the player. Ring 0 = the player's own chunk.
    /// </summary>
    public static float IntervalMultiplier(float chunkDistance)
    {
        // No player seen yet -> treat everything as mid priority so the world
        // still refreshes at a sane rate.
        if (!hasFocus) return 2f;

        if (chunkDistance < 1.5f) return 1f;    // the player's chunk + immediate neighbours
        if (chunkDistance < 3.0f) return 2f;    // near ring
        if (chunkDistance < 5.0f) return 4f;    // mid ring
        if (chunkDistance < 8.0f) return 8f;    // far ring
        return 16f;                              // distant — still updates, just rarely
    }
}
