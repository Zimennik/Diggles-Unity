#nullable enable

namespace Diggles.Runtime
{
    using System.IO;
    using UnityEngine;

    public enum TgaAlphaKind
    {
        // No transparency at all — render as fully opaque.
        Opaque = 0,
        // Binary alpha (foliage, fences) — most pixels are 0 or 255.
        // Render as alpha-test + A2C for smooth edges via MSAA.
        Cutout = 1,
        // True gradient alpha (lights, glows, FX) — many pixels with
        // partial alpha. Render with alpha-blend + ZWrite Off.
        Transparent = 2,
    }

    public sealed class TgaResult
    {
        public Texture2D Texture { get; init; } = null!;
        public bool HasAlpha { get; init; }
        public TgaAlphaKind AlphaKind { get; init; }
    }

    public static class TgaLoader
    {
        public static TgaResult? LoadFromFile(string path)
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return LoadFromBytes(bytes, Path.GetFileNameWithoutExtension(path));
        }

        public static TgaResult? LoadFromBytes(byte[] bytes, string textureName)
        {
            if (bytes.Length < 18)
            {
                throw new InvalidDataException("TGA file too short to contain a header");
            }

            var idLength = bytes[0];
            var imageType = bytes[2];
            var width = bytes[12] | (bytes[13] << 8);
            var height = bytes[14] | (bytes[15] << 8);
            var bitsPerPixel = bytes[16];
            var descriptor = bytes[17];

            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException(
                    $"TGA invalid dimensions: {width}x{height}");
            }
            if (bitsPerPixel != 24 && bitsPerPixel != 32)
            {
                throw new InvalidDataException(
                    $"TGA unsupported bpp: {bitsPerPixel} (need 24 or 32)");
            }

            var bytesPerPixel = bitsPerPixel / 8;
            var pixelDataOffset = 18 + idLength;

            var pixels = new Color32[width * height];
            switch (imageType)
            {
                case 2:
                    DecodeUncompressed(bytes, pixelDataOffset, pixels,
                        width, height, bytesPerPixel);
                    break;
                case 10:
                    DecodeRunLengthEncoded(bytes, pixelDataOffset, pixels,
                        width, height, bytesPerPixel);
                    break;
                default:
                    throw new InvalidDataException(
                        $"TGA unsupported image type: {imageType} (need 2 or 10)");
            }

            var topLeftOrigin = (descriptor & 0x20) != 0;
            if (topLeftOrigin)
            {
                FlipVertically(pixels, width, height);
            }

            var alphaKind = bytesPerPixel == 4
                ? ClassifyAlpha(pixels)
                : TgaAlphaKind.Opaque;
            var hasAlpha = alphaKind != TgaAlphaKind.Opaque;

            // Color-bleed preprocessing for cutout-style textures only.
            // Cutout materials use alpha-test; bilinear filtering on partial-
            // alpha edge texels pulls the artist's background color (often
            // blue) into the visible edge if RGB at alpha=0 is junk.
            // Transparent materials don't need bleed — their gradient is
            // intentional and uses real RGB across the whole texture.
            if (alphaKind == TgaAlphaKind.Cutout)
                BleedColorIntoTransparent(pixels, width, height, iterations: 4);

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true)
            {
                name = textureName,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);

            return new TgaResult
            {
                Texture = tex,
                HasAlpha = hasAlpha,
                AlphaKind = alphaKind,
            };
        }

        // Classify alpha distribution to pick the right render mode:
        // Opaque — no meaningful alpha=0 pixels (texture is solid)
        // Cutout — alpha is mostly binary (0 or 255). Foliage, fences,
        // grass. Renders with alpha-test + A2C.
        // Transparent — large fraction of pixels with partial alpha. True
        // gradient transparency (lights, glows, FX). Renders
        // with alpha-blend + ZWrite Off.
        private static TgaAlphaKind ClassifyAlpha(Color32[] pixels)
        {
            int zeroCount = 0, partialCount = 0;
            foreach (var p in pixels)
            {
                if (p.a == 0) zeroCount++;
                else if (p.a < 255) partialCount++;
            }
            int total = pixels.Length;
            int zeroThreshold = System.Math.Max(2, total / 10000);
            // No (or near-zero) transparent pixels → fully opaque.
            if (zeroCount < zeroThreshold) return TgaAlphaKind.Opaque;
            // Significant partial-alpha fraction → true gradient transparency.
            // Threshold 20% picks up FX_Licht (35-81% partial) but lets foliage
            // (typically <10% partial) stay as Cutout.
            if (partialCount * 100 >= total * 20) return TgaAlphaKind.Transparent;
            return TgaAlphaKind.Cutout;
        }

        // "Real" transparency means: at least a few pixels with alpha=0
        // (i.e. fully transparent edge/hole). This filters out the many Diggles
        // TGAs that have garbage in their alpha channel — gradient noise across
        // every pixel but no true transparent region. ~457 of the 2052 shipped
        // TGAs fall into that bucket; treating them as alpha-blend produces the
        // wall-flicker Z-fighting issue.
        //
        // Threshold: max(2, 0.01% of pixels). For a 1024-pixel TGA that's 2;
        // for a 65536-pixel TGA that's ~7. Catches artifact pixels (1-2 stray
        // zero-alpha pixels) without dropping real transparent textures.
        private static bool HasRealTransparency(Color32[] pixels)
        {
            int zeroCount = 0;
            foreach (var p in pixels)
            {
                if (p.a == 0) zeroCount++;
            }
            int threshold = System.Math.Max(2, pixels.Length / 10000);
            return zeroCount >= threshold;
        }

        // Replaces RGB of fully-transparent pixels (alpha=0) with the average
        // RGB of their alpha>0 neighbours. Repeats N times so the bleed
        // propagates several pixels into the transparent region. Alpha stays
        // unchanged so the texel is still transparent — only the RGB seen by
        // the bilinear filter changes.
        private static void BleedColorIntoTransparent(
            Color32[] pixels, int width, int height, int iterations)
        {
            // Mark which pixels were originally opaque (so we don't bleed
            // from a just-bled pixel of the current iteration; we want
            // bleeding to expand outward like a flood-fill, but we still
            // only sample from opaque-source data per iteration).
            var isOpaqueSource = new bool[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                isOpaqueSource[i] = pixels[i].a > 0;

            for (int iter = 0; iter < iterations; iter++)
            {
                var newOpaqueSource = new bool[pixels.Length];
                System.Array.Copy(isOpaqueSource, newOpaqueSource, pixels.Length);
                bool anyChanged = false;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = y * width + x;
                        if (isOpaqueSource[idx]) continue;

                        int r = 0, g = 0, b = 0, n = 0;
                        // 4-neighbour sampling
                        if (x > 0          && isOpaqueSource[idx - 1])     { var p = pixels[idx - 1];     r += p.r; g += p.g; b += p.b; n++; }
                        if (x < width - 1  && isOpaqueSource[idx + 1])     { var p = pixels[idx + 1];     r += p.r; g += p.g; b += p.b; n++; }
                        if (y > 0          && isOpaqueSource[idx - width]) { var p = pixels[idx - width]; r += p.r; g += p.g; b += p.b; n++; }
                        if (y < height - 1 && isOpaqueSource[idx + width]) { var p = pixels[idx + width]; r += p.r; g += p.g; b += p.b; n++; }

                        if (n > 0)
                        {
                            // Keep alpha=0; only replace RGB.
                            pixels[idx] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 0);
                            newOpaqueSource[idx] = true;
                            anyChanged = true;
                        }
                    }
                }
                isOpaqueSource = newOpaqueSource;
                if (!anyChanged) break;
            }
        }

        private static void DecodeUncompressed(
            byte[] src, int srcOffset, Color32[] dst,
            int width, int height, int bytesPerPixel)
        {
            var pixelCount = width * height;
            for (var i = 0; i < pixelCount; i++)
            {
                var pos = srcOffset + i * bytesPerPixel;
                var b = src[pos + 0];
                var g = src[pos + 1];
                var r = src[pos + 2];
                var a = bytesPerPixel == 4 ? src[pos + 3] : (byte)255;
                dst[i] = new Color32(r, g, b, a);
            }
        }

        private static void DecodeRunLengthEncoded(
            byte[] src, int srcOffset, Color32[] dst,
            int width, int height, int bytesPerPixel)
        {
            var pixelCount = width * height;
            var srcPos = srcOffset;
            var dstPos = 0;
            while (dstPos < pixelCount)
            {
                if (srcPos >= src.Length)
                {
                    throw new InvalidDataException("TGA RLE truncated");
                }
                var packetHeader = src[srcPos++];
                var runLength = (packetHeader & 0x7F) + 1;
                var isRun = (packetHeader & 0x80) != 0;
                if (isRun)
                {
                    var b = src[srcPos + 0];
                    var g = src[srcPos + 1];
                    var r = src[srcPos + 2];
                    var a = bytesPerPixel == 4 ? src[srcPos + 3] : (byte)255;
                    srcPos += bytesPerPixel;
                    var color = new Color32(r, g, b, a);
                    for (var i = 0; i < runLength && dstPos < pixelCount; i++)
                    {
                        dst[dstPos++] = color;
                    }
                }
                else
                {
                    for (var i = 0; i < runLength && dstPos < pixelCount; i++)
                    {
                        var b = src[srcPos + 0];
                        var g = src[srcPos + 1];
                        var r = src[srcPos + 2];
                        var a = bytesPerPixel == 4 ? src[srcPos + 3] : (byte)255;
                        srcPos += bytesPerPixel;
                        dst[dstPos++] = new Color32(r, g, b, a);
                    }
                }
            }
        }

        private static void FlipVertically(Color32[] pixels, int width, int height)
        {
            for (var y = 0; y < height / 2; y++)
            {
                var topStart = y * width;
                var botStart = (height - 1 - y) * width;
                for (var x = 0; x < width; x++)
                {
                    (pixels[topStart + x], pixels[botStart + x]) =
                        (pixels[botStart + x], pixels[topStart + x]);
                }
            }
        }
    }
}
