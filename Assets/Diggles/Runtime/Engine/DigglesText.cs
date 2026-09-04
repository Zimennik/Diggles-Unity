#nullable enable

namespace Diggles.Runtime
{
    using System.IO;

    /// <summary>
    /// Encoding for the original-game text files, selected by the configured <see cref="Locale"/>.
    ///
    /// The original data is single-byte: en/de/fr/it/nl are Latin-1 (ISO-8859-1); the Russian
    /// release is Windows-1251 (Cyrillic). We decode manually (not Encoding.GetEncoding(1251),
    /// which is not guaranteed on IL2CPP without the code-pages provider). ASCII (0x00-0x7F) is
    /// identical in both, so non-localized code scripts are unaffected regardless of locale.
    ///
    /// Default is "en"/Latin-1 -> byte-for-byte the previous behaviour when no config sets a locale.
    /// Set once at startup from DigglesConfig.ResolveLocale; the Tcl `locale` command returns
    /// <see cref="Locale"/>, and the data-file loaders read via <see cref="ReadAllText"/>.
    /// </summary>
    public static class DigglesText
    {
        /// <summary>Current locale code (e.g. "en", "de", "ru"). Returned by the Tcl `locale` command.</summary>
        public static string Locale { get; private set; } = "en";

        static bool _cyrillic; // true => decode file bytes as Windows-1251

        public static void SetLocale(string? locale)
        {
            Locale = string.IsNullOrWhiteSpace(locale) ? "en" : locale!.Trim().ToLowerInvariant();
            // ru is the only Cyrillic locale Diggles ships (Windows-1251). en/de/fr/it/nl are Latin-1.
            // (A CP1250 language like pl/cs would need its own branch, but none is shipped.)
            _cyrillic = Locale == "ru";
        }

        /// <summary>
        /// Auto-detect the data set's locale from the suffix of its localized text files. Steam
        /// downloads exactly one locale's files (e.g. <c>Scripts/text/talk/commontalk_ru.tcl</c>) and
        /// re-downloads them when the language is switched, so the data itself is the source of truth.
        /// Returns "" if it can't tell (caller falls back to a default).
        /// </summary>
        public static string DetectLocale(string dataRoot)
        {
            if (string.IsNullOrEmpty(dataRoot)) return "";
            try
            {
                var dir = Path.Combine(dataRoot, "Scripts", "text", "talk");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "commontalk_*.tcl"))
                    {
                        var name = Path.GetFileNameWithoutExtension(f); // commontalk_<locale>
                        int us = name.LastIndexOf('_');
                        if (us >= 0 && us + 1 < name.Length)
                            return name.Substring(us + 1).ToLowerInvariant();
                    }
                }
            }
            catch { /* fs error — report no detection, caller defaults */ }
            return "";
        }

        /// <summary>Read a data file as text using the current locale's single-byte encoding.</summary>
        public static string ReadAllText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return _cyrillic ? DecodeCp1251(bytes) : DecodeLatin1(bytes);
        }

        // ISO-8859-1: every byte maps 1:1 to the same Unicode code point.
        static string DecodeLatin1(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        // Windows-1251 -> Unicode. 0x00-0x7F ASCII passthrough; 0xC0-0xFF is the contiguous
        // Cyrillic block (..) computed linearly; 0x80-0xBF via the table below.
        static string DecodeCp1251(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                int b = bytes[i];
                chars[i] = b < 0x80 ? (char)b
                    : b >= 0xC0 ? (char)(0x0410 + (b - 0xC0))
                    : (char)Cp1251_80toBF[b - 0x80];
            }
            return new string(chars);
        }

        // Windows-1251 code points for bytes 0x80..0xBF (numeric only — no char literals).
        static readonly ushort[] Cp1251_80toBF =
        {
            0x0402, 0x0403, 0x201A, 0x0453, 0x201E, 0x2026, 0x2020, 0x2021, // 80-87
            0x20AC, 0x2030, 0x0409, 0x2039, 0x040A, 0x040C, 0x040B, 0x040F, // 88-8F
            0x0452, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, // 90-97
            0x0098, 0x2122, 0x0459, 0x203A, 0x045A, 0x045C, 0x045B, 0x045F, // 98-9F
            0x00A0, 0x040E, 0x045E, 0x0408, 0x00A4, 0x0490, 0x00A6, 0x00A7, // A0-A7
            0x0401, 0x00A9, 0x0404, 0x00AB, 0x00AC, 0x00AD, 0x00AE, 0x0407, // A8-AF
            0x00B0, 0x00B1, 0x0406, 0x0456, 0x0491, 0x00B5, 0x00B6, 0x00B7, // B0-B7
            0x0451, 0x2116, 0x0454, 0x00BB, 0x0458, 0x0405, 0x0455, 0x0457, // B8-BF
        };
    }
}
