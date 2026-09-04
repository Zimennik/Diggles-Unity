#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.IO;

    /// <summary>
    /// Lossless current-version CElf singleton state serialized by FUN_00429100.
    /// </summary>
    public sealed class OriginalSaveElfDocument
    {
        internal OriginalSaveElfDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.ElfStateOffset);
            int end = checked((int)metadata.ElfStateEndOffset);
            var cursor = new Cursor(sourcePayload, start, end);
            TextureVariant = cursor.ReadInt32();
            CurrentRegisteredAnimation = cursor.ReadInt32();
            RequestedRegisteredAnimation = cursor.ReadInt32();
            NativeModelDatabaseId = cursor.ReadInt32();
            NativeMeshId = cursor.ReadInt32();
            FrameAlpha = cursor.ReadRaw32();
            LookAtObjectId = cursor.ReadInt32();
            Position = cursor.ReadVector3();
            LeanA = cursor.ReadRaw32();
            LeanB = cursor.ReadRaw32();
            NativeState64 = cursor.ReadRaw32();
            Target = cursor.ReadVector3();
            Mode = cursor.ReadInt32();
            DrawOverUi = cursor.ReadBooleanByte("CElf draw-over-UI flag");
            AnimationHasMoreFrames = cursor.ReadBooleanByte("CElf animation continuation flag");
            FirstParticleSourceHandle = cursor.ReadInt32();
            SecondParticleSourceHandle = cursor.ReadInt32();
            if (cursor.Position != end)
            {
                throw new InvalidDataException(
                    $"Typed CElf parsing ended at {cursor.Position}, expected {end}.");
            }
        }

        public int TextureVariant { get; }
        public int CurrentRegisteredAnimation { get; }
        public int RequestedRegisteredAnimation { get; }
        public int NativeModelDatabaseId { get; }
        public int NativeMeshId { get; }
        public OriginalSaveRaw32 FrameAlpha { get; }
        public int LookAtObjectId { get; }
        public OriginalSaveVector3 Position { get; }
        public OriginalSaveRaw32 LeanA { get; }
        public OriginalSaveRaw32 LeanB { get; }
        public OriginalSaveRaw32 NativeState64 { get; }
        public OriginalSaveVector3 Target { get; }
        public int Mode { get; }
        public bool DrawOverUi { get; }
        public bool AnimationHasMoreFrames { get; }
        public int FirstParticleSourceHandle { get; }
        public int SecondParticleSourceHandle { get; }

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

            internal OriginalSaveRaw32 ReadRaw32() =>
                new(ReadInt32());

            internal OriginalSaveVector3 ReadVector3() => new(
                ReadRaw32().AsSingle,
                ReadRaw32().AsSingle,
                ReadRaw32().AsSingle);

            internal bool ReadBooleanByte(string label)
            {
                EnsureAvailable(sizeof(byte));
                byte value = _source[Position++];
                if (value > 1)
                    throw new InvalidDataException($"Invalid {label} value {value}.");
                return value != 0;
            }

            private void EnsureAvailable(int size)
            {
                if (size < 0 || (long)Position + size > _end)
                    throw new EndOfStreamException("Unexpected end of native CElf state.");
            }
        }
    }
}
