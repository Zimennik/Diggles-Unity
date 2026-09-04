#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.IO;
    using Diggles.Runtime.World;

    public sealed class OriginalSaveFixedEngineStateSynthesizer
    {
        public const int BlockLength = 205;
        public const float DefaultViewBegin = -100f;
        public const float CampaignViewBegin = 23f;

        private const int ExploredRectangleValueCount = FogOfWarState.Players * 4;
        private const int PortEmptyMaximum = -0x8000;
        private const int NativeEmptyMaximum = -1;

        public byte[] Synthesize(
            double gameTime,
            DigglesCameraController camera,
            float viewBegin,
            FogOfWarState.SaveState fogOfWar)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (fogOfWar == null) throw new ArgumentNullException(nameof(fogOfWar));
            if (!Finite(gameTime) || gameTime < 0d)
                throw new InvalidDataException("The native game clock is invalid.");
            double nextTick = gameTime + 0.0001d;
            if (!Finite(nextTick))
                throw new InvalidDataException("The native next-tick clock is invalid.");
            if (!Finite(camera.FocusX) || !Finite(camera.FocusY) || !Finite(camera.Zoom) ||
                !Finite(camera.Dx) || !Finite(camera.Dy) || !Finite(viewBegin))
            {
                throw new InvalidDataException("The native fixed-engine camera state is invalid.");
            }
            if (fogOfWar.ExploredRect == null ||
                fogOfWar.ExploredRect.Length != ExploredRectangleValueCount)
            {
                throw new InvalidDataException(
                    "The native fixed-engine state requires eight explored rectangles.");
            }

            using var output = new MemoryStream(BlockLength);
            using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
            {
                writer.Write(nextTick);
                writer.Write(0d);
                writer.Write(0d);
                writer.Write(-1000f);
                writer.Write(-1000f);
                writer.Write(camera.Zoom);
                writer.Write((byte)0);
                writer.Write(camera.Dx);
                writer.Write(camera.Dy);
                writer.Write(camera.FocusX);
                writer.Write(camera.FocusY);
                writer.Write(camera.Zoom);
                writer.Write(camera.Dx);
                writer.Write(camera.Dy);
                writer.Write(0);
                writer.Write(0);
                writer.Write(viewBegin);

                for (int player = 0; player < FogOfWarState.Players; player++)
                {
                    int rectangleOffset = player * 4;
                    writer.Write(fogOfWar.ExploredRect[rectangleOffset]);
                    writer.Write(fogOfWar.ExploredRect[rectangleOffset + 1]);
                    writer.Write(ConvertMaximum(fogOfWar.ExploredRect[rectangleOffset + 2]));
                    writer.Write(ConvertMaximum(fogOfWar.ExploredRect[rectangleOffset + 3]));
                }
            }

            byte[] result = output.ToArray();
            if (result.Length != BlockLength)
            {
                throw new InvalidDataException(
                    $"The fixed-engine block is {result.Length} bytes; expected {BlockLength}.");
            }
            return result;
        }

        private static int ConvertMaximum(int value)
        {
            return value == PortEmptyMaximum ? NativeEmptyMaximum : value;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    public sealed class OriginalSaveViewBeginSessionStore
    {
        public OriginalSaveViewBeginSessionStore(bool campaignWorld)
        {
            Value = campaignWorld
                ? OriginalSaveFixedEngineStateSynthesizer.CampaignViewBegin
                : OriginalSaveFixedEngineStateSynthesizer.DefaultViewBegin;
        }

        public float Value { get; private set; }

        public void SetViewBegin(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            Value = value;
        }
    }
}
