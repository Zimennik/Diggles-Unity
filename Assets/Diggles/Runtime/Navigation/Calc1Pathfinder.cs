#nullable enable

namespace Diggles.Runtime.Navigation
{
    using System.Collections.Generic;

    // Faithful port of CMapPathfinder::Calc1 (FUN_0059d640) — the original tile-level pathfinder,
    // running on NavWorld's FUN_005a6350-derived graph. Ground truth: Docs/re_parts/calc1-confirm.md
    // — which corrected several claims of the older
    // ORIGINAL_PATHFINDING_RE.md §2. Key spec points implemented here:
    //
    // • float wrapper FUN_0059d440 scales ×8 and drops the slab-depth (native z) — strictly 2-D
    // in the (x,y) side-view plane;
    // • start relax (r=1..32): only the 4 axis probes per ring, order +y,+x,−x,−y (not the ring
    // perimeter); no hit ⇒ start stays put (validity decides later);
    // • goal relax (r=1..12): the 8-probe directional fan, heavily +y(down)-biased:
    // (0,+r) (0,−r/4) (+r/2,0) (−r/2,0) (+r/3,+r/2) (−r/3,+r/2) (+r/3,−r/5) (−r/3,−r/5);
    // • post-snap overhang nudge on both endpoints: while (sub(x, y+1) & 0x02) y += 1 — slide
    // down to the bottom floor row of a ledge (keys on the floor bit, not the walkable bit);
    // • walkable(s):= tile BaseCost != −1 and (+0x3c & 0x01);
    // • search: FIFO Bellman-Ford over tiles, neighbour order +y, −y, +x, −x; seed
    // dist[start] = Base[start] + 1 (the engine's self-cost); strict < improvement;
    // • variant dispatch (0059d640:417-433, corrected direction): iOwner > 7 ⇒ cost v1
    // (cliff 0x114=276), no owner gate; iOwner ≤ 7 ⇒ cost v2 (cliff 0x10a=266) + the owner
    // gate tile[0x34+iOwner] (gate data not present in our world load — hook passes; see note);
    // CAct_MetaWalk's CalcPath passes iOwner = layer + (owner[+0x7d8]==0 ? 100: 0), so the
    // campaign default (+0x7d8==0) lands in the >7 band = v1;
    // • non-climbers gate neighbours on the +0x12&0x40 floor/object-passage flag. Variant B's
    // −x mask is the native 0xA4 defect: 0x20/0x04 are the packed pair's subY/subX high bits
    // and 0x80 is the extra flag (0059bf90:328; packed decode 0059d640:579-584);
    // • cost: cliffXOR ⇒ Base[T]+cliff−16·min(dig); neither-floor ⇒ Base[T]+1+16·|Δdig|;
    // both-floor ⇒ Base[T]+1; dig(t) = BDB aux[0x24] of the tile's block;
    // • budget: counter = enqueued writes; on first write of the goal clamp
    // maxNodes = min(maxNodes, round(enqueued·1.5)); stop when head ≥ maxNodes;
    // • endpoint margin: both endpoints must lie ≥ 0x28=40 sub-cells inside the sub-bounds
    // (0059d640:102-105 start + FUN_0040b8a0 goal) ⇒ else null;
    // • output: relaxed start, each tile's packed exit pair (+0x12 bits0-2=x, bits3-5=y), relaxed goal; start==goal
    // after relax ⇒ a 1-point path. bLines is not used by MetaWalk (bLines=0) — not ported.
    public sealed class Calc1Pathfinder
    {
        const int CliffV1 = 0x114;      // 276 — iOwner > 7 (campaign default via the +100 band shift)
        const int CliffV2 = 0x10a;      // 266 — iOwner ≤ 7 (the owner-gated/territory policy)
        const int StepCost = 0x10;      // 16 per dig-nibble step
        const int EdgeMargin = 0x28;    // 40 sub-cells endpoint reject
        const int DefaultMaxNodes = 200000;   // caller default; the ×1.5 clamp self-limits after goal hit

        readonly NavWorld _nav;
        readonly int[] _dist;
        readonly int[] _pred;
        readonly int[] _stamp;          // generation stamps — O(1) reset between calls
        int _gen;
        readonly Queue<int> _queue = new();

        public Calc1Pathfinder(NavWorld nav)
        {
            _nav = nav;
            int n = nav.Width * nav.Height;
            _dist = new int[n];
            _pred = new int[n];
            _stamp = new int[n];
        }

        // Endpoints in SUB-CELL coords. iOwner: pass layer + 100 for the campaign default
        // (no territory gates, cost v1) or the bare layer 0..7 for the owner-gated policy.
        public List<(int sx, int sy)>? FindPath(int startX, int startY, int goalX, int goalY,
                                                bool canClimb, int iOwner = 100)
        {
            // OOB margin reject (0059d640:102-105).
            if (!InMargin(startX, startY) || !InMargin(goalX, goalY)) return null;

            // Start relax: 4 axis probes per ring, +y first (0059d640:113-148).
            if (!Walkable(startX, startY))
            {
                for (int r = 1; r <= 32; r++)
                {
                    if (Walkable(startX, startY + r)) { startY += r; break; }
                    if (Walkable(startX + r, startY)) { startX += r; break; }
                    if (Walkable(startX - r, startY)) { startX -= r; break; }
                    if (Walkable(startX, startY - r)) { startY -= r; break; }
                }
                // no hit ⇒ start stays put; the tile-validity check below decides.
            }

            // Goal relax: the 8-probe +y-biased fan (0059d640:152-239); null if still not walkable.
            if (!Walkable(goalX, goalY))
            {
                bool found = false;
                for (int r = 1; r <= 12 && !found; r++)
                {
                    int r2 = r >> 1, r3 = r / 3, r4 = r >> 2, r5 = r / 5;
                    found = Probe(ref goalX, ref goalY, 0, r)
                         || Probe(ref goalX, ref goalY, 0, -r4)
                         || Probe(ref goalX, ref goalY, r2, 0)
                         || Probe(ref goalX, ref goalY, -r2, 0)
                         || Probe(ref goalX, ref goalY, r3, r2)
                         || Probe(ref goalX, ref goalY, -r3, r2)
                         || Probe(ref goalX, ref goalY, r3, -r5)
                         || Probe(ref goalX, ref goalY, -r3, -r5);
                }
                if (!found && !Walkable(goalX, goalY)) return null;
            }

            // Post-snap overhang nudge, start then goal (0059d640:241-307): slide down (+y) while
            // the sub-cell one row below carries the floor bit — land on the ledge's bottom row.
            while ((_nav.SubAt(startX, startY + 1) & NavWorld.SubFloor) != 0) startY++;
            while ((_nav.SubAt(goalX, goalY + 1) & NavWorld.SubFloor) != 0) goalY++;

            int W = _nav.Width;
            int sTile = (startY >> 3) * W + (startX >> 3);
            int gTile = (goalY >> 3) * W + (goalX >> 3);
            if (_nav.EffectiveTileBaseCost(sTile) == -1 || _nav.EffectiveTileBaseCost(gTile) == -1) return null;
            if (sTile == gTile && startX == goalX && startY == goalY)
                return new List<(int sx, int sy)> { (startX, startY) };   // degenerate 1-point path

            bool ownerVariant = iOwner <= 7;
            int cliff = ownerVariant ? CliffV2 : CliffV1;

            _gen++;
            _queue.Clear();
            _dist[sTile] = _nav.EffectiveTileBaseCost(sTile) + 1;   // engine self-cost seed (calc1-confirm §5.3)
            _pred[sTile] = -1; _stamp[sTile] = _gen;
            _queue.Enqueue(sTile);

            int enqueued = 1, head = 0, maxNodes = DefaultMaxNodes;
            bool goalSeen = false;
            while (_queue.Count > 0 && head < maxNodes)
            {
                int cur = _queue.Dequeue();
                head++;
                int cx = cur % W, cy = cur / W;
                int dCur = _dist[cur];
                bool curFloorOrPassage = _nav.EffectiveTileHasFloorOrPassage(cur);
                int digCur = _nav.TileDigByte(cx, cy);

                // Neighbour order +y, −y, +x, −x (0059ab90 et al — FIFO order shapes tie-breaks).
                Step(cur, cx, cy + 1, dCur, curFloorOrPassage, digCur, canClimb,
                    ownerVariant, false, cliff, gTile, ref enqueued, ref goalSeen, ref maxNodes);
                Step(cur, cx, cy - 1, dCur, curFloorOrPassage, digCur, canClimb,
                    ownerVariant, false, cliff, gTile, ref enqueued, ref goalSeen, ref maxNodes);
                Step(cur, cx + 1, cy, dCur, curFloorOrPassage, digCur, canClimb,
                    ownerVariant, false, cliff, gTile, ref enqueued, ref goalSeen, ref maxNodes);
                Step(cur, cx - 1, cy, dCur, curFloorOrPassage, digCur, canClimb,
                    ownerVariant, true, cliff, gTile, ref enqueued, ref goalSeen, ref maxNodes);
            }
            if (_stamp[gTile] != _gen) return null;

            // Tile chain goal→start via predecessors, reversed to start→goal.
            var tiles = new List<int>();
            for (int t = gTile; t != -1; t = _pred[t])
            {
                tiles.Add(t);
                if (tiles.Count > _dist.Length) return null;   // corrupt chain guard
            }
            tiles.Reverse();

            // Polyline: relaxed start, interior tiles' packed exit pairs, relaxed goal
            // (0059d640:579-584 and:702-707).
            var wps = new List<(int sx, int sy)>(tiles.Count + 1) { (startX, startY) };
            for (int i = 1; i < tiles.Count; i++)
            {
                int t = tiles[i];
                int exitPair = _nav.EffectivePackedExitPair(t);
                int tx = t % W, ty = t / W;
                wps.Add((
                    tx * 8 + (exitPair & NavWorld.ExitSubXMask),
                    ty * 8 + ((exitPair & NavWorld.ExitSubYMask) >> 3)));
            }
            if (wps.Count > 1) wps[wps.Count - 1] = (goalX, goalY);
            else wps.Add((goalX, goalY));
            return wps;
        }

        void Step(int cur, int tx, int ty, int dCur, bool curFloorOrPassage, int digCur, bool canClimb,
                  bool ownerVariant, bool leftNeighbour, int cliff, int gTile, ref int enqueued,
                  ref bool goalSeen, ref int maxNodes)
        {
            int W = _nav.Width;
            if ((uint)tx >= (uint)W || (uint)ty >= (uint)_nav.Height) return;
            int t = ty * W + tx;
            int baseCost = _nav.EffectiveTileBaseCost(t);
            if (baseCost == -1) return;                                   // gate 1: validity (+0x06 != −1)
            bool targetFloorOrPassage = _nav.EffectiveTileHasFloorOrPassage(t);
            // Gate 3 reads raw tile+0x12. Native ladder influence writes cost -20/type 4 only
            // (FUN_0060b600:88-98); treating the overlay as floor let non-climbers enter ladder-only
            // tiles. Variant B's -x copy/paste mask is 0xA4 (0059bf90:328), whose 0x20 and
            // 0x04 bits are packed subY/subX coordinate bits under the corrected pair semantics.
            byte gateMask = ownerVariant && leftNeighbour ? (byte)0xA4 : NavWorld.ExitFloorOrPassageFlag;
            if (!canClimb && (_nav.ExitPairAndFlags[t] & gateMask) == 0) return;
            // gate 4 (B/D): tile[0x34+iOwner] != 0 — per-owner territory flags are not loaded in the
            // port's world (runtime gameplay data, not PMP); the gate passes. Hook point kept here.
            if (ownerVariant) { /* owner gate hook */ }

            int cost;                                                     // calc1-confirm §6
            if (curFloorOrPassage != targetFloorOrPassage)
                cost = baseCost + cliff - StepCost * System.Math.Min(digCur, _nav.TileDigByte(tx, ty));
            else if (!curFloorOrPassage)
                cost = baseCost + 1 + StepCost * System.Math.Abs(digCur - _nav.TileDigByte(tx, ty));
            else
                cost = baseCost + 1;

            int nd = dCur + cost;
            if (_stamp[t] == _gen && nd >= _dist[t]) return;              // gate 2: strict improvement
            _dist[t] = nd; _pred[t] = cur; _stamp[t] = _gen;
            _queue.Enqueue(t);
            enqueued++;
            if (t == gTile && !goalSeen)
            {
                goalSeen = true;                                          // budget clamp at goal write:
                int clamp = (int)System.Math.Round(enqueued * 1.5f);      // round(enqueued × 1.5)
                if (clamp < maxNodes) maxNodes = clamp;
            }
        }

        bool Probe(ref int gx, ref int gy, int dx, int dy)
        {
            if (!Walkable(gx + dx, gy + dy)) return false;
            gx += dx; gy += dy;
            return true;
        }

        // walkable(sx,sy):= tile[+6] != −1 && (+0x3c & 0x01) (0059d640:104 predicate).
        bool Walkable(int sx, int sy)
        {
            if ((uint)sx >= (uint)_nav.SubW || (uint)sy >= (uint)_nav.SubH) return false;
            if (_nav.EffectiveTileBaseCost((sy >> 3) * _nav.Width + (sx >> 3)) == -1) return false;
            return (_nav.SubAt(sx, sy) & NavWorld.SubWalk) != 0;
        }

        // 0059d640:102-105 (start, inline) + FUN_0040b8a0 (goal): both endpoints must sit at least
        // 0x28=40 sub-cells inside the sub-bounds — `bounds[+0x94..]+0x28 <= p < bounds[+0x9c..]-0x28`
        // rejects near-edge endpoints outright. (The earlier reading tolerated 40 outside — inverted.)
        bool InMargin(int sx, int sy) =>
            sx >= EdgeMargin && sy >= EdgeMargin
            && sx < _nav.SubW - EdgeMargin && sy < _nav.SubH - EdgeMargin;
    }
}
