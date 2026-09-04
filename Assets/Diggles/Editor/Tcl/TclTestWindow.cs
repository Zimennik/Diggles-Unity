#nullable enable

namespace Diggles.Editor.Tcl
{
    using System.Text;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using UnityEditor;
    using UnityEngine;

    // Small interactive test harness for the embedded Tcl interpreter.
    public sealed class TclTestWindow : EditorWindow
    {
        const string Phase1Script = @"# Phase 1 acceptance test
set ANIM_LOOP 2
proc add {a b} { return [expr {$a + $b}] }
set y [add 10 5]
if {$y > 12} { puts ""y is big: $y"" } else { puts ""y is small"" }
foreach item {alpha beta gamma} { puts $item }
puts ""sum = [expr 3*4 + 2]""
";

        const string Phase2Script = @"# Phase 2 acceptance test — engine integration

# Declare a class with a default anim.
def_class Pilz food material 0 {} {
    class_defaultanim pilz.standard
    class_physic none
}

# Spawn an instance at a position.
set h [new Pilz ""my_pilz"" ""10 20 5"" ""0 0 0""]
puts ""spawned handle: $h""

# Read back the position.
puts ""pos: [get_pos $h]""

# Move it.
set_pos $h 100 200 50
puts ""new pos: [get_pos $h]""

# Velocity round-trip.
set_vel $h ""1 0 0""
puts ""vel: [get_vel $h]""

# random + irandom
puts ""rand: [random 100]""
puts ""irand: [irandom 10]""

# Log to Unity console
log ""Pilz instance $h is alive at [get_pos $h]""

# Cleanup
del $h
puts ""deleted""
";

        const string Phase3Script = @"# Phase 3 sanity — new features (array, upvar, switch, // comments, source)
// double-slash comment works now (Diggles uses it everywhere)

set lst {a b c d e}
foreach x $lst { puts ""item: $x"" }

# array
array set colors {red 1 green 2 blue 3}
puts ""array size: [array size colors]""
puts ""array exists: [array exists colors]""

# switch
proc kind {x} {
    switch $x {
        a { return ""alpha"" }
        b { return ""beta"" }
        default { return ""other"" }
    }
}
puts ""switch a: [kind a]""
puts ""switch z: [kind z]""

# upvar
proc setit {varname value} {
    upvar 1 $varname v
    set v $value
}
setit myvar ""hello""
puts ""upvar result: $myvar""
";

        bool engineMode = true;
        bool stubUnknown = false;
        string scriptPath = "";
        string script = Phase2Script;
        string output = "";
        Vector2 scrollIn, scrollOut;

        [MenuItem("Diggles/Tcl Test")]
        public static void Open()
        {
            var w = GetWindow<TclTestWindow>("Tcl Test");
            w.minSize = new Vector2(500, 500);
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(scriptPath))
                scriptPath = System.IO.Path.Combine(Diggles.Runtime.DigglesConfig.ConfiguredDataRoot,
                    "Scripts", "classes", "items", "calls", "takeitems.tcl").Replace('\\', '/');
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Tcl script", EditorStyles.boldLabel);
            scrollIn = EditorGUILayout.BeginScrollView(scrollIn, GUILayout.MinHeight(200));
            script = EditorGUILayout.TextArea(script, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Run", GUILayout.MaxWidth(80))) RunScript();
            engineMode = EditorGUILayout.ToggleLeft("Engine commands", engineMode, GUILayout.MaxWidth(140));
            stubUnknown = EditorGUILayout.ToggleLeft("Stub unknown", stubUnknown, GUILayout.MaxWidth(120));
            if (GUILayout.Button("Phase 1", GUILayout.MaxWidth(70))) script = Phase1Script;
            if (GUILayout.Button("Phase 2", GUILayout.MaxWidth(70))) script = Phase2Script;
            if (GUILayout.Button("Phase 3", GUILayout.MaxWidth(70))) script = Phase3Script;
            if (GUILayout.Button("Clear", GUILayout.MaxWidth(60))) output = "";
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            scriptPath = EditorGUILayout.TextField("Load file", scriptPath);
            if (GUILayout.Button("Load", GUILayout.MaxWidth(60)))
            {
                try
                {
                    script = System.IO.File.ReadAllText(scriptPath,
                        System.Text.Encoding.GetEncoding("ISO-8859-1"));
                }
                catch (System.Exception e) { output = $"[LOAD ERROR] {e.Message}"; }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            scrollOut = EditorGUILayout.BeginScrollView(scrollOut, GUILayout.MinHeight(150));
            EditorGUILayout.TextArea(output, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        void RunScript()
        {
            var interp = new TclInterpreter();
            if (engineMode) DigglesTclEngine.Register(interp);

            // Override `puts` to capture into our text area.
            var captured = new StringBuilder();
            interp.RegisterCommand("puts", (i, args) =>
            {
                int k = 1;
                if (k < args.Length && args[k] == "-nonewline") k++;
                if (k + 1 < args.Length && (args[k] == "stdout" || args[k] == "stderr")) k++;
                if (k < args.Length) captured.AppendLine(args[k]);
                return "";
            });
            if (engineMode)
            {
                interp.RegisterCommand("log", (i, args) =>
                {
                    var msg = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
                    captured.AppendLine($"[log] {msg}");
                    return "";
                });
            }

            // Stub mode: log every unknown-command call so we can see
            // what real Diggles scripts depend on. Return "0" by default
            // so boolean checks (`if {[some_cmd]}`) don't crash on empty
            // expressions.
            var unknownCalls = new System.Collections.Generic.Dictionary<string, int>();
            if (stubUnknown)
            {
                interp.UnknownCommandHandler = (i, args) =>
                {
                    string name = args[0];
                    if (!unknownCalls.ContainsKey(name)) unknownCalls[name] = 0;
                    unknownCalls[name]++;
                    return "0";
                };
            }

            try
            {
                string result = interp.Eval(script);
                output = captured.ToString();
                if (!string.IsNullOrEmpty(result))
                    output += $"\n[result] {result}";
            }
            catch (System.Exception e)
            {
                output = captured.ToString() + $"\n[ERROR] {e.Message}";
                Debug.LogException(e);
            }

            if (stubUnknown && unknownCalls.Count > 0)
            {
                output += "\n--- unknown commands stubbed ---\n";
                var sorted = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(unknownCalls);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var kv in sorted)
                    output += $"  {kv.Key} × {kv.Value}\n";
            }
        }
    }
}
