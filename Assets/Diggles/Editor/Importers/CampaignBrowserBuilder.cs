#nullable enable

namespace Diggles.Editor.Importers
{
    using System.Collections.Generic;
    using System.IO;
    using UnityEditor.AssetImporters;
    using UnityEngine;
    using Diggles.Runtime;
    using Diggles.Runtime.World;

    // Editor-only campaign-browser world builder: statically parses a Campaign.tcl
    // (via TclCampaignParser) and spawns its markers via MarkerModelResolver. The
    // runtime loader instead uses Tcl `new` + RuntimeWorldSpawner. Terrain meshing
    // is shared with the runtime BdbPmpWorldBuilder.
    public static class CampaignBrowserBuilder
    {
        static System.Action<string, UnityEngine.Object>? Reg(AssetImportContext? c)
            => c == null ? null : (n, o) => c.AddObjectToAsset(n, o);

        public static void BuildFromCampaign(
            GameObject root, AssetImportContext? ctx,
            string bdbPath, string campaignTclPath, string dataRoot,
            bool renderDefaultEarth = false,
            FirstLevelConfig? firstLevelConfig = null)
        {
            var bdb = BdbParser.Parse(bdbPath);
            var world = TclCampaignParser.ParseCampaign(campaignTclPath, dataRoot, firstLevelConfig);
            Debug.Log($"[BdbPmpWorldBuilder] Campaign {Path.GetFileName(campaignTclPath)}: " +
                      $"{world.WorldWidth}x{world.WorldHeight}, {world.Templates.Count} templates");
            foreach (var d in world.Diagnostics) Debug.Log("  " + d);

            // Initialize world arrays — default block_idx = 0 (solid earth), zone = 4, terrain = 0.
            int N = world.WorldWidth * world.WorldHeight;
            var worldBlockIdx = new uint[N];
            var worldZone = new byte[N];
            var worldTerrain = new byte[N];
            var worldCellId = new ushort[N];  // from L2M overlay (corners + blockType + animFrame)
            var worldHasTpl = new System.Collections.BitArray(N);
            for (int i = 0; i < N; i++) worldZone[i] = 4;

            // Blit each template into the world
            int loadedTpl = 0, missingTpl = 0, l2mOverlays = 0;
            foreach (var tpl in EnumerateLeaves(world.Templates))
            {
                if (tpl.PmpPath == null || !File.Exists(tpl.PmpPath))
                {
                    missingTpl++;
                    continue;
                }
                try
                {
                    var pmp = PmpParser.Parse(tpl.PmpPath);
                    BdbPmpWorldBuilder.BlitPmpIntoWorld(pmp, tpl.WorldX, tpl.WorldY,
                        world.WorldWidth, world.WorldHeight,
                        worldBlockIdx, worldZone, worldTerrain, worldHasTpl);
                    loadedTpl++;

                    // map_setlayer2 — L2M overlay for materials/corners
                    if (tpl.L2mPath != null && File.Exists(tpl.L2mPath))
                    {
                        try
                        {
                            var l2m = L2mParser.Parse(tpl.L2mPath);
                            BdbPmpWorldBuilder.BlitL2mIntoWorld(l2m, tpl.WorldX, tpl.WorldY,
                                world.WorldWidth, world.WorldHeight, worldCellId);
                            l2mOverlays++;
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogWarning($"  L2M overlay failed for {tpl.Name}: {e.Message}");
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"  PMP load failed for {tpl.Name}: {e.Message}");
                    missingTpl++;
                }
            }
            Debug.Log($"[BdbPmpWorldBuilder] templates: loaded={loadedTpl} skipped={missingTpl} l2m-overlays={l2mOverlays}");

            // Find content bbox so we don't render the empty world
            int xMin = world.WorldWidth, yMin = world.WorldHeight, xMax = -1, yMax = -1;
            if (renderDefaultEarth)
            {
                xMin = 0; yMin = 0; xMax = world.WorldWidth - 1; yMax = world.WorldHeight - 1;
            }
            else
            {
                for (int ty = 0; ty < world.WorldHeight; ty++)
                {
                    for (int tx = 0; tx < world.WorldWidth; tx++)
                    {
                        if (worldHasTpl[ty * world.WorldWidth + tx])
                        {
                            if (tx < xMin) xMin = tx;
                            if (ty < yMin) yMin = ty;
                            if (tx > xMax) xMax = tx;
                            if (ty > yMax) yMax = ty;
                        }
                    }
                }
                if (xMax < 0) { Debug.LogWarning("No templates loaded — nothing to render"); return; }
                // Margin so we get a little default-earth around the playable area
                xMin = System.Math.Max(0, xMin - 4);
                yMin = System.Math.Max(0, yMin - 4);
                xMax = System.Math.Min(world.WorldWidth - 1, xMax + 4);
                yMax = System.Math.Min(world.WorldHeight - 1, yMax + 4);
            }
            Debug.Log($"[BdbPmpWorldBuilder] render bbox: X {xMin}..{xMax}, Y {yMin}..{yMax} " +
                      $"({(xMax - xMin + 1) * (yMax - yMin + 1)} tiles)");

            BdbPmpWorldBuilder.RenderWorldArrays(root, Reg(ctx), bdb,
                worldBlockIdx, worldZone, worldTerrain, worldCellId, world.WorldWidth,
                xMin, yMin, xMax, yMax, dataRoot);

            SpawnTclMarkers(root, ctx, world, dataRoot);
        }

        private static void SpawnTclMarkers(
            GameObject root, AssetImportContext? ctx, CampaignWorld world, string dataRoot)
        {
            // Wipe the cache so stale Mesh / Material instances from previous loads
            // don't leak; fresh instances are built instead.
            MarkerModelResolver.ClearCache();

            var bucket = new GameObject("Markers (.tcl)");
            bucket.transform.SetParent(root.transform, false);

            int placed = 0, unresolved = 0, hidden = 0;
            Mesh? fallbackMesh = null;
            Material? fallbackMat = null;
            var loggedClass = new HashSet<string>();

            foreach (var tpl in EnumerateLeaves(world.Templates))
            {
                string? tclPath = null;
                // 1. Sibling of L2M (most common for surface templates like urw_unq_start).
                if (tpl.L2mPath != null)
                {
                    var candidate = Path.ChangeExtension(tpl.L2mPath, ".tcl");
                    if (File.Exists(candidate)) tclPath = candidate;
                }
                // 2. Original campaign tpl.TclPath if available.
                if (tclPath == null && !string.IsNullOrEmpty(tpl.TclPath) && File.Exists(tpl.TclPath))
                {
                    tclPath = tpl.TclPath;
                }
                // 3. Sibling of PMP.
                if (tclPath == null && tpl.PmpPath != null)
                {
                    var candidate = Path.ChangeExtension(tpl.PmpPath, ".tcl");
                    if (File.Exists(candidate)) tclPath = candidate;
                }
                if (tclPath == null)
                {
                    Debug.Log($"  [markers] no .tcl for template {tpl.Name} (l2m={tpl.L2mPath} pmp={tpl.PmpPath})");
                    continue;
                }

                List<TclObject> markers;
                try { markers = TclObjectParser.Parse(tclPath); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"  TCL parse failed for {tpl.Name}: {e.Message}");
                    continue;
                }

                // Detect chunk-level offset adjustment:
                // set xoffset [expr {$xoffset - 16}]
                // set yoffset [expr {$yoffset - 16}]
                // split_template chunks apply this before spawning markers, so it
                // affects every subsequent `[new X "" "[expr X+$xoffset] [expr Y+$yoffset] Z"...]`.
                int xOffsetAdjust = 0, yOffsetAdjust = 0;
                try
                {
                    var tclText = File.ReadAllText(tclPath);
                    var xMatch = System.Text.RegularExpressions.Regex.Match(
                        tclText, @"set\s+xoffset\s+\[\s*expr\s+\{\s*\$xoffset\s*([+\-])\s*(\d+)\s*\}\s*\]");
                    var yMatch = System.Text.RegularExpressions.Regex.Match(
                        tclText, @"set\s+yoffset\s+\[\s*expr\s+\{\s*\$yoffset\s*([+\-])\s*(\d+)\s*\}\s*\]");
                    if (xMatch.Success)
                        xOffsetAdjust = (xMatch.Groups[1].Value == "-" ? -1 : 1) * int.Parse(xMatch.Groups[2].Value);
                    if (yMatch.Success)
                        yOffsetAdjust = (yMatch.Groups[1].Value == "-" ? -1 : 1) * int.Parse(yMatch.Groups[2].Value);
                }
                catch { /* ignore — fall back to no adjustment */ }

                Debug.Log($"  [markers] {tpl.Name} ({Path.GetFileName(tclPath)}): " +
                          $"{markers.Count} markers parsed, offset adjust=({xOffsetAdjust},{yOffsetAdjust})");

                foreach (var m in markers)
                {
                    // Two-level marker: the outer object ({ClassName}_L#) carries
                    // DigglesObject, the collider and the already axis-converted
                    // position/rotation; the child "Model" carries the mesh and
                    // DigglesAnimator. Mesh vertices are axis-converted (Diggles
                    // Z-up → Unity Y-up) by MarkerModelResolver.ApplyAxisConvention,
                    // so the model stands upright at outer.rotation = identity.
                    var go = new GameObject($"{m.ClassName}_L{m.LineNumber}");
                    go.transform.SetParent(bucket.transform, false);

                    float tieBreaker = ((m.LineNumber * 1103515245 + 12345) & 0xFFF) / 4095f * 0.01f - 0.005f;
                    go.transform.localPosition = new Vector3(
                        m.Position.x + tpl.WorldX + xOffsetAdjust,
                        m.Position.z * BdbPmpWorldBuilder.MarkerHeightScale + BdbPmpWorldBuilder.MarkerYOffset,
                        -(m.Position.y + tpl.WorldY + yOffsetAdjust) + tieBreaker);
                    go.transform.localRotation = Quaternion.identity;

                    var markerRot = Quaternion.Euler(
                        m.Rotation.x * Mathf.Rad2Deg,
                       -m.Rotation.y * Mathf.Rad2Deg,
                        m.Rotation.z * Mathf.Rad2Deg);

                    var dyn = MarkerModelResolver.ResolveDynamic(m.ClassName, dataRoot, ctx);
                    if (dyn != null && dyn.Clips.Length > 0 && dyn.Clips[0].Frames.Length > 0)
                    {
                        if (loggedClass.Add(m.ClassName))
                            Debug.Log($"  [markers] {m.ClassName} → {dyn.DebugLabel} " +
                                      $"(ANIM clip='{dyn.DefaultClipName}' frames={dyn.Clips[0].Frames.Length})");

                        var model = new GameObject("Model");
                        model.transform.SetParent(go.transform, false);
                        if (System.Math.Abs(BdbPmpWorldBuilder.MarkerScale - 1f) > 0.001f)
                            model.transform.localScale = Vector3.one * BdbPmpWorldBuilder.MarkerScale;
                        // Legacy spawn rotation — visually correct.
                        model.transform.localRotation =
                            Quaternion.Euler(-90f, 0f, 0f) * markerRot;

                        var mf = model.AddComponent<MeshFilter>();
                        var frame0 = dyn.Clips[0].Frames[0];
                        if (frame0 != null) mf.sharedMesh = frame0;
                        var mr = model.AddComponent<MeshRenderer>();
                        if (dyn.Materials != null && dyn.Materials.Length > 0)
                            mr.sharedMaterials = dyn.Materials;
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                        mr.receiveShadows = true;
                        var animator = model.AddComponent<DigglesAnimator>();
                        animator.Initialize(mf, dyn.Clips, dyn.DefaultClipName,
                            dyn.DefaultMode, dyn.DefaultStartFrame);

                        bool isCharacter = !m.ClassName.StartsWith("Dummy_",
                            System.StringComparison.Ordinal);
                        if (isCharacter)
                        {
                            var dObj = go.AddComponent<DigglesObject>();
                            dObj.Initialize(animator, m.ClassName);
                            // Collider sits on the inner Model so it shares the
                            // mesh transform chain and follows mesh rotation
                            // exactly; a raycast hit walks up to the outer
                            // DigglesObject via GetComponentInParent.
                            if (frame0 != null)
                            {
                                var b = frame0.bounds;
                                var bc = model.AddComponent<BoxCollider>();
                                bc.center = b.center;
                                bc.size = b.size;
                            }
                        }
                        placed++;
                        continue;
                    }

                    var resolved = MarkerModelResolver.Resolve(m.ClassName, dataRoot, ctx);
                    if (resolved?.Mesh != null)
                    {
                        if (loggedClass.Add(m.ClassName))
                            Debug.Log($"  [markers] {m.ClassName} → {resolved.DebugLabel} " +
                                      $"(mesh verts={resolved.Mesh.vertexCount}, mats={resolved.Materials?.Length ?? 0})");
                        var model = new GameObject("Model");
                        model.transform.SetParent(go.transform, false);
                        if (System.Math.Abs(BdbPmpWorldBuilder.MarkerScale - 1f) > 0.001f)
                            model.transform.localScale = Vector3.one * BdbPmpWorldBuilder.MarkerScale;
                        // Legacy spawn rotation — visually correct.
                        model.transform.localRotation =
                            Quaternion.Euler(-90f, 0f, 0f) * markerRot;
                        var mf = model.AddComponent<MeshFilter>();
                        mf.sharedMesh = resolved.Mesh;
                        var mr = model.AddComponent<MeshRenderer>();
                        if (resolved.Materials != null && resolved.Materials.Length > 0)
                            mr.sharedMaterials = resolved.Materials;
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                        mr.receiveShadows = true;
                        placed++;
                    }
                    else if (m.ClassName.StartsWith("Trigger_") ||
                             m.ClassName.StartsWith("Sequence") ||
                             m.ClassName.StartsWith("Info_") ||
                             m.ClassName.StartsWith("Fake_"))
                    {
                        hidden++;
                    }
                    else
                    {
                        if (fallbackMesh == null)
                        {
                            fallbackMesh = CreateMarkerOctahedron();
                            if (ctx != null) ctx.AddObjectToAsset("MarkerOctahedron", fallbackMesh);
                            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                            fallbackMat = new Material(shader!) { name = "MarkerUnresolvedMat" };
                            if (fallbackMat.HasProperty("_BaseColor"))
                                fallbackMat.SetColor("_BaseColor", new Color(1f, 0.2f, 0.6f));
                            if (ctx != null) ctx.AddObjectToAsset("MarkerUnresolvedMat", fallbackMat);
                        }
                        go.transform.localScale = Vector3.one * 0.4f;
                        var mf = go.AddComponent<MeshFilter>();
                        mf.sharedMesh = fallbackMesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = fallbackMat;
                        unresolved++;
                    }
                }
            }
            Debug.Log($"[BdbPmpWorldBuilder] markers: placed={placed} unresolved={unresolved} hidden={hidden}");
        }


        private static Mesh CreateMarkerOctahedron()
        {
            // Simple 6-vertex octahedron for unresolved-marker placeholder.
            var m = new Mesh { name = "MarkerOctahedron" };
            m.SetVertices(new[] {
                new Vector3( 0,  1, 0), new Vector3( 0, -1, 0),
                new Vector3( 1,  0, 0), new Vector3(-1,  0, 0),
                new Vector3( 0,  0, 1), new Vector3( 0,  0,-1),
            });
            m.SetTriangles(new[] {
                0,2,4, 0,4,3, 0,3,5, 0,5,2,
                1,4,2, 1,3,4, 1,5,3, 1,2,5,
            }, 0);
            m.RecalculateNormals();
            return m;
        }


        private static System.Collections.Generic.IEnumerable<CampaignTemplate> EnumerateLeaves(
            System.Collections.Generic.IEnumerable<CampaignTemplate> templates)
        {
            foreach (var t in templates)
            {
                if (t.SubChunks.Count == 0) { yield return t; continue; }
                foreach (var s in EnumerateLeaves(t.SubChunks)) yield return s;
            }
        }

    }
}
