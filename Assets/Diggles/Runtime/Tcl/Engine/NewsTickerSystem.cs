#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using Diggles.Runtime;
    using Diggles.Runtime.Tcl;
    using UnityEngine;

    public enum NewsTickerCategory
    {
        Fight = 0,
        Progress = 1,
        GnomeCritical = 2,
        GnomeInfo = 3,
        Material = 4,
        Mood = 5,
    }

    public sealed class NewsTickerEntry
    {
        public int Id { get; internal set; }
        public string Text { get; internal set; } = "msg";
        public NewsTickerCategory Category { get; internal set; }
        public Color32 Color { get; internal set; } = new(255, 255, 255, 255);
        public float Priority { get; internal set; } = 1f;
        public float RemainingTime { get; internal set; } = 30f;
        // Native -click replaces the entry's Tcl object at +0x28 (FUN_0051a3a0:22-30).
        // Scripts/classes/zwerg/baby.tcl:427-438 builds the canonical deferred set_view action.
        public string ClickScript { get; internal set; } = "";
        public bool AppearsInHistory { get; internal set; } = true;
        internal int PackedColorWord { get; set; } = -1;
        internal int NativeCategoryWord { get; set; }
        internal int TextMetricX { get; set; }
        internal int TextMetricY { get; set; }
        internal float EffectivePriority { get; set; } = 1f;
        internal byte PresentationFlag0 { get; set; }
        internal byte PresentationFlag1 { get; set; }
        internal double Timestamp { get; set; }

        internal NewsTickerEntry Copy() => new()
        {
            Id = Id,
            Text = Text,
            Category = Category,
            Color = Color,
            Priority = Priority,
            RemainingTime = RemainingTime,
            ClickScript = ClickScript,
            AppearsInHistory = AppearsInHistory,
            PackedColorWord = PackedColorWord,
            NativeCategoryWord = NativeCategoryWord,
            TextMetricX = TextMetricX,
            TextMetricY = TextMetricY,
            EffectivePriority = EffectivePriority,
            PresentationFlag0 = PresentationFlag0,
            PresentationFlag1 = PresentationFlag1,
            Timestamp = Timestamp,
        };
    }

    // Context-owned mirror of ViWinNewsTicker. The native command handler is FUN_0051acc0;
    // active entries live in its +0x58 array, the history mirror at +0x60, the category mask at
    // +0x68, and the next id at +0x44. The actual binary defaults to a 30-second timeout even
    // though the shipped Tcl HTML says 60 seconds.
    public sealed class NewsTickerSystem
    {
        public const int CategoryCount = 6;
        public const float DefaultTimeout = 30f;

        readonly DigglesTclContext _context;
        readonly List<NewsTickerEntry> _active = new();
        readonly List<NewsTickerEntry> _history = new();
        readonly List<NewsTickerDisplayQueueEntry> _displayQueue = new();
        int _nextId = 1;
        int _enabledMask = -1;

        public NewsTickerSystem(DigglesTclContext context) => _context = context;

        public IReadOnlyList<NewsTickerEntry> ActiveEntries => _active;
        public IReadOnlyList<NewsTickerEntry> HistoryEntries => _history;
        public IReadOnlyList<NewsTickerDisplayQueueEntry> DisplayQueueEntries => _displayQueue;
        public int EnabledMask => _enabledMask;
        public int Version { get; private set; }

        public void Clear()
        {
            if (_active.Count == 0 && _history.Count == 0 && _displayQueue.Count == 0) return;

            // ObjClear FUN_006229f0 truncates the GUI news DSFastArray through
            // FUN_00564a60(0), releasing every retained row. Keep the category mask and
            // monotonic id allocator (neither is reset there), but drop all visible/history
            // mirrors so a replacement campaign cannot inherit stale messages.
            _active.Clear();
            _history.Clear();
            _displayQueue.Clear();
            Version++;
        }

        public bool IsCategoryEnabled(NewsTickerCategory category) =>
            (_enabledMask & (1 << (int)category)) != 0;

        public bool TryGetActive(int id, out NewsTickerEntry entry)
        {
            entry = Find(_active, id)!;
            return entry != null;
        }

        public void CopyVisibleEntries(List<NewsTickerEntry> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            foreach (var entry in _active)
                if (IsCategoryEnabled(entry.Category)) destination.Add(entry);
            destination.Sort(static (a, b) =>
            {
                int priority = b.Priority.CompareTo(a.Priority);
                return priority != 0 ? priority : a.Id.CompareTo(b.Id);
            });
        }

        public void ReplaceDisplayQueue(IReadOnlyList<NewsTickerDisplayQueueEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            _displayQueue.Clear();
            for (int index = 0; index < entries.Count; index++)
            {
                NewsTickerDisplayQueueEntry entry = entries[index];
                if (entry.Id <= 0 || Find(_active, entry.Id) == null ||
                    float.IsNaN(entry.Position) || float.IsInfinity(entry.Position))
                {
                    throw new ArgumentException(
                        "News ticker display-queue layout state is invalid.",
                        nameof(entries));
                }
                _displayQueue.Add(entry);
            }
        }

        public void Tick(double scaledDeltaTime)
        {
            if (scaledDeltaTime <= 0.0) return;
            bool removed = false;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var entry = _active[i];
                // Native time values at or below zero are not decremented and therefore remain
                // active indefinitely. A positive timer that crosses zero expires normally.
                if (entry.RemainingTime <= 0f) continue;
                entry.RemainingTime -= (float)scaledDeltaTime;
                if (entry.RemainingTime > 0f) continue;
                _active.RemoveAt(i);
                removed = true;
            }
            if (removed)
            {
                InvalidateDisplayQueue();
                Version++;
            }
        }

        public bool Click(int id)
        {
            var entry = Find(_active, id);
            entry ??= Find(_history, id);
            // Native skips its evaluator when the active entry or history row has no action object
            // (FUN_00519800:61-77, 91-95).
            if (entry == null || string.IsNullOrEmpty(entry.ClickScript)) return false;
            string script = entry.ClickScript;
            try
            {
                // The native hit path reads the active entry's +0x28 action, or the history row's
                // action, and calls its evaluator once only when that object is non-null
                // (FUN_00519800:61-77, 87-95). At FUN_00519800:77 the evaluator is the main game's
                // DAT_00772400+0x368 Tcl console; FUN_00555a00:50-57 forwards to
                // Tcl_EvalObjEx(interp, action, 0) in FUN_006bc2c0:70-76. Unlike the per-object path,
                // which installs DAT_007723f4 around evaluation (FUN_005152e0:15-28), this UI event
                // has no object/proc frame, so flags 0 selects the main global frame. A bare Eval on
                // the context's main interpreter matches that scope.
                _context.Interp.Eval(script);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[NewsTicker] click script for entry {id} failed: {exception.Message}");
                return false;
            }
        }

        public SaveState Capture()
        {
            var state = new SaveState
            {
                NextId = _nextId,
                EnabledMask = _enabledMask,
                Active = CaptureEntries(_active),
                History = CaptureEntries(_history),
                DisplayQueue = CaptureDisplayQueue(),
            };
            return state;
        }

        public void Restore(SaveState state)
        {
            ValidateSaveState(state);
            _nextId = state.NextId;
            _enabledMask = state.EnabledMask;
            RestoreEntries(_active, state.Active);
            RestoreEntries(_history, state.History);
            RestoreDisplayQueue(state.DisplayQueue);
            Version++;
        }

        public static void ValidateSaveState(SaveState? state)
        {
            if (state == null || state.NextId < 1 || state.Active == null || state.History == null ||
                state.DisplayQueue == null)
                throw new ArgumentException("News ticker save state is invalid.", nameof(state));
            ValidateEntries(state.Active, state.NextId, history: false);
            ValidateEntries(state.History, state.NextId, history: true);
            foreach (DisplayQueueSaveState entry in state.DisplayQueue)
            {
                if (entry == null || entry.Id <= 0 ||
                    float.IsNaN(entry.Position) || float.IsInfinity(entry.Position))
                {
                    throw new ArgumentException(
                        "News ticker display-queue save state is invalid.",
                        nameof(state));
                }
            }
        }

        public static void RegisterCommands(DigglesTclContext context, TclInterpreter interpreter)
        {
            interpreter.RegisterCommand("newsticker", (_, args) => Execute(context.NewsTicker, context, args));
        }

        static string Execute(NewsTickerSystem ticker, DigglesTclContext context, string[] args)
        {
            if (args.Length < 2)
                throw new TclException("wrong # args: should be \"newsticker option ?arg ...?\"");

            string subCommand = args[1];
            if (subCommand == "new")
            {
                if (args.Length < 3)
                    throw new TclException("wrong # args: should be \"newsticker new owner ?option value ...?\"");
                int owner = ParseInt(args[2]);
                // Native filtering happens before option parsing. Foreign-player notifications
                // are silently ignored and leave an empty Tcl result.
                if (owner != context.LocalPlayer && owner != -1) return "";
                int id = ticker.AddDefault();
                try { ApplyOptions(ticker, id, args, 3); }
                catch
                {
                    ticker.Delete(id);
                    throw;
                }
                return id.ToString(CultureInfo.InvariantCulture);
            }

            if (subCommand == "change")
            {
                if (args.Length < 3)
                    throw new TclException("wrong # args: should be \"newsticker change id ?option value ...?\"");
                // Archived message-id vars can be "" (a corpus dwarf whose ntAltMessage was
                // never armed): z_events guards only `== -1`, so an empty id flows here. The
                // shipped engine no-ops it; regression: a throw here broke the whole
                // attribupdate handler with "expected integer".
                if (!int.TryParse(args[2], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int id))
                {
                    return "0";
                }
                if (id == -1) id = ticker.AddDefault();
                try { ApplyOptions(ticker, id, args, 3); }
                catch
                {
                    ticker.Delete(id);
                    throw;
                }
                return id.ToString(CultureInfo.InvariantCulture);
            }

            if (subCommand == "delete")
            {
                if (args.Length < 3)
                    throw new TclException("wrong # args: should be \"newsticker delete id\"");
                if (int.TryParse(args[2], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int deleteId))
                {
                    ticker.Delete(deleteId);
                }
                return "";
            }

            if (subCommand == "mask") return ApplyMask(ticker, args);
            throw new TclException("unknown sub-command");
        }

        int AddDefault()
        {
            InvalidateDisplayQueue();
            var entry = new NewsTickerEntry
            {
                Id = _nextId++,
                Timestamp = _context.Sim?.GameTime ?? 0d,
            };
            ApplyTextMetrics(entry, entry.Text);
            _active.Add(entry);
            SyncHistory(entry);
            Version++;
            return entry.Id;
        }

        void Delete(int id)
        {
            bool removed = false;
            for (int i = _active.Count - 1; i >= 0; i--)
                if (_active[i].Id == id)
                {
                    _active.RemoveAt(i);
                    Version++;
                    removed = true;
                }
            if (removed) InvalidateDisplayQueue();
            // FUN_0051a1c0 removes the active/display records but deliberately leaves the
            // separate history copy intact.
        }

        void Mutate(int id, Action<NewsTickerEntry> mutation)
        {
            var entry = Find(_active, id);
            if (entry == null) return;
            InvalidateDisplayQueue();
            mutation(entry);
            SyncHistory(entry);
            Version++;
        }

        void SetText(int id, string text)
        {
            Mutate(id, entry =>
            {
                entry.Text = text;
                ApplyTextMetrics(entry, text);
            });
        }

        void ApplyTextMetrics(NewsTickerEntry entry, string text)
        {
            entry.TextMetricX = 0;
            entry.TextMetricY = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(_context.DataRoot)) return;
                string fontPath = Path.Combine(
                    _context.DataRoot,
                    "GUI",
                    "Fonts",
                    "Verdana_11.kft");
                if (!KftFont.TryLoadCached(fontPath, out KftFont? font) || font == null) return;
                entry.TextMetricX = font.MeasureWidth(text);
                entry.TextMetricY = font.LineHeight;
            }
            catch
            {
                entry.TextMetricX = 0;
                entry.TextMetricY = 0;
            }
        }

        void SyncHistory(NewsTickerEntry entry)
        {
            for (int i = _history.Count - 1; i >= 0; i--)
                if (_history[i].Id == entry.Id) _history.RemoveAt(i);
            if (entry.AppearsInHistory) _history.Insert(0, entry.Copy());
        }

        static void ApplyOptions(NewsTickerSystem ticker, int id, string[] args, int start)
        {
            for (int index = start; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length) throw new TclException("missing value");
                string option = args[index];
                string value = args[index + 1];
                switch (option)
                {
                    case "-text":
                        ticker.SetText(id, value);
                        break;
                    case "-color":
                    {
                        var color = ParseColor(value);
                        ticker.Mutate(id, entry =>
                        {
                            entry.Color = color;
                            entry.PackedColorWord = PackColor(color);
                        });
                        break;
                    }
                    case "-priority":
                    {
                        float priority = ParseFloat(value);
                        ticker.Mutate(id, entry =>
                        {
                            entry.Priority = priority;
                            entry.EffectivePriority = priority;
                        });
                        break;
                    }
                    case "-category":
                    {
                        var category = ParseCategory(value);
                        ticker.Mutate(id, entry =>
                        {
                            entry.Category = category;
                            entry.NativeCategoryWord = (int)category;
                        });
                        break;
                    }
                    case "-time":
                    {
                        float time = ParseFloat(value);
                        ticker.Mutate(id, entry => entry.RemainingTime = time);
                        break;
                    }
                    case "-click":
                        // FUN_0051acc0:526-550 routes -click to FUN_0051a3a0, which stores the
                        // supplied Tcl object at entry+0x28 (FUN_0051a3a0:22-30).
                        ticker.Mutate(id, entry => entry.ClickScript = value);
                        break;
                    case "-history":
                    {
                        bool history = ParseBoolean(value);
                        ticker.Mutate(id, entry => entry.AppearsInHistory = history);
                        break;
                    }
                    default:
                        throw new TclException("unknown parameter");
                }
            }
        }

        static string ApplyMask(NewsTickerSystem ticker, string[] args)
        {
            bool anyEnabledQuery = false;
            for (int i = 2; i < args.Length; i++)
            {
                string token = args[i];
                if (token.Length < 2) throw new TclException("invalid category");
                var category = ParseCategory(token.Substring(1));
                int bit = 1 << (int)category;
                switch (token[0])
                {
                    case '+': ticker._enabledMask |= bit; ticker.InvalidateDisplayQueue(); ticker.Version++; break;
                    case '-': ticker._enabledMask &= ~bit; ticker.InvalidateDisplayQueue(); ticker.Version++; break;
                    case '?': anyEnabledQuery |= (ticker._enabledMask & bit) != 0; break;
                    // The native validates the category after the first byte, then ignores an
                    // unrecognized prefix rather than rejecting the token.
                }
            }
            return anyEnabledQuery ? "1" : "0";
        }

        static NewsTickerEntry? Find(List<NewsTickerEntry> entries, int id)
        {
            foreach (var entry in entries) if (entry.Id == id) return entry;
            return null;
        }

        static int ParseInt(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                throw new TclException("expected integer");
            return result;
        }

        static float ParseFloat(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                || float.IsNaN(result) || float.IsInfinity(result))
                throw new TclException("expected floating-point number");
            return result;
        }

        static Color32 ParseColor(string value)
        {
            var values = TclList.Parse(value);
            if (values.Count != 3) throw new TclException("expected 3-element list");
            return new Color32(
                (byte)Mathf.Clamp(ParseInt(values[0]), 0, 255),
                (byte)Mathf.Clamp(ParseInt(values[1]), 0, 255),
                (byte)Mathf.Clamp(ParseInt(values[2]), 0, 255), 255);
        }

        static int PackColor(Color32 color) => unchecked((int)(
            (uint)color.a << 24 |
            (uint)color.r << 16 |
            (uint)color.g << 8 |
            color.b));

        static bool ParseBoolean(string value)
        {
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            throw new TclException("expected boolean value");
        }

        static NewsTickerCategory ParseCategory(string value)
        {
            if (value.Equals("fight", StringComparison.OrdinalIgnoreCase)) return NewsTickerCategory.Fight;
            if (value.Equals("progress", StringComparison.OrdinalIgnoreCase)) return NewsTickerCategory.Progress;
            if (value.Equals("gnomecritical", StringComparison.OrdinalIgnoreCase)) return NewsTickerCategory.GnomeCritical;
            if (value.Equals("gnomeinfo", StringComparison.OrdinalIgnoreCase)) return NewsTickerCategory.GnomeInfo;
            if (value.Equals("material", StringComparison.OrdinalIgnoreCase)) return NewsTickerCategory.Material;
            if (value.Equals("mood", StringComparison.OrdinalIgnoreCase)) return NewsTickerCategory.Mood;
            throw new TclException("invalid category");
        }

        static EntrySaveState[] CaptureEntries(List<NewsTickerEntry> entries)
        {
            var result = new EntrySaveState[entries.Count];
            for (int i = 0; i < entries.Count; i++) result[i] = CaptureEntry(entries[i]);
            return result;
        }

        DisplayQueueSaveState[] CaptureDisplayQueue()
        {
            var result = new DisplayQueueSaveState[_displayQueue.Count];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = new DisplayQueueSaveState
                {
                    Id = _displayQueue[index].Id,
                    Position = _displayQueue[index].Position,
                };
            }
            return result;
        }

        void RestoreDisplayQueue(DisplayQueueSaveState[] source)
        {
            _displayQueue.Clear();
            foreach (DisplayQueueSaveState entry in source)
                _displayQueue.Add(new NewsTickerDisplayQueueEntry(entry.Id, entry.Position));
        }

        void InvalidateDisplayQueue() => _displayQueue.Clear();

        static EntrySaveState CaptureEntry(NewsTickerEntry entry) => new()
        {
            Id = entry.Id,
            Text = entry.Text,
            Category = (int)entry.Category,
            Red = entry.Color.r,
            Green = entry.Color.g,
            Blue = entry.Color.b,
            PackedColorWord = entry.PackedColorWord,
            NativeCategoryWord = entry.NativeCategoryWord,
            TextMetricX = entry.TextMetricX,
            TextMetricY = entry.TextMetricY,
            Priority = entry.Priority,
            EffectivePriority = entry.EffectivePriority,
            RemainingTime = entry.RemainingTime,
            PresentationFlag0 = entry.PresentationFlag0,
            PresentationFlag1 = entry.PresentationFlag1,
            ClickScript = entry.ClickScript,
            Timestamp = entry.Timestamp,
            AppearsInHistory = entry.AppearsInHistory,
        };

        static void RestoreEntries(List<NewsTickerEntry> destination, EntrySaveState[] source)
        {
            destination.Clear();
            foreach (var entry in source)
                destination.Add(new NewsTickerEntry
                {
                    Id = entry.Id,
                    Text = entry.Text,
                    Category = (NewsTickerCategory)entry.Category,
                    Color = new Color32((byte)entry.Red, (byte)entry.Green, (byte)entry.Blue, 255),
                    PackedColorWord = entry.PackedColorWord,
                    NativeCategoryWord = entry.NativeCategoryWord,
                    TextMetricX = entry.TextMetricX,
                    TextMetricY = entry.TextMetricY,
                    Priority = entry.Priority,
                    EffectivePriority = entry.EffectivePriority,
                    RemainingTime = entry.RemainingTime,
                    PresentationFlag0 = entry.PresentationFlag0,
                    PresentationFlag1 = entry.PresentationFlag1,
                    ClickScript = entry.ClickScript,
                    Timestamp = entry.Timestamp,
                    AppearsInHistory = entry.AppearsInHistory,
                });
        }

        static void ValidateEntries(EntrySaveState[] entries, int nextId, bool history)
        {
            var ids = new HashSet<int>();
            foreach (var entry in entries)
            {
                if (entry == null || entry.Id <= 0 || entry.Id >= nextId || !ids.Add(entry.Id)
                    || entry.Text == null || entry.ClickScript == null
                    || entry.Category < 0 || entry.Category >= CategoryCount
                    || entry.Red < 0 || entry.Red > 255 || entry.Green < 0 || entry.Green > 255
                    || entry.Blue < 0 || entry.Blue > 255
                    || float.IsNaN(entry.Priority) || float.IsInfinity(entry.Priority)
                    || float.IsNaN(entry.EffectivePriority) ||
                       float.IsInfinity(entry.EffectivePriority)
                    || float.IsNaN(entry.RemainingTime) || float.IsInfinity(entry.RemainingTime)
                    || double.IsNaN(entry.Timestamp) || double.IsInfinity(entry.Timestamp)
                    || (history && !entry.AppearsInHistory))
                    throw new ArgumentException("News ticker entry save state is invalid.", nameof(entries));
            }
        }

        [Serializable]
        public sealed class SaveState
        {
            public int NextId = 1;
            public int EnabledMask = -1;
            public EntrySaveState[] Active = Array.Empty<EntrySaveState>();
            public EntrySaveState[] History = Array.Empty<EntrySaveState>();
            public DisplayQueueSaveState[] DisplayQueue = Array.Empty<DisplayQueueSaveState>();
        }

        [Serializable]
        public sealed class EntrySaveState
        {
            public int Id;
            public string Text = "";
            public int Category;
            public int Red = 255;
            public int Green = 255;
            public int Blue = 255;
            public int PackedColorWord = -1;
            public int NativeCategoryWord;
            public int TextMetricX;
            public int TextMetricY;
            public float Priority = 1f;
            public float EffectivePriority = 1f;
            public float RemainingTime = DefaultTimeout;
            public byte PresentationFlag0;
            public byte PresentationFlag1;
            public string ClickScript = "";
            public double Timestamp;
            public bool AppearsInHistory = true;
        }

        [Serializable]
        public sealed class DisplayQueueSaveState
        {
            public int Id;
            public float Position;
        }
    }

    public readonly struct NewsTickerDisplayQueueEntry
    {
        public NewsTickerDisplayQueueEntry(int id, float position)
        {
            Id = id;
            Position = position;
        }

        public int Id { get; }
        public float Position { get; }
    }
}
