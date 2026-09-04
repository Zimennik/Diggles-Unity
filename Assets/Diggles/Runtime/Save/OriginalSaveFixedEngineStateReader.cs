#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.IO;

    public readonly struct OriginalSaveFixedEngineState
    {
        internal OriginalSaveFixedEngineState(
            float focusX,
            float focusY,
            float zoom,
            float dx,
            float dy,
            float viewBegin)
        {
            FocusX = focusX;
            FocusY = focusY;
            Zoom = zoom;
            Dx = dx;
            Dy = dy;
            ViewBegin = viewBegin;
        }

        public float FocusX { get; }
        public float FocusY { get; }
        public float Zoom { get; }
        public float Dx { get; }
        public float Dy { get; }
        public float ViewBegin { get; }
    }

    public static class OriginalSaveFixedEngineStateReader
    {
        private const int FocusXOffset = 45;
        private const int FocusYOffset = 49;
        private const int ZoomOffset = 53;
        private const int DxOffset = 57;
        private const int DyOffset = 61;
        private const int ViewBeginOffset = 73;

        public static OriginalSaveFixedEngineState Read(byte[] block)
        {
            return Read(block, 0);
        }

        public static OriginalSaveFixedEngineState Read(byte[] payload, int offset)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (offset < 0 ||
                offset > payload.Length - OriginalSaveFixedEngineStateSynthesizer.BlockLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    "The original-save payload does not contain a complete fixed-engine block.");
            }

            var state = new OriginalSaveFixedEngineState(
                BitConverter.ToSingle(payload, offset + FocusXOffset),
                BitConverter.ToSingle(payload, offset + FocusYOffset),
                BitConverter.ToSingle(payload, offset + ZoomOffset),
                BitConverter.ToSingle(payload, offset + DxOffset),
                BitConverter.ToSingle(payload, offset + DyOffset),
                BitConverter.ToSingle(payload, offset + ViewBeginOffset));
            Validate(state);
            return state;
        }

        public static OriginalSaveFixedEngineState Read(
            OriginalSaveArchiveDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            int offset = checked((int)document.Metadata.FixedEngineStateOffset);
            byte[] block = document.CopyPayloadRange(
                offset,
                OriginalSaveFixedEngineStateSynthesizer.BlockLength);
            return Read(block);
        }

        private static void Validate(OriginalSaveFixedEngineState state)
        {
            if (!Finite(state.FocusX) || !Finite(state.FocusY) ||
                !Finite(state.Zoom) || !Finite(state.Dx) || !Finite(state.Dy) ||
                !Finite(state.ViewBegin))
            {
                throw new InvalidDataException(
                    "The original save contains non-finite fixed-engine camera state.");
            }
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
