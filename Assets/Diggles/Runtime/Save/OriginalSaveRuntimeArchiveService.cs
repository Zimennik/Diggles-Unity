#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using UnityEngine.Scripting;

    public interface IOriginalSaveRuntimeArchiveService
    {
        void CaptureAndWriteAtomic(
            string path,
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context);

        void CaptureAndWriteAtomic(
            string path,
            DigglesTclContext context,
            DigglesCameraController camera,
            OriginalSaveViewBeginSessionStore viewBeginStore);

        void CaptureAndWriteAtomic(
            string path,
            DigglesTclContext context,
            DigglesCameraController camera,
            float viewBegin);

        /// <summary>
        /// True while a background save is still serializing/compressing/writing. At most one
        /// write is in flight; async capture calls throw when busy (autosaves should skip).
        /// </summary>
        bool WriteInFlight { get; }

        /// <summary>
        /// Captures on the calling (main) thread — the only step that reads live state — and
        /// serializes/compresses/writes on a background thread. The returned task completes
        /// with the destination path.
        /// </summary>
        System.Threading.Tasks.Task<string> CaptureAndWriteAtomicAsync(
            string path,
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context);

        System.Threading.Tasks.Task<string> CaptureAndWriteAtomicAsync(
            string path,
            DigglesTclContext context,
            DigglesCameraController camera,
            OriginalSaveViewBeginSessionStore viewBeginStore);
    }

    public sealed class OriginalSaveRuntimeArchiveService : IOriginalSaveRuntimeArchiveService
    {
        private readonly IOriginalSaveRuntimePatchMapper _mapper;
        private readonly IOriginalSaveCurrentArchiveWriter _writer;
        private readonly IOriginalSaveLoadPlanner _planner;
        private readonly IOriginalSaveRepository _repository;
        private readonly OriginalSaveFixedEngineStateSynthesizer _fixedEngineStateSynthesizer;

        [Preserve]
        public OriginalSaveRuntimeArchiveService(
            IOriginalSaveRuntimePatchMapper mapper,
            IOriginalSaveCurrentArchiveWriter writer,
            IOriginalSaveLoadPlanner planner,
            IOriginalSaveRepository repository)
        {
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _fixedEngineStateSynthesizer = new OriginalSaveFixedEngineStateSynthesizer();
        }

        public void CaptureAndWriteAtomic(
            string path,
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (context == null) throw new ArgumentNullException(nameof(context));

            OriginalSaveCurrentArchivePatch patch = _mapper.Capture(baseline, context);
            OriginalSaveArchiveDocument document =
                _writer.Rebuild(baseline.SourceDocument, patch);
            _planner.Prepare(document);
            _repository.WriteAtomic(path, document);
        }

        public void CaptureAndWriteAtomic(
            string path,
            DigglesTclContext context,
            DigglesCameraController camera,
            OriginalSaveViewBeginSessionStore viewBeginStore)
        {
            if (viewBeginStore == null)
                throw new ArgumentNullException(nameof(viewBeginStore));
            CaptureAndWriteAtomic(path, context, camera, viewBeginStore.Value);
        }

        public void CaptureAndWriteAtomic(
            string path,
            DigglesTclContext context,
            DigglesCameraController camera,
            float viewBegin)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            OriginalSaveCanonicalCapture capture = _mapper.CaptureCanonical(null, context);
            byte[] fixedEngineState = SynthesizeFixedEngineState(capture, camera, viewBegin);
            OriginalSaveArchiveDocument document = _writer.Assemble(
                capture,
                fixedEngineState,
                context.LightBeginRow);
            _planner.Prepare(document);
            _repository.WriteAtomic(path, document);
        }

        // Async path: capture on the caller's thread, serialize+compress+write in the background.
        // Everything the background step touches is immutable once captured (the patch/canonical
        // POCOs and the baseline's retained payload bytes are never mutated).
        private int _writeInFlight;

        public bool WriteInFlight => Volatile.Read(ref _writeInFlight) != 0;

        public Task<string> CaptureAndWriteAtomicAsync(
            string path,
            OriginalSaveRuntimeSnapshot baseline,
            DigglesTclContext context)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (context == null) throw new ArgumentNullException(nameof(context));

            AcquireWriteSlot();
            try
            {
                OriginalSaveCurrentArchivePatch patch = _mapper.Capture(baseline, context);
                return RunWrite(path, () => _writer.Rebuild(baseline.SourceDocument, patch));
            }
            catch
            {
                ReleaseWriteSlot();
                throw;
            }
        }

        public Task<string> CaptureAndWriteAtomicAsync(
            string path,
            DigglesTclContext context,
            DigglesCameraController camera,
            OriginalSaveViewBeginSessionStore viewBeginStore)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (viewBeginStore == null) throw new ArgumentNullException(nameof(viewBeginStore));

            AcquireWriteSlot();
            try
            {
                OriginalSaveCanonicalCapture capture = _mapper.CaptureCanonical(null, context);
                byte[] fixedEngineState = SynthesizeFixedEngineState(
                    capture,
                    camera,
                    viewBeginStore.Value);
                int lightBeginRow = context.LightBeginRow;
                return RunWrite(
                    path,
                    () => _writer.Assemble(capture, fixedEngineState, lightBeginRow));
            }
            catch
            {
                ReleaseWriteSlot();
                throw;
            }
        }

        private byte[] SynthesizeFixedEngineState(
            OriginalSaveCanonicalCapture capture,
            DigglesCameraController camera,
            float viewBegin)
        {
            FogOfWarState.SaveState fogOfWar = capture.FogOfWar ??
                throw new InvalidOperationException(
                    "No-baseline original-save writing requires live fog-of-war state.");
            return _fixedEngineStateSynthesizer.Synthesize(
                capture.GameTime,
                camera,
                viewBegin,
                fogOfWar);
        }

        private void AcquireWriteSlot()
        {
            if (Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "An original-save background write is already in flight.");
            }
        }

        private void ReleaseWriteSlot() => Volatile.Write(ref _writeInFlight, 0);

        private Task<string> RunWrite(
            string path,
            Func<OriginalSaveArchiveDocument> buildDocument)
        {
            return Task.Run(() =>
            {
                try
                {
                    OriginalSaveArchiveDocument document = buildDocument();
                    _planner.Prepare(document);
                    _repository.WriteAtomic(path, document);
                    return path;
                }
                finally
                {
                    ReleaseWriteSlot();
                }
            });
        }
    }
}
