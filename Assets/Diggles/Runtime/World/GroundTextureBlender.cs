#nullable enable

namespace Diggles.Runtime.World
{
    using System.Collections.Generic;
    using System.IO;
    using Diggles.Runtime;
    using UnityEngine;

    /// <summary>
    /// Diggles ground texture pipeline (verified against game screenshots + RE of
    /// FUN_004cd020 / FUN_004cd9d0 / FUN_004cde30).
    ///
    /// Per tile, the engine composites THREE textures via 4-corner alpha mask:
    /// <code>
    /// 1. BASE = Gnd_X{NN}.tga random variant from 01..15
    /// family X by terrain_type (A grey-stone,
    /// B brown-earth, C warm-tan)
    /// 2. OVERLAY = urw2_NNNN.tga (Urwald grass/moss with corner pattern)
    /// 3. MASK = Msk_XXXX01.tga grayscale 0..255 (A=keep base, B=show overlay)
    /// result.rgb = lerp(BASE.rgb, OVERLAY.rgb, MASK.r / 255)
    /// </code>
    ///
    /// <para>For pure-pattern tiles (corners all 0 → no overlay, or all 1 → full overlay)
    /// there's no MASK file: just use the relevant layer directly.</para>
    ///
    /// <para>Variant index per tile is hashed from (tx, ty) so adjacent tiles get
    /// different variants → no obvious repeating grid.</para>
    /// </summary>
    public static class GroundTextureBlender
    {
        // Per-tile baked cache key: (signature → texture).
        private static readonly Dictionary<long, Texture2D> BakedCache = new();
        private static readonly Dictionary<string, Texture2D> SourceCache = new();
        private static string? cachedDataRoot;

        public static void ClearCache()
        {
            foreach (var t in BakedCache.Values) if (t != null) Object.DestroyImmediate(t);
            BakedCache.Clear();
            foreach (var t in SourceCache.Values) if (t != null) Object.DestroyImmediate(t);
            SourceCache.Clear();
            cachedDataRoot = null;
        }

        /// <summary>
        /// Returns a per-tile composited ground texture. Inputs:
        /// terrain_type from PMP, corner_pattern (0..15) from 4 neighbours' surface bits,
        /// tx/ty for variant hashing.
        /// </summary>
        public static Texture2D? GetTileTexture(string dataRoot, byte terrainType,
            int cornerPattern, int tx, int ty)
        {
            if (cachedDataRoot != null && cachedDataRoot != dataRoot) ClearCache();
            cachedDataRoot = dataRoot;

            // 4-bit pattern (0..15) + 4-bit variant + 4-bit base-family
            int variant = HashVariant(tx, ty);
            char baseFamily = TerrainBaseFamily(terrainType);
            long key = ((long)baseFamily << 16) | ((long)variant << 8) | (long)(cornerPattern & 0xF);
            if (BakedCache.TryGetValue(key, out var existing)) return existing;

            var basePath = Path.Combine(dataRoot, "Ground", $"Gnd_{baseFamily}{variant:D2}.tga");
            var baseTex = LoadOrSourceCache(basePath);
            if (baseTex == null)
            {
                Debug.LogWarning($"Missing base texture: {basePath}");
                return null;
            }

            // Pure base: no overlay needed (corners all 0 = no surface neighbours).
            if (cornerPattern == 0)
            {
                BakedCache[key] = baseTex;
                return baseTex;
            }

            // Overlay path: pick urw2_<NNNN> for the moss surface.
            int nw = (cornerPattern >> 3) & 1;
            int ne = (cornerPattern >> 2) & 1;
            int sw = (cornerPattern >> 1) & 1;
            int se = (cornerPattern >> 0) & 1;
            var overlayPath = Path.Combine(dataRoot, "Ground", "detail",
                $"urw2_{nw}{ne}{sw}{se}.tga");
            var overlayTex = LoadOrSourceCache(overlayPath);
            if (overlayTex == null)
            {
                BakedCache[key] = baseTex;
                return baseTex;
            }

            // All-corners overlay = no mask needed (urw2_1111 = full surface).
            if (cornerPattern == 0xF)
            {
                BakedCache[key] = overlayTex;
                return overlayTex;
            }

            // Mixed: load Msk_<XXXX>01.tga where bit=1 → B (overlay), bit=0 → A (base).
            // Mask is grayscale: WHITE (255) = base side (A), BLACK (0) = overlay side (B).
            char nwL = (nw == 1) ? 'B' : 'A';
            char neL = (ne == 1) ? 'B' : 'A';
            char swL = (sw == 1) ? 'B' : 'A';
            char seL = (se == 1) ? 'B' : 'A';
            var maskPath = Path.Combine(dataRoot, "Ground",
                $"Msk_{nwL}{neL}{swL}{seL}01.tga");
            var maskTex = LoadOrSourceCache(maskPath);
            if (maskTex == null)
            {
                Debug.LogWarning($"Missing mask: {maskPath}");
                BakedCache[key] = baseTex;
                return baseTex;
            }

            var baked = Composite(baseTex, overlayTex, maskTex);
            BakedCache[key] = baked!;
            return baked;
        }

        public static Texture2D? GetSideTexture(string dataRoot, int sub)
        {
            if (cachedDataRoot != null && cachedDataRoot != dataRoot) ClearCache();
            cachedDataRoot = dataRoot;
            // Cliff side & cave roof always use stone family (no random variant for now).
            var path = Path.Combine(dataRoot, "Ground", sub == 1 ? "Gnd_A05.tga" : "Gnd_A10.tga");
            return LoadOrSourceCache(path);
        }

        public static int TerrainSurfaceBit(byte terrainType)
        {
            // User feedback: "moss can grow anywhere". Any tile inside a template
            // (terrain_type != 0) contributes "B" (overlay) corner bit so moss can
            // grow there. Tiles outside templates (terrain == 0 default solid earth)
            // contribute "A" (base) so they stay plain brown.
            return terrainType != 0 ? 1 : 0;
        }

        // --- internals -------------------------------------------------------

        private static char TerrainBaseFamily(byte terrainType)
        {
            byte t = (byte)(terrainType & 0x7F);
            // 0x05 grass meadow / 0x04 stone → stone-like base under moss overlay = A
            // 0x02 dirt low → muted neutral (was Gnd_B but user said too brown) → A
            // default (terrain=0) → diggable earth, must remain BROWN but less saturated
            return t == 0 ? 'B' : 'A';
        }

        private static int HashVariant(int tx, int ty)
        {
            // Cheap hash → 1..15 (matches Gnd_X01..X15 variant indices).
            unchecked
            {
                int h = tx * 73856093 ^ ty * 19349663;
                h ^= h >> 13;
                return ((h & 0x7FFFFFFF) % 15) + 1;
            }
        }

        private static Texture2D? Composite(Texture2D baseTex, Texture2D overlayTex, Texture2D maskTex)
        {
            int w = baseTex.width, h = baseTex.height;
            if (overlayTex.width != w || overlayTex.height != h ||
                maskTex.width != w || maskTex.height != h)
            {
                Debug.LogWarning($"Composite size mismatch: " +
                    $"base {w}x{h}, overlay {overlayTex.width}x{overlayTex.height}, " +
                    $"mask {maskTex.width}x{maskTex.height}");
                return baseTex;
            }
            var bp = baseTex.GetPixels32();
            var op = overlayTex.GetPixels32();
            var mp = maskTex.GetPixels32();
            var outPx = new Color32[w * h];
            for (int i = 0; i < w * h; i++)
            {
                // Msk grayscale: white (R=255) = use base; black (R=0) = use overlay.
                int wt = 255 - mp[i].r;   // overlay weight
                int wb = 255 - wt;
                outPx[i] = new Color32(
                    (byte)((bp[i].r * wb + op[i].r * wt) / 255),
                    (byte)((bp[i].g * wb + op[i].g * wt) / 255),
                    (byte)((bp[i].b * wb + op[i].b * wt) / 255),
                    255);
            }
            var baked = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true)
            {
                name = $"Tile_{baseTex.name}_{overlayTex.name}_{maskTex.name}",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            baked.SetPixels32(outPx);
            baked.Apply(updateMipmaps: true);
            return baked;
        }

        private static Texture2D? LoadOrSourceCache(string path)
        {
            if (SourceCache.TryGetValue(path, out var cached)) return cached;
            Texture2D? t = null;
            if (File.Exists(path))
            {
                try
                {
                    var r = TgaLoader.LoadFromFile(path);
                    if (r != null) t = r.Texture;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"TGA load failed for {path}: {e.Message}");
                }
            }
            SourceCache[path] = t!;
            return t;
        }
    }
}
