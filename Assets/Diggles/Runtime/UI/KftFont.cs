#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public sealed class KftFont
    {
        private const int CharacterCount = 256;
        private const int HeaderSize = 16;
        private const int CharacterMapSize = CharacterCount * sizeof(short);
        private const int GlyphRecordSize = 12;
        private const int GlyphPointSize = 3;
        private const int InitialRightmost = -1000;

        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, KftFont> CachedFonts =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly short[] characterToGlyph;
        private readonly GlyphRecord[] glyphs;
        private readonly sbyte[] glyphPointXs;
        private readonly sbyte[] kerning;

        private KftFont(
            int lineHeight,
            short[] characterToGlyph,
            GlyphRecord[] glyphs,
            sbyte[] glyphPointXs,
            sbyte[] kerning)
        {
            LineHeight = lineHeight;
            this.characterToGlyph = characterToGlyph;
            this.glyphs = glyphs;
            this.glyphPointXs = glyphPointXs;
            this.kerning = kerning;
        }

        public int LineHeight { get; }

        public static KftFont Load(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < HeaderSize + CharacterMapSize)
                throw new InvalidDataException("The KFT header is truncated.");

            int lineHeight = reader.ReadInt32();
            reader.ReadInt32();
            int glyphCount = reader.ReadInt32();
            int glyphPointCount = reader.ReadInt32();
            if (glyphCount < 0 || glyphCount > short.MaxValue + 1)
                throw new InvalidDataException("The KFT glyph count is invalid.");
            if (glyphPointCount < 0)
                throw new InvalidDataException("The KFT glyph-point count is invalid.");

            long kerningCount = (long)glyphCount * glyphCount;
            long requiredLength = HeaderSize + CharacterMapSize
                + (long)glyphCount * GlyphRecordSize
                + (long)glyphPointCount * GlyphPointSize
                + kerningCount;
            if (kerningCount > int.MaxValue || stream.Length < requiredLength)
                throw new InvalidDataException("The KFT data is truncated or too large.");

            var characterToGlyph = new short[CharacterCount];
            for (int index = 0; index < characterToGlyph.Length; index++)
            {
                short glyphIndex = reader.ReadInt16();
                if (glyphIndex < -1 || glyphIndex >= glyphCount)
                    throw new InvalidDataException("The KFT character map is invalid.");
                characterToGlyph[index] = glyphIndex;
            }

            var glyphs = new GlyphRecord[glyphCount];
            for (int index = 0; index < glyphs.Length; index++)
            {
                int advance = reader.ReadInt32();
                int pointCount = reader.ReadInt32();
                int pointIndex = reader.ReadInt32();
                if (pointCount > 0 &&
                    (pointIndex < 0 || (long)pointIndex + pointCount > glyphPointCount))
                {
                    throw new InvalidDataException("The KFT glyph-point range is invalid.");
                }
                glyphs[index] = new GlyphRecord(advance, pointCount, pointIndex);
            }

            var glyphPointXs = new sbyte[glyphPointCount];
            for (int index = 0; index < glyphPointXs.Length; index++)
            {
                glyphPointXs[index] = unchecked((sbyte)reader.ReadByte());
                reader.ReadByte();
                reader.ReadByte();
            }

            var kerning = new sbyte[(int)kerningCount];
            for (int index = 0; index < kerning.Length; index++)
                kerning[index] = unchecked((sbyte)reader.ReadByte());

            return new KftFont(lineHeight, characterToGlyph, glyphs, glyphPointXs, kerning);
        }

        public int MeasureWidth(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            int penX = 0;
            int rightmost = InitialRightmost;
            int previousCharacter = 0;
            for (int index = 0; index < text.Length; index++)
            {
                int currentCharacter = text[index];
                if (currentCharacter == 0) break;

                int glyphIndex = GetGlyphIndex(currentCharacter);
                if (glyphIndex >= 0)
                {
                    int previousGlyphIndex = GetGlyphIndex(previousCharacter);
                    int pairKerning = previousGlyphIndex < 0
                        ? 0
                        : kerning[previousGlyphIndex * glyphs.Length + glyphIndex];
                    GlyphRecord glyph = glyphs[glyphIndex];
                    if (glyph.PointCount == 0)
                    {
                        int glyphRight = unchecked(penX + glyph.Advance);
                        if (glyphRight > rightmost) rightmost = glyphRight;
                    }
                    else if (glyph.PointCount > 0)
                    {
                        int pointLimit = glyph.PointIndex + glyph.PointCount;
                        for (int pointIndex = glyph.PointIndex;
                             pointIndex < pointLimit;
                             pointIndex++)
                        {
                            int glyphRight = unchecked(penX + glyphPointXs[pointIndex] + 1);
                            if (glyphRight > rightmost) rightmost = glyphRight;
                        }
                    }
                    penX = unchecked(penX + glyph.Advance + pairKerning);
                }
                previousCharacter = currentCharacter;
            }
            return rightmost < 0 ? 0 : rightmost;
        }

        internal static bool TryLoadCached(string path, out KftFont? font)
        {
            font = null;
            try
            {
                string fullPath = Path.GetFullPath(path);
                lock (CacheLock)
                {
                    if (CachedFonts.TryGetValue(fullPath, out KftFont cached))
                    {
                        font = cached;
                        return true;
                    }
                    font = Load(fullPath);
                    CachedFonts.Add(fullPath, font);
                    return true;
                }
            }
            catch
            {
                font = null;
                return false;
            }
        }

        private int GetGlyphIndex(int character) =>
            (uint)character < CharacterCount ? characterToGlyph[character] : -1;

        private readonly struct GlyphRecord
        {
            internal GlyphRecord(int advance, int pointCount, int pointIndex)
            {
                Advance = advance;
                PointCount = pointCount;
                PointIndex = pointIndex;
            }

            internal int Advance { get; }
            internal int PointCount { get; }
            internal int PointIndex { get; }
        }
    }
}
