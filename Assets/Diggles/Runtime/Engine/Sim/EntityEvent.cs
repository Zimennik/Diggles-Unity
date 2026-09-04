#nullable enable

namespace Diggles.Runtime.Sim
{
    using System.Collections.Generic;

    // Single in-flight event for an entity (a scheduled timer matured,
    // or a script explicitly raised it). Payload is the bag of named
    // strings the original engine exposes through `event_get -item KEY`.
    //
    // Value type — small, copied freely; no need for identity.
    public readonly struct EntityEvent
    {
        public readonly string Name;
        public readonly Dictionary<string, string>? Payload;

        public EntityEvent(string name, Dictionary<string, string>? payload = null)
        {
            Name = name;
            Payload = payload;
        }
    }
}
