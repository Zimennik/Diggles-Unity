#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using Diggles.Runtime.World;
    using NUnit.Framework;

    public sealed class LevelGeneratorPresetTests
    {
        [Test]
        public void AddPreset_PlacesAreaOffsetRecordsFirstAndInCallOrder()
        {
            var (interpreter, _, generator) = BuildHost(200, 200);
            generator.AddSyntheticPiece("first.tcl", 8, 8);
            generator.AddSyntheticPiece("second.tcl", 8, 8);
            interpreter.Eval("lg_set_area 16 16 144 144");

            Assert.That(interpreter.Eval("lg_add_preset first.tcl 16 16"), Is.Empty);
            Assert.That(interpreter.Eval("lg_add_preset second.tcl 32 16"), Is.Empty);
            Assert.That(generator.MapW, Is.EqualTo(200),
                "map create dimensions must be wired before EmitTerrain/HasTpl exists");
            Assert.That(generator.MapH, Is.EqualTo(200));
            Assert.That(generator.ForcedRecordCount, Is.EqualTo(2));
            Assert.That(interpreter.Eval("lg_start"), Is.EqualTo("1"));

            Assert.That(generator.LastLevel.Count, Is.EqualTo(2));
            Assert.That(generator.LastLevel[0], Is.EqualTo(("first.tcl", 32, 32)));
            Assert.That(generator.LastLevel[1], Is.EqualTo(("second.tcl", 48, 32)));
        }

        [Test]
        public void AddPreset_SilentlySkipsInvalidMetadataPoseInsetAndStampedFootprint()
        {
            var (interpreter, _, generator) = BuildHost(100, 100);
            generator.AddSyntheticPiece("valid.tcl", 8, 8);
            int registeredBeforeMissing = generator.PieceCount;
            interpreter.Eval("lg_set_area 16 16 96 96");

            Assert.That(interpreter.Eval("lg_add_preset missing.tcl 16 16"), Is.Empty);
            Assert.That(generator.PieceCount, Is.EqualTo(registeredBeforeMissing),
                "a failed find-or-register must unregister its invalid metadata row");
            Assert.That(interpreter.Eval("lg_add_preset valid.tcl 1 16"), Is.Empty,
                "absolute (area + argument) pose is not 4-aligned");
            Assert.That(interpreter.Eval("lg_add_preset valid.tcl -12 16"), Is.Empty,
                "absolute x=4 is outside the eight-tile map inset");
            Assert.That(interpreter.Eval("lg_add_preset valid.tcl 68 16"), Is.Empty,
                "absX + width == map.x1 - 8 must fail the native strict upper bound");

            generator.TileBlocked = (x, y) => x == 48 && y == 32;
            generator.AddPreset("valid.tcl", 32, 16);
            Assert.That(generator.ForcedRecordCount, Is.Zero,
                "a stamped-template node anywhere in the footprint rejects the preset");
            generator.TileBlocked = null;

            Assert.That(interpreter.Eval("lg_add_preset valid.tcl 32 16"), Is.Empty);
            Assert.That(generator.ForcedRecordCount, Is.EqualTo(1));
            Assert.That(generator.ForcedRecords[0], Is.EqualTo(("valid.tcl", 32, 16)));
            Assert.Throws<TclException>(() =>
                interpreter.Eval("lg_add_preset valid.tcl 32"));
        }

        [Test]
        public void TpClear_ClearsPresetConnectionReservedAndAreaStateAcrossCycles()
        {
            var (interpreter, _, generator) = BuildHost(200, 200);
            generator.AddSyntheticPiece("preset.tcl", 8, 8);
            generator.SetTemplateGroup("PresetSet", new[] { "preset.tcl" });
            interpreter.Eval("lg_set_area 16 16 144 144");
            interpreter.Eval("lg_add_preset preset.tcl 16 16");
            generator.AddConnection(0, 1);
            generator.AddReservedRect(8, 8, 24, 24);

            Assert.That(generator.ForcedRecordCount, Is.EqualTo(1));
            Assert.That(generator.ConnectionCount, Is.EqualTo(1));
            Assert.That(generator.ReservedRectCount, Is.EqualTo(1));
            Assert.That(generator.PoolCount, Is.EqualTo(1));

            interpreter.Eval("lg_tp_clear");

            Assert.That(generator.ForcedRecordCount, Is.Zero);
            Assert.That(generator.ConnectionCount, Is.Zero);
            Assert.That(generator.ReservedRectCount, Is.Zero);
            Assert.That(generator.PoolCount, Is.Zero);
            interpreter.Eval("lg_add_preset preset.tcl 16 16");
            Assert.That(generator.ForcedRecordCount, Is.Zero,
                "FUN_00583650 also clears the area gate");

            AddCycle(interpreter, generator);
            Assert.That(generator.ForcedRecordCount, Is.EqualTo(1));
            interpreter.Eval("lg_tp_clear");
            AddCycle(interpreter, generator);
            Assert.That(generator.ForcedRecordCount, Is.EqualTo(1),
                "a clear/re-add cycle must replace, not double, preset records");
        }

        static void AddCycle(TclInterpreter interpreter, LevelGenerator generator)
        {
            interpreter.Eval("lg_tp_addtemplatesets PresetSet");
            interpreter.Eval("lg_set_area 16 16 144 144");
            interpreter.Eval("lg_add_preset preset.tcl 16 16");
            Assert.That(generator.PoolCount, Is.EqualTo(1));
        }

        static (TclInterpreter interpreter, DigglesTclContext context, LevelGenerator generator)
            BuildHost(int mapWidth, int mapHeight)
        {
            var interpreter = new TclInterpreter();
            DigglesTclContext context = DigglesTclEngine.Register(interpreter);
            LevelGenerator generator = context.LevelGen!;
            var mapBuilder = new RuntimeMapBuilder("", "");
            mapBuilder.CreateMap(mapWidth, mapHeight);
            context.MapBuilder = mapBuilder;
            generator.Rng = new System.Random(20260807);
            return (interpreter, context, generator);
        }
    }
}
