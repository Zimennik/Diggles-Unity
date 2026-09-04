#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;

    public sealed class OriginalSaveFogDocument
    {
        const int PlayerCount = 8;

        readonly ReadOnlyCollection<OriginalSaveIntPairCollection> _fadeTilesByPlayer;

        internal OriginalSaveFogDocument(
            byte[] payload,
            int offset,
            OriginalSaveWorldMetadata metadata)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            var fadeTiles = new OriginalSaveIntPairCollection[PlayerCount];
            for (int player = 0; player < fadeTiles.Length; player++)
            {
                int count = OriginalSaveFluidDocument.ReadAndValidateCount(
                    payload, ref offset, metadata.FogSparseCounts[player], "fog fade tile");
                fadeTiles[player] = new OriginalSaveIntPairCollection(payload, offset, count);
                offset = checked(offset + count * OriginalSaveIntPairCollection.RecordSize);
            }
            _fadeTilesByPlayer = Array.AsReadOnly(fadeTiles);

            if (offset != metadata.Marker4Offset)
                throw new ArgumentException(
                    "The typed world document does not end at native marker 4.", nameof(payload));
            EndOffset = offset;
        }

        // Each pair is a native (tileX, tileY) entry in the player's active fade list.
        public IReadOnlyList<OriginalSaveIntPairCollection> FadeTilesByPlayer => _fadeTilesByPlayer;
        public int EndOffset { get; }
    }
}
