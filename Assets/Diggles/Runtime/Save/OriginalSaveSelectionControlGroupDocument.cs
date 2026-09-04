#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;

    public sealed class OriginalSaveSelectionControlGroupDocument
    {
        public const int GroupCount = 9;
        public const int SerializedCopyCount = 9;

        private const int MaximumMemberCount = 8192;

        private readonly ReadOnlyCollection<IReadOnlyList<int>> _serializedGroups;
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _groups;

        internal OriginalSaveSelectionControlGroupDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            int start = checked((int)metadata.MatrixSectionOffset);
            int end = checked((int)metadata.Marker12Offset);
            if (start < 0 || end < start || end > sourcePayload.Length)
                throw new ArgumentOutOfRangeException(nameof(metadata));

            int serializedCount = checked(GroupCount * SerializedCopyCount);
            var serializedGroups = new IReadOnlyList<int>[serializedCount];
            int position = start;
            for (int vectorIndex = 0; vectorIndex < serializedGroups.Length; vectorIndex++)
            {
                int memberCount = ReadInt32(sourcePayload, ref position, end);
                if (memberCount < 0 || memberCount > MaximumMemberCount)
                {
                    throw new InvalidDataException(
                        $"Invalid native selection control-group member count {memberCount}.");
                }
                var members = new int[memberCount];
                for (int member = 0; member < members.Length; member++)
                    members[member] = ReadInt32(sourcePayload, ref position, end);
                serializedGroups[vectorIndex] = Array.AsReadOnly(members);
            }
            if (position != end)
            {
                throw new InvalidDataException(
                    $"Typed selection control groups ended at {position}, expected marker 12 at {end}.");
            }
            _serializedGroups = Array.AsReadOnly(serializedGroups);

            var groups = new IReadOnlyList<int>[GroupCount];
            for (int group = 0; group < groups.Length; group++)
                groups[group] = serializedGroups[group];
            _groups = Array.AsReadOnly(groups);
        }

        public IReadOnlyList<IReadOnlyList<int>> Groups => _groups;
        public IReadOnlyList<IReadOnlyList<int>> SerializedGroups => _serializedGroups;

        public IReadOnlyList<int> GetSerializedGroup(int copy, int group)
        {
            if ((uint)copy >= SerializedCopyCount) throw new ArgumentOutOfRangeException(nameof(copy));
            if ((uint)group >= GroupCount) throw new ArgumentOutOfRangeException(nameof(group));
            return _serializedGroups[copy * GroupCount + group];
        }

        private static int ReadInt32(byte[] source, ref int position, int end)
        {
            if (position > end - sizeof(int))
                throw new EndOfStreamException("Unexpected end of native selection control groups.");
            int value = OriginalSaveWorldDocument.ReadInt32(source, position);
            position += sizeof(int);
            return value;
        }
    }

    public sealed class OriginalSaveRuntimeSelectionControlGroupSnapshot
    {
        private readonly ReadOnlyCollection<IReadOnlyList<int>> _groups;

        internal OriginalSaveRuntimeSelectionControlGroupSnapshot(
            OriginalSaveSelectionControlGroupDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var groups = new IReadOnlyList<int>[source.Groups.Count];
            for (int group = 0; group < groups.Length; group++)
            {
                IReadOnlyList<int> sourceMembers = source.Groups[group];
                var members = new int[sourceMembers.Count];
                for (int member = 0; member < members.Length; member++)
                    members[member] = sourceMembers[member];
                groups[group] = Array.AsReadOnly(members);
            }
            _groups = Array.AsReadOnly(groups);
        }

        public IReadOnlyList<IReadOnlyList<int>> Groups => _groups;
    }
}
