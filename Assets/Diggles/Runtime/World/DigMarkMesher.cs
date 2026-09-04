#nullable enable

namespace Diggles.Runtime.World
{
    using System.Collections.Generic;
    using UnityEngine;

    // Vertex/index sink for the DigMark tessellator. Positions are in the native tile frame
    // (x = tile x, y = tile y — the same 2D coords FUN_00568410 writes into its CVectorBuffer);
    // Sel = the native per-vertex "color selector" (vertex+0xc before the draw pass rewrites it):
    // 0 = the standard/valid pass, 1 = the depth/invalid pass. The renderer (DigMarkOverlay)
    // maps Sel to the actual palette per mesh kind and lifts positions onto the plane z = 15.
    public sealed class DigMarkMeshData
    {
        public readonly List<Vector2> Pos = new();
        public readonly List<byte> Sel = new();
        public readonly List<int> Tris = new();

        public int VertexCount => Pos.Count;

        public void Clear() { this.Pos.Clear(); this.Sel.Clear(); this.Tris.Clear(); }

        internal int Add(float x, float y, byte sel)
        {
            this.Pos.Add(new Vector2(x, y));
            this.Sel.Add(sel);
            return this.Pos.Count - 1;
        }

        internal void Tri(int a, int b, int c) { this.Tris.Add(a); this.Tris.Add(b); this.Tris.Add(c); }
    }

    // Faithful port of the original DigMark marching-squares tessellation
    // (Wiggles\Map\DigMark.cpp — file name leaked at FUN_00568410:56):
    //
    // FUN_0056ae30 (EmitCell) — the field function. One marching-squares cell (x, y) samples
    // the per-owner dig nibbles of its 4 corner tiles: A=(x,y), B=(x+1,y), C=(x,y+1),
    // D=(x+1,y+1) (the caller FUN_0056af90:497-508 fetches exactly those four tiles and
    // shifts out `>> owner*4 & 0xF`). The cell is rendered twice, split on nibble bit 3:
    // pass sel=1: corner present iff (nib & 8) != 0 — the depth-dig marks (cnt|8)
    // pass sel=0: corner present iff nib != 0 && !(nib&8) — the standard tunnel dummy (3)
    // (the (nib&7)+1 "strength" the original passes is only ever tested against 0 — it exists
    // to keep nibble 8 nonzero; the geometry uses fixed radii.)
    // roundFlag (FUN_0056ae30:34-37) is computed on the combined corners: true when >2 corners
    // are marked, or exactly 2 are marked adjacently ((A!=0) != (D!=0) — A/D are diagonal).
    // It coarsens single-corner arcs to 2 segments and bulges their midpoint by √2
    // (DAT_007371a0) so quadrants from different passes/cells meet without a pinch.
    //
    // FUN_00568410 (EmitPass) — the cell tessellator, by per-pass marked-corner count:
    // 4: full unit quad (skipped entirely in outline mode — FUN_00568410:67-69);
    // 1: quarter-disc "pie" at the marked corner: center + N-segment arc (N = roundFlag?2:4,
    // FUN_00568410:261) with two rings — outer 0.49 (_DAT_00737088) and rim 0.30
    // (DAT_00737060); fill = center fan + rim band, where the band triangles overlap the
    // fan → the rim renders at double alpha (the bright contour). Outline mode emits the
    // band only (6 vs 9 indices per segment, FUN_00568410:384-546);
    // 2 adjacent: a half-cell strip from the marked edge to 0.49, rim band 0.30..0.49
    // doubled (mirrored side uses 0.51/0.70 = 1−0.49/1−0.30, FUN_00568410:555-818);
    // 2 diagonal: recursion — two independent single-corner pies (FUN_00568410:819-822);
    // 3: rounded notch around the one empty corner: 3 arc positions (step π/4 =
    // DAT_007370d0, midpoint ×√2 reaches the far cell corner exactly), rings at
    // 0.51/0.70/1.0, same doubled-rim fill (FUN_00568410:823-1304).
    // Corner angle table (both pie and notch, FUN_00568410:236-260 /:823-848):
    // A → −π/2 (DAT_00737744), B → −π (DAT_00737790), C → 0, D → +π/2 (DAT_007371e0);
    // arc point = (cx + cos(a)·r, cy − sin(a)·r), sweep +π/2.
    //
    // All float constants are verified against Diggles.exe.rdata.
    public static class DigMarkMesher
    {
        const float OuterR = 0.49f;      // _DAT_00737088 — the contour radius / strip edge
        const float RimR = 0.30f;        // DAT_00737060 — the inner rim radius
        const float FarOuterR = 0.51f;   // _DAT_00737098 — contour from the far side (1 − 0.49)
        const float FarRimR = 0.70f;     // DAT_007370b0 — rim from the far side (1 − 0.30)
        const float Sqrt2 = 1.41421354f; // DAT_007371a0 — midpoint bulge (reach the diagonal)
        const float HalfPi = Mathf.PI * 0.5f;

        // FUN_0056ae30 — split the cell's 4 corner nibbles into the two draw passes.
        // a/b/c/d = the dig nibbles of tiles (x,y), (x+1,y), (x,y+1), (x+1,y+1).
        public static void EmitCell(int x, int y, int a, int b, int c, int d, bool outline, DigMarkMeshData md)
        {
            int count = (a != 0 ? 1 : 0) + (b != 0 ? 1 : 0) + (c != 0 ? 1 : 0) + (d != 0 ? 1 : 0);
            if (count == 0) return;
            bool round = count > 2 || (count == 2 && (a != 0) != (d != 0));
            EmitPass(x, y, 1, round, (a & 8) != 0, (b & 8) != 0, (c & 8) != 0, (d & 8) != 0, outline, md);
            EmitPass(x, y, 0, round,
                a != 0 && (a & 8) == 0, b != 0 && (b & 8) == 0,
                c != 0 && (c & 8) == 0, d != 0 && (d & 8) == 0, outline, md);
        }

        // FUN_00568410 — one pass of one cell (corner booleans of THIS pass only).
        static void EmitPass(int x, int y, byte sel, bool round, bool a, bool b, bool c, bool d,
            bool outline, DigMarkMeshData md)
        {
            while (true)
            {
                int n = (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0);
                switch (n)
                {
                    case 0: return;
                    case 4:
                        if (!outline) EmitQuad(x, y, sel, md);   // FUN_00568410:67-69
                        return;
                    case 1:
                        EmitCorner(x, y, sel, round, b, c, d, outline, md);
                        return;
                    case 3:
                        EmitNotch(x, y, sel, a, b, c, outline, md);
                        return;
                }
                // n == 2. Adjacent iff exactly one of the A/D diagonal is marked (FUN_00568410:555).
                if (a != d)
                {
                    if (a)
                    {
                        if (b) EmitBandTop(x, y, sel, outline, md);
                        if (c) EmitBandLeft(x, y, sel, outline, md);
                    }
                    if (d)
                    {
                        if (c) EmitBandBottom(x, y, sel, outline, md);
                        if (b) EmitBandRight(x, y, sel, outline, md);
                    }
                    return;
                }
                // Diagonal pair (A&D or B&C): two independent pies — the native tail recursion
                // FUN_00568410:819-822 first draws {A,B}, then loops on {C,D}.
                EmitPass(x, y, sel, round, a, b, false, false, outline, md);
                a = false; b = false;
            }
        }

        // count==4 — full unit quad (FUN_00568410:67-235).
        static void EmitQuad(int x, int y, byte sel, DigMarkMeshData md)
        {
            int v0 = md.Add(x, y, sel);
            md.Add(x + 1f, y, sel);
            md.Add(x, y + 1f, sel);
            md.Add(x + 1f, y + 1f, sel);
            md.Tri(v0, v0 + 1, v0 + 2);
            md.Tri(v0 + 1, v0 + 2, v0 + 3);
        }

        // count==1 — quarter-disc pie at the marked corner (FUN_00568410:236-550).
        // Only b/c/d are passed: "none of them" means the marked corner is A.
        static void EmitCorner(int x, int y, byte sel, bool round, bool b, bool c, bool d,
            bool outline, DigMarkMeshData md)
        {
            float cx = x, cy = y, ang = -HalfPi;              // A (DAT_00737744)
            if (b) { cx = x + 1f; cy = y; ang = -Mathf.PI; }  // B (DAT_00737790)
            if (c) { cx = x; cy = y + 1f; ang = 0f; }         // C
            if (d) { cx = x + 1f; cy = y + 1f; ang = HalfPi; } // D (DAT_007371e0)

            int n = round ? 2 : 4;                             // (param_5 ^ 1)*2 + 2
            float step = HalfPi / n;
            int v0 = md.Add(cx, cy, sel);                      // center (unreferenced in outline mode)
            for (int i = 0; i <= n; i++)
            {
                float k = round && i == 1 ? Sqrt2 : 1f;        // DAT_00737138 → DAT_007371a0
                float ca = Mathf.Cos(ang + i * step);
                float sa = Mathf.Sin(ang + i * step);
                md.Add(cx + ca * (OuterR * k), cy - sa * (OuterR * k), sel);
                md.Add(cx + ca * (RimR * k), cy - sa * (RimR * k), sel);
            }
            for (int s = 0; s < n; s++)
            {
                int o0 = v0 + 1 + 2 * s, i0 = v0 + 2 + 2 * s, o1 = v0 + 3 + 2 * s, i1 = v0 + 4 + 2 * s;
                if (!outline) md.Tri(v0, o0, o1);              // fan — covers the whole pie
                md.Tri(o0, o1, i1);                            // rim band (overlaps the fan →
                md.Tri(o0, i0, i1);                            // double alpha = bright contour)
            }
        }

        // count==2 adjacent — half-cell strip + doubled rim band (FUN_00568410:555-818).
        // Vertex layout per the native branches: v0/v1 = rim line, v2/v3 = contour line,
        // v4/v5 = the marked cell edge. Band = {0,1,2}{1,2,3}; fill adds {2,3,4}{3,4,5}.
        static void EmitBandTop(int x, int y, byte sel, bool outline, DigMarkMeshData md) =>
            EmitBand(sel, outline, md,
                new Vector2(x, y + RimR), new Vector2(x + 1f, y + RimR),
                new Vector2(x, y + OuterR), new Vector2(x + 1f, y + OuterR),
                new Vector2(x, y), new Vector2(x + 1f, y));

        static void EmitBandLeft(int x, int y, byte sel, bool outline, DigMarkMeshData md) =>
            EmitBand(sel, outline, md,
                new Vector2(x + RimR, y), new Vector2(x + RimR, y + 1f),
                new Vector2(x + OuterR, y), new Vector2(x + OuterR, y + 1f),
                new Vector2(x, y), new Vector2(x, y + 1f));

        static void EmitBandBottom(int x, int y, byte sel, bool outline, DigMarkMeshData md) =>
            EmitBand(sel, outline, md,
                new Vector2(x, y + FarRimR), new Vector2(x + 1f, y + FarRimR),
                new Vector2(x, y + FarOuterR), new Vector2(x + 1f, y + FarOuterR),
                new Vector2(x, y + 1f), new Vector2(x + 1f, y + 1f));

        static void EmitBandRight(int x, int y, byte sel, bool outline, DigMarkMeshData md) =>
            EmitBand(sel, outline, md,
                new Vector2(x + FarRimR, y), new Vector2(x + FarRimR, y + 1f),
                new Vector2(x + FarOuterR, y), new Vector2(x + FarOuterR, y + 1f),
                new Vector2(x + 1f, y), new Vector2(x + 1f, y + 1f));

        static void EmitBand(byte sel, bool outline, DigMarkMeshData md,
            Vector2 rim0, Vector2 rim1, Vector2 con0, Vector2 con1, Vector2 edge0, Vector2 edge1)
        {
            int v0 = md.Add(rim0.x, rim0.y, sel);
            md.Add(rim1.x, rim1.y, sel);
            md.Add(con0.x, con0.y, sel);
            md.Add(con1.x, con1.y, sel);
            md.Add(edge0.x, edge0.y, sel);
            md.Add(edge1.x, edge1.y, sel);
            md.Tri(v0, v0 + 1, v0 + 2);          // rim band (overlaps the fill strip → 2× alpha)
            md.Tri(v0 + 1, v0 + 2, v0 + 3);
            if (outline) return;
            md.Tri(v0 + 2, v0 + 3, v0 + 4);      // fill: contour line → marked edge
            md.Tri(v0 + 3, v0 + 4, v0 + 5);
        }

        // count==3 — rounded notch around the single empty corner (FUN_00568410:823-1304).
        // Only a/b/c are passed as "is empty": none set means D is the empty one.
        static void EmitNotch(int x, int y, byte sel, bool aSet, bool bSet, bool cSet,
            bool outline, DigMarkMeshData md)
        {
            float cx = x + 1f, cy = y + 1f, ang = HalfPi;      // D empty (DAT_007371e0)
            if (!aSet) { cx = x; cy = y; ang = -HalfPi; }      // A empty
            if (!bSet) { cx = x + 1f; cy = y; ang = -Mathf.PI; } // B empty
            if (!cSet) { cx = x; cy = y + 1f; ang = 0f; }      // C empty

            int v0 = -1;
            for (int i = 0; i < 3; i++)                        // step π/4 (DAT_007370d0)
            {
                float k = i == 1 ? Sqrt2 : 1f;                 // midpoint reaches the far corner
                float ca = Mathf.Cos(ang + i * (HalfPi * 0.5f));
                float sa = Mathf.Sin(ang + i * (HalfPi * 0.5f));
                int v = md.Add(cx + ca * (FarOuterR * k), cy - sa * (FarOuterR * k), sel);
                if (i == 0) v0 = v;
                md.Add(cx + ca * (FarRimR * k), cy - sa * (FarRimR * k), sel);
                md.Add(cx + ca * k, cy - sa * k, sel);         // far ring, radius 1·k
            }
            // Native index tables (FUN_00568410:975-1192 fill /:1195-1303 outline).
            if (!outline)
            {
                md.Tri(v0 + 2, v0 + 5, v0 + 3);
                md.Tri(v0 + 3, v0 + 6, v0 + 8);
                md.Tri(v0 + 0, v0 + 2, v0 + 3);
                md.Tri(v0 + 0, v0 + 1, v0 + 3);                // rim band (doubled by the fill)
                md.Tri(v0 + 4, v0 + 3, v0 + 1);
                md.Tri(v0 + 3, v0 + 7, v0 + 6);
                md.Tri(v0 + 3, v0 + 4, v0 + 7);
                md.Tri(v0 + 5, v0 + 3, v0 + 8);
                return;
            }
            md.Tri(v0 + 0, v0 + 1, v0 + 3);
            md.Tri(v0 + 4, v0 + 3, v0 + 1);
            md.Tri(v0 + 3, v0 + 7, v0 + 6);
            md.Tri(v0 + 3, v0 + 4, v0 + 7);
        }
    }
}
