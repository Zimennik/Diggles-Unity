#nullable enable

namespace Diggles.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using Diggles.Runtime;
    using Diggles.Runtime.Save;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class OriginalSaveAiDocumentTests
    {
        [Test]
        public void ReadsEveryAvailableCurrentSaveIntoTypedAiDocuments()
        {
            string root = Path.Combine(OriginalDataTestFixture.DataRoot, "Gamesave");
            Assume.That(Directory.Exists(root),
                $"Original save fixture directory was not found at '{root}'.");
            string[] paths = Directory.GetFiles(root, "*.sav", SearchOption.AllDirectories);
            Assume.That(paths, Is.Not.Empty);

            int enabledOwners = 0;
            int presentInterpreters = 0;
            int variables = 0;
            var scripts = new Dictionary<string, int>();
            var codec = new OriginalSaveArchiveCodec();
            foreach (string path in paths)
            {
                OriginalSaveArchiveDocument document = codec.Read(path);
                Assert.That(document.Ai, Is.Not.Null, path);
                Assert.That(document.Ai!.Owners, Has.Count.EqualTo(8), path);
                OriginalSaveRuntimeSnapshot runtime = new OriginalSaveRuntimeStager().Stage(
                    new OriginalSaveLoadPlanner().Prepare(document));
                Assert.That(runtime.Ai.Owners, Has.Count.EqualTo(8), path);
                for (int owner = 0; owner < document.Ai.Owners.Count; owner++)
                {
                    OriginalSaveAiOwnerDocument state = document.Ai.Owners[owner];
                    OriginalSaveRuntimeAiOwnerSnapshot staged = runtime.Ai.Owners[owner];
                    Assert.That(state.Owner, Is.EqualTo(owner), path);
                    Assert.That(state.ScriptName.CopyBytesIncludingTerminator()[^1], Is.Zero, path);
                    Assert.That(state.InterpreterPresent,
                        Is.EqualTo(state.ScriptName.Text.Length != 0), path);
                    if (state.Enabled)
                        Assert.That(state.InterpreterPresent, Is.True, path);
                    if (state.Enabled) enabledOwners++;
                    if (state.InterpreterPresent) presentInterpreters++;
                    variables += state.Variables.Count;
                    scripts.TryGetValue(state.ScriptName.Text, out int count);
                    scripts[state.ScriptName.Text] = count + 1;
                    Assert.That(
                        state.Variables.Select(variable => variable.Name).Distinct().Count(),
                        Is.EqualTo(state.Variables.Count),
                        path);
                    Assert.That(staged.Owner, Is.EqualTo(state.Owner), path);
                    Assert.That(staged.Enabled, Is.EqualTo(state.Enabled), path);
                    Assert.That(staged.InterpreterPresent,
                        Is.EqualTo(state.InterpreterPresent), path);
                    Assert.That(staged.ScriptName, Is.EqualTo(state.ScriptName.Text), path);
                    Assert.That(staged.Variables, Is.EqualTo(state.Variables), path);
                }
            }

            TestContext.Out.WriteLine(
                $"enabled={enabledOwners}; interpreters={presentInterpreters}; variables={variables}");
            foreach (KeyValuePair<string, int> script in scripts.OrderBy(pair => pair.Key))
                TestContext.Out.WriteLine($"script='{script.Key}' count={script.Value}");
            Assert.That(paths.Length, Is.GreaterThanOrEqualTo(98));
        }

        [Test]
        public void FreshAiOwnersMatchNativeInitializationAndTclGlobals()
        {
            var root = new GameObject("FreshAiOwnerTest");
            try
            {
                DigglesEngine engine = new DigglesEngineFactory().Create(
                    OriginalDataTestFixture.DataRoot,
                    root.transform,
                    "BlockDB2.BDB");
                engine.LoadRuntimeDefinitions();
                for (int owner = 1; owner <= 4; owner++)
                {
                    engine.Interp.Eval($"ai init {owner} {AiSystem.NativeStandardScriptPath}");
                    engine.Interp.Eval($"ai enable {owner}");
                }

                AiSystem.SaveState state = engine.Ctx.Ai!.CaptureSaveState();
                for (int owner = 0; owner < state.Owners.Length; owner++)
                {
                    AiSystem.OwnerSaveState saved = state.Owners[owner];
                    Assert.That(saved.Initialized, Is.EqualTo(owner <= 4), $"owner {owner}");
                    Assert.That(saved.Enabled, Is.EqualTo(owner is >= 1 and <= 4), $"owner {owner}");
                    if (!saved.Initialized)
                    {
                        Assert.That(saved.VariableNames, Is.Empty, $"owner {owner}");
                        continue;
                    }

                    Assert.That(saved.VariableNames, Has.Length.EqualTo(158), $"owner {owner}");
                    var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int index = 0; index < saved.VariableNames.Length; index++)
                        variables.Add(saved.VariableNames[index], saved.VariableValues[index]);
                    Assert.That(variables["tcl_version"], Is.EqualTo("8.3"), $"owner {owner}");
                    Assert.That(variables["env"], Is.Empty, $"owner {owner}");
                    Assert.That(variables["tclDefaultLibrary"], Is.Empty, $"owner {owner}");
                    Assert.That(variables["tcl_patchLevel"], Is.EqualTo("8.3.2"), $"owner {owner}");
                    Assert.That(variables["tcl_platform"], Is.Empty, $"owner {owner}");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AiWriterOmitsUninitializedOwnerInterpreters()
        {
            var owners = new AiSystem.OwnerSaveState[8];
            for (int owner = 0; owner < owners.Length; owner++)
            {
                bool initialized = owner <= 4;
                owners[owner] = new AiSystem.OwnerSaveState
                {
                    Initialized = initialized,
                    Enabled = owner is >= 1 and <= 4,
                    VariableNames = initialized ? new[] { "tcl_version" } : Array.Empty<string>(),
                    VariableValues = initialized ? new[] { "8.3" } : Array.Empty<string>(),
                };
            }
            var state = new AiSystem.SaveState
            {
                Owners = owners,
                Notices = Array.Empty<AiSystem.DiplomacyNotice>(),
            };
            MethodInfo writeAi = typeof(OriginalSaveCurrentArchiveWriter).GetMethod(
                "WriteAi",
                BindingFlags.NonPublic | BindingFlags.Static) ??
                throw new AssertionException("AI writer method was not found.");

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.GetEncoding(1252), true))
                writeAi.Invoke(null, new object[] { writer, state });
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.GetEncoding(1252), true);
            for (int owner = 0; owner < owners.Length; owner++)
            {
                Assert.That(reader.ReadByte(), Is.EqualTo(owner is >= 1 and <= 4 ? 1 : 0));
                Assert.That(reader.ReadInt32(), Is.EqualTo(owner));
                string script = ReadNativeString(reader);
                int interpreterPresent = reader.ReadInt32();
                if (owner <= 4)
                {
                    Assert.That(script, Is.EqualTo(OriginalSaveAiDocument.NativeStandardScriptPath));
                    Assert.That(interpreterPresent, Is.EqualTo(1));
                    int variableCount = reader.ReadInt32();
                    Assert.That(variableCount, Is.EqualTo(1));
                    Assert.That(ReadNativeString(reader), Is.EqualTo("tcl_version"));
                    Assert.That(ReadNativeString(reader), Is.EqualTo("8.3"));
                }
                else
                {
                    Assert.That(script, Is.Empty);
                    Assert.That(interpreterPresent, Is.Zero);
                }
            }
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        }

        private static string ReadNativeString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            byte[] bytes = reader.ReadBytes(length);
            Assert.That(bytes, Has.Length.EqualTo(length));
            Assert.That(bytes[^1], Is.Zero);
            return Encoding.GetEncoding(1252).GetString(bytes, 0, bytes.Length - 1);
        }
    }
}
