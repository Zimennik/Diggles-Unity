#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSavePlayerSlotDocument
    {
        private readonly ReadOnlyCollection<int> _diplomacy;

        internal OriginalSavePlayerSlotDocument(
            OriginalSaveNativeString playerName,
            int slotState,
            int playerOwnerId,
            int clanOwnerId,
            OriginalSaveNativeString secondaryName,
            int[] diplomacy)
        {
            PlayerName = playerName ?? throw new ArgumentNullException(nameof(playerName));
            SlotState = slotState;
            PlayerOwnerId = playerOwnerId;
            ClanOwnerId = clanOwnerId;
            SecondaryName = secondaryName ?? throw new ArgumentNullException(nameof(secondaryName));
            _diplomacy = Array.AsReadOnly(
                diplomacy ?? throw new ArgumentNullException(nameof(diplomacy)));
        }

        public OriginalSaveNativeString PlayerName { get; }
        public int SlotState { get; }
        public int PlayerOwnerId { get; }
        public int ClanOwnerId { get; }
        public OriginalSaveNativeString SecondaryName { get; }
        public IReadOnlyList<int> Diplomacy => _diplomacy;
    }

    public sealed class OriginalSavePlayerDocument
    {
        private const int OwnerCount = 8;
        private const int ReservedValueCount = 32;
        private const int MaximumStringLength = 1024 * 1024;

        private readonly ReadOnlyCollection<int> _reservedFraming;
        private readonly ReadOnlyCollection<OriginalSavePlayerSlotDocument> _slots;

        internal OriginalSavePlayerDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int reservedStart = checked((int)metadata.ReservedPlayerFramingOffset);
            int reservedEnd = checked((int)metadata.ReservedPlayerFramingEndOffset);
            var reservedCursor = new Cursor(sourcePayload, reservedStart, reservedEnd);
            var reserved = new int[ReservedValueCount];
            for (int index = 0; index < reserved.Length; index++)
                reserved[index] = reservedCursor.ReadInt32();
            reservedCursor.ExpectEnd("reserved player framing");
            _reservedFraming = Array.AsReadOnly(reserved);

            int playerStart = checked((int)metadata.PlayerStateOffset);
            int playerEnd = checked((int)metadata.PlayerStateEndOffset);
            var playerCursor = new Cursor(sourcePayload, playerStart, playerEnd);
            var slots = new OriginalSavePlayerSlotDocument[OwnerCount];
            for (int owner = 0; owner < slots.Length; owner++)
            {
                OriginalSaveNativeString playerName = playerCursor.ReadNativeString();
                int slotState = playerCursor.ReadInt32();
                int playerOwnerId = playerCursor.ReadInt32();
                int clanOwnerId = playerCursor.ReadInt32();
                OriginalSaveNativeString secondaryName = playerCursor.ReadNativeString();
                var diplomacy = new int[OwnerCount];
                for (int target = 0; target < diplomacy.Length; target++)
                    diplomacy[target] = playerCursor.ReadInt32();
                slots[owner] = new OriginalSavePlayerSlotDocument(
                    playerName,
                    slotState,
                    playerOwnerId,
                    clanOwnerId,
                    secondaryName,
                    diplomacy);
            }
            playerCursor.ExpectEnd("player state");
            _slots = Array.AsReadOnly(slots);
        }

        public IReadOnlyList<int> ReservedFraming => _reservedFraming;
        public IReadOnlyList<OriginalSavePlayerSlotDocument> Slots => _slots;

        private sealed class Cursor
        {
            private readonly byte[] _source;
            private readonly int _end;

            internal Cursor(byte[] source, int start, int end)
            {
                _source = source;
                if (start < 0 || end < start || end > source.Length)
                    throw new ArgumentOutOfRangeException(nameof(start));
                Position = start;
                _end = end;
            }

            internal int Position { get; private set; }

            internal int ReadInt32()
            {
                EnsureAvailable(sizeof(int));
                int value = OriginalSaveWorldDocument.ReadInt32(_source, Position);
                Position += sizeof(int);
                return value;
            }

            internal OriginalSaveNativeString ReadNativeString()
            {
                int length = ReadInt32();
                if (length <= 0 || length > MaximumStringLength)
                    throw new InvalidDataException($"Invalid native player string length {length}.");
                EnsureAvailable(length);
                var bytes = new byte[length];
                Buffer.BlockCopy(_source, Position, bytes, 0, length);
                Position += length;
                if (bytes[^1] != 0)
                    throw new InvalidDataException("Native player string is not NUL-terminated.");
                return new OriginalSaveNativeString(bytes);
            }

            internal void ExpectEnd(string label)
            {
                if (Position != _end)
                    throw new InvalidDataException(
                        $"Typed {label} parsing ended at {Position}, expected {_end}.");
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native player state.");
            }
        }
    }
}
