#nullable enable

namespace Diggles.Runtime.Save
{
    using System;
    using System.IO;

    public sealed class OriginalSaveTrailingPaddingDocument
    {
        public const int SerializedLength = 0x1000;

        private readonly byte[] _bytes;

        internal OriginalSaveTrailingPaddingDocument(
            byte[] sourcePayload,
            OriginalSaveLogicMetadata metadata)
        {
            if (sourcePayload == null) throw new ArgumentNullException(nameof(sourcePayload));
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));

            int start = checked((int)metadata.FinalBlockOffset);
            int end = checked(start + SerializedLength);
            if (start < 0 || end != sourcePayload.Length || end != metadata.PayloadLength)
                throw new InvalidDataException("Native trailing padding has an invalid extent.");

            _bytes = new byte[SerializedLength];
            Buffer.BlockCopy(sourcePayload, start, _bytes, 0, _bytes.Length);
            IsCanonicalZeroPadding = IsAllZero(_bytes);
        }

        public int Length => _bytes.Length;
        public bool IsCanonicalZeroPadding { get; }

        public byte[] CopyBytes()
        {
            var copy = new byte[_bytes.Length];
            Buffer.BlockCopy(_bytes, 0, copy, 0, copy.Length);
            return copy;
        }

        public static byte[] CreateCanonicalBytes() => new byte[SerializedLength];

        private static bool IsAllZero(byte[] bytes)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] != 0) return false;
            }
            return true;
        }
    }
}
