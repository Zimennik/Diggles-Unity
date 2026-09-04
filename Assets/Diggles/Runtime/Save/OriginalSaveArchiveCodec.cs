#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using VContainer;

    public interface IOriginalSaveArchiveCodec
    {
        OriginalSaveArchiveDocument Read(string path);
        OriginalSaveArchiveDocument Read(Stream compressedStream, bool leaveOpen = false);
        void Write(string path, OriginalSaveArchiveDocument document);
        void Write(Stream compressedStream, OriginalSaveArchiveDocument document, bool leaveOpen = false);
    }

    // Lossless native archive codec. The payload remains opaque until individual sections are mapped
    // into the versioned runtime document, but it is fully validated before a document is returned.
    public sealed class OriginalSaveArchiveCodec : IOriginalSaveArchiveCodec
    {
        private const int CopyBufferSize = 64 * 1024;

        private readonly OriginalSaveArchiveReader _reader;
        private readonly OriginalSaveArchiveCodecOptions _options;

        [Inject]
        [UnityEngine.Scripting.Preserve]
        public OriginalSaveArchiveCodec(
            OriginalSaveArchiveReader reader,
            OriginalSaveArchiveCodecOptions options)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public OriginalSaveArchiveCodec(OriginalSaveArchiveReader reader)
            : this(reader, new OriginalSaveArchiveCodecOptions()) { }

        public OriginalSaveArchiveCodec()
            : this(new OriginalSaveArchiveReader(), new OriginalSaveArchiveCodecOptions()) { }

        public OriginalSaveArchiveDocument Read(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(file);
        }

        public OriginalSaveArchiveDocument Read(Stream compressedStream, bool leaveOpen = false)
        {
            if (compressedStream == null) throw new ArgumentNullException(nameof(compressedStream));

            using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen))
                return ReadDecompressed(gzip);
        }

        private OriginalSaveArchiveDocument ReadDecompressed(Stream decompressedStream)
        {
            byte[] payload = ReadBoundedPayload(
                decompressedStream,
                _options.MaximumDecompressedPayloadLength);
            using var validationStream = new MemoryStream(payload, false);
            OriginalSaveArchiveMetadata metadata = _reader.ReadDecompressedMetadata(validationStream);
            return new OriginalSaveArchiveDocument(payload, metadata);
        }

        private static byte[] ReadBoundedPayload(Stream source, int maximumLength)
        {
            using var buffer = new MemoryStream(Math.Min(maximumLength, CopyBufferSize));
            var copyBuffer = new byte[CopyBufferSize];
            while (true)
            {
                int read = source.Read(copyBuffer, 0, copyBuffer.Length);
                if (read == 0) return buffer.ToArray();
                if (buffer.Length > maximumLength - read)
                    throw new InvalidDataException(
                        $"Original save payload exceeds the decompressed limit of {maximumLength} bytes.");
                buffer.Write(copyBuffer, 0, read);
            }
        }

        public void Write(string path, OriginalSaveArchiveDocument document)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Write(file, document);
        }

        public void Write(
            Stream compressedStream,
            OriginalSaveArchiveDocument document,
            bool leaveOpen = false)
        {
            if (compressedStream == null) throw new ArgumentNullException(nameof(compressedStream));
            if (document == null) throw new ArgumentNullException(nameof(document));

            // Fastest, not Optimal: the ~24 MB payload compresses in a fraction of the time
            // (sub-second saves, the original's own tempo) and the format carries no
            // expectation about compression ratio — the original reads any valid gzip stream.
            using var gzip = new GZipStream(
                compressedStream, CompressionLevel.Fastest, leaveOpen);
            document.WritePayloadTo(gzip);
        }
    }

    public sealed class OriginalSaveArchiveCodecOptions
    {
        public const int DefaultMaximumDecompressedPayloadLength = 256 * 1024 * 1024;

        [Inject]
        [UnityEngine.Scripting.Preserve]
        public OriginalSaveArchiveCodecOptions()
            : this(DefaultMaximumDecompressedPayloadLength) { }

        public OriginalSaveArchiveCodecOptions(int maximumDecompressedPayloadLength)
        {
            if (maximumDecompressedPayloadLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumDecompressedPayloadLength));
            MaximumDecompressedPayloadLength = maximumDecompressedPayloadLength;
        }

        public int MaximumDecompressedPayloadLength { get; }
    }

    public sealed class OriginalSaveArchiveDocument
    {
        private readonly byte[] _payload;

        internal OriginalSaveArchiveDocument(
            byte[] payload,
            OriginalSaveArchiveMetadata metadata)
        {
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            World = new OriginalSaveWorldDocument(_payload, Metadata.World);
            ClassLights = Metadata.Control != null
                ? new OriginalSaveClassLightDocument(_payload, Metadata.Control)
                : null;
            Energy = Metadata.Control != null
                ? new OriginalSaveEnergyDocument(_payload, Metadata.Control)
                : null;
            AnimationControllers = Metadata.Graphics != null
                ? new OriginalSaveAnimationControllerDocument(_payload, Metadata.Graphics)
                : null;
            Particles = Metadata.Graphics != null
                ? new OriginalSaveParticleDocument(_payload, Metadata.Graphics)
                : null;
            Objects = Metadata.Objects != null
                ? new OriginalSaveObjectCollection(_payload, Metadata.Objects)
                : null;
            Timers = Metadata.Objects != null
                ? new OriginalSaveTimerDocument(_payload, Metadata.Objects)
                : null;
            SpatialSeeds = Metadata.Objects != null
                ? new OriginalSaveSpatialSeedDocument(_payload, Metadata.Objects)
                : null;
            Actions = Metadata.Logic != null
                ? new OriginalSaveActionTableDocument(_payload, Metadata.Logic)
                : null;
            Story = Metadata.Logic != null
                ? new OriginalSaveStoryDocument(_payload, Metadata.Logic)
                : null;
            SelectionControlGroups = Metadata.Logic != null
                ? new OriginalSaveSelectionControlGroupDocument(_payload, Metadata.Logic)
                : null;
            Sparetime = Metadata.Logic != null
                ? new OriginalSaveSparetimeDocument(_payload, Metadata.Logic)
                : null;
            Triggers = Metadata.Logic != null
                ? new OriginalSaveTriggerTableDocument(_payload, Metadata.Logic)
                : null;
            Horizon = Metadata.Logic != null
                ? new OriginalSaveHorizonDocument(_payload, Metadata.Logic)
                : null;
            AdaptiveSoundMarkers = Metadata.Logic != null
                ? new OriginalSaveAdaptiveSoundMarkerDocument(_payload, Metadata.Logic)
                : null;
            NewsTicker = Metadata.Logic != null
                ? new OriginalSaveNewsTickerDocument(_payload, Metadata.Logic)
                : null;
            ObjectIcons = Metadata.Logic != null
                ? new OriginalSaveObjectIconDocument(_payload, Metadata.Logic)
                : null;
            CaveRecords = Metadata.Logic != null
                ? new OriginalSaveCaveRecordDocument(_payload, Metadata.Logic)
                : null;
            Ai = Metadata.Logic != null
                ? new OriginalSaveAiDocument(_payload, Metadata.Logic)
                : null;
            Elf = Metadata.Logic != null
                ? new OriginalSaveElfDocument(_payload, Metadata.Logic)
                : null;
            Players = Metadata.Logic != null
                ? new OriginalSavePlayerDocument(_payload, Metadata.Logic)
                : null;
            TrailingPadding = Metadata.Logic != null
                ? new OriginalSaveTrailingPaddingDocument(_payload, Metadata.Logic)
                : null;
        }

        public OriginalSaveArchiveMetadata Metadata { get; }
        public OriginalSaveWorldDocument World { get; }
        public OriginalSaveClassLightDocument? ClassLights { get; }
        public OriginalSaveEnergyDocument? Energy { get; }
        public OriginalSaveAnimationControllerDocument? AnimationControllers { get; }
        public OriginalSaveParticleDocument? Particles { get; }
        public OriginalSaveObjectCollection? Objects { get; }
        public OriginalSaveTimerDocument? Timers { get; }

        // Marker-7 dormant scenery seeds (Dummy_*/Urw_caves_* deco). Retained bytes still
        // round-trip verbatim; this typed view is read-only, used only to materialize the deco
        // on load (OriginalSaveRuntimeSessionStager → DigglesTclEngine.MaterializeSaveDecoSeeds).
        public OriginalSaveSpatialSeedDocument? SpatialSeeds { get; }
        public OriginalSaveActionTableDocument? Actions { get; }
        public OriginalSaveStoryDocument? Story { get; }
        public OriginalSaveSelectionControlGroupDocument? SelectionControlGroups { get; }
        public OriginalSaveSparetimeDocument? Sparetime { get; }
        public OriginalSaveTriggerTableDocument? Triggers { get; }
        public OriginalSaveHorizonDocument? Horizon { get; }
        public OriginalSaveAdaptiveSoundMarkerDocument? AdaptiveSoundMarkers { get; }
        public OriginalSaveNewsTickerDocument? NewsTicker { get; }
        public OriginalSaveObjectIconDocument? ObjectIcons { get; }
        public OriginalSaveCaveRecordDocument? CaveRecords { get; }
        public OriginalSaveAiDocument? Ai { get; }
        public OriginalSaveElfDocument? Elf { get; }
        public OriginalSavePlayerDocument? Players { get; }
        public OriginalSaveTrailingPaddingDocument? TrailingPadding { get; }
        public int PayloadLength => _payload.Length;

        public byte[] CopyPayload()
        {
            var copy = new byte[_payload.Length];
            Buffer.BlockCopy(_payload, 0, copy, 0, copy.Length);
            return copy;
        }

        internal byte[] CopyPayloadRange(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > _payload.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));
            var copy = new byte[count];
            Buffer.BlockCopy(_payload, offset, copy, 0, count);
            return copy;
        }

        internal void WritePayloadTo(Stream destination)
        {
            destination.Write(_payload, 0, _payload.Length);
        }
    }
}
