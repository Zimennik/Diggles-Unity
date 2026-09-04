#nullable enable

namespace Diggles.Runtime
{
    using Diggles.Runtime.Tcl.Engine;
    using System.Collections.Generic;
    using UnityEngine;

    // Runtime IDigglesEntitySpawner that places entities with the canonical
    // Diggles world coordinate convention (see Docs/COORDINATE_SYSTEM.md §3):
    // * outer GameObject at localPos = (x, z*0.5+7, -y) under a Q_x(-90) root;
    // * inner "Model" GameObject at localRot = Q_x(-90) * markerRot, scaled by
    // DigglesModelLibrary.ModelToWorldScale (lib meshes are raw ~200u bbox).
    //
    // Models + clips come from the runtime DigglesModelLibrary (lazy clips) and each
    // entity gets a DigglesAnimController so running Tcl (`set_anim` / `action this
    // anim …`) drives its animation. The editor Tcl Spawn Test wires the same class,
    // so Edit-mode and Play-mode spawning are identical.
    //
    // Unresolvable classes fall back to a small wireframe octahedron; the
    // intentionally-invisible markers (Trigger_/Info_/Sequence/Fake_) get a bare
    // outer for handle bookkeeping.
    public class RuntimeWorldSpawner : IDigglesEntitySpawner
    {
        // Set by the save-session stager around candidate materialization: restored entities
        // spawn at their exact archived position (no dig-drop settle on spawn).
        public bool SuppressSpawnSettle;

        public readonly struct ClassLightSaveState
        {
            public ClassLightSaveState(
                Vector3 nativePosition,
                Color color,
                float range,
                int objectId)
            {
                NativePosition = nativePosition;
                Color = color;
                Range = range;
                ObjectId = objectId;
            }

            public Vector3 NativePosition { get; }
            public Color Color { get; }
            public float Range { get; }
            public int ObjectId { get; }
            public bool IsObjectBound => ObjectId >= 0;
        }

        protected readonly string DataRoot;
        readonly DigglesModelLibrary _lib;
        static readonly System.Collections.Generic.HashSet<string> _placeholderWarned = new();

        // Canonical engine elevation transform (top_z = nibble * 0.5 + 7.0).
        const float MarkerHeightScale = 0.5f;
        const float MarkerYOffset = 7.0f;
        const float ModelScale = DigglesModelLibrary.ModelToWorldScale;

        // Editor-only hierarchy hiding. At 1000+ spawned entities the Unity editor's own
        // hierarchy/scene bookkeeping (repaint, selection, dirty tracking) dominates the Play
        // frame — measured 23 ms editor vs 3 ms game at 1338 entities — while a standalone build
        // pays none of it. Stamping each spawned entity's outer GameObject with
        // HideFlags.HideInHierarchy drops it (and its children, implicitly) out of the Hierarchy
        // window so the editor stops tracking it. The Spawn stamp is guarded by UNITY_EDITOR so
        // builds are byte-identical, and it does not touch DontSave, so scene serialization /
        // lifecycle is unchanged. Toggle live with F6 (PerfHud), which also re-sweeps
        // already-spawned entities.
        public static bool HideEntitiesInHierarchy = true;

        Mesh? _fallbackMesh;
        Material? _fallbackMat;

        public RuntimeWorldSpawner(string dataRoot)
        {
            DataRoot = dataRoot;
            _lib = new DigglesModelLibrary(dataRoot);
        }

        // Share a pre-built library (so a runtime host can reuse one cache for
        // spawning + anim lookups).
        public RuntimeWorldSpawner(DigglesModelLibrary lib, string dataRoot)
        {
            DataRoot = dataRoot;
            _lib = lib;
        }

        public DigglesModelLibrary Library => _lib;

        // The live diggable terrain (wired by DigglesWorldRuntime once the world exists) —
        // lets the spawner settle physics-enabled drops out of solid rock (see Spawn).
        public Diggles.Runtime.World.DiggableTerrain? Terrain { get; set; }

        // Optional per-class override descriptors (model / anim / material / collider / visibility).
        // Wired by DigglesWorldRuntime from the DI container. null / no matching entry → spawn unchanged.
        public DigglesClassAssetRegistry? ClassRegistry { get; set; }

        // IDigglesEntitySpawner.Spawn — the normal path registers class_light sources.
        public GameObject? Spawn(DigglesTclEntity ent, Transform parent) =>
            Spawn(ent, parent, registerClassLight: true);

        // registerClassLight=false: build the visual only, without enrolling a class_light source.
        // Used by save-restored marker-7 deco seeds (DigglesTclEngine.SpawnDecoSeed), which the
        // native treats as lightweight render instances — their lighting comes from the
        // separately-restored class-light table, and enrolling a live light here would pollute the
        // save-capture class-light section (CaptureClassLights enumerates _classLights).
        public GameObject? Spawn(DigglesTclEntity ent, Transform parent, bool registerClassLight)
        {
            var cls = ent.Class;
            string className = cls?.Name ?? "";
            if (string.IsNullOrEmpty(className)) return null;

            // Dig-drop settle: dig_execute spawns the resource drop at $digpoint
            // (z_dignwalk.tcl:365-369 `new $cc "" $digpoint`), whose z = dig_next's
            // clamp(rim+4+2, 1, 14) (FUN_00597b20:229-254) and whose (x,y) is the
            // still-partly-solid marked tile. In the original the stone ends up on the tunnel
            // floor because rohstoffe.tcl turns physics on (`set_physic this 1`) and the engine
            // settles it out of the rock. The port has no item physics, so settle once at spawn:
            // snap to the nearest walkable sub-cell, fall down the open band to the floor line
            // (gravity), clamp the depth into that column's open band — the drop lands in the
            // newly opened cell. Save restoration spawns entities at their exact archived
            // position — the archive already holds the settled truth, and re-settling drifts it
            // (nav rounding).
            if (!SuppressSpawnSettle && IsResourcePickup(className) && Terrain != null && Terrain.Nav != null)
                ent.Position = SettleDropPosition(Terrain.Nav, ent.Position);

            var go = new GameObject($"{className}_{ent.Handle}");
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localPosition = NativeToLocal(ent.Position);
            go.transform.localRotation = Quaternion.identity;
            // Editor-only: hide this entity's subtree from the Hierarchy window so the editor
            // stops paying per-object bookkeeping in Play (see HideEntitiesInHierarchy). Outer
            // GameObject only — children inherit hierarchy hiding. No effect in builds.
#if UNITY_EDITOR
            if (HideEntitiesInHierarchy) go.hideFlags |= HideFlags.HideInHierarchy;
#endif
            // Remember the spawn spot (Position drifts once the entity moves) — wander/fly anchors here.
            if (!ent.HasSpawnPosition) { ent.SpawnPosition = ent.Position; ent.HasSpawnPosition = true; }

            // class_light → a real URP point light on the marker (LightSrc markers + torches).
            // The entity rides along so the upload can gate the light by its fog visibility
            // (otherwise the torch light kept shining through the gray veil after the models
            // fog-hid — see UploadClassLights).
            if (registerClassLight && cls!.Light != null) AddLight(go, cls.Light, ent);

            var markerRot = MarkerRotFromNative(ent.Rotation);

            // Per-class override descriptor (DigglesClassAsset), or null when no registry is wired or
            // no asset matches this class → everything below behaves exactly as before.
            var ov = ClassRegistry?.Resolve(className);
            if (ov != null && ov.visible == DigglesClassAsset.VisibleOverride.ForceHidden)
                return go;   // descriptor forces this class invisible — keep the outer (handle / light)

            // Invisible classes — decided before model resolution (the outer stays for handle
            // bookkeeping/lights). Marker prefixes, `def_class … info` marker classes, and
            // TSV-known modelless classes. This must precede the resolution chain: Info_Coll_*
            // (collision markers) carry an obj_init `set_anim this kolli_box_a.standard`
            // (infoobjs.tcl:398-403, editor-only visuals), so the obj_init-pose fallback below
            // would happily render their checkerboard collision boxes into the world.
            if (className.StartsWith("Trigger_", System.StringComparison.Ordinal) ||
                className.StartsWith("Info_", System.StringComparison.Ordinal) ||
                className.StartsWith("Sequence", System.StringComparison.Ordinal) ||
                className.StartsWith("Fake_", System.StringComparison.Ordinal) ||
                className == "ProductionManager" ||   // native helper singleton, never rendered
                cls!.Kind == "info" || _lib.IsClassInvisible(className))
            {
                return go;
            }

            // Resolve the source object + its spawn-pose clip. The class→model
            // TSV is authoritative (generated from the shipped data); fall back
            // to the obj baked into class_defaultanim, then to the class name.
            var rc = _lib.ResolveClass(className);
            string obj = rc?.Obj ?? "";
            string defaultAnim;
            if (!string.IsNullOrEmpty(obj))
            {
                // Use class_defaultanim only when it targets this object (hand
                // stubs sometimes name a stale obj); otherwise the TSV's anim.
                defaultAnim = !string.IsNullOrEmpty(cls!.DefaultAnim) &&
                              cls.DefaultAnim!.StartsWith(obj + ".", System.StringComparison.Ordinal)
                    ? cls.DefaultAnim!
                    : obj + "." + (string.IsNullOrEmpty(rc!.Value.DefaultAnim) ? "standard" : rc.Value.DefaultAnim);
            }
            else if (!string.IsNullOrEmpty(cls!.DefaultAnim))
            {
                int dot = cls.DefaultAnim!.IndexOf('.');
                obj = dot > 0 ? cls.DefaultAnim!.Substring(0, dot) : "";
                defaultAnim = cls.DefaultAnim!;
            }
            else if (_lib.GetObjDb(className) != null)
            {
                obj = className;
                defaultAnim = obj + ".standard";
            }
            // Last resort: the obj_init already picked a pose (`set_anim this X.Y` ran before this
            // spawn) — derive the object from its prefix. This is how classes with no TSV row and
            // no class_defaultanim get their model in the native (the engine binds whatever anim
            // the object carries): the *brocken ore family (brocken.tcl:59 `set_anim this
            // stein_00.standard` etc.) spawned as pink placeholders without it.
            else if (!string.IsNullOrEmpty(ent.CurrentAnim) && ent.CurrentAnim!.IndexOf('.') > 0)
            {
                obj = ent.CurrentAnim!.Substring(0, ent.CurrentAnim!.IndexOf('.'));
                defaultAnim = ent.CurrentAnim!;
            }
            // Native CAct_Anim resolves a logical action through the class animation table and
            // receives the concrete animation descriptor, whose object id selects the 3db
            // (FUN_004082a0:58-75 → FUN_004613d0; set_class_anim stores that descriptor through
            // FUN_00615650:138-155 → FUN_006026e0:73-80). Unity must bind one model before the first action,
            // so use the same descriptor's `obj.` prefix as the final spawn-time adaptation.
            // Koenig_im_Bett is the shipped proof: actors.tcl:503-527 has no class_defaultanim,
            // while every king* entry resolves to mann.koenig_*.
            else ResolveClassAnimModel(cls, out obj, out defaultAnim);

            // Descriptor model / default-anim overrides, layered on the TSV/Tcl resolution above.
            if (ov != null)
            {
                if (!string.IsNullOrEmpty(ov.modelObjectOverride))
                {
                    obj = ov.modelObjectOverride;
                    defaultAnim = obj + ".standard";   // also set defaultAnimOverride if "standard" is absent
                }
                if (!string.IsNullOrEmpty(ov.defaultAnimOverride))
                    defaultAnim = ov.defaultAnimOverride.IndexOf('.') >= 0
                        ? ov.defaultAnimOverride
                        : obj + "." + ov.defaultAnimOverride;
            }

            if (!string.IsNullOrEmpty(obj))
            {
                var model = BuildInner(go.transform, markerRot);
                var mf = model.AddComponent<MeshFilter>();
                var mr = model.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
                var animator = model.AddComponent<DigglesAnimator>();
                var ctrl = model.AddComponent<DigglesAnimController>();
                ctrl.Configure(_lib, animator, obj);
                var animMap = cls!.AnimMap;
                if (ov != null && ov.animMapOverrides != null && ov.animMapOverrides.Count > 0)
                {
                    animMap = new Dictionary<string, string>(cls.AnimMap);
                    foreach (var sc in ov.animMapOverrides)
                        if (!string.IsNullOrEmpty(sc.state)) animMap[sc.state] = sc.clip;
                }
                ctrl.SetTables(animMap, cls.Animsets);

                // Spawn pose. The engine binds the object then runs obj_init, so an obj_init
                // `set_anim` (which ran before this spawn, when ent.GameObject was still null) is the
                // authoritative spawn pose — prefer it over the class/TSV default. This is what makes
                // rings spin (naturring.drehen), barrels spawn closed (tonne.zu), props use their
                // intended loop, etc. Falls back to defaultAnim when obj_init set no anim.
                string spawnAnim = defaultAnim;
                var spawnMode = (DigglesAnimator.AnimMode)cls.DefaultAnimMode;
                if (!string.IsNullOrEmpty(ent.CurrentAnim))
                {
                    spawnAnim = ent.CurrentAnim!;
                    if (ent.CurrentAnimMode >= 0) spawnMode = (DigglesAnimator.AnimMode)ent.CurrentAnimMode;
                }
                ctrl.PlayClip(spawnAnim, spawnMode);

                // Loader couldn't build the clip (unknown obj/anim) → drop the
                // empty inner and fall through to the wireframe placeholder.
                if (mf.sharedMesh == null)
                {
                    Object.DestroyImmediate(model);
                }
                else
                {
                    // Resource pickups (Stein + ores) get no special scale — the actually-spawned
                    // drops stein_01..03/erz_01 have raw bbox diagonals of 203-208u, i.e. exactly
                    // the ~200u calibration the flat ModelScale (1/200) assumes → ≈0.70×0.46×0.58 wu
                    // on the ground, the original's pickup size. The historical bug (a stone the
                    // size of a whole tunnel) was stein_00 (the huge embedded boulder) being picked
                    // by the broken 2-arg irandom (DigglesTclEngine CmdIrandom comment,
                    // rohstoffe.tcl:64 `stein_0[irandom 1 4]`) — long fixed. The bbox-normalization
                    // branch that lived here was compensating for that dead bug and shrank
                    // legitimate drops; deleted.
                    // Descriptor material override: replace every submesh material with the supplied
                    // one. (shaderOverride is deferred — it needs per-instance clones so it won't
                    // mutate the per-db shared material cache.)
                    if (ov?.materialOverride != null)
                    {
                        int sub = mr.sharedMaterials.Length < 1 ? 1 : mr.sharedMaterials.Length;
                        var ovMats = new Material[sub];
                        for (int k = 0; k < sub; k++) ovMats[k] = ov.materialOverride;
                        mr.sharedMaterials = ovMats;
                    }

                    if (IsCharacter(className))
                    {
                        var dObj = go.AddComponent<DigglesObject>();
                        dObj.Initialize(animator, className);
                    }

                    // Collider: descriptor policy overrides the default (characters → bounds box,
                    // others → none). AutoFromBounds / ExplicitBox / None force the choice.
                    //
                    // The original picks against the baked bbox of the currently displayed mesh —
                    // the anim-class record resolves the live frame's CMesh (stride 0xE8,
                    // FUN_004059a0) and tests its +0x44..0x58 box as an OBB in the model transform.
                    // The old port froze the spawn-pose mesh bounds into the BoxCollider — a
                    // crate-spawned building kept its crate box after unpacking to the full model,
                    // an animated prop kept its frame-0 extremity (either bigger or smaller than
                    // it should be). NativePickBody re-syncs the box to the live sharedMesh (the
                    // Unity mesh bounds are the dequantisation box = the CMesh record's bbox; the
                    // collider sits on the Model GO, so it is the same oriented box the native tests).
                    var colPolicy = ov?.collider ?? DigglesClassAsset.ColliderPolicy.Inherit;
                    bool boundsBox = colPolicy == DigglesClassAsset.ColliderPolicy.AutoFromBounds
                        || (colPolicy == DigglesClassAsset.ColliderPolicy.Inherit && IsCharacter(className));
                    if (boundsBox)
                    {
                        var b = mf.sharedMesh.bounds;
                        var bc = model.AddComponent<BoxCollider>();
                        bc.center = b.center;
                        bc.size = b.size;
                        model.AddComponent<NativePickBody>().Bind(mf, bc);
                    }
                    else if (colPolicy == DigglesClassAsset.ColliderPolicy.ExplicitBox)
                    {
                        var bc = model.AddComponent<BoxCollider>();
                        bc.center = mf.sharedMesh.bounds.center;
                        bc.size = ov!.colliderSize;
                    }

                    return go;
                }
            }

            // Pure light source (no model) — the light itself is the visual; no pink octahedron.
            // (The invisible-class gates run before resolution now — see the top of the method.)
            if (cls!.Light != null) return go;

            // Unknown class — small wireframe placeholder so we can see it. Logged so a single
            // menu+campaign run yields the real outstanding list. The `why` tail names the first
            // failing resolution step.
            string why;
            if (string.IsNullOrEmpty(obj))
                why = "no model object (TSV/class/obj_init/class-anim table all empty)";
            else if (_lib.GetObjDb(obj) == null)
                why = $"obj '{obj}' not in obj_db map (maps loaded: {_lib.MapsLoaded})";
            else
            {
                // Bypass the controller: if the library CAN build the clip directly, the
                // failure is controller-side (null animator/lib — the silent PlayClip
                // early-outs); if it can't, the [3db] warning right above names the step.
                string attempted = string.IsNullOrEmpty(ent.CurrentAnim) ? defaultAnim : ent.CurrentAnim!;
                int dot = attempted.IndexOf('.');
                string probeObj = dot > 0 ? attempted.Substring(0, dot) : obj;
                string probeClip = dot > 0 ? attempted.Substring(dot + 1) : attempted;
                bool direct = _lib.GetClip(probeObj, probeClip) != null;
                why = $"clip build failed for '{attempted}' from obj '{obj}' " +
                    $"(direct lib build: {direct})";
            }
            // Once per class: headless corpus runs spawn thousands of placeholders and the
            // per-instance warning (with its managed stack walk) dominated batch test time
            // (390 MB logs per full suite).
            if (_placeholderWarned.Add(className))
                Debug.LogWarning($"[spawner] PLACEHOLDER class={className} kind={cls.Kind} defaultAnim={cls.DefaultAnim} — {why}");
            var fallbackModel = BuildInner(go.transform, markerRot);
            var fmf = fallbackModel.AddComponent<MeshFilter>();
            fmf.sharedMesh = GetFallbackMesh();
            var fmr = fallbackModel.AddComponent<MeshRenderer>();
            fmr.sharedMaterial = GetFallbackMat();
            return go;
        }

        public void Destroy(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }

        public void ApplyPosition(GameObject go, Vector3 nativePos)
        {
            if (go == null) return;
            go.transform.localPosition = NativeToLocal(nativePos);
        }

        public void ApplyRotation(GameObject go, Vector3 nativeRotRadians)
        {
            if (go == null) return;
            var model = go.transform.Find("Model");
            if (model == null) return;
            model.localRotation = Quaternion.Euler(-90f, 0f, 0f)
 * MarkerRotFromNative(nativeRotRadians);
        }

        public void ApplyAnim(GameObject go, string animName, int startFrame,
            DigglesAnimator.AnimMode mode)
        {
            if (go == null) return;
            // Route through the controller so any of the object's clips can be
            // lazily built & played (not just a fixed preload set).
            var ctrl = go.GetComponentInChildren<DigglesAnimController>();
            if (ctrl != null) { ctrl.PlayClip(animName, mode, startFrame); return; }
            var animator = go.GetComponentInChildren<DigglesAnimator>();
            if (animator != null && animator.HasClip(animName))
                animator.SetAnim(animName, startFrame, mode);
        }

        // --- helpers ---

        private void ResolveClassAnimModel(
            DigglesClass cls,
            out string obj,
            out string defaultAnim)
        {
            foreach (KeyValuePair<string, string> entry in cls.AnimMap)
            {
                string concreteAnim = entry.Value;
                int dot = concreteAnim.IndexOf('.');
                if (dot <= 0) continue;

                string candidate = concreteAnim.Substring(0, dot);
                if (_lib.GetObjDb(candidate) == null) continue;

                obj = candidate;
                defaultAnim = concreteAnim;
                return;
            }

            obj = "";
            defaultAnim = "";
        }

        // Native Diggles position → WorldRoot-local position. Public so the cutscene presenter
        // can aim the camera into the same space entities render in (focus points come from
        // get_pos = native coords; the entity GameObject lives at WorldRoot.TransformPoint(this)).
        public static Vector3 NativeToLocal(Vector3 native) =>
            new Vector3(native.x, native.z * MarkerHeightScale + MarkerYOffset, -native.y);

        // Inverse of NativeToLocal: WorldRoot-local position → native Diggles coords.
        // Used to turn a world-space click (terrain raycast → WorldRoot.InverseTransformPoint)
        // back into native coords so it can be fed to the Tcl `action … walk -target {x y z}`
        // verb, which (like every faithful caller) expects native coords and re-applies
        // NativeToLocal. Passing world coords there caused the double-transform nav bug.
        public static Vector3 LocalToNative(Vector3 local) =>
            new Vector3(local.x, -local.z, (local.y - MarkerYOffset) / MarkerHeightScale);

        // Native euler (rx,ry,rz radians) → Unity quaternion for the inner Model.
        //
        // The native law (Fuchsschwanz, urw_unq_vodo_001_a.tcl:407): the engine composes the
        // object rotation as M = Ry(ry)·Rx(rx)·Rz(rz) — Z innermost, then X, then Y — with
        // every per-axis matrix the transpose of Unity's same-axis numeric matrix
        // (native rotation directions are mirrored vs Unity's left-hand convention).
        // Verified at two independent decompile sites that consume the +0x40/44/48
        // rotation fields:
        // * FUN_004d2420 (link point → world → screen): rz block x'=x·c+y·s /
        // y'=y·c−x·s; rx block y''=y'·c+z·s / z''=z·c−y'·s; ry block
        // x=x'·c−z''·s / z=z''·c+x'·s — each is Unity's axis matrix at −θ.
        // * FUN_005cbf70 (object offset vector → world): identical shape and order.
        // Unity's Quaternion.Euler(x,y,z) composes Qy·Qx·Qz — the same order — so the
        // exact mapping is Euler(−rx,−ry,−rz): negate all three angles, keep the order.
        // (Same convention DigglesAnimator.LinkEulerToQuat already uses for link
        // eulers — one engine, one euler law.)
        //
        // The old form negated only yaw (Euler(+rx,−ry,+rz)). Yaw-only rotations —
        // every character, most props — are unaffected (x=z=0), which is why the
        // movers/facing laws validated; any authored rx/rz spun the wrong way
        // (the herbalist-camp saw rendered 165° off, blade swung ~1 m screen-right
        // out of its support beam; the.sav bits were correct — presentation only).
        public static Quaternion MarkerRotFromNative(Vector3 nativeRadians) =>
            Quaternion.Euler(-nativeRadians.x * Mathf.Rad2Deg,
                             -nativeRadians.y * Mathf.Rad2Deg,
                             -nativeRadians.z * Mathf.Rad2Deg);

        static GameObject BuildInner(Transform outer, Quaternion markerRot)
        {
            var model = new GameObject("Model");
            model.transform.SetParent(outer, false);
            model.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f) * markerRot;
            model.transform.localScale = Vector3.one * ModelScale;
            return model;
        }

        static bool IsCharacter(string className) =>
            !className.StartsWith("Dummy_", System.StringComparison.Ordinal);

        // Dug-resource pickup classes (z_dignwalk dig_execute drop,:365) — used by the spawn-time
        // settle (no item physics); they render at the standard ModelScale like everything else.
        static bool IsResourcePickup(string className) =>
            className == "Stein" || className == "Eisenerz" || className == "Kohle"
            || className == "Golderz" || className == "Kristallerz";

        // The native ghost/box vertical settle — FUN_0046b190, shared verbatim by the placement
        // tick's mode-2 seat (PlacementController) and the drop settle below. Column-local, in
        // the sub-row grid: (1) climb out of solid — while the sub-column's hmap says rock at
        // this depth (h > z), step one sub-row up (−y), ≤ upBudget rows (native = 16); (2) fall
        // through the open rows (h ≤ z) to the first solid row — the support the object rests
        // on at the picked depth (the meadow line row, a slope's relief row, a front ledge),
        // ≤ downBudget rows (native = 16; drops get a physics-length fall). Either march
        // exhausting ⇒ false (the native then invalidates the spot, FUN_0046b190:113-120).
        // x and z are never touched — only the vertical settles (the mode-2 law).
        public static bool TrySettleToSupport(Navigation.NavWorld nav, ref Vector3 native,
            int upBudget = 16, int downBudget = 16)
        {
            int subX = Mathf.RoundToInt(native.x * 8f);
            int row = Mathf.RoundToInt(native.y * 8f);
            int i = 0;
            for (; i < upBudget && nav.GetHmapSub(subX, row) > native.z; i++) row--;
            if (i == upBudget) return false;
            for (i = 0; i < downBudget && !(nav.GetHmapSub(subX, row) > native.z); i++) row++;
            if (i == downBudget) return false;
            native.y = row * 0.125f;
            return true;
        }

        // Stand-in for the original's item physics on a freshly dug drop / boxed crate (see
        // Spawn + CmdSetBoxed): native coords in/out.
        // (1) the faithful vertical settle (TrySettleToSupport — FUN_0046b190 with a
        // physics-length fall budget): out of rock, then fall to the support row at the
        // drop's own (x, depth). If the landing sub-cell is walkable (+0x3c & 1 — gnomes
        // can fetch it), done;
        // (2) else the legacy spiral-snap (the vector_fixdig/get_digedge idiom, FUN_00594e00's
        // +0x3c & 1 probe) to the nearest walkable sub-cell + the open-band fall — covers
        // drops whose own column has no reachable support (deep inside a wall);
        // (3) depth: rock = z < h, air = z ≥ h (ant-farm frame), so
        // clamp z into [h(row above)+0.5, 14.5] (14.5 = DAT_00737564, the vector_fixdig
        // cap) of the settled column — never inside the rock face. Unreachable nav keeps
        // the raw position. Public+static so the EditMode terrain tests can pin it.
        public static Vector3 SettleDropPosition(Navigation.NavWorld nav, Vector3 native)
        {
            // (1) the faithful column-local settle first
            var p = native;
            p.z = Mathf.Clamp(p.z, 0.5f, 14.5f);
            if (TrySettleToSupport(nav, ref p, upBudget: 16, downBudget: 256))
            {
                int px = Mathf.RoundToInt(p.x * 8f), py = Mathf.RoundToInt(p.y * 8f);
                if ((nav.SubAt(px, py) & Navigation.NavWorld.SubWalk) != 0)
                {
                    float hh = nav.GetHmapSub(px, py - 1);
                    p.z = Mathf.Max(Mathf.Min(p.z, 14.5f), hh + 0.5f);
                    return p;
                }
            }
            // (2) the legacy nearest-walkable spiral
            int sx = Mathf.RoundToInt(native.x * 8f), sy = Mathf.RoundToInt(native.y * 8f);
            if (!TrySnapToWalkable(nav, sx, sy, 24, out int fx, out int fy)) return native;
            // Fall down the open band to the floor bit (gravity).
            for (int k = 0; k < 16 && (nav.SubAt(fx, fy) & Navigation.NavWorld.SubFloor) == 0; k++)
            {
                if ((nav.SubAt(fx, fy + 1) & Navigation.NavWorld.SubWalk) == 0) break;   // band ended
                fy++;                                                                    // keep falling
            }
            // Depth: like a standing gnome, the drop's body occupies the open pocket above the
            // floor row — "out of rock" is the row-above height (a floor row's own column is the
            // ground, h often 15), capped at 14.5 (DAT_00737564, the vector_fixdig cap).
            float h = nav.GetHmapSub(fx, fy - 1);
            float z = Mathf.Max(Mathf.Min(native.z, 14.5f), h + 0.5f);
            return new Vector3(fx * 0.125f, fy * 0.125f, z);
        }

        // The native point-physics drop, as a one-shot: pure gravity down the item's own column.
        // set_boxed / set_pos on a physic-enabled free item kick the physics job in the original
        // (FUN_0067dc80:128-129 → FUN_005ebc90 physics-mgr + FUN_005eb470 enqueue; gated on flag
        // 0x10 physic-enabled, not contained (+0x3d0 bit 2), no carrier +0x238/+0x278) — and that
        // job only ever makes the item fall. It never climbs out of rock and never snaps
        // laterally (those belong to SettleDropPosition, the dig-drop spawn stand-in). The support
        // probe is the sub-row below the rounded point, ROUND(y*8)+1 — the same verified native
        // settle-row convention as FloorPathMap's interior settle (FUN_0041d8a0:985, floor test
        //:1013, y advance:1022). Probing the point's own air row can mistake a relief height for
        // support and stop a falling crate early. A point already supported in both its anchor and
        // below rows keeps the exact authored position, which leaves a growth template's boxed
        // crate inside its Schatztonne's ±0.32/±0.64 takeitems capture box
        // (urw_gng_004_f.tcl:18-29 + takeitems.tcl:17).
        public static Vector3 GravitySettle(Navigation.NavWorld nav, Vector3 native, int downBudget = 256)
        {
            int subX = Mathf.RoundToInt(native.x * 8f);
            int anchorRow = Mathf.RoundToInt(native.y * 8f);
            bool anchorSupported = nav.GetHmapSub(subX, anchorRow) > native.z;
            int probeRow = anchorRow + 1;
            for (int i = 0; i < downBudget; i++, probeRow++)
            {
                if (nav.GetHmapSub(subX, probeRow) <= native.z) continue;
                if (i == 0 && anchorSupported) return native;
                return new Vector3(native.x, probeRow * 0.125f, native.z);
            }
            return native;
        }

        // The death/release scatter settle: the same one-shot point gravity as GravitySettle,
        // then vector_fix's non-walkable seat law on the depth axis at the landed row —
        // z = min(max(z, hmap(x, y−1) + 1), 14.5) (FUN_00595770:255-267; cap DAT_00737564).
        // Consumers are the raw `set_physic 1 → set_pos` scatter chains that never vector_fix:
        // the gnome death drop (inv_rem_all, z_procs.tcl:1854-1863 — items authored one tile
        // above the corpse, {0 -1 0}) and the production release (release_item,
        // genericprod.tcl:520-537). In the native these rely on the physics job
        // (FUN_0067dc80:128-129 → FUN_005eb470) to fall; the depth clamp keeps a drop that
        // lands beside a relief step from being buried behind the exposed face — the same
        // post-settle law the item putdown preview applies (PlacementController
        //.ClampItemDropDepthToFloor). Gravity itself never moves x or z (point physics).
        //
        // The clamp runs only when the item actually fell: a kept authored position (double
        // support, or no support in budget) is script truth and must stay byte-exact — z
        // included — or the Schatztonne ±0.32/±0.64 capture box misses its template crates
        // (urw_gng_004_f.tcl:18-29 + takeitems.tcl:17), and a half-row seat's row-above can be
        // solid rock (hmap 15) which would slam z to the 14.5 cap. After a real fall the
        // row above the landing row provably read hmap ≤ z during the probe, so the clamp
        // raises z by at most 1 — just out of the relief face.
        public static Vector3 SettleFreeItemDrop(Navigation.NavWorld nav, Vector3 native)
        {
            var p = GravitySettle(nav, native);
            if (p.y <= native.y + 1e-4f) return p;   // did not fall — authored seat is script truth
            int subX = Mathf.RoundToInt(p.x * 8f);
            int subY = Mathf.RoundToInt(p.y * 8f);
            if (subX < 0 || subX >= nav.SubW || subY <= 0 || subY >= nav.SubH) return p;
            float supportDepth = nav.GetHmapSub(subX, subY - 1) + 1f;
            p.z = Mathf.Min(Mathf.Max(p.z, supportDepth), 14.5f);
            return p;
        }

        // ---- loose-item drop-on-dig (the original's set_physic gravity, rohstoffe.tcl) ----
        // When a carve removes the ground under a resting loose item (a dug-out Stein, a Steinbrocken
        // boulder, ore chunks — all def_class … material with set_physic 1), it must fall onto the
        // new floor. Death-scattered tools and logic-less transports (panniers) are the same
        // native physics clients — inv_rem_all runs set_physic 1 on every dropped item
        // (z_procs.tcl:1859), and the enqueue gate FUN_005eb470 is flag-0x10 + not-contained,
        // not kind-based — so they fall too, gated on the live physic flag (a posed prod output
        // has set_physic 0, z_work_prod.tcl:616, and must stick).
        // The original runs continuous item physics; the port has none, so we re-settle
        // on demand when the terrain reports a changed tile rect (DiggableTerrain.TilesChanged →
        // this). For each eligible item whose column overlaps the rect and that sits at/above the
        // carve (support is below = larger native y), re-run the same column-local drop settle used
        // at spawn; if it fell (native y increased) commit the new position + move its GameObject.
        // Returns the number of items that dropped. `onMoved` lets the caller sync the transform.
        public static int ResettleLooseItems(Navigation.NavWorld nav,
            System.Collections.Generic.IEnumerable<DigglesTclEntity> items,
            int tx0, int ty0, int tx1, int ty1, System.Action<DigglesTclEntity>? onMoved = null)
        {
            if (nav == null || items == null) return 0;
            int moved = 0;
            foreach (var e in items)
            {
                if (e?.Class == null || e.GameObject == null || !ResettleEligible(e)) continue;
                // Contained items are out of the world spatial grid (native InvAdd pulls them;
                // the physics enqueue FUN_005eb470 refuses objects with the +0x3d0 contained
                // bit 2) — a carve must never re-float a barrel's contents or a Lager's
                // shelf-posed stock (lager.tcl store_item:734-744 poses items above the floor).
                if (e.ContainedIn >= 0) continue;
                int itx = Mathf.RoundToInt(e.Position.x), ity = Mathf.RoundToInt(e.Position.y);
                // column must overlap the changed x-range, and the item must not sit below the whole
                // carve (a carve strictly under the item can drop it; one above it cannot).
                if (itx < tx0 - 1 || itx > tx1 + 1 || ity > ty1 + 1) continue;
                var settled = SettleDropPosition(nav, e.Position);
                if (settled.y > e.Position.y + 0.02f)   // support removed → the item fell
                {
                    e.Position = settled;
                    if (!e.HasSpawnPosition) { e.SpawnPosition = e.Position; e.HasSpawnPosition = true; }
                    moved++;
                    onMoved?.Invoke(e);
                }
            }
            return moved;
        }

        // Which loose items a carve re-settles. Materials keep the original unconditional law
        // (every rohstoffe drop runs set_physic 1 at spawn; older saves may lack the var).
        // Tools and logic-less transports (the death-scatter classes, z_procs.tcl:1854-1863;
        // the same item scope as the putdown seat law) qualify only with the live
        // physic flag set — matching the native enqueue gate flag 0x10 (FUN_005eb470), so a
        // posed/held tool with set_physic 0 (z_work_prod.tcl:616) never re-floats or drops.
        static bool ResettleEligible(DigglesTclEntity e)
        {
            string kind = e.Class!.Kind;
            if (kind == "material") return true;
            if (kind != "tool" && !(kind == "transport" && e.TransportLogic == null)) return false;
            return e.VarsScope.Vars.TryGetValue("_physic", out var v) && (v == "1" || v == "true");
        }

        // Square-ring spiral to the nearest sub-cell with the WALK bit (+0x3c & 1) set. Within a
        // ring the true nearest (min dx²+dy²) wins — the old first-hit scan order biased the snap
        // up-left by up to a full ring radius (3 native units at r=24).
        static bool TrySnapToWalkable(Navigation.NavWorld nav, int sx, int sy, int maxR,
            out int fx, out int fy)
        {
            fx = sx; fy = sy;
            if ((nav.SubAt(sx, sy) & Navigation.NavWorld.SubWalk) != 0) return true;
            for (int r = 1; r <= maxR; r++)
            {
                int best = int.MaxValue;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;   // perimeter only
                        if ((nav.SubAt(sx + dx, sy + dy) & Navigation.NavWorld.SubWalk) == 0) continue;
                        int d2 = dx * dx + dy * dy;
                        if (d2 < best) { best = d2; fx = sx + dx; fy = sy + dy; }
                    }
                if (best != int.MaxValue) return true;
            }
            return false;
        }

        // Light tuning (live-tweakable). class_light range is native/world units; colour is premultiplied by
        // LightIntensity at upload. These feed a static custom light buffer the Diggles shaders loop (not URP
        // additional lights), like the original's software per-vertex accumulation — so URP Forward+
        // clustering (near-camera flicker, per-tile/per-object light limits) never applies.
        public static float LightRangeScale = 1.0f;
        public static float LightIntensity = 1.0f;
        const int MaxClassLights = 128;

        // Static (token 0/1) lights carry their exact archive floats in nativePos/nativeRange —
        // the presentation transform (NativeToLocal, range scale + 0.5 clamp) is lossy, so
        // capture must never re-derive them from the Transform.
        readonly List<(Transform t, Color col, float range, DigglesTclEntity? ent,
            Vector3 nativePos, float nativeRange)> _classLights = new();

        public ClassLightSaveState[] CaptureClassLights()
        {
            var result = new List<ClassLightSaveState>(_classLights.Count);
            for (int index = 0; index < _classLights.Count; index++)
            {
                var (transform, color, range, entity, nativePos, nativeRange) = _classLights[index];
                if (transform == null) continue;
                Vector3 nativePosition = entity == null
                    ? nativePos
                    : new Vector3(
                        transform.localPosition.x,
                        -transform.localPosition.z,
                        transform.localPosition.y / MarkerHeightScale);
                result.Add(new ClassLightSaveState(
                    nativePosition,
                    color,
                    entity == null ? nativeRange :
                        (LightRangeScale > 0f ? range / LightRangeScale : range),
                    entity?.Handle ?? -1));
            }
            ClassLightSaveState[] captured = result.ToArray();
            ValidateClassLights(captured);
            return captured;
        }

        public static void ValidateClassLights(IReadOnlyList<ClassLightSaveState>? lights)
        {
            if (lights == null) throw new System.ArgumentNullException(nameof(lights));
            for (int index = 0; index < lights.Count; index++)
            {
                ClassLightSaveState light = lights[index];
                if (!Finite(light.NativePosition) || !Finite(light.Color.r) ||
                    !Finite(light.Color.g) || !Finite(light.Color.b) ||
                    !Finite(light.Range) || light.Range < 0f || light.ObjectId < -1)
                {
                    throw new System.ArgumentException(
                        "A class-light capture contains invalid native state.",
                        nameof(lights));
                }
            }
        }

        static bool Finite(Vector3 value) =>
            Finite(value.x) && Finite(value.y) && Finite(value.z);

        static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        // Save-resident token 0/1 lights have no owning entity to recreate them at spawn.
        // Keep them in the same shader buffer, but anchor them directly under the world root.
        public void AddStaticLight(
            Transform parent,
            Vector3 nativePosition,
            Color color,
            float range)
        {
            if (parent == null) throw new System.ArgumentNullException(nameof(parent));
            var lightGo = new GameObject("StaticLight");
            lightGo.transform.SetParent(parent, false);
            lightGo.transform.localPosition = NativeToLocal(nativePosition);
            _classLights.Add((
                lightGo.transform,
                color,
                Mathf.Max(0.5f, range * LightRangeScale),
                null,
                nativePosition,
                range));
        }

        void AddLight(GameObject outer, LightSpec spec, DigglesTclEntity? ent = null)
        {
            var lightGo = new GameObject("Light");
            lightGo.transform.SetParent(outer.transform, false);
            // class_light offset is native (x,y,z); map its linear delta like NativeToLocal.
            lightGo.transform.localPosition = new Vector3(spec.Ox, spec.Oz * MarkerHeightScale, -spec.Oy);
            _classLights.Add((lightGo.transform,
                new Color(spec.R, spec.G, spec.B),
                Mathf.Max(0.5f, spec.Range * LightRangeScale),
                ent,
                new Vector3(spec.Ox, spec.Oy, spec.Oz),
                spec.Range));
        }

        // Reusable upload buffers (no per-frame allocation).
        readonly Vector4[] _lightPos = new Vector4[MaxClassLights];
        readonly Vector4[] _lightCol = new Vector4[MaxClassLights];
        readonly Plane[] _lightFrustum = new Plane[6];
        Camera? _lightCam;

        // A light whose falloff sphere is entirely outside the camera frustum can't affect any visible
        // pixel, so it's dropped before upload — shrinking _DigLightCount, which every terrain + model
        // fragment loops over. The falloff (exp(-2(d/R)^2)) is ~0.03% at 2R, so cull at 2×range to keep
        // any light whose lit area could still reach on-screen geometry (no edge popping). Visually
        // identical to uploading all of them; the win is the per-fragment loop no longer iterating
        // off-screen torches in a big lit cave (the diagnosis's dominant GPU cost).
        const float CullRangePad = 2f;

        // Fog display field for the smooth light gate: set by
        // DigglesWorldRuntime alongside the presenter; null at the menu / in headless.
        public Diggles.Runtime.World.FogOfWarPresenter? FogPresenter;
        public Diggles.Runtime.World.FogOfWarState? FogState;

        // The per-light fog factor, from the presenter's smoothed display texel at the
        // owner's tile — the same field the terrain/model shaders sample for the veil, so
        // the light dims in exact lockstep with the graying of the geometry it lights
        // (the old binary FowHidden gate popped at the 0x80 crossing; this factor fades
        // instead). r = veil fogFactor display, g = black-indicator display:
        // viewinfog owners (LightSrc ambience): g/255 — lit while
        // explored (matches their gray-fog ghost models), smooth on first reveal;
        // normal owners (auto torches): (g/255)·(1−r/255) — fade in over
        // the reveal dissolve (~0.7 s), fade out with the veil creep; ≈0 at full gray
        // exactly when the old binary gate fired, but with no pop.
        // Pure — unit-tested (TunnelTorchTests.FogLightFactor_*).
        public static float FogLightFactor(byte displayR, byte displayG, bool viewInFog)
        {
            float g = displayG / 255f;
            return viewInFog ? g : g * (1f - displayR / 255f);
        }

        // Refill the global light arrays (_DigLights / _DigLightCols / _DigLightCount) from the live marker
        // transforms and push them — DigglesGround + DigglesModel loop these. Called every frame (LateUpdate)
        // so runtime add / remove / move of class_light sources is reflected; destroyed markers (null
        // transform) are skipped. Cheap: a ≤128 loop + a 4 KB constant upload, no allocation.
        public void UploadClassLights()
        {
            if (_lightCam == null) _lightCam = Camera.main;
            bool cull = _lightCam != null;
            if (cull) GeometryUtility.CalculateFrustumPlanes(_lightCam, _lightFrustum);
            // Smooth path only while the fog actually shows (cutscene reveal / -nofow /
            // menu ⇒ lights at full). DisplayAt reads the same smoothed field the shaders
            // sample — one mechanism drives the veil AND the light fade.
            bool fogged = FogPresenter != null && FogState is { Enabled: true, ShowFog: true };
            int n = 0;
            for (int i = 0; i < _classLights.Count && n < MaxClassLights; i++)
            {
                var (t, c, r, e, _, _) = _classLights[i];
                if (t == null) continue;                  // marker entity was destroyed
                float f = 1f;
                if (e != null)
                {
                    if (fogged)
                    {
                        bool vif = e.Class?.ViewInFog ?? false;
                        if (e.VarsScope.Vars.TryGetValue("_viewinfog", out var vv))
                            vif = vv == "1" || vv == "true";
                        var d = FogPresenter!.DisplayAt((int)e.Position.x, (int)e.Position.y);
                        f = FogLightFactor(d.r, d.g, vif);
                        if (f <= 0.004f) continue;        // fully veiled/black — free the slot
                    }
                    // No presenter (headless) — keep the binary gate as the fallback.
                    else if (e.FowHidden) continue;
                }
                var p = t.position;                       // live world-space position
                if (cull && !SphereInFrustum(p, r * CullRangePad)) continue;   // off-screen → no visible effect
                _lightPos[n] = new Vector4(p.x, p.y, p.z, r);
                float k = LightIntensity * f;
                _lightCol[n] = new Vector4(c.r * k, c.g * k, c.b * k, 1f);
                n++;
            }
            Shader.SetGlobalVectorArray("_DigLights", _lightPos);
            Shader.SetGlobalVectorArray("_DigLightCols", _lightCol);
            Shader.SetGlobalInt("_DigLightCount", n);
            Shader.SetGlobalFloat("_DigLightCap", ClassLightCap);
        }

        // Accumulated class-light ceiling the shaders clamp the summed torch term at.
        // 1.0 = the native diffuse saturation min(I,255) in port units (RENDERING_LIGHTING_
        // RESEARCH §2a) — 3-4 overlapping torches plateau instead of blinding. A tunable knob:
        // diggles_config.json "classLightCap" overrides this at world build (DigglesWorldRuntime),
        // or tweak live in the Inspector. >1 = brighter-than-native stacking, small values dim all torches.
        [Range(0f, 4f)] public float ClassLightCap = 1f;

        bool SphereInFrustum(Vector3 center, float radius)
        {
            for (int i = 0; i < 6; i++)
                if (_lightFrustum[i].GetDistanceToPoint(center) < -radius) return false;
            return true;
        }

        // Test seam: lets presentation asserts distinguish a real model from the pink
        // placeholder octahedron (both have a non-null sharedMesh).
        public static bool IsFallbackMesh(Mesh? mesh) =>
            mesh != null && mesh.name == "SpawnFallback";

        Mesh GetFallbackMesh()
        {
            if (_fallbackMesh != null) return _fallbackMesh;
            // Octahedron, sized up since the inner model carries the small
            // ModelScale (raw bbox units would otherwise be invisible).
            const float r = 1f / ModelScale * 0.5f;
            var verts = new[] {
                new Vector3( 0, r, 0), new Vector3( r, 0, 0), new Vector3( 0, 0, r),
                new Vector3(-r, 0, 0), new Vector3( 0, 0,-r), new Vector3( 0,-r, 0),
            };
            var tris = new[] {
                0,1,2, 0,2,3, 0,3,4, 0,4,1,
                5,2,1, 5,3,2, 5,4,3, 5,1,4,
            };
            _fallbackMesh = new Mesh { name = "SpawnFallback", vertices = verts, triangles = tris };
            _fallbackMesh.RecalculateNormals();
            _fallbackMesh.RecalculateBounds();
            return _fallbackMesh;
        }

        Material GetFallbackMat()
        {
            if (_fallbackMat != null) return _fallbackMat;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _fallbackMat = new Material(shader!) { name = "SpawnFallbackMat" };
            if (_fallbackMat.HasProperty("_BaseColor"))
                _fallbackMat.SetColor("_BaseColor", new Color(1f, 0.3f, 0.8f, 1f));
            return _fallbackMat;
        }
    }

    // The boxed-crate content icon: the native set_boxed boxing side effect attaches an
    // object icon of the boxed class to the object — FUN_0067dc80:150 calls
    // FUN_005dc780(obj, className): find a free slot in the object's icon table (obj+0x20,
    // slots 8..15), resolve the class name in the icon registry (FUN_004c2b50's stricmp
    // list) and set the slot via FUN_005dc850(slot, obj, type=5, iconIdx). Unboxing clears
    // the slots (FUN_005dc6a0). Port: one SpriteRenderer child fed by the same
    // data/gui/icons/<Class>.tga pipeline the panel uses (IconLoader).
    //
    // Render contract: in the original the icon sits flat on the front face of the crate
    // box, not a floating billboard — the icon quad lies on the crate's camera-side face,
    // world-axis aligned, sized to the face.
    // Frames: the outer entity GO sits under the Q_x(-90) WorldRoot, so its local axes map
    // +z → world +Y (game up) and +y → world −Z (toward the camera — DigglesCameraController
    // orbits the world −Z side looking +Z). Local −z is world down, so a sign error here
    // renders the icon underground.
    // A Unity sprite is drawn in its transform's XY plane and viewed from its −Z side, so a
    // world-identity rotation faces our camera; position/size come from the live crate
    // model's renderer bounds (kiste.standard is already swapped in by set_boxed's PlayClip
    // before Attach runs).
    public static class BoxContentIcon
    {
        const string ChildName = "BoxContentIcon";
        const float FaceFill = 0.8f;        // icon width as a fraction of the front face
        static IconLoader? s_icons;
        static string? s_iconsRoot;

        public static void Attach(GameObject owner, string className, string dataRoot)
        {
            if (owner == null || string.IsNullOrEmpty(className) || string.IsNullOrEmpty(dataRoot)) return;
            Detach(owner);
            if (s_icons == null || s_iconsRoot != dataRoot)
            {
                s_icons?.Dispose();
                s_icons = new IconLoader(dataRoot);
                s_iconsRoot = dataRoot;
            }
            var sprite = s_icons.Get(className);
            if (sprite == null) return;                    // class without an icon — nothing shown
            var go = new GameObject(ChildName);
            go.transform.SetParent(owner.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            Seat(go, owner, sprite);
            // The kiste mesh swap can land a frame late (PlayClip → animator tick) — re-seat once
            // on the next frame so the face is measured off the crate, not the boxed building.
            go.AddComponent<BoxContentIconReseat>().Init(owner, sprite);
        }

        internal static void Seat(GameObject go, GameObject owner, Sprite sprite)
        {
            float w = sprite.rect.width / sprite.pixelsPerUnit;
            // Flat on the front face: world-identity rotation (sprite XY plane, −Z toward camera).
            go.transform.rotation = Quaternion.identity;
            if (TryGetModelBounds(owner, out var b))
            {
                // Centered on the front (world −Z) face, a hair off the surface so it does not
                // z-fight the crate texture.
                go.transform.position = new Vector3(b.center.x, b.center.y, b.min.z - 0.02f);
                float target = Mathf.Min(b.size.x, b.size.y) * FaceFill;
                if (w > 0.01f && target > 0.01f)
                {
                    // Uniform world size regardless of any inherited parent scale.
                    float inherited = Mathf.Max(0.0001f, go.transform.lossyScale.x
                                                         / Mathf.Max(0.0001f, go.transform.localScale.x));
                    go.transform.localScale = Vector3.one * (target / w / inherited);
                }
            }
            else
            {
                // No renderable model (headless tests / missing 3db) — seat on the near side at
                // half height: local +y = toward camera, local +z = world up (frame note above).
                go.transform.localPosition = new Vector3(0f, 0.5f, 0.5f);
                if (w > 0.01f) go.transform.localScale = Vector3.one * (0.55f / w);
            }
        }

        // The displayed model's mesh only. Two traps:
        // • Renderer.bounds lies here: DigglesAnimator freezes MeshRenderer.localBounds across
        // clip swaps (render-sort stability), so after set_boxed it still reports the boxed
        // building's box, not the kiste's. Measure mesh.bounds of the current sharedMesh
        // through the model transform instead.
        // • GetComponentsInChildren<Renderer> also swept link children (hats/items), widening
        // the face. Restrict to the animator's own mesh; renderer sweep stays as fallback.
        static bool TryGetModelBounds(GameObject owner, out Bounds bounds)
        {
            bounds = default;
            var anim = owner.GetComponentInChildren<DigglesAnimator>();
            var mf = anim != null ? anim.GetComponent<MeshFilter>() : null;
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh != null)
            {
                var lb = mesh.bounds;
                bool any = false;
                for (int i = 0; i < 8; i++)
                {
                    var corner = lb.center + Vector3.Scale(lb.extents, new Vector3(
                        (i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    var wpt = mf!.transform.TransformPoint(corner);
                    if (!any) { bounds = new Bounds(wpt, Vector3.zero); any = true; }
                    else bounds.Encapsulate(wpt);
                }
                if (any && bounds.size.sqrMagnitude > 1e-6f) return true;
            }
            bool anyR = false;
            foreach (var r in owner.GetComponentsInChildren<Renderer>())
            {
                if (r.gameObject.name == ChildName) continue;
                if (!anyR) { bounds = r.bounds; anyR = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return anyR && bounds.size.sqrMagnitude > 1e-6f;
        }

        // One-shot: re-seats the icon on the frame after attach (the PlayClip("kiste.standard")
        // mesh swap may only land on the animator's next tick), then removes itself.
        sealed class BoxContentIconReseat : MonoBehaviour
        {
            GameObject? _owner;
            Sprite? _sprite;
            public void Init(GameObject owner, Sprite sprite) { _owner = owner; _sprite = sprite; }
            void LateUpdate()
            {
                if (_owner != null && _sprite != null) Seat(gameObject, _owner, _sprite);
                Destroy(this);
            }
        }

        public static void Detach(GameObject owner)
        {
            if (owner == null) return;
            var t = owner.transform.Find(ChildName);
            if (t != null)
            {
                if (Application.isPlaying) Object.Destroy(t.gameObject);
                else Object.DestroyImmediate(t.gameObject);
            }
        }
    }

    // The native pick-body law: the mouse pick tests the baked bbox of the
    // object's currently displayed mesh (anim-class record → live frame's CMesh +0x44..0x58,
    // FUN_004059a0's 0xE8-stride table) as an OBB in the model transform. The port's equivalent:
    // keep the Model GO's BoxCollider synced to the live sharedMesh bounds — DigglesAnimator
    // swaps the frame mesh (10 FPS mesh-swap path) or holds a clip's VAT base mesh (bounds = the
    // clip's reference frame, within a frame of the native per-frame box). Reference-compare per
    // LateUpdate — no per-frame bounds math unless the mesh actually changed (anim/clip swap,
    // set_boxed crate model swap, unpack restore).
    public sealed class NativePickBody : MonoBehaviour
    {
        MeshFilter? _mf;
        BoxCollider? _bc;
        Mesh? _lastMesh;

        public void Bind(MeshFilter mf, BoxCollider bc)
        {
            _mf = mf;
            _bc = bc;
            _lastMesh = mf.sharedMesh;
        }

        void OnEnable() => DigglesFrameDriver.Register(this);    // central late tick
        void OnDisable() => DigglesFrameDriver.Unregister(this);

        // Public so EditMode tests can drive the sync (no player loop there).
        public void SyncNow()
        {
            if (_mf == null || _bc == null) return;
            var mesh = _mf.sharedMesh;
            if (ReferenceEquals(mesh, _lastMesh)) return;
            _lastMesh = mesh;
            if (mesh == null) return;
            var b = mesh.bounds;
            _bc.center = b.center;
            _bc.size = b.size;
        }
    }
}
