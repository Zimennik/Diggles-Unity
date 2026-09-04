#nullable enable

namespace Diggles.Runtime.Tcl.Engine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using Diggles.Runtime.Diagnostics;
    using Diggles.Runtime.Tcl;

    public sealed class DocumentWindowLink
    {
        public int Id { get; internal set; }
        public string Target { get; internal set; } = "";
        public string RightTarget { get; internal set; } = "";
        public bool ExecutesTcl { get; internal set; }

    }

    // Context-owned model for the original ViWinLayout document surface. The layout dispatcher is
    // FUN_005154c0 and textwin is FUN_00531c60. A document script is evaluated with DAT_007723f4
    // pointing at its target window; layout commands outside that evaluation fail with
    // "no window open". Link record zero is a sentinel, so the first usable link id is one.
    public sealed class DocumentWindowSystem
    {
        private const int MaxLinks = 65_535;

        private static readonly Dictionary<string, string> InfoScripts = new(StringComparer.Ordinal)
        {
            ["gnomes"] = "info_gnomes.tcl",
            ["prods"] = "info_prods.tcl",
            ["pedigree"] = "info_stammbaum.tcl",
            ["questlog"] = "diary_15.tcl",
        };

        private readonly DigglesTclContext _context;
        private readonly StringBuilder _markup = new();
        private readonly List<DocumentWindowLink?> _links = new() { null };
        private int _layoutDepth;

        public DocumentWindowSystem(DigglesTclContext context) =>
            _context = context ?? throw new ArgumentNullException(nameof(context));

        public bool IsOpen { get; private set; }
        public bool Visible { get; private set; }
        public string CurrentScript { get; private set; } = "";
        public string Markup => _markup.ToString();
        public IReadOnlyList<DocumentWindowLink?> Links => _links;
        public int Version { get; private set; }
        public bool HasActiveLayout => _layoutDepth > 0;

        public bool TryGetLink(int id, out DocumentWindowLink link)
        {
            link = id > 0 && id < _links.Count ? _links[id]! : null!;
            return link != null;
        }

        public void Close()
        {
            if (!IsOpen && !Visible) return;
            IsOpen = false;
            Visible = false;
            CurrentScript = "";
            _markup.Clear();
            ResetLinks();
            MirrorLegacyVisibility();
            Version++;
        }

        private void Show()
        {
            if (!IsOpen || Visible) return;
            Visible = true;
            MirrorLegacyVisibility();
            Version++;
        }

        public bool Click(int id, bool rightButton = false)
        {
            if (!TryGetLink(id, out var link)) return false;
            string target = rightButton ? link.RightTarget : link.Target;
            if (string.IsNullOrWhiteSpace(target)) return false;

            if (link.ExecutesTcl)
            {
                WithActiveLayout(() => _context.Interp.Eval(target));
                Version++;
            }
            else
            {
                Run(target);
            }
            return true;
        }

        private void Run(string scriptName)
        {
            if (string.IsNullOrWhiteSpace(scriptName))
            {
                Close();
                return;
            }

            string resolved = ResolveDocumentScript(scriptName);
            IsOpen = true;
            Visible = true;
            CurrentScript = resolved;
            ClearContent();
            MirrorLegacyVisibility();

            try
            {
                WithActiveLayout(() => _context.Interp.Eval(
                    TclList.Format(new[] { "call", resolved })));
            }
            catch
            {
                Close();
                throw;
            }
            Version++;
        }

        private void OpenInfo(string mode)
        {
            mode ??= "";
            if (!InfoScripts.TryGetValue(mode, out string script))
            {
                Close();
                return;
            }
            Run(script);
        }

        private void Reload()
        {
            RequireActiveLayout();
            string script = CurrentScript;
            if (string.IsNullOrEmpty(script)) return;
            ClearContent();
            WithActiveLayout(() => _context.Interp.Eval(
                TclList.Format(new[] { "call", script })));
            Version++;
        }

        public SaveState Capture()
        {
            var links = new LinkSaveState[Math.Max(0, _links.Count - 1)];
            for (int i = 1; i < _links.Count; i++)
            {
                var link = _links[i];
                links[i - 1] = link == null
                    ? new LinkSaveState { Id = i, Present = false }
                    : new LinkSaveState
                    {
                        Id = link.Id,
                        Present = true,
                        Target = link.Target,
                        RightTarget = link.RightTarget,
                        ExecutesTcl = link.ExecutesTcl,
                    };
            }
            return new SaveState
            {
                IsOpen = IsOpen,
                Visible = Visible,
                CurrentScript = CurrentScript,
                Markup = Markup,
                Links = links,
            };
        }

        public void Restore(SaveState state)
        {
            ValidateSaveState(state);
            IsOpen = state.IsOpen;
            Visible = state.Visible;
            CurrentScript = state.CurrentScript;
            _markup.Clear();
            _markup.Append(state.Markup);
            ResetLinks();
            foreach (var saved in state.Links)
            {
                while (_links.Count <= saved.Id) _links.Add(null);
                if (saved.Present)
                {
                    _links[saved.Id] = new DocumentWindowLink
                    {
                        Id = saved.Id,
                        Target = saved.Target,
                        RightTarget = saved.RightTarget,
                        ExecutesTcl = saved.ExecutesTcl,
                    };
                }
            }
            MirrorLegacyVisibility();
            Version++;
        }

        public static void ValidateSaveState(SaveState? state)
        {
            if (state == null || state.CurrentScript == null || state.Markup == null || state.Links == null)
                throw new ArgumentException("Document-window save state is invalid.", nameof(state));
            if (!state.IsOpen && (state.Visible || state.CurrentScript.Length != 0 || state.Markup.Length != 0))
                throw new ArgumentException("Closed document-window state contains live content.", nameof(state));
            if (state.Links.Length > MaxLinks)
                throw new ArgumentException("Document-window link table is too large.", nameof(state));

            if (!state.IsOpen && state.Links.Length != 0)
                throw new ArgumentException("Closed document-window state contains live links.", nameof(state));

            for (int index = 0; index < state.Links.Length; index++)
            {
                var link = state.Links[index];
                if (link == null || link.Id != index + 1 || link.Target == null ||
                    link.RightTarget == null || (link.Present && string.IsNullOrEmpty(link.Target)))
                    throw new ArgumentException("Document-window link table is invalid.", nameof(state));
            }
        }

        public static void RegisterCommands(DigglesTclContext context, TclInterpreter interpreter)
        {
            interpreter.RegisterCommand("layout", (_, args) => context.Documents.ExecuteLayout(args));
            interpreter.RegisterCommand("textwin", (_, args) => context.Documents.ExecuteTextWindow(args));
        }

        private string ExecuteLayout(string[] args)
        {
            if (args.Length < 2)
                throw WrongArgs("layout subcommand ?arg ...?");

            switch (args[1])
            {
                case "present":
                    RequireExact(args, 2, "layout present");
                    return HasActiveLayout ? "1" : "0";

                case "log":
                    if (args.Length > 2)
                        DigglesDebugLog.Log(string.Join(" ", args, 2, args.Length - 2));
                    return "";

                case "clear":
                    RequireExact(args, 2, "layout clear");
                    RequireActiveLayout();
                    ClearContent();
                    Version++;
                    return "";

                case "print":
                    RequireActiveLayout();
                    for (int i = 2; i < args.Length; i++) _markup.Append(args[i]);
                    Version++;
                    return "";

                case "addlink":
                    RequireExact(args, 3, "layout addlink target");
                    RequireActiveLayout();
                    return AddLink(args[2], "", executesTcl: false).ToString(CultureInfo.InvariantCulture);

                case "autolink":
                    return AutoLink(args, executesTcl: false);

                case "addxlink":
                    RequireExact(args, 3, "layout addxlink target");
                    RequireActiveLayout();
                    return AddLink(args[2], "", executesTcl: true).ToString(CultureInfo.InvariantCulture);

                case "autoxlink":
                    return AutoLink(args, executesTcl: true);

                case "reload":
                    RequireExact(args, 2, "layout reload");
                    Reload();
                    return "";

                default:
                    throw new TclException($"bad sub-command \"{args[1]}\": must be present, log, clear, print, addlink, autolink, addxlink, autoxlink, or reload");
            }
        }

        private string ExecuteTextWindow(string[] args)
        {
            if (args.Length < 2)
                throw WrongArgs("textwin subcommand ?arg ...?");

            switch (args[1])
            {
                case "show":
                    RequireExact(args, 2, "textwin show");
                    Show();
                    return "";
                case "hide":
                    RequireExact(args, 2, "textwin hide");
                    Close();
                    return "";
                case "run":
                    RequireExact(args, 3, "textwin run scriptname");
                    Run(args[2]);
                    return "";
                case "info":
                    if (args.Length > 3) throw WrongArgs("textwin info ?mode?");
                    if (args.Length == 2) Close();
                    else OpenInfo(args[2]);
                    return "";
                case "deflink":
                    RequireExact(args, 4, "textwin deflink id scriptname");
                    RequireActiveLayout();
                    DefineLink(ParsePositiveInt(args[2]), args[3]);
                    return "";
                default:
                    return ExecuteLayout(args);
            }
        }

        private string AutoLink(string[] args, bool executesTcl)
        {
            if (args.Length < 4 || args.Length > 5)
                throw WrongArgs($"layout {(executesTcl ? "autoxlink" : "autolink")} target text ?rmbtarget?");
            RequireActiveLayout();
            int id = AddLink(args[2], args.Length == 5 ? args[4] : "", executesTcl);
            return $"/(su{id.ToString(CultureInfo.InvariantCulture)}){args[3]}/(su0)";
        }

        private int AddLink(string target, string rightTarget, bool executesTcl)
        {
            if (_links.Count > MaxLinks)
                throw new TclException("document-window link table is full");
            int id = _links.Count;
            _links.Add(new DocumentWindowLink
            {
                Id = id,
                Target = target,
                RightTarget = rightTarget,
                ExecutesTcl = executesTcl,
            });
            Version++;
            return id;
        }

        private void DefineLink(int id, string target)
        {
            while (_links.Count <= id) _links.Add(null);
            _links[id] = new DocumentWindowLink
            {
                Id = id,
                Target = target,
                ExecutesTcl = false,
            };
            Version++;
        }

        private void ClearContent()
        {
            _markup.Clear();
            ResetLinks();
        }

        private void ResetLinks()
        {
            _links.Clear();
            _links.Add(null);
        }

        private void RequireActiveLayout()
        {
            if (!HasActiveLayout) throw new TclException("no window open");
        }

        private void WithActiveLayout(Action action)
        {
            _layoutDepth++;
            try { action(); }
            finally { _layoutDepth--; }
        }

        private string ResolveDocumentScript(string scriptName)
        {
            string normalized = scriptName.Replace('\\', '/').Trim();
            if (normalized.IndexOf('/') >= 0) return normalized;
            return $"data/scripts/text/doc/{Diggles.Runtime.DigglesText.Locale}/{normalized}";
        }

        private void MirrorLegacyVisibility()
        {
            if (_context.Dialogue != null) _context.Dialogue.TextWindowVisible = Visible;
        }

        private static int ParsePositiveInt(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ||
                result <= 0 || result > MaxLinks)
                throw new TclException($"expected positive integer but got \"{value}\"");
            return result;
        }

        private static void RequireExact(string[] args, int count, string usage)
        {
            if (args.Length != count) throw WrongArgs(usage);
        }

        private static TclException WrongArgs(string usage) =>
            new($"wrong # args: should be \"{usage}\"");

        [Serializable]
        public sealed class SaveState
        {
            public bool IsOpen;
            public bool Visible;
            public string CurrentScript = "";
            public string Markup = "";
            public LinkSaveState[] Links = Array.Empty<LinkSaveState>();
        }

        [Serializable]
        public sealed class LinkSaveState
        {
            public int Id;
            public bool Present;
            public string Target = "";
            public string RightTarget = "";
            public bool ExecutesTcl;
        }
    }
}
