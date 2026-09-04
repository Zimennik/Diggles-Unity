#nullable enable

namespace Diggles.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using Diggles.Runtime.Tcl.Engine;

    public enum DocumentRenderBlockKind
    {
        Text,
        Image,
    }

    public enum DocumentBlockAlignment
    {
        Left,
        Center,
        Right,
    }

    public sealed class DocumentRenderBlock
    {
        public DocumentRenderBlockKind Kind { get; internal set; }
        public string RichText { get; internal set; } = "";
        public string ImagePath { get; internal set; } = "";
        public DocumentBlockAlignment Alignment { get; internal set; }
        public float Inset { get; internal set; }
    }

    // Converts the original ViWinLayout formatting stream into a small set of uGUI render blocks.
    // Text stays in TMP rich text (including native suN link ids); TGA directives become separate
    // image blocks so original data can be loaded directly instead of baking a sprite atlas.
    public static class DocumentMarkupParser
    {
        public static void Parse(
            string markup,
            DigglesTclContext context,
            List<DocumentRenderBlock> destination)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();

            var text = new StringBuilder(Math.Max(64, markup?.Length ?? 0));
            bool hasVisibleText = false;
            bool linkOpen = false;
            float imageInset = 0f;
            DocumentBlockAlignment alignment = DocumentBlockAlignment.Left;
            string source = markup ?? "";

            for (int index = 0; index < source.Length;)
            {
                if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == 'p')
                {
                    text.Append('\n');
                    hasVisibleText = true;
                    index += 2;
                    continue;
                }

                if (index + 1 < source.Length && source[index] == '/' && source[index + 1] == '(')
                {
                    int end = source.IndexOf(')', index + 2);
                    if (end >= 0)
                    {
                        string body = source.Substring(index + 2, end - index - 2);
                        foreach (string rawDirective in body.Split(','))
                        {
                            string directive = rawDirective.TrimStart();
                            if (directive.Length == 0) continue;

                            if (TryImage(directive, out string imagePath, out var imageAlignment))
                            {
                                FlushText(destination, text, ref hasVisibleText, alignment, ref linkOpen);
                                destination.Add(new DocumentRenderBlock
                                {
                                    Kind = DocumentRenderBlockKind.Image,
                                    ImagePath = imagePath,
                                    Alignment = imageAlignment,
                                    Inset = imageInset,
                                });
                                continue;
                            }

                            ApplyDirective(
                                directive,
                                context,
                                text,
                                ref hasVisibleText,
                                ref linkOpen,
                                ref imageInset,
                                ref alignment);
                        }
                        index = end + 1;
                        continue;
                    }
                }

                AppendEscaped(text, source[index]);
                hasVisibleText = true;
                index++;
            }

            FlushText(destination, text, ref hasVisibleText, alignment, ref linkOpen);
        }

        private static void ApplyDirective(
            string directive,
            DigglesTclContext context,
            StringBuilder text,
            ref bool hasVisibleText,
            ref bool linkOpen,
            ref float imageInset,
            ref DocumentBlockAlignment alignment)
        {
            if (directive == "ac")
            {
                alignment = DocumentBlockAlignment.Center;
                text.Append("<align=center>");
                return;
            }
            if (directive == "al")
            {
                alignment = DocumentBlockAlignment.Left;
                text.Append("<align=left>");
                return;
            }
            if (directive == "ar")
            {
                alignment = DocumentBlockAlignment.Right;
                text.Append("<align=right>");
                return;
            }
            if (directive == "ab")
            {
                alignment = DocumentBlockAlignment.Left;
                text.Append("<align=justified>");
                return;
            }

            if (TryNumberSuffix(directive, "fn", out float fontIndex))
            {
                text.Append("<size=").Append(FontSize((int)fontIndex).ToString(CultureInfo.InvariantCulture)).Append('>');
                return;
            }
            if (TryNumberSuffix(directive, "is", out float inset))
            {
                imageInset = inset;
                return;
            }
            if (TryNumberSuffix(directive, "ta", out float tab))
            {
                text.Append("<pos=").Append(tab.ToString("0.###", CultureInfo.InvariantCulture)).Append("px>");
                return;
            }
            if (TryNumberSuffix(directive, "tb", out float tabWidth))
            {
                text.Append("<space=").Append(tabWidth.ToString("0.###", CultureInfo.InvariantCulture)).Append("px>");
                return;
            }
            if (TryNumberSuffix(directive, "ml", out float marginLeft))
            {
                text.Append("<margin-left=").Append(marginLeft.ToString("0.###", CultureInfo.InvariantCulture)).Append("px>");
                return;
            }
            if (TryNumberSuffix(directive, "mr", out float marginRight))
            {
                text.Append("<margin-right=").Append(marginRight.ToString("0.###", CultureInfo.InvariantCulture)).Append("px>");
                return;
            }
            if (TryNumberSuffix(directive, "ls", out float lineSpacing))
            {
                text.Append("<line-height=")
                    .Append(Math.Max(70f, 100f + lineSpacing * 5f).ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("%>");
                return;
            }
            if (TryNumberSuffix(directive, "ws", out float characterSpacing))
            {
                text.Append("<cspace=").Append(characterSpacing.ToString("0.###", CultureInfo.InvariantCulture)).Append("px>");
                return;
            }
            if (TryNumberSuffix(directive, "bo", out float offset))
            {
                text.Append("<voffset=").Append(offset.ToString("0.###", CultureInfo.InvariantCulture)).Append("px>");
                return;
            }
            if (TryNumberSuffix(directive, "hp", out float paragraphGap))
            {
                int lines = Math.Max(1, (int)Math.Round(paragraphGap, MidpointRounding.AwayFromZero));
                text.Append('\n', lines);
                hasVisibleText = true;
                return;
            }
            if (TryNumberSuffix(directive, "su", out float linkIdValue))
            {
                int linkId = (int)linkIdValue;
                if (linkOpen)
                {
                    text.Append("</u></color></link>");
                    linkOpen = false;
                }
                if (linkId > 0)
                {
                    text.Append("<link=\"").Append(linkId.ToString(CultureInfo.InvariantCulture))
                        .Append("\"><color=#F0C85A><u>");
                    linkOpen = true;
                }
                return;
            }
            if (directive.StartsWith("tx", StringComparison.Ordinal))
            {
                AppendEscaped(text, directive.Substring(2));
                hasVisibleText = true;
                return;
            }
            if (directive.StartsWith("ccHealthbar", StringComparison.Ordinal))
            {
                AppendAttributeBar(text, context, directive, "ccHealthbar");
                hasVisibleText = true;
                return;
            }
            if (directive.StartsWith("ccAttribbar", StringComparison.Ordinal))
            {
                AppendAttributeBar(text, context, directive, "ccAttribbar");
                hasVisibleText = true;
                return;
            }
            if (directive.StartsWith("ccAttribrangebar", StringComparison.Ordinal))
            {
                AppendSkillRange(text, context, directive.Substring("ccAttribrangebar".Length).Trim());
                hasVisibleText = true;
                return;
            }
            if (directive.StartsWith("ccEditName", StringComparison.Ordinal))
            {
                string handle = directive.Substring("ccEditName".Length).Trim();
                if (context.GetEntity(handle) is { } entity)
                    AppendEscaped(text, string.IsNullOrEmpty(entity.InstanceName) ? entity.Class.Name : entity.InstanceName);
                hasVisibleText = true;
            }
        }

        private static bool TryImage(
            string directive,
            out string path,
            out DocumentBlockAlignment alignment)
        {
            path = "";
            alignment = DocumentBlockAlignment.Center;
            if (directive.StartsWith("ii", StringComparison.Ordinal))
            {
                path = directive.Substring(2).Trim();
                alignment = DocumentBlockAlignment.Center;
            }
            else if (directive.StartsWith("il", StringComparison.Ordinal))
            {
                path = directive.Substring(2).Trim();
                alignment = DocumentBlockAlignment.Left;
            }
            else if (directive.StartsWith("ir", StringComparison.Ordinal))
            {
                path = directive.Substring(2).Trim();
                alignment = DocumentBlockAlignment.Right;
            }
            return path.Length > 0;
        }

        private static void FlushText(
            List<DocumentRenderBlock> destination,
            StringBuilder text,
            ref bool hasVisibleText,
            DocumentBlockAlignment alignment,
            ref bool linkOpen)
        {
            if (linkOpen)
            {
                text.Append("</u></color></link>");
                linkOpen = false;
            }
            if (hasVisibleText)
            {
                destination.Add(new DocumentRenderBlock
                {
                    Kind = DocumentRenderBlockKind.Text,
                    RichText = text.ToString(),
                    Alignment = alignment,
                });
            }
            text.Clear();
            hasVisibleText = false;
        }

        private static void AppendAttributeBar(
            StringBuilder destination,
            DigglesTclContext context,
            string directive,
            string command)
        {
            string[] words = directive.Substring(command.Length).Trim()
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || context.GetEntity(words[0]) is not { } entity)
            {
                destination.Append("[----------]");
                return;
            }

            double ratio = AttributeRatio(context, entity, words[1]);
            int filled = (int)Math.Round(ratio * 10.0, MidpointRounding.AwayFromZero);
            destination.Append('[').Append('#', filled).Append('-', 10 - filled).Append(']');
        }

        private static void AppendSkillRange(StringBuilder destination, DigglesTclContext context, string handle)
        {
            if (context.GetEntity(handle) is not { } entity)
            {
                destination.Append("[skills]");
                return;
            }
            double maximum = 0.0;
            foreach (string name in context.Stats.Registry.DefinedInOrder)
            {
                if (name.StartsWith("exp_", StringComparison.Ordinal))
                    maximum = Math.Max(maximum, AttributeRatio(context, entity, name));
            }
            int filled = (int)Math.Round(maximum * 10.0,
                MidpointRounding.AwayFromZero);
            destination.Append('[').Append('#', filled).Append('-', 10 - filled).Append(']');
        }

        private static double AttributeRatio(
            DigglesTclContext context,
            DigglesTclEntity entity,
            string name)
        {
            double value = context.Stats.Get(entity.Stats, name);
            if (!context.Stats.Registry.TryGet(name, out var definition) || definition.Max <= definition.Min)
                return Math.Clamp(value, 0.0, 1.0);
            return Math.Clamp((value - definition.Min) / (definition.Max - definition.Min), 0.0, 1.0);
        }

        private static bool TryNumberSuffix(string directive, string prefix, out float value)
        {
            value = 0f;
            return directive.StartsWith(prefix, StringComparison.Ordinal) &&
                float.TryParse(directive.Substring(prefix.Length).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value);
        }

        private static int FontSize(int index) => index switch
        {
            <= 0 => 16,
            1 => 20,
            2 => 27,
            3 => 31,
            _ => 36,
        };

        private static void AppendEscaped(StringBuilder destination, string text)
        {
            foreach (char character in text) AppendEscaped(destination, character);
        }

        private static void AppendEscaped(StringBuilder destination, char character)
        {
            switch (character)
            {
                case '<': destination.Append("&lt;"); break;
                case '>': destination.Append("&gt;"); break;
                case '&': destination.Append("&amp;"); break;
                case '\u0091': case '\u0092': case '\u2018': case '\u2019': destination.Append('\''); break;
                case '\u0093': case '\u0094': case '\u201c': case '\u201d': destination.Append('"'); break;
                case '\u0096': case '\u0097': case '\u2013': case '\u2014': destination.Append('-'); break;
                case '\u0085': case '\u2026': destination.Append("..."); break;
                case '\u0095': case '\u2022': destination.Append('*'); break;
                default: destination.Append(character); break;
            }
        }
    }
}
