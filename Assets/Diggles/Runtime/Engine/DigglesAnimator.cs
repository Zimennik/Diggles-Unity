#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    [Serializable]
    public sealed class DigglesAnimClip
    {
        public string Name = "";
        // Source anim-DB object this clip was built from ("mann", "frau", "feuerstelle", …).
        // Stamped by DigglesModelLibrary.GetClip / the editor importer. The world-sound service
        // keys the native per-anim-frame SFX table by "<SourceObjName>.<Name>" — exactly the
        // qualified name `sound anim` registrations use (soundinit.tcl, FUN_004cf770).
        public string SourceObjName = "";

        // Cached "<SourceObjName>.<Name>" for the anim-frame SFX table lookup. Both parts are set
        // at build time and never mutated, so compute it once instead of concatenating a fresh
        // string on every frame advance of every animator.
        [System.NonSerialized] string? _qualified;
        public string QualifiedName => _qualified ??= SourceObjName + "." + Name;

        public DigglesAnimator.AnimMode DefaultMode = DigglesAnimator.AnimMode.Loop;
        public Vector3 MotionOffset;
        public Vector3 RotationDelta;
        public Mesh[] Frames = Array.Empty<Mesh>();

        // Optional per-submesh materials for this clip (frame 0 layout). When
        // non-null, DigglesAnimator applies them to the MeshRenderer on SetAnim.
        // The import pipeline leaves this null and sets renderer materials once;
        // the runtime DigglesModelLibrary fills it so clips that live on the same
        // object but reference different sub-meshes still render with the right
        // textures.
        public Material[]? Materials;

        // GPU vertex-morph (interpolation) data, built by DigglesModelLibrary for
        // multi-frame clips with consistent topology:
        // * BaseMesh — one static mesh (frame-0 topology) whose UV1.x carries
        // each vertex's column in the VAT. Shared across all instances.
        // * Vat — RGBAFloat texture, width = vertexCount, height = frameCount;
        // pixel (v,f) = object-space position of vertex v at frame f.
        // * Materials (above) use the Diggles/ModelMorph shader; the animator
        // feeds _F0/_F1/_Blend (+ the VAT) per frame via a property block, and
        // the vertex shader lerps frame f0→f1 → smooth 60 FPS from 10 FPS keys.
        // Null when morph isn't available (single frame / mismatched topology) —
        // the animator then falls back to per-frame mesh swapping.
        public Mesh? BaseMesh;
        public Texture2D? Vat;
        public int VatFrameCount;

        // Source.3db + per-frame morph-mesh index, so link/attachment points (hats,
        // held items) can be looked up for the current frame. Populated by
        // DigglesModelLibrary; null for import-built clips (no link points then).
        public ThreeDbModel? SourceModel;
        public int[] SourceMeshIndices = Array.Empty<int>();
        // Per-frame vertex-decode affine (world = A + u16*S), so link points map into the
        // same space as the frame's vertices. Parallel to Frames.
        public Vector3[] FrameLinkA = Array.Empty<Vector3>();
        public Vector3[] FrameLinkS = Array.Empty<Vector3>();
    }

    // Vertex-morph animator. Plays Diggles.3db animations as a sequence of
    // pre-baked Mesh swaps on a MeshFilter. Mirrors the engine's playback:
    // frame = startFrame + round((now - startTime) * 10 + 0.05)
    // with Still/Once/Loop semantics from animinit.tcl.
    //
    // The editor's MarkerModelResolver populates Clips at import time;
    // OnEnable kicks off the default clip.
    public sealed class DigglesAnimator : MonoBehaviour
    {
        public enum AnimMode { Still = 0, Once = 1, Loop = 2 }

        const float Fps = 10f;
        const float RoundOffset = 0.05f;

        // Frame-advance rate multiplier (1 = the native 10 FPS). Locomotion sets
        // this so the leg cycle speeds up with the body → feet don't skate when
        // moving faster than the clip's authored stride. Reset to 1 for idle.
        public float PlaybackSpeed = 1f;

        [SerializeField] MeshFilter? meshFilter;
        [SerializeField] MeshRenderer? meshRenderer;
        [SerializeField] DigglesAnimClip[] clips = Array.Empty<DigglesAnimClip>();
        [SerializeField] string defaultClipName = "";
        [SerializeField] AnimMode defaultMode = AnimMode.Loop;
        [SerializeField] int defaultStartFrame;

        // GPU vertex-morph interpolation: smooth the 10 FPS keyframes by lerping
        // between consecutive frames in the vertex shader (VAT). Off → hard
        // per-frame mesh swaps (the original look).
        public bool Interpolate = true;

        // ---- GPU-instancing prototype (off by default) --------------------------------------
        // Every morph model sets a per-renderer MaterialPropertyBlock each frame (_UseVat/_Vat/_F0/
        // _F1/_Blend below). A non-empty MPB makes a renderer SRP-Batcher-ineligible, so each gnome/
        // prop becomes its own draw + SetPass. GPU instancing is the way around it: renderers that
        // share (mesh=clip.BaseMesh, material, VAT) draw together with _F0/_F1/_Blend supplied
        // per-instance via the MPB.
        //
        // Enabling this:
        // * makes DigglesModelLibrary.GetMaterials set material.enableInstancing = true, and
        // * the Diggles/Model shader's instancing variant (#pragma multi_compile_instancing) reads
        // _F0/_F1/_Blend from its UNITY_INSTANCING_BUFFER instead of UnityPerMaterial.
        // The MPB feed in ApplyCurrentFrame is unchanged — Unity dynamic-instances renderers that
        // share mesh+material and carry per-instance MPB floats. Only same-clip characters (same VAT
        // texture) instance together.
        //
        // With the flag off the shader renders identically to the faithful path and stays
        // SRP-Batcher-compatible; turning it on is a rendering change that needs visual validation.
        public static bool UseInstancing = false;

        DigglesAnimClip? currentClip;
        AnimMode currentMode;
        int currentStartFrame;
        double currentStartTime;

        // Game-time scale for anim playback (gametime factor / stop). The native logic loop
        // frame-steps anims inside the scaled SrvDoThink pass, so `gametime factor 0.3` slows
        // every clip too and a stopped clock freezes them.
        //
        // Ownership: published only by the active context's frame-loop driver via
        // PublishTimeScale — EngineTickService (play scenes, from the live scope's current
        // engine.Sim) or GameSimDriver (tool hosts). It must never be written from
        // DigglesGameSim.Step: any stepped sim (a stale scope mid-teardown, an EditMode test
        // sim, a paused tools sim) would stomp the global every step and freeze or slow every
        // animator in the process.
        public static double GlobalTimeScale => s_timeScale;
        static double s_timeScale = 1.0;

        // The one blessed writer — see the ownership note above. scale 0 is legitimate
        // (gametime stop / factor 0 → faithful global anim freeze); negatives/NaN are not.
        public static void PublishTimeScale(double scale)
        {
            if (!double.IsFinite(scale) || scale < 0.0) return;
            s_timeScale = scale;
        }

        // Per-instance game-time clock: advanced only by TickClock (render dt × GlobalTimeScale).
        // All playback phase math runs on this clock, never Time.timeAsDouble. Mixing the two
        // domains (start stamp from _animClock, elapsed from absolute Unity time) makes every
        // clip look minutes old at issue: Once clips snap to their last frame instantly and
        // loops ignore slow-mo.
        double _animClock;
        public double AnimClock => _animClock;
        int lastEmittedFrame = -1;

        // ---- anim-frame SFX seam ----------------------------------------------------------
        // The native engine plays per-anim-frame sounds from a per-object render-tick that
        // compares the object's last sound frame (obj+0x3b8) with the current visual frame and
        // fires the registered entries for every newly-entered frame (FUN_005cb9a0:132-222).
        // The port mirrors that: whenever ApplyCurrentFrame lands on a different frame than the
        // last raise, AnimFrameAdvanced fires with (animator, clip, previousFrame, currentFrame).
        // The subscriber (WorldSoundService) owns the native frame-iteration policy. prev = -1
        // right after SetAnim (a clipstart), matching the native "frame changed" outcome
        // on a clip switch. Static so one service hears every animator without per-spawn wiring.
        public static event Action<DigglesAnimator, DigglesAnimClip, int, int>? AnimFrameAdvanced;
        int _lastSoundFrame = -1;   // obj+0x3b8 equivalent
        // Current interpolation state (set every ApplyCurrentFrame, both morph + swap):
        // frame f0 → next f1 with `blend`. Used to ride link points (hats/items) smoothly.
        int _curFrame, _curNext;
        float _curBlend;
        // Morph-path change-guard: the last (f0,f1,blend) actually pushed to the property block.
        // ApplyCurrentFrame skips the GetPropertyBlock/SetPropertyBlock marshal when these are
        // unchanged (Still clips, time-frozen loops), mirroring the swap path's lastEmittedFrame.
        int _lastMorphFrame = -1, _lastMorphNext = -1;
        float _lastMorphBlend = float.NaN;
        Dictionary<string, DigglesAnimClip>? byName;
        MaterialPropertyBlock? mpb;

        static readonly int IdUseVat = Shader.PropertyToID("_UseVat");
        static readonly int IdVat = Shader.PropertyToID("_Vat");
        static readonly int IdF0 = Shader.PropertyToID("_F0");
        static readonly int IdF1 = Shader.PropertyToID("_F1");
        static readonly int IdBlend = Shader.PropertyToID("_Blend");

        public IReadOnlyList<DigglesAnimClip> Clips => clips;
        public DigglesAnimClip? CurrentClip => currentClip;
        public string CurrentAnimName => currentClip?.Name ?? "";
        public AnimMode CurrentMode => currentMode;
        public Vector3 CurrentMotionOffset => currentClip?.MotionOffset ?? Vector3.zero;
        public Vector3 CurrentRotationDelta => currentClip?.RotationDelta ?? Vector3.zero;
        public int CurrentFrameCount => currentClip?.Frames?.Length ?? 0;
        public int CurrentFrame => _curFrame;

        // Bumped whenever SetAnim reassigns the renderer materials, so DigglesTextureAnimator
        // knows to re-insert its per-submesh material clone (the swap overwrote it).
        public int MaterialVersion { get; private set; }

        // Bumped every time SetAnim actuallystarts a clip — a deduped same-clip loop
        // re-issue does not bump it. Lets tests verify that the Tcl think-tick churn is not
        // restarting the running loop at frame 0 every ~0.14 s.
        public int RestartCount { get; private set; }

        // Base material name of submesh `submesh` at the current frame — for texture-variant
        // lookup (def_texture / set_textureanimation). From the source.3db: frame → mesh
        // keyframe → submesh → material name. Null if unavailable / out of range.
        public string? GetSubmeshBaseMaterialName(int submesh)
        {
            var c = currentClip;
            if (c?.SourceModel == null || c.SourceMeshIndices == null || c.SourceMeshIndices.Length == 0)
                return null;
            int f = Mathf.Clamp(_curFrame, 0, c.SourceMeshIndices.Length - 1);
            int meshIdx = c.SourceMeshIndices[f];
            var db = c.SourceModel;
            if (meshIdx < 0 || meshIdx >= db.Meshes.Count) return null;
            var subs = db.Meshes[meshIdx].SubMeshes;
            if (submesh < 0 || submesh >= subs.Count) return null;
            int matIdx = subs[submesh].MaterialIndex;
            if (matIdx < 0 || matIdx >= db.Materials.Count) return null;
            return db.Materials[matIdx].Name;
        }

        // Model-local offset of link point `linkId` (hats=4, hands 0/1/7…) for the
        // current frame, interpolated between keyframes f0→f1 by the same blend the mesh
        // morph uses — so an attached hat/item rides the animation smoothly (not just at
        // 10 FPS keyframes). False if this clip has no source link data / empty slot.
        public bool TryGetLinkOffset(int linkId, out Vector3 offset)
        {
            offset = Vector3.zero;
            var c = currentClip;
            if (c?.SourceModel == null || c.SourceMeshIndices == null || c.SourceMeshIndices.Length == 0)
                return false;
            int n = c.SourceMeshIndices.Length;
            if (c.FrameLinkA.Length != n || c.FrameLinkS.Length != n) return false;
            int f0 = Mathf.Clamp(_curFrame, 0, n - 1);
            if (!DigglesLinkPoints.TryGetLocalOffset(c.SourceModel, c.SourceMeshIndices[f0], linkId,
                    c.FrameLinkA[f0], c.FrameLinkS[f0], out var o0))
                return false;
            int f1 = Mathf.Clamp(_curNext, 0, n - 1);
            if (f1 != f0 && _curBlend > 0f
                && DigglesLinkPoints.TryGetLocalOffset(c.SourceModel, c.SourceMeshIndices[f1], linkId,
                    c.FrameLinkA[f1], c.FrameLinkS[f1], out var o1))
                offset = Vector3.Lerp(o0, o1, _curBlend);
            else
                offset = o0;
            return true;
        }

        // The CPU mesh of the current visual frame — the swap path's live sharedMesh, or the
        // pre-baked frame mesh when the VAT morph is on (whose sharedMesh is the static
        // frame-0-topology BaseMesh). The selection-aura band projects its screen silhouette
        // from this (the native hull pass reads the same lerped frame vertices, FUN_00490cc0).
        public Mesh? CurrentFrameMesh()
        {
            var c = currentClip;
            if (c != null && Interpolate && c.Vat != null && c.BaseMesh != null && c.VatFrameCount > 1
                && c.Frames != null && c.Frames.Length > 0)
                return c.Frames[Mathf.Clamp(_curFrame, 0, c.Frames.Length - 1)];
            return meshFilter != null ? meshFilter.sharedMesh : null;
        }

        // The interpolated vertex state the GPU morph actually shows: frame f0's mesh, frame
        // f1's mesh and the same blend t the vertex shader gets as _Blend (ApplyCurrentFrame).
        // f1 = null / t = 0 ⇒ the discrete frame (interpolation off, swap path, or sitting
        // exactly on a keyframe). The selection-aura band lerps its hull input vertices with
        // this t so the silhouette tracks the tween instead of jumping at 10 FPS while the
        // visual mesh glides. Frame meshes share the VAT's vertex order — an index-wise lerp
        // is exactly the shader's morph.
        public (Mesh? f0, Mesh? f1, float t) CurrentFrameMorph()
        {
            var c = currentClip;
            if (c != null && Interpolate && c.Vat != null && c.BaseMesh != null && c.VatFrameCount > 1
                && c.Frames != null && c.Frames.Length > 0)
            {
                var m0 = c.Frames[Mathf.Clamp(_curFrame, 0, c.Frames.Length - 1)];
                var m1 = c.Frames[Mathf.Clamp(_curNext, 0, c.Frames.Length - 1)];
                return ReferenceEquals(m1, m0) || _curBlend <= 0f ? (m0, null, 0f) : (m0, m1, _curBlend);
            }
            return (meshFilter != null ? meshFilter.sharedMesh : null, null, 0f);
        }

        // The engine-law link offset (u16/4 − 8192, raw model axes) of the current frame —
        // the decode the native uses for walk -dummy goals (FUN_00420840:1929-1938); see
        // DigglesLinkPoints.TryGetEngineOffset. No interpolation: the native reads the
        // record raw, and the ring dummies of work targets are static across frames.
        public bool TryGetLinkOffsetEngine(int linkId, out Vector3 offset)
        {
            offset = Vector3.zero;
            var c = currentClip;
            if (c?.SourceModel == null || c.SourceMeshIndices == null || c.SourceMeshIndices.Length == 0)
                return false;
            int f0 = Mathf.Clamp(_curFrame, 0, c.SourceMeshIndices.Length - 1);
            return DigglesLinkPoints.TryGetEngineOffset(c.SourceModel, c.SourceMeshIndices[f0], linkId, out offset);
        }

        // The raw per-frame link Euler (get_linkrot): intrinsic XYZ radians, native model space,
        // current frame, no interpolation — scripts do component arithmetic on it
        // (`set_roty this [lindex [get_linkrot $item $d] 1] + π`, z_work_common.tcl:500).
        public bool TryGetLinkEuler(int linkId, out Vector3 eulerRad)
        {
            eulerRad = Vector3.zero;
            var c = currentClip;
            if (c?.SourceModel == null || c.SourceMeshIndices == null || c.SourceMeshIndices.Length == 0)
                return false;
            int f0 = Mathf.Clamp(_curFrame, 0, c.SourceMeshIndices.Length - 1);
            return DigglesLinkPoints.TryGetLocalEuler(c.SourceModel, c.SourceMeshIndices[f0], linkId, out eulerRad);
        }

        // Per-frame link orientation (model-local), interpolated f0→f1 like the offset.
        // The native Euler is intrinsic XYZ radians (Y = yaw). Returned as a quaternion
        // to compose under the inner Model node (which carries Q_x(-90)).
        public bool TryGetLinkRotation(int linkId, out Quaternion rot)
        {
            rot = Quaternion.identity;
            var c = currentClip;
            if (c?.SourceModel == null || c.SourceMeshIndices == null || c.SourceMeshIndices.Length == 0)
                return false;
            int n = c.SourceMeshIndices.Length;
            int f0 = Mathf.Clamp(_curFrame, 0, n - 1);
            if (!DigglesLinkPoints.TryGetLocalEuler(c.SourceModel, c.SourceMeshIndices[f0], linkId, out var e0))
                return false;
            var q0 = LinkEulerToQuat(e0);
            int f1 = Mathf.Clamp(_curNext, 0, n - 1);
            if (f1 != f0 && _curBlend > 0f
                && DigglesLinkPoints.TryGetLocalEuler(c.SourceModel, c.SourceMeshIndices[f1], linkId, out var e1))
                rot = Quaternion.Slerp(q0, LinkEulerToQuat(e1), _curBlend);
            else
                rot = q0;
            return true;
        }

        // Engine link Euler is intrinsic XYZ radians in a Y-down, right-handed model
        // frame; Unity is left-handed, so rotation directions are mirrored and the angles
        // must be negated. (Positions don't need this — only rotation handedness flips.)
        static Quaternion LinkEulerToQuat(Vector3 eulerRad) =>
            Quaternion.Euler(-eulerRad.x * Mathf.Rad2Deg, -eulerRad.y * Mathf.Rad2Deg, -eulerRad.z * Mathf.Rad2Deg);

        public bool HasClip(string name)
        {
            EnsureIndex();
            return byName != null && byName.ContainsKey(name);
        }

        // Drop all loaded clips (so the next PlayClip rebuilds fresh). Used when the model
        // is re-skinned to a different source object (e.g. mann→frau) — clips are cached by
        // bare name, so the old object's clips would otherwise shadow the new object's.
        public void ClearClips()
        {
            clips = Array.Empty<DigglesAnimClip>();
            byName = new Dictionary<string, DigglesAnimClip>();
            currentClip = null;
            lastEmittedFrame = -1;
        }

        public void AddClip(DigglesAnimClip clip) => AddClip(clip, null);

        // Register a clip at runtime; an existing clip of the same key is replaced, and the
        // renderer's local bounds grow to cover the new frames so added clips don't reintroduce
        // transparent-sort flicker. keyOverride registers a clip under a qualified name
        // (e.g. "kiste.standard") so a cross-object clip doesn't collide with the model's own
        // same-named clip ("standard"). Null ⇒ use the clip's bare Name.
        public void AddClip(DigglesAnimClip clip, string? keyOverride)
        {
            if (clip == null) return;
            string key = string.IsNullOrEmpty(keyOverride) ? clip.Name : keyOverride!;
            if (string.IsNullOrEmpty(key)) return;
            EnsureIndex();
            // Mirror into the `clips` array too — the public Clips list must reflect
            // runtime-registered clips. Library-less rigs (tests/tools) register clips only
            // through here, and DigglesAnimController.ClipMotionMagnitude's fallback
            // enumerates Clips for the root-motion |VecA|; a dictionary-only registration
            // leaves that list empty, so rm reads 0 and every animset falls back to the walk
            // speed.
            byName!.TryGetValue(key, out var replaced);
            byName[key] = clip;
            int at = replaced != null ? Array.IndexOf(clips, replaced) : -1;
            if (at >= 0) clips[at] = clip;
            else
            {
                Array.Resize(ref clips, clips.Length + 1);
                clips[clips.Length - 1] = clip;
            }
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null && clip.Frames != null)
            {
                var b = meshRenderer.localBounds;
                bool any = b.size != Vector3.zero;
                foreach (var m in clip.Frames)
                {
                    if (m == null) continue;
                    if (!any) { b = m.bounds; any = true; }
                    else b.Encapsulate(m.bounds);
                }
                if (any) meshRenderer.localBounds = b;
            }
        }

        void Reset()
        {
            meshFilter = GetComponent<MeshFilter>();
        }

        void OnEnable()
        {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            EnsureIndex();
            if (!string.IsNullOrEmpty(defaultClipName))
            {
                SetAnim(defaultClipName, defaultStartFrame, defaultMode);
            }
            DigglesFrameDriver.Register(this);   // central per-frame tick
        }

        void OnDisable() => DigglesFrameDriver.Unregister(this);

        // Invoked by Unity during the culling pass of the frame this renderer re-enters view,
        // before that frame renders. Re-applies the current pose immediately because the
        // off-screen check in ApplyCurrentFrame skipped MPB pushes while culled.
        void OnBecameVisible()
        {
            // Visibility messages can arrive regardless of the enabled flag — a disabled
            // animator must not push poses (it stopped ticking).
            if (isActiveAndEnabled && currentClip != null) ApplyCurrentFrame();
        }

        // Advance the anim clock by `deltaTime` unscaled seconds (the game-time scale is applied
        // here) and pose the current frame. Update feeds Time.deltaTime; headless tests drive
        // this directly with a fake clock instead.
        public void TickClock(float deltaTime)
        {
            _animClock += deltaTime * s_timeScale;
            if (currentClip == null) return;
            ApplyCurrentFrame();
        }

        public void SetAnim(string animName, int startFrame, AnimMode mode)
        {
            EnsureIndex();
            if (byName == null) return;
            if (!byName.TryGetValue(animName, out var clip)) return;
            // Same-clip loop dedupe: re-issuing the loop that is already looping must not rewind
            // to frame 0. The Tcl work/idle pump re-issues its current anim every think tick
            // (~0.14 s), and an unconditional restart would pin every loop to its first frames.
            // A loop is phase-free, so the re-issue is a no-op. Once keeps restart semantics (an
            // explicit replay of a one-shot gesture must play again; a Once clip that already
            // finished sits in Still mode, so it restarts through here too). startFrame must
            // match the loop's anchor frame — an explicit seek to a different frame is honoured.
            if (mode == AnimMode.Loop && currentMode == AnimMode.Loop
                && ReferenceEquals(clip, currentClip) && startFrame == currentStartFrame)
                return;
            RestartCount++;
            currentClip = clip;
            currentStartFrame = startFrame;
            currentMode = mode;
            currentStartTime = _animClock;
            lastEmittedFrame = -1;
            _lastMorphFrame = -1; _lastMorphNext = -1; _lastMorphBlend = float.NaN;   // force a re-push
            _lastSoundFrame = -1;   // new clip → its first applied frame fires anim SFX
            // Lazy component binding (like meshRenderer below): Reset only runs in Edit
            // Mode and OnEnable never runs on an inactive hierarchy — and save staging
            // spawns the whole candidate world under an inactive root. Without this, a
            // restored entity applies its first frame to a null MeshFilter, the spawner sees
            // an empty mesh and destroys the model.
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (clip.Materials != null && meshRenderer != null)
            {
                meshRenderer.sharedMaterials = clip.Materials;
                MaterialVersion++;   // signals DigglesTextureAnimator to re-assert its clones
            }

            // Morph vs hard-swap setup. Morph: render the static BaseMesh and
            // drive the VAT in the shader; otherwise swap per-frame meshes.
            bool morph = Interpolate && clip.Vat != null && clip.BaseMesh != null && clip.VatFrameCount > 1;
            if (mpb == null) mpb = new MaterialPropertyBlock();
            if (morph)
            {
                if (meshFilter != null) meshFilter.sharedMesh = clip.BaseMesh;
                mpb.SetFloat(IdUseVat, 1f);
                mpb.SetTexture(IdVat, clip.Vat!);
            }
            else
            {
                mpb.SetFloat(IdUseVat, 0f);
            }
            if (meshRenderer != null) meshRenderer.SetPropertyBlock(mpb);
            ApplyCurrentFrame();
        }

        void EnsureIndex()
        {
            if (byName != null) return;
            byName = new Dictionary<string, DigglesAnimClip>(clips.Length);
            foreach (var c in clips)
            {
                if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                if (byName.ContainsKey(c.Name)) continue;
                byName[c.Name] = c;
            }
        }

        void ApplyCurrentFrame()
        {
            var clip = currentClip;
            if (clip == null) return;
            var frames = clip.Frames;
            int n = frames == null ? 0 : frames!.Length;
            if (n == 0) return;

            // Fractional frame position (no rounding) so we can interpolate.
            float ff;
            if (currentMode == AnimMode.Still)
            {
                ff = Mathf.Clamp(currentStartFrame, 0, n - 1);
            }
            else
            {
                // Elapsed on the per-instance game clock — the same domain SetAnim stamps
                // currentStartTime from. Reading Time.timeAsDouble here against an _animClock
                // stamp would make elapsed ≈ the whole session, finishing every Once clip at
                // issue time.
                ff = currentStartFrame +
                     (float)((_animClock - currentStartTime) * Fps * PlaybackSpeed);
                if (currentMode == AnimMode.Once && ff >= n - 1)
                {
                    ff = n - 1;
                    currentMode = AnimMode.Still;     // freeze on last (engine semantics)
                    currentStartFrame = n - 1;
                }
            }

            int frame, next;
            float blend;
            if (currentMode == AnimMode.Loop)
            {
                float wrapped = ff % n; if (wrapped < 0f) wrapped += n;
                frame = (int)wrapped;
                blend = wrapped - frame;
                next = (frame + 1) % n;             // wrap last→first
            }
            else
            {
                frame = Mathf.Clamp(Mathf.FloorToInt(ff), 0, n - 1);
                blend = Mathf.Clamp01(ff - frame);
                next = Mathf.Min(frame + 1, n - 1);
                if (next == frame) blend = 0f;
            }

            _curFrame = frame; _curNext = next; _curBlend = blend;   // for link-point riding

            // Anim-frame SFX hook — before the morph early-return so it fires on both the VAT
            // and the mesh-swap paths (native per-object check FUN_005cb9a0:132 `lastFrame !=
            // currentFrame`).
            if (frame != _lastSoundFrame)
            {
                int prevFrame = _lastSoundFrame;
                _lastSoundFrame = frame;
                AnimFrameAdvanced?.Invoke(this, clip, prevFrame, frame);
            }

            bool morph = Interpolate && clip.Vat != null && clip.BaseMesh != null && clip.VatFrameCount > 1;
            if (morph)
            {
                if (meshFilter != null && meshFilter.sharedMesh != clip.BaseMesh)
                    meshFilter.sharedMesh = clip.BaseMesh;
                // Off-screen skip: a culled renderer (isVisible false covers
                // every camera and shadow rendering) can't affect any pixel, so skip the
                // per-frame GetPropertyBlock/SetPropertyBlock marshal. The frame bookkeeping
                // above (curFrame/blend, anim SFX) already ran, so link points and sounds are
                // unaffected, and OnBecameVisible re-poses during the culling pass of the first
                // visible frame. EditMode is exempt: isVisible is meaningless without a player
                // loop.
                if (Application.isPlaying && meshRenderer != null && !meshRenderer.isVisible)
                {
                    _lastMorphFrame = -1; _lastMorphNext = -1; _lastMorphBlend = float.NaN;
                    return;
                }
                // Change-guard: only re-push the VAT property block when the frame pair or blend
                // actually changed. A Still clip (bau_* buildings, frozen props, a settled Once)
                // or a clock-frozen loop holds constant f0/f1/blend, so without this it would
                // re-upload an identical MaterialPropertyBlock every render frame.
                if (frame == _lastMorphFrame && next == _lastMorphNext && blend == _lastMorphBlend)
                    return;
                _lastMorphFrame = frame; _lastMorphNext = next; _lastMorphBlend = blend;
                if (mpb == null) mpb = new MaterialPropertyBlock();
                if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
                if (meshRenderer != null) meshRenderer.GetPropertyBlock(mpb);
                mpb.SetFloat(IdUseVat, 1f);
                mpb.SetTexture(IdVat, clip.Vat!);
                mpb.SetFloat(IdF0, (frame + 0.5f) / clip.VatFrameCount);
                mpb.SetFloat(IdF1, (next + 0.5f) / clip.VatFrameCount);
                mpb.SetFloat(IdBlend, blend);
                if (meshRenderer != null) meshRenderer.SetPropertyBlock(mpb);
                return;
            }

            if (frame == lastEmittedFrame) return;
            var mesh = frames![frame];
            if (mesh != null && meshFilter != null) meshFilter.sharedMesh = mesh;
            lastEmittedFrame = frame;
        }

        // Editor-time setup (called by MarkerModelResolver during asset import).
        public void Initialize(
            MeshFilter filter,
            DigglesAnimClip[] animClips,
            string startClipName,
            AnimMode startMode,
            int startFrame)
        {
            meshFilter = filter;
            clips = animClips ?? Array.Empty<DigglesAnimClip>();
            defaultClipName = startClipName ?? "";
            defaultMode = startMode;
            defaultStartFrame = startFrame;
            byName = null;

            // Compute the union of bounds across every frame and pin the
            // MeshRenderer.localBounds to it. Without this, every mesh swap
            // would change MeshRenderer.bounds, which makes Unity re-sort
            // transparent renderers each frame — causing per-frame flicker
            // between Edit and Play mode (Edit mode is fine because Update
            // doesn't run, so bounds stay constant).
            var mr = GetComponent<MeshRenderer>();
            if (mr == null) return;
            bool first = true;
            Bounds union = default;
            foreach (var c in clips)
            {
                if (c?.Frames == null) continue;
                foreach (var m in c.Frames!)
                {
                    if (m == null) continue;
                    if (first) { union = m.bounds; first = false; }
                    else union.Encapsulate(m.bounds);
                }
            }
            if (!first) mr.localBounds = union;
        }
    }
}
