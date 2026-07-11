using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Shared, cached materials for block types. All chunks use the SAME material
/// instance for a given block type, so we don't create hundreds of duplicate
/// materials as the world grows.
/// </summary>
public class MaterialLibrary
{
    private Dictionary<string, Material> cache = new Dictionary<string, Material>();
    private Shader urpLit;

    public MaterialLibrary()
    {
        urpLit = Shader.Find("Universal Render Pipeline/Lit");
    }

    public Material Get(string blockId)
    {
        if (cache.TryGetValue(blockId, out Material m)) return m;

        m = new Material(urpLit);
        m.color = ColorFor(blockId);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        cache[blockId] = m;
        return m;
    }

    Color ColorFor(string id)
    {
        switch (id)
        {
            case "minecraft:grass_block":  return new Color(0.35f, 0.65f, 0.30f);
            case "minecraft:dirt":         return new Color(0.45f, 0.32f, 0.20f);
            case "minecraft:stone":        return new Color(0.50f, 0.50f, 0.52f);
            case "minecraft:cobblestone":  return new Color(0.45f, 0.45f, 0.47f);
            case "minecraft:gravel":       return new Color(0.42f, 0.40f, 0.40f);
            case "minecraft:coal_ore":     return new Color(0.25f, 0.25f, 0.27f);
            case "minecraft:iron_ore":     return new Color(0.70f, 0.60f, 0.50f);
            case "minecraft:sand":         return new Color(0.85f, 0.80f, 0.60f);
            case "minecraft:sandstone":    return new Color(0.80f, 0.75f, 0.58f);
            case "minecraft:water":        return new Color(0.25f, 0.40f, 0.85f);
            case "minecraft:lava":         return new Color(0.90f, 0.35f, 0.10f);
            case "minecraft:oak_log":      return new Color(0.40f, 0.30f, 0.18f);
            case "minecraft:birch_log":    return new Color(0.80f, 0.75f, 0.62f);
            case "minecraft:oak_leaves":   return new Color(0.25f, 0.55f, 0.25f);
            case "minecraft:birch_leaves": return new Color(0.35f, 0.60f, 0.30f);
            case "minecraft:deepslate":    return new Color(0.30f, 0.30f, 0.33f);
            case "minecraft:tuff":         return new Color(0.40f, 0.42f, 0.40f);
            case "minecraft:bedrock":      return new Color(0.15f, 0.15f, 0.15f);
            case "minecraft:snow_block":   return new Color(0.95f, 0.95f, 0.97f);
            case "minecraft:oak_planks":   return new Color(0.65f, 0.52f, 0.33f);
            default:                       return new Color(0.60f, 0.60f, 0.62f);
        }
    }
}
