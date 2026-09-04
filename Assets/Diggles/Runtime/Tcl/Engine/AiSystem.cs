#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Diggles.Runtime.Tcl;
    using UnityEngine;

    // Native CAIManager bridge. The shipped std_ai.tcl remains the policy layer; this class owns
    // per-player Tcl globals, the staggered five-second scheduler, and the engine-side world facts
    // consumed by the original policy.
    public sealed class AiSystem
    {
        const int OwnerCount = 8;
        const int TickPeriod = 50;
        const double LogicTickSeconds = 0.1;
        public const string NativeStandardScriptPath = "data/scripts/ai/std_ai.tcl";
        const string TheftPolicyProcedure = "check_for_thieves";
        const float PopulationJoinDistance = 20f; // DAT_00737578 in FUN_0042a650.

        sealed class Population
        {
            public Vector3 Centre;
            public readonly List<int> Handles = new();
        }

        sealed class Slot
        {
            public TclScope Scope = new();
            public string TickHandler = "";
            public bool Initialized;
            public bool Enabled;
            public double NextTick;
            public readonly List<Population> GnomePopulations = new();
            public readonly List<Population> ProductionPopulations = new();
            public readonly List<int> IntruderGnomes = new();
            public readonly List<int> IntruderProductions = new();
        }

        [Serializable]
        public sealed class OwnerSaveState
        {
            public string[] VariableNames = Array.Empty<string>();
            public string[] VariableValues = Array.Empty<string>();
            public string TickHandler = "";
            public bool Initialized;
            public bool Enabled;
            public double NextTick;
        }

        [Serializable]
        public sealed class SaveState
        {
            public OwnerSaveState[] Owners = Array.Empty<OwnerSaveState>();
            public DiplomacyNotice[] Notices = Array.Empty<DiplomacyNotice>();
        }

        [Serializable]
        public sealed class DiplomacyNotice
        {
            public int ClanOwner;
            public int ThiefOwner;
            public int ItemHandle;
            public string Position = "";
            public string MessageKey = "";
        }

        readonly DigglesTclContext _ctx;
        readonly Slot[] _slots = new Slot[OwnerCount];
        readonly List<DiplomacyNotice> _notices = new();
        int _currentOwner = -1;

        public IReadOnlyList<DiplomacyNotice> Notices => _notices;
        public int CurrentOwner => _currentOwner;

        public SaveState CaptureSaveState()
        {
            var owners = new OwnerSaveState[OwnerCount];
            for (int owner = 0; owner < OwnerCount; owner++)
            {
                var slot = _slots[owner];
                var names = new string[slot.Scope.Vars.Count];
                var values = new string[names.Length];
                int index = 0;
                foreach (var pair in slot.Scope.Vars)
                {
                    names[index] = pair.Key;
                    values[index] = pair.Value;
                    index++;
                }
                owners[owner] = new OwnerSaveState
                {
                    VariableNames = names,
                    VariableValues = values,
                    TickHandler = slot.TickHandler,
                    Initialized = slot.Initialized,
                    Enabled = slot.Enabled,
                    NextTick = slot.NextTick,
                };
            }
            var notices = new DiplomacyNotice[_notices.Count];
            for (int i = 0; i < notices.Length; i++)
            {
                var source = _notices[i];
                notices[i] = new DiplomacyNotice
                {
                    ClanOwner = source.ClanOwner,
                    ThiefOwner = source.ThiefOwner,
                    ItemHandle = source.ItemHandle,
                    Position = source.Position,
                    MessageKey = source.MessageKey,
                };
            }
            return new SaveState { Owners = owners, Notices = notices };
        }

        public void RestoreSaveState(SaveState state)
        {
            ValidateSaveState(state);
            for (int owner = 0; owner < OwnerCount; owner++)
            {
                var source = state.Owners[owner];
                var slot = _slots[owner] = CreateSlot(source.Initialized);
                slot.TickHandler = source.TickHandler ?? "";
                slot.Enabled = source.Enabled;
                slot.NextTick = source.NextTick;
                for (int i = 0; i < source.VariableNames.Length; i++)
                    slot.Scope.Vars[source.VariableNames[i]] = source.VariableValues[i] ?? "";
            }
            _notices.Clear();
            if (state.Notices != null)
            {
                foreach (var notice in state.Notices)
                {
                    _notices.Add(new DiplomacyNotice
                    {
                        ClanOwner = notice.ClanOwner,
                        ThiefOwner = notice.ThiefOwner,
                        ItemHandle = notice.ItemHandle,
                        Position = notice.Position,
                        MessageKey = notice.MessageKey,
                    });
                }
            }
            _currentOwner = -1;
        }

        public static void ValidateSaveState(SaveState? state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Owners == null || state.Owners.Length != OwnerCount)
                throw new ArgumentException("Saved AI owner count is invalid.", nameof(state));
            for (int owner = 0; owner < state.Owners.Length; owner++)
            {
                OwnerSaveState source = state.Owners[owner] ??
                    throw new ArgumentException("A saved AI owner is null.", nameof(state));
                if (source.VariableNames == null || source.VariableValues == null ||
                    source.VariableNames.Length != source.VariableValues.Length)
                {
                    throw new ArgumentException("Saved AI variable table is invalid.", nameof(state));
                }
                if (source.TickHandler == null || double.IsNaN(source.NextTick) ||
                    double.IsInfinity(source.NextTick))
                {
                    throw new ArgumentException("Saved AI scheduling state is invalid.", nameof(state));
                }
                if (!source.Initialized && (source.Enabled || source.VariableNames.Length != 0))
                    throw new ArgumentException(
                        "An uninitialized AI owner contains interpreter state.", nameof(state));
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < source.VariableNames.Length; index++)
                {
                    if (source.VariableNames[index] == null || source.VariableValues[index] == null ||
                        !names.Add(source.VariableNames[index]))
                    {
                        throw new ArgumentException("Saved AI variable table is invalid.", nameof(state));
                    }
                }
            }
            if (state.Notices == null)
                throw new ArgumentException("Saved AI notices are missing.", nameof(state));
            foreach (DiplomacyNotice? notice in state.Notices)
            {
                if (notice == null || notice.Position == null || notice.MessageKey == null)
                    throw new ArgumentException("A saved AI diplomacy notice is invalid.", nameof(state));
            }
        }

        public void RestoreNativeOwnerState(
            int owner,
            bool interpreterPresent,
            bool enabled,
            string[] variableNames,
            string[] variableValues,
            double gameTime)
        {
            if ((uint)owner >= OwnerCount)
                throw new ArgumentOutOfRangeException(nameof(owner));
            if (variableNames == null) throw new ArgumentNullException(nameof(variableNames));
            if (variableValues == null) throw new ArgumentNullException(nameof(variableValues));
            if (variableNames.Length != variableValues.Length)
                throw new ArgumentException("Native AI variable names and values do not match.");
            if (double.IsNaN(gameTime) || double.IsInfinity(gameTime))
                throw new ArgumentOutOfRangeException(nameof(gameTime));
            if (!interpreterPresent)
            {
                if (enabled || variableNames.Length != 0)
                    throw new ArgumentException("Native AI state exists without an interpreter.");
                _slots[owner] = new Slot();
                return;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < variableNames.Length; index++)
            {
                string name = variableNames[index] ??
                    throw new ArgumentException("Native AI variable name is null.", nameof(variableNames));
                if (!names.Add(name))
                    throw new ArgumentException(
                        $"Native AI variable '{name}' is duplicated.", nameof(variableNames));
                if (variableValues[index] == null)
                    throw new ArgumentException(
                        $"Native AI variable '{name}' has a null value.", nameof(variableValues));
            }

            // The save-provided path is never accepted here. Transactional preflight recognizes
            // only the shipped policy and this method always sources that hard-coded trusted file.
            // The candidate session is still inactive, so policy initialization cannot touch the
            // active world. Saved globals are overlaid only after the policy has recreated procs
            // and the native tick handler, matching FUN_00429d00.
            var slot = _slots[owner] = CreateSlot(initialized: true);
            slot.NextTick = FirstTickTime(owner, gameTime);
            try
            {
                Run(owner, TclList.Format(new[] { "call", NativeStandardScriptPath }));
                for (int index = 0; index < variableNames.Length; index++)
                    slot.Scope.Vars[variableNames[index]] = variableValues[index];
                slot.Enabled = enabled;
                slot.NextTick = FirstTickTime(owner, gameTime);
            }
            catch
            {
                _slots[owner] = new Slot();
                throw;
            }
        }

        AiSystem(DigglesTclContext ctx)
        {
            _ctx = ctx;
            for (int owner = 0; owner < OwnerCount; owner++)
                _slots[owner] = new Slot();
        }

        public static void RegisterCommands(DigglesTclContext ctx, TclInterpreter interp)
        {
            var ai = new AiSystem(ctx);
            ctx.Ai = ai;

            interp.RegisterCommand("ai", (_, args) => ai.Command(args));
            interp.RegisterCommand("ai_getownerid", (_, _) =>
                ai._currentOwner.ToString(CultureInfo.InvariantCulture));
            interp.RegisterCommand("ai_tickhandler", (_, args) =>
            {
                if (args.Length != 2)
                    throw new TclException(
                        "wrong # args: should be \"ai_tickhandler script\"");
                ai.RequireCurrentSlot().TickHandler = args[1];
                return "";
            });
            // The release executable routes both commands to its quiet print callback. Keeping
            // them silent avoids turning the very chatty std_ai diagnostic stream into Unity spam.
            interp.RegisterCommand("ai_log", (_, _) => "");
            interp.RegisterCommand("ai_nprint", (_, _) => "");
            interp.RegisterCommand("ai_npring", (_, _) => "");

            interp.RegisterCommand("ai_gnomepopscount", (_, _) =>
                ai.PopulationCount(false));
            interp.RegisterCommand("ai_gnomepop_getcount", (_, args) =>
                ai.PopulationMemberCount(false, args));
            interp.RegisterCommand("ai_gnomepop_getpos", (_, args) =>
                ai.PopulationPosition(false, args));
            interp.RegisterCommand("ai_gnomepop_getlist", (_, args) =>
                ai.PopulationMembers(false, args));
            interp.RegisterCommand("ai_gnomepop_getobj", (_, args) =>
                ai.PopulationMember(false, args));
            interp.RegisterCommand("ai_nextgnomepop", (_, args) =>
                ai.NearestPopulation(false, args));

            interp.RegisterCommand("ai_prodpopscount", (_, _) =>
                ai.PopulationCount(true));
            interp.RegisterCommand("ai_prodpop_getcount", (_, args) =>
                ai.PopulationMemberCount(true, args));
            interp.RegisterCommand("ai_prodpop_getpos", (_, args) =>
                ai.PopulationPosition(true, args));
            interp.RegisterCommand("ai_prodpop_getlist", (_, args) =>
                ai.PopulationMembers(true, args));
            interp.RegisterCommand("ai_prodpop_getobj", (_, args) =>
                ai.PopulationMember(true, args));
            interp.RegisterCommand("ai_nextprodpop", (_, args) =>
                ai.NearestPopulation(true, args));

            interp.RegisterCommand("ai_getintrudergnomes", (_, _) =>
                ai.Intruders(false));
            interp.RegisterCommand("ai_getintruderprods", (_, _) =>
                ai.Intruders(true));

            interp.RegisterCommand("ai_getpossibleinventions", (_, args) =>
                ai.PossibleInventions(args));
            interp.RegisterCommand("ai_setbuildup", (_, args) =>
                ai.SetBuildup(args));
            interp.RegisterCommand("ai_setpack", (_, args) =>
                ai.SetPack(args));
            interp.RegisterCommand("ai_validlocation", (_, args) =>
                ai.ValidLocation(args));
            interp.RegisterCommand("ai_getprodlocations", (_, args) =>
                ai.ProductionLocations(args));

            interp.RegisterCommand("prod_get_task_all_places", (_, args) =>
                ai.ProductionTaskPlaces(args, false));
            interp.RegisterCommand("prod_get_task_active_places", (_, args) =>
                ai.ProductionTaskPlaces(args, true));
            interp.RegisterCommand("prod_get_task_total_cnt", (_, args) =>
                ai.ProductionTaskTotal(args));
        }

        public void Tick(double gameTime)
        {
            for (int owner = 0; owner < OwnerCount; owner++)
            {
                var slot = _slots[owner];
                if (!slot.Initialized || !slot.Enabled || gameTime + 1e-9 < slot.NextTick)
                    continue;

                do slot.NextTick += TickPeriod * LogicTickSeconds;
                while (slot.NextTick <= gameTime + 1e-9);

                // The whole owner turn runs under the engine-standard tolerant guard. The
                // native CAIManager evaluates its policy in a background per-owner Tcl interp
                // whose errors are logged and dropped, and check_for_thieves is part of that
                // same evaluation (std_ai.tcl:318 calls it from the tick body) — so an error
                // inside the theft policy aborts the rest of that owner's turn there too, but
                // never unwinds the game loop. Before this guard covered ProcessPendingTheft,
                // one bad script line (a missing lmsg key, a custom AI file without the proc)
                // threw out of AiSystem.Tick into DigglesGameSim.Step every owner tick.
                try
                {
                    RefreshWorldFacts(owner, slot);
                    ProcessPendingTheft(owner, slot);
                    if (slot.TickHandler.Length > 0) Run(owner, slot.TickHandler);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AiSystem] owner {owner} tick failed: {e.Message}");
                }
            }
        }

        public string Execute(int owner, string script)
        {
            var slot = RequireSlot(owner);
            if (!slot.Initialized)
                throw new TclException($"AI owner {owner} is not initialized");
            return Run(owner, script);
        }

        string Command(string[] args)
        {
            if (args.Length < 3)
                throw new TclException(
                    "wrong # args: should be \"ai init|enable|disable|exec owner ?argument?\"");
            int owner = ParseOwner(args[2]);
            switch (args[1])
            {
                case "init":
                    if (args.Length != 4)
                        throw new TclException("wrong # args: should be \"ai init owner file\"");
                    Initialize(owner, args[3]);
                    return "";
                case "enable":
                    if (args.Length != 3)
                        throw new TclException("wrong # args: should be \"ai enable owner\"");
                    Enable(owner, true);
                    return "";
                case "disable":
                    if (args.Length != 3)
                        throw new TclException("wrong # args: should be \"ai disable owner\"");
                    Enable(owner, false);
                    return "";
                case "exec":
                    if (args.Length != 4)
                        throw new TclException("wrong # args: should be \"ai exec owner script\"");
                    return Execute(owner, args[3]);
                default:
                    throw new TclException($"unknown ai operation \"{args[1]}\"");
            }
        }

        void Initialize(int owner, string path)
        {
            var slot = _slots[owner] = CreateSlot(initialized: true);
            slot.NextTick = FirstTickTime(owner, _ctx.Sim?.GameTime ?? 0.0);
            try
            {
                Run(owner, TclList.Format(new[] { "call", path }));
            }
            catch
            {
                _slots[owner] = new Slot();
                throw;
            }
        }

        void Enable(int owner, bool enabled)
        {
            var slot = RequireSlot(owner);
            if (enabled && !slot.Initialized)
                throw new TclException($"AI owner {owner} is not initialized");
            slot.Enabled = enabled;
            if (enabled)
                slot.NextTick = FirstTickTime(owner, _ctx.Sim?.GameTime ?? 0.0);
        }

        static Slot CreateSlot(bool initialized)
        {
            var slot = new Slot { Initialized = initialized };
            if (!initialized) return slot;
            slot.Scope.Vars.Add("tcl_version", "8.3");
            slot.Scope.Vars.Add("env", "");
            slot.Scope.Vars.Add("tclDefaultLibrary", "");
            slot.Scope.Vars.Add("tcl_patchLevel", "8.3.2");
            slot.Scope.Vars.Add("tcl_platform", "");
            return slot;
        }

        string Run(int owner, string script)
        {
            var slot = RequireSlot(owner);
            int previousOwner = _currentOwner;
            var previousThis = _ctx.This;
            var previousClass = _ctx.CurrentClass;
            var previousEvent = _ctx.CurrentEvent;
            _currentOwner = owner;
            _ctx.This = null;
            _ctx.CurrentClass = null;
            _ctx.CurrentEvent = null;
            try
            {
                return _ctx.Interp.EvalInScope(script, slot.Scope);
            }
            finally
            {
                _ctx.CurrentEvent = previousEvent;
                _ctx.CurrentClass = previousClass;
                _ctx.This = previousThis;
                _currentOwner = previousOwner;
            }
        }

        void ProcessPendingTheft(int clanOwner, Slot slot)
        {
            if (!TryInt(slot.Scope, "iNeutralGnomeStoleItem", out int thiefOwner) ||
                thiefOwner < 0 || thiefOwner >= OwnerCount)
                return;

            TryInt(slot.Scope, "rStolenItem", out int itemHandle);
            bool boxed = itemHandle > 0 && _ctx.Entities.TryGetValue(itemHandle, out var item) &&
                IsTrue(item.VarsScope.Vars.TryGetValue("_boxed", out string value) ? value : "0");
            bool ignore = slot.Scope.Vars.TryGetValue("bIgnoreThieves", out string ignoreValue) &&
                IsTrue(ignoreValue);
            int[] counts = ReadTheftCounts(slot.Scope);
            int count = counts[thiefOwner];
            slot.Scope.Vars.TryGetValue("vStolenPos", out string position);

            string messageKey = count > 1 || (boxed && count > 0)
                ? "Clantoenemy"
                : count > 0 ? "Clanwarning1" : "Clanstealwarning";
            Run(clanOwner, TheftPolicyProcedure);
            if (ignore && !boxed) return;

            _notices.Add(new DiplomacyNotice
            {
                ClanOwner = clanOwner,
                ThiefOwner = thiefOwner,
                ItemHandle = itemHandle,
                Position = position ?? "",
                MessageKey = messageKey,
            });
        }

        string PopulationCount(bool production)
        {
            var slot = RequireCurrentSlot();
            RefreshWorldFacts(_currentOwner, slot);
            return Populations(slot, production).Count.ToString(CultureInfo.InvariantCulture);
        }

        string PopulationMemberCount(bool production, string[] args)
        {
            RequireArgs(args, 2, args[0] + " population");
            var pop = GetPopulation(RequireCurrentSlot(), production, ParseIndex(args[1]));
            return pop.Handles.Count.ToString(CultureInfo.InvariantCulture);
        }

        string PopulationPosition(bool production, string[] args)
        {
            RequireArgs(args, 2, args[0] + " population");
            var pop = GetPopulation(RequireCurrentSlot(), production, ParseIndex(args[1]));
            return FormatVector(pop.Centre);
        }

        string PopulationMembers(bool production, string[] args)
        {
            RequireArgs(args, 2, args[0] + " population");
            var pop = GetPopulation(RequireCurrentSlot(), production, ParseIndex(args[1]));
            return FormatHandles(pop.Handles);
        }

        string PopulationMember(bool production, string[] args)
        {
            RequireArgs(args, 3, args[0] + " population object");
            var pop = GetPopulation(RequireCurrentSlot(), production, ParseIndex(args[1]));
            int objectIndex = ParseIndex(args[2]);
            if ((uint)objectIndex >= (uint)pop.Handles.Count)
                throw new TclException("invalid object index");
            return pop.Handles[objectIndex].ToString(CultureInfo.InvariantCulture);
        }

        string NearestPopulation(bool production, string[] args)
        {
            RequireArgs(args, 2, args[0] + " position");
            Vector3 position = ParseVector(args[1]);
            var populations = Populations(RequireCurrentSlot(), production);
            int nearest = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < populations.Count; i++)
            {
                float dx = populations[i].Centre.x - position.x;
                float dy = populations[i].Centre.y - position.y;
                float distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                nearest = i;
            }
            return nearest.ToString(CultureInfo.InvariantCulture);
        }

        string Intruders(bool production)
        {
            var slot = RequireCurrentSlot();
            RefreshWorldFacts(_currentOwner, slot);
            return FormatHandles(production ? slot.IntruderProductions : slot.IntruderGnomes);
        }

        string PossibleInventions(string[] args)
        {
            RequireArgs(args, 1, "ai_getpossibleinventions");
            RequireCurrentSlot();
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var place in _ctx.Entities.Values)
            {
                if (place.Owner != _currentOwner || !IsProduction(place) ||
                    !place.BuildupComplete || IsBoxed(place))
                    continue;

                foreach (string item in Catalog(place))
                {
                    if (!seen.Add(item) || OwnerAttrib(_currentOwner, "Bp" + item) > 0.5)
                        continue;
                    if (_ctx.ProdDispatcher != null &&
                        !_ctx.ProdDispatcher.OwnerCanInventAt(place, item, _currentOwner))
                        continue;
                    result.Add(item);
                }
            }
            return TclList.Format(result);
        }

        string ProductionTaskPlaces(string[] args, bool activeOnly)
        {
            RequireArgs(args, 3, args[0] + " owner class");
            int owner = ParseOwner(args[1]);
            string item = args[2];
            var handles = new List<int>();
            foreach (var place in _ctx.Entities.Values)
            {
                if (place.Owner != owner || !IsProduction(place) ||
                    !CatalogContains(place, item))
                    continue;
                if (activeOnly &&
                    (!place.ProdSlots.TryGetValue(item, out double count) || count <= 0.0))
                    continue;
                handles.Add(place.Handle);
            }
            return FormatHandles(handles);
        }

        string ProductionTaskTotal(string[] args)
        {
            RequireArgs(args, 3, "prod_get_task_total_cnt owner class");
            int owner = ParseOwner(args[1]);
            string item = args[2];
            int total = 0;
            foreach (var place in _ctx.Entities.Values)
            {
                if (place.Owner != owner || !IsProduction(place) ||
                    !CatalogContains(place, item) ||
                    !place.ProdSlots.TryGetValue(item, out double count) || count <= 0.0)
                    continue;
                total += Math.Max(0, (int)count);
            }
            return total.ToString(CultureInfo.InvariantCulture);
        }

        string SetBuildup(string[] args)
        {
            RequireArgs(args, 3, "ai_setbuildup production position");
            var place = ResolveProduction(args[1]);
            Vector3 position = ParseVector(args[2]);

            // FUN_0042d9b0 writes the planner record as one deferred unpack/build order:
            // +0x28=1/+0x29=0, +0x2a=0/+0x2b=1, +0x2c/+0x2d=0, +0x3d word=0x100.
            place.UnpackRequested = true;
            place.PackRequested = false;
            place.BuildupRequested = false;
            place.MoveAfterPack = false;
            place.OrderAsBox = false;
            place.OrderPos = position;
            place.OrderGhostShown = true;
            _ctx.Placement?.RegisterAiOrderGhost(place, position);
            return "";
        }

        string SetPack(string[] args)
        {
            RequireArgs(args, 2, "ai_setpack production");
            var place = ResolveProduction(args[1]);

            // FUN_0042db80 writes +0x29/+0x2a as 1/0, arms the move byte +0x2c and clears
            // the outstanding-order word. The command deliberately leaves +0x28 untouched.
            place.PackRequested = true;
            place.BuildupRequested = false;
            place.MoveAfterPack = true;
            place.OrderGhostShown = false;
            _ctx.Placement?.HideOrderGhost(place.Handle);
            return "";
        }

        string ValidLocation(string[] args)
        {
            RequireArgs(args, 3, "ai_validlocation class position");
            if (!_ctx.Classes.TryGetValue(args[1], out var cls))
                throw new TclException($"unknown class \"{args[1]}\"");
            Vector3 position = ParseVector(args[2]);
            return _ctx.Placement == null ||
                _ctx.Placement.ComputeAiBuildingValid(cls, position, settle: true) ? "1" : "0";
        }

        string ProductionLocations(string[] args)
        {
            RequireArgs(args, 4, "ai_getprodlocations class position range");
            if (!_ctx.Classes.TryGetValue(args[1], out var cls))
                throw new TclException($"unknown class \"{args[1]}\"");
            Vector3 centre = ParseVector(args[2]);
            float range = Math.Max(0f, ParseFloat(args[3]));
            var nav = _ctx.Terrain?.Nav;
            if (nav == null || range <= 0f) return "";

            // FUN_0042eaf0 scans the map at 1/8-tile vertical resolution and two-tile
            // horizontal steps. Its vertical search radius is range*0.3, depths run from
            // DAT_00737330=2.0 to DAT_00737548=12.5 in DAT_00737138=1.0 steps, and the final
            // list is sorted by squared distance from the requested centre.
            int sx0 = Math.Max(10, (int)(centre.x - range)) * 8;
            int sx1 = Math.Min(nav.Width - 10, (int)(centre.x + range)) * 8;
            int sy0 = Math.Max(10, (int)(centre.y - range * 0.3f)) * 8;
            int sy1 = Math.Min(nav.Height - 10, (int)(centre.y + range * 0.3f)) * 8;
            if (sx0 > sx1 || sy0 > sy1) return "";

            var candidates = new List<Vector3>();
            for (int sy = sy0; sy <= sy1; sy++)
            {
                for (int sx = sx0; sx <= sx1; sx += 16)
                {
                    if (!nav.SubIsFloor(sx, sy) || nav.SubIsFloor(sx, sy + 1)) continue;
                    for (float depth = 2f; depth < 12.5f; depth += 1f)
                    {
                        var position = new Vector3(sx * 0.125f, sy * 0.125f, depth);
                        if (_ctx.Placement != null &&
                            !_ctx.Placement.ComputeAiBuildingValid(cls, position, settle: false))
                            continue;
                        candidates.Add(position);
                    }
                }
            }

            candidates.Sort((left, right) =>
                SquaredDistance(left, centre).CompareTo(SquaredDistance(right, centre)));
            const int MaxReturnedLocations = 2048;
            int count = Math.Min(candidates.Count, MaxReturnedLocations);
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = FormatVector(candidates[i]);
            return TclList.Format(result);
        }

        List<string> Catalog(DigglesTclEntity place)
        {
            if (place.Class == null) return new List<string>();
            try
            {
                string command = TclList.Format(new[]
                    { "call_method_static", place.Class.Name, "prod_items" });
                return new List<string>(TclList.Parse(_ctx.Interp.Eval(command)));
            }
            catch
            {
                return new List<string>();
            }
        }

        bool CatalogContains(DigglesTclEntity place, string item)
        {
            foreach (string candidate in Catalog(place))
                if (string.Equals(candidate, item, StringComparison.Ordinal)) return true;
            return false;
        }

        DigglesTclEntity ResolveProduction(string value)
        {
            var entity = _ctx.ResolveEntity(value);
            if (!IsProduction(entity)) throw new TclException("no prod-site");
            return entity;
        }

        double OwnerAttrib(int owner, string name) =>
            _ctx.OwnerAttribs.TryGetValue(owner, out var values) &&
            values.TryGetValue(name, out double value) ? value : 0.0;

        static float SquaredDistance(Vector3 left, Vector3 right)
        {
            Vector3 delta = left - right;
            return delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
        }

        void RefreshWorldFacts(int owner, Slot slot)
        {
            slot.GnomePopulations.Clear();
            slot.ProductionPopulations.Clear();
            slot.IntruderGnomes.Clear();
            slot.IntruderProductions.Clear();

            foreach (var entity in _ctx.Entities.Values)
            {
                bool gnome = IsGnomeLike(entity);
                bool production = IsProduction(entity);
                if (!gnome && !production) continue;

                if (entity.Owner == owner)
                {
                    if (gnome)
                        AddPopulation(slot.GnomePopulations, entity);
                    else if (!IsBoxed(entity))
                        AddPopulation(slot.ProductionPopulations, entity);
                    continue;
                }

                if (!VisibleTo(owner, entity)) continue;
                if (gnome) slot.IntruderGnomes.Add(entity.Handle);
                else slot.IntruderProductions.Add(entity.Handle);
            }
        }

        bool VisibleTo(int owner, DigglesTclEntity entity)
        {
            if (_ctx.FogOfWar == null) return true;
            // CAIManager (FUN_0042a650) gates both intruder lists on the object's CObj::GetMaxFow
            // value, not on the object's pivot tile. Large and wall-mounted targets therefore use
            // the same seven-point model-extent visibility law as every other native consumer.
            return DigglesTclEngine.GetMaxFow(_ctx, entity, owner) > 0xc5;
        }

        static void AddPopulation(List<Population> populations, DigglesTclEntity entity)
        {
            foreach (var population in populations)
            {
                float dx = Math.Abs(entity.Position.x - population.Centre.x);
                float dy = Math.Abs(entity.Position.y - population.Centre.y);
                float metric = dx + dy - Math.Min(dx, dy) * 0.5f;
                if (metric >= PopulationJoinDistance) continue;
                int previousCount = population.Handles.Count;
                population.Centre =
                    (population.Centre * previousCount + entity.Position) / (previousCount + 1);
                population.Handles.Add(entity.Handle);
                return;
            }

            var created = new Population { Centre = entity.Position };
            created.Handles.Add(entity.Handle);
            populations.Add(created);
        }

        static List<Population> Populations(Slot slot, bool production) =>
            production ? slot.ProductionPopulations : slot.GnomePopulations;

        static Population GetPopulation(Slot slot, bool production, int index)
        {
            var populations = Populations(slot, production);
            if ((uint)index >= (uint)populations.Count)
                throw new TclException("invalid population index");
            return populations[index];
        }

        static bool IsGnomeLike(DigglesTclEntity entity) =>
            entity.Class.Kind is "gnome" or "baby" or "monster";

        static bool IsProduction(DigglesTclEntity entity) =>
            entity.Class.Kind is "production" or "elevator" or "store" or "energy" or
                "protection";

        static bool IsBoxed(DigglesTclEntity entity) =>
            entity.VarsScope.Vars.TryGetValue("_boxed", out string value) && IsTrue(value);

        static string FormatHandles(List<int> handles)
        {
            var values = new string[handles.Count];
            for (int i = 0; i < handles.Count; i++)
                values[i] = handles[i].ToString(CultureInfo.InvariantCulture);
            return TclList.Format(values);
        }

        static string FormatVector(Vector3 value) => TclList.Format(new[]
        {
            value.x.ToString("R", CultureInfo.InvariantCulture),
            value.y.ToString("R", CultureInfo.InvariantCulture),
            value.z.ToString("R", CultureInfo.InvariantCulture),
        });

        static Vector3 ParseVector(string value)
        {
            var parts = TclList.Parse(value);
            return new Vector3(
                parts.Count > 0 ? ParseFloat(parts[0]) : 0f,
                parts.Count > 1 ? ParseFloat(parts[1]) : 0f,
                parts.Count > 2 ? ParseFloat(parts[2]) : 0f);
        }

        static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : 0f;

        static int ParseIndex(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;

        static void RequireArgs(string[] args, int count, string usage)
        {
            if (args.Length != count)
                throw new TclException("wrong # args: should be \"" + usage + "\"");
        }

        static int[] ReadTheftCounts(TclScope scope)
        {
            var result = new int[OwnerCount];
            if (!scope.Vars.TryGetValue("ClansStoleCntList", out string raw)) return result;
            var values = TclList.Parse(raw);
            for (int i = 0; i < result.Length && i < values.Count; i++)
                int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out result[i]);
            return result;
        }

        static bool TryInt(TclScope scope, string name, out int value)
        {
            value = -1;
            return scope.Vars.TryGetValue(name, out string raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        static bool IsTrue(string value) => value == "1" ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

        Slot RequireCurrentSlot()
        {
            if (_currentOwner < 0)
                throw new TclException("AI command used outside an AI interpreter");
            return _slots[_currentOwner];
        }

        Slot RequireSlot(int owner) => _slots[owner];

        static int ParseOwner(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int owner) || owner < 0 || owner >= OwnerCount)
                throw new TclException($"invalid AI owner \"{value}\"");
            return owner;
        }

        static double FirstTickTime(int owner, double gameTime)
        {
            int currentTick = Math.Max(0, (int)Math.Floor(gameTime / LogicTickSeconds + 1e-9));
            int phase = owner * 6 % TickPeriod;
            int nextTick = currentTick;
            while ((nextTick + phase) % TickPeriod != 0) nextTick++;
            return nextTick * LogicTickSeconds;
        }
    }
}
