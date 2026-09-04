#nullable enable

namespace Diggles.Runtime.Sim
{
    using System;
    using System.Collections.Generic;

    // In-process scheduler for `timer_event $h evt_name -interval N
    // -repeat M -attime T` Tcl calls. Holds a flat list of pending
    // entries and on Mature(now) flushes all entries whose NextFire
    // is ≤ now to the caller-supplied delivery delegate. Recurring
    // entries (RepeatsLeft > 1) are re-queued with NextFire+=Interval;
    // RepeatsLeft < 0 means infinite (`timer_event … -repeat -1`, e.g.
    // the dwarf's 1-second attrib/face heartbeat) — never expires.
    //
    // Simple O(N) scan per tick — fine for N up to a few hundred,
    // which matches Diggles' usage (most entities have 0-2 active
    // timers). Could be promoted to a min-heap later if profiling
    // shows a bottleneck.
    //
    // Mirrors CTimerEvtMgr in the original engine (re/notes/recon_summary.md,
    // see also Docs/ENGINE_ARCHITECTURE.md § 4 "Timer events").
    public sealed class TimerScheduler
    {
        public struct Entry
        {
            public int Handle;
            public string Event;
            public double NextFire;
            public double Interval;
            public int RepeatsLeft;
            public Dictionary<string, string>? Payload;
        }

        readonly List<Entry> _entries = new();

        public int Count => _entries.Count;

        public Entry[] CaptureEntries()
        {
            var result = new Entry[_entries.Count];
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Payload != null)
                    entry.Payload = new Dictionary<string, string>(entry.Payload);
                result[i] = entry;
            }
            return result;
        }

        public void RestoreEntries(IEnumerable<Entry> entries)
        {
            ValidateEntries(entries);
            _entries.Clear();
            foreach (var source in entries)
            {
                var entry = source;
                if (entry.Payload != null)
                    entry.Payload = new Dictionary<string, string>(entry.Payload);
                _entries.Add(entry);
            }
        }

        public static void ValidateEntries(IEnumerable<Entry>? entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            foreach (Entry entry in entries)
            {
                if (entry.Handle < 0)
                    throw new ArgumentException("A scheduled timer has an invalid owner handle.", nameof(entries));
                if (string.IsNullOrEmpty(entry.Event) || entry.Event.Length > 31 ||
                    entry.Event.IndexOf('\0') >= 0)
                {
                    throw new ArgumentException("A scheduled timer has an invalid native event name.", nameof(entries));
                }
                // RepeatsLeft == 0 is a legal restored state: the native removes exhausted timers
                // lazily, so original saves carry them; they complete on the first ordinary tick.
                if (!Finite(entry.NextFire) || !Finite(entry.Interval) || entry.Interval < 0d)
                {
                    throw new ArgumentException(
                        $"A scheduled timer has invalid timing state (event '{entry.Event}', " +
                        $"handle {entry.Handle}, nextFire {entry.NextFire}, interval {entry.Interval}).",
                        nameof(entries));
                }
                if (entry.Payload == null) continue;
                foreach (KeyValuePair<string, string> value in entry.Payload)
                {
                    if (value.Key == null || value.Value == null)
                        throw new ArgumentException("A scheduled timer has a null payload value.", nameof(entries));
                }
            }
        }

        // Schedules an event to fire on a handle. `firstFire` is the
        // absolute game-time of the first fire; `interval` is the gap
        // between fires; `repeats` is the total number of fires — a
        // negative value (`-repeat -1`) means infinite (never expires).
        public void Schedule(int handle, string eventName,
            double firstFire, double interval, int repeats,
            Dictionary<string, string>? payload = null)
        {
            if (repeats == 0) repeats = 1;   // 0 → fire once; <0 stays = infinite sentinel
            _entries.Add(new Entry
            {
                Handle = handle,
                Event = eventName,
                NextFire = firstFire,
                Interval = interval,
                RepeatsLeft = repeats,
                Payload = payload,
            });
        }

        // Cancel all scheduled fires of a (handle, eventName) pair.
        public int Unset(int handle, string eventName) =>
            _entries.RemoveAll(e => e.Handle == handle && e.Event == eventName);

        // Cancel every scheduled event on a handle (e.g. on entity destroy).
        public int UnsetAll(int handle) =>
            _entries.RemoveAll(e => e.Handle == handle);

        // Flush matured entries through the deliver callback. Recurring
        // entries get re-queued; one-shots (repeats == 1) are removed.
        // Returns count of events delivered.
        public int Mature(double now, Action<int, EntityEvent> deliver)
        {
            int delivered = 0;
            int i = 0;
            while (i < _entries.Count)
            {
                var e = _entries[i];
                if (e.NextFire > now) { i++; continue; }
                deliver(e.Handle, new EntityEvent(e.Event, e.Payload));
                delivered++;
                if (e.RepeatsLeft > 0)        // finite; RepeatsLeft < 0 = infinite (never expires)
                {
                    e.RepeatsLeft -= 1;
                    if (e.RepeatsLeft == 0)
                    {
                        _entries.RemoveAt(i);
                        continue;
                    }
                }
                e.NextFire += e.Interval > 0 ? e.Interval : 0.001;  // advance (guard zero-interval spin)
                _entries[i] = e;
                // Don't advance i — the same slot might still be due
                // if interval is very small relative to dt.
            }
            return delivered;
        }

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
