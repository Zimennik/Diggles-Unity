#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;

    // Icon alpha conditioning — shared by the runtime loader (IconLoader) and the edit-time importer
    // (GuiSpriteProvisioner) so there is one source of truth.
    //
    // The original GUI icons (data/gui/icons/*) are RGB objects on a uniform gray color-key matte:
    // every fully-transparent texel is ≈ RGB(164,163,150). The DX7 engine keys that gray out and the
    // soft 8-bit alpha (body only ~56-89%, never 255) is just the 3D-render coverage. Sharpen does a
    // levels remap of the alpha (Floor → 0, Ceil → 255, smooth ramp between) so the soft body fills to
    // opaque while the anti-aliased edge gradient survives, and extrudes solid-object colour into the
    // rim so the AA shows object colour, not the matte. A high threshold hollows the body; a narrow
    // band snaps the edge to 1-bit.
    public static class IconAlpha
    {
        public const int Floor = 6;    // black point: alpha ≤ this → fully transparent (drops the gray key)
        public const int Ceil = 96;    // white point: alpha ≥ this → fully opaque (fills the soft body)
        public const int Solid = 96;   // "definitely object" alpha used as the colour-extrusion source

        public static void Sharpen(Color32[] px, int w, int h)
        {
            float lo = Floor, hi = Ceil;
            if (hi <= lo) hi = lo + 1f;

            // Extrude colour of solid-object texels (alpha ≥ Solid) outward into the soft/edge/bg region,
            // so the kept AA rim shows object colour instead of the gray render-matte.
            var solid = new bool[px.Length];
            for (int i = 0; i < px.Length; i++) solid[i] = px[i].a >= Solid;
            for (int iter = 0; iter < 8; iter++)
            {
                var next = (bool[])solid.Clone();
                bool any = false;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (solid[idx]) continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    if (x > 0     && solid[idx - 1]) { var p = px[idx - 1]; r += p.r; g += p.g; b += p.b; n++; }
                    if (x < w - 1 && solid[idx + 1]) { var p = px[idx + 1]; r += p.r; g += p.g; b += p.b; n++; }
                    if (y > 0     && solid[idx - w]) { var p = px[idx - w]; r += p.r; g += p.g; b += p.b; n++; }
                    if (y < h - 1 && solid[idx + w]) { var p = px[idx + w]; r += p.r; g += p.g; b += p.b; n++; }
                    if (n > 0) { px[idx].r = (byte)(r / n); px[idx].g = (byte)(g / n); px[idx].b = (byte)(b / n); next[idx] = true; any = true; }
                }
                solid = next;
                if (!any) break;
            }

            // Levels-remap the alpha → opaque body, transparent gray bg, smooth anti-aliased edge.
            for (int i = 0; i < px.Length; i++)
            {
                float na = Mathf.Clamp01((px[i].a - lo) / (hi - lo));
                px[i].a = (byte)Mathf.RoundToInt(na * 255f);
            }
        }
    }
}
