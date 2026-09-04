#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    // Headless-testable particle/FX model driven by the engine's particle commands
    // (change/set/blow/free/create_particlesource, obj_to_particle, particle_processing).
    // Records the emitters + their parameters (type, offset, direction, budget, rate, link/
    // submesh, active) and ages temporary world-space sources. The per-`type` VISUAL preset
    // (texture/blend/gravity/lifetime/colour-ramp, baked in the original Particle.cpp) is mapped
    // by a Play-mode component into Unity ParticleSystems; here we just carry the int type +
    // the script-supplied params. See Docs/CUTSCENE_SYSTEM.md §6.
    public sealed class ParticleSystemState
    {
        public const int NativeWorldSourceObjectId = int.MinValue;

        public sealed class SaveState
        {
            public bool ProcessingEnabled;
            public SourceEntry[] Sources = Array.Empty<SourceEntry>();
            public TempSource[] Temps = Array.Empty<TempSource>();
        }

        public sealed class SourceEntry
        {
            public int ObjectId;
            public int SourceIndex;
            public Source State = new();
        }

        public sealed class Source
        {
            public int Type;
            public Vector3 RelPos;       // emitter offset (object-local) or world pos (temp)
            public Vector3 Direction;    // initial particle velocity/spread axis
            public int MaxParticles;
            public int NumPerFrame;
            public bool VisibleInFog;
            public int LinkId = -1;      // attach emitter to a model link-point (-1 = none)
            public int SubMesh = -1;
            public bool Active;
            // blow_particlesource wind accumulator (native emitter+0x30..0x38): blows ADD here
            // (FUN_00675d10:147-149) and the presenter folds the sum into the emitter's decaying
            // drift once, then clears it (the FUN_00498590:303-308 fold-and-zero law). Blow NEVER
            // touches Direction — an earlier revision overwrote it, which skewed the fire sideways
            // forever after prod_blowright.
            public Vector3 BlowImpulse;
            public float Flag;           // class_particle trailing token → lifetime/size nudge
            public bool WorldAnchored;   // RelPos is an ABSOLUTE native position (owner not in
                                         // ctx.Entities — e.g. the elf's hand sparkles, which the
                                         // owner updates per frame); presenter skips the entity add
            public float MaxRangeSquared;
            public bool HasNativeState;
            public int NativeParticleHandle = -1;
            public int NativeMode;
            public int NativeRawObjectToken;
            public int NativeRawValue74;
            public int NativeRawValue80;
        }

        public sealed class TempSource
        {
            public int Type;
            public Vector3 Pos, Direction;
            public int Num;
            public float Remaining;
            public bool Spawned;         // one-shot world burst already emitted by the presenter
            // Optional SHATTER source (the deco/creature death shatter): the
            // dying model's CURRENT-frame vertex positions in UNITY world space + per-vertex
            // tints, PRECOMPUTED by the retire seam (DigglesTclEngine.ShatterEntity) so the
            // object can be freed the same tick — the native two-stage destroy (FUN_005eae80:
            // shatter now via FUN_004845a0/FUN_004830a0, deferred free via the obj+0x404 list).
            // The presenter hands these to CpuParticleEmitter.SetShatterSource (set 43).
            public Vector3[]? ShatterVerts;
            public Color32[]? ShatterCols;
            public int NativeParticleHandle = -1;
        }

        public bool ProcessingEnabled = true;

        // Persistent per-object sources, keyed by (object handle, source index).
        public readonly Dictionary<(int obj, int idx), Source> Sources = new();
        // Temporary self-expiring world-space sources (create_particlesource).
        public readonly List<TempSource> Temps = new();

        public SaveState Capture()
        {
            var sources = new SourceEntry[Sources.Count];
            int sourceIndex = 0;
            foreach (KeyValuePair<(int obj, int idx), Source> pair in Sources)
            {
                sources[sourceIndex++] = new SourceEntry
                {
                    ObjectId = pair.Key.obj,
                    SourceIndex = pair.Key.idx,
                    State = CloneSource(pair.Value),
                };
            }
            Array.Sort(sources, CompareSourceEntries);

            var temps = new TempSource[Temps.Count];
            for (int index = 0; index < temps.Length; index++)
                temps[index] = CloneTemp(Temps[index]);
            return new SaveState
            {
                ProcessingEnabled = ProcessingEnabled,
                Sources = sources,
                Temps = temps,
            };
        }

        public void Restore(SaveState state)
        {
            Validate(state);
            ProcessingEnabled = state.ProcessingEnabled;
            Sources.Clear();
            for (int index = 0; index < state.Sources.Length; index++)
            {
                SourceEntry entry = state.Sources[index];
                Sources.Add((entry.ObjectId, entry.SourceIndex), CloneSource(entry.State));
            }
            Temps.Clear();
            for (int index = 0; index < state.Temps.Length; index++)
                Temps.Add(CloneTemp(state.Temps[index]));
        }

        public static void Validate(SaveState? state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Sources == null || state.Temps == null)
                throw new ArgumentException("Particle save-state collections are missing.", nameof(state));
            var keys = new HashSet<(int, int)>();
            for (int index = 0; index < state.Sources.Length; index++)
            {
                SourceEntry? entry = state.Sources[index];
                if (entry == null || entry.SourceIndex < 0 || entry.State == null ||
                    !keys.Add((entry.ObjectId, entry.SourceIndex)))
                {
                    throw new ArgumentException("A particle source key is invalid or duplicated.", nameof(state));
                }
                ValidateSource(entry.State, nameof(state));
            }
            for (int index = 0; index < state.Temps.Length; index++)
            {
                TempSource? temp = state.Temps[index];
                if (temp == null || temp.Num < 0 || !Finite(temp.Pos) ||
                    !Finite(temp.Direction) || !Finite(temp.Remaining) || temp.Remaining < 0f ||
                    (temp.ShatterVerts == null) != (temp.ShatterCols == null))
                {
                    throw new ArgumentException("A temporary particle source is invalid.", nameof(state));
                }
                if (temp.ShatterVerts != null && temp.ShatterCols != null &&
                    temp.ShatterVerts.Length != temp.ShatterCols.Length)
                {
                    throw new ArgumentException("Particle shatter vertices and colours do not match.", nameof(state));
                }
                if (temp.ShatterVerts == null) continue;
                for (int vertex = 0; vertex < temp.ShatterVerts.Length; vertex++)
                {
                    if (!Finite(temp.ShatterVerts[vertex]))
                        throw new ArgumentException("A particle shatter vertex is invalid.", nameof(state));
                }
            }
        }

        static int CompareSourceEntries(SourceEntry left, SourceEntry right)
        {
            int objectOrder = left.ObjectId.CompareTo(right.ObjectId);
            return objectOrder != 0 ? objectOrder : left.SourceIndex.CompareTo(right.SourceIndex);
        }

        static Source CloneSource(Source source) => new()
        {
            Type = source.Type,
            RelPos = source.RelPos,
            Direction = source.Direction,
            MaxParticles = source.MaxParticles,
            NumPerFrame = source.NumPerFrame,
            VisibleInFog = source.VisibleInFog,
            LinkId = source.LinkId,
            SubMesh = source.SubMesh,
            Active = source.Active,
            BlowImpulse = source.BlowImpulse,
            Flag = source.Flag,
            WorldAnchored = source.WorldAnchored,
            MaxRangeSquared = source.MaxRangeSquared,
            HasNativeState = source.HasNativeState,
            NativeParticleHandle = source.NativeParticleHandle,
            NativeMode = source.NativeMode,
            NativeRawObjectToken = source.NativeRawObjectToken,
            NativeRawValue74 = source.NativeRawValue74,
            NativeRawValue80 = source.NativeRawValue80,
        };

        static TempSource CloneTemp(TempSource source) => new()
        {
            Type = source.Type,
            Pos = source.Pos,
            Direction = source.Direction,
            Num = source.Num,
            Remaining = source.Remaining,
            Spawned = source.Spawned,
            ShatterVerts = source.ShatterVerts == null ? null : (Vector3[])source.ShatterVerts.Clone(),
            ShatterCols = source.ShatterCols == null ? null : (Color32[])source.ShatterCols.Clone(),
            NativeParticleHandle = source.NativeParticleHandle,
        };

        static void ValidateSource(Source source, string parameterName)
        {
            if (source.MaxParticles < 0 || source.NumPerFrame < 0 || source.LinkId < -1 ||
                source.SubMesh < -1 || !Finite(source.RelPos) || !Finite(source.Direction) ||
                !Finite(source.BlowImpulse) || !Finite(source.Flag) ||
                !Finite(source.MaxRangeSquared) || source.MaxRangeSquared < 0f)
            {
                throw new ArgumentException("A persistent particle source is invalid.", parameterName);
            }
        }

        static bool Finite(Vector3 value) =>
            Finite(value.x) && Finite(value.y) && Finite(value.z);

        static bool Finite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        public Source Configure(int obj, int idx, int type, Vector3 relPos, Vector3 dir,
            int max, int rate, bool visFog, int link, int submesh)
        {
            var key = (obj, idx);
            if (!Sources.TryGetValue(key, out var s)) { s = new Source(); Sources[key] = s; }
            s.Type = type; s.RelPos = relPos; s.Direction = dir; s.MaxParticles = max;
            s.NumPerFrame = rate; s.VisibleInFog = visFog; s.LinkId = link; s.SubMesh = submesh;
            return s;
        }

        public void SetActive(int obj, int idx, bool active)
        {
            if (Sources.TryGetValue((obj, idx), out var s)) s.Active = active;
        }

        // blow_particlesource <obj> <idx> <dir> — a one-shot GUST, not a re-aim: the native
        // handler only ACCUMULATES the vector into the emitter's wind slots (FUN_00675d10:
        // 147-149, `+=` on emitter+0x30/0x34/0x38); no burst, no direction change. The Tcl blow
        // procs (prod_blowright etc., z_work_prod.tcl:131-152) never restore anything — the
        // drift decay IS the restore.
        public void Blow(int obj, int idx, Vector3 dir)
        {
            if (Sources.TryGetValue((obj, idx), out var s)) s.BlowImpulse += dir;
        }

        public void Free(int obj, int idx) => Sources.Remove((obj, idx));

        // Deleting an object frees its bound emitters, like the native teardown — real saves
        // never carry particle records whose owner is gone, and the save validator (manual test
        // chopped mushroom, F2 → "particle slot references undeclared object")
        // rightly rejects such an archive.
        public void OnEntityRemoved(int handle)
        {
            List<(int obj, int idx)>? stale = null;
            foreach ((int obj, int idx) key in Sources.Keys)
            {
                if (key.obj != handle) continue;
                stale ??= new List<(int, int)>();
                stale.Add(key);
            }
            if (stale == null) return;
            foreach ((int, int) key in stale) Sources.Remove(key);
        }

        // The SHATTER one-shot burst (deco removal now; creature death rides the same seam —
        // see DigglesTclEngine.ShatterEntity). `verts` = the dying model's current-frame
        // vertices in UNITY world space, `cols` = optional per-vertex tints; both PRECOMPUTED
        // by the caller (no transform/mesh lifetime coupling — the object may die same-tick).
        public TempSource CreateTempShatter(int type, Vector3 nativePos, Vector3[] verts,
            Color32[]? cols, int num, float time)
        {
            var t = new TempSource
            {
                Type = type, Pos = nativePos, Direction = Vector3.zero, Num = num,
                Remaining = Mathf.Max(0.01f, time), ShatterVerts = verts, ShatterCols = cols,
            };
            Temps.Add(t);
            return t;
        }

        public TempSource CreateTemp(int type, Vector3 pos, Vector3 dir, int num, float time)
        {
            var t = new TempSource { Type = type, Pos = pos, Direction = dir, Num = num, Remaining = Mathf.Max(0.01f, time) };
            Temps.Add(t);
            return t;
        }

        public void Tick(double dt)
        {
            List<(int obj, int idx)>? removedNativeSources = null;
            foreach (KeyValuePair<(int obj, int idx), Source> pair in Sources)
            {
                Source source = pair.Value;
                if (!source.HasNativeState) continue;
                if (source.NativeMode == 5)
                {
                    source.NativeMode = 0;
                    source.Active = false;
                }
                else if (source.NativeMode == 6)
                {
                    removedNativeSources ??= new List<(int obj, int idx)>();
                    removedNativeSources.Add(pair.Key);
                }
            }
            if (removedNativeSources != null)
            {
                foreach (var key in removedNativeSources)
                    Sources.Remove(key);
            }

            for (int i = Temps.Count - 1; i >= 0; i--)
            {
                Temps[i].Remaining -= (float)dt;
                if (Temps[i].Remaining <= 0f) Temps.RemoveAt(i);
            }
        }
    }
}
