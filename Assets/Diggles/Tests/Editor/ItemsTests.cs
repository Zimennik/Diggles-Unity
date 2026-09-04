#nullable enable

namespace Diggles.Tests
{
    using Diggles.Runtime.Sim;
    using Diggles.Runtime.Tcl;
    using Diggles.Runtime.Tcl.Engine;
    using NUnit.Framework;
    using UnityEngine;

    // "Items & player commands": the original click→context-command layer
    // (Scripts/init/eventgen.tcl) running as-is on the de-stubbed natives, plus the
    // inventory/visibility semantics the pickup/putdown/storage chains need.
    //
    // Native ground truth cited per test:
    // • event_generator = TCL:CTclGlobalMethods::EventGenerator (FUN_0063e700): stores the
    // 4-param body globally ("param list needs 4 names");
    // • evtgen_attrib = TCL:CTclGlobalMethods::EvtGenAttrib (FUN_0063eb70): fills the pending
    // event spec at DAT_00772400+0xa134… (-evtid/-object/-subject1/-pos1/-cursor/-desc/…),
    // which the UI raises on the -object on a -command click (≡ set_event);
    // • InvAdd = FUN_0069bd30 (:132 clears the +0x3d0 bit-0 VISIBLE flag; containment and
    // render visibility are independent — lager.tcl:734-737 re-shows shelved items);
    // • InvRem = FUN_0069c0f0 (:97-99 drop pos defaults to the HOLDER's,:171-172 re-sets
    // the visible bit);
    // • SetVisibility/GetVisibility = FUN_0067cca0/FUN_0067cef0 (default = visible);
    // • SetProdEnabled/GetProdEnabled = FUN_006950a0/FUN_00694e80 (job-emission gate the
    // Lager toggles, lager.tcl:145/154/254/257/326/329; consumer FUN_005f0850:128).
    public sealed class ItemsTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        // Light host: engine + the real eventgen.tcl (sourced directly — in the game
        // classinit.tcl:111 does the same `call`) + minimal classes with the real kinds
        // (gnome/material/tool/transport/production/store) so the generator's
        // get_objtype/get_objclass/check_method dispatch is exercised verbatim.
        static (TclInterpreter interp, DigglesTclContext ctx) EvtGenHost()
        {
            var interp = new TclInterpreter();
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            interp.Eval("call scripts/init/eventgen.tcl");
            interp.Eval(@"
                def_class Zwerg flesh gnome 0 {} {}
                def_class Pilz food material 0 {} {}
                def_class Stein stone material 0 {} {}
                def_class Holzkiepe wood transport 0 {} {}
                def_class Feuerstelle wood production 0 {} {}
                def_class Lager wood store 0 {} {}
                def_class Heiltrank none material 0 {} { method use {user} {} }
            ");
            return (interp, ctx);
        }

        static int Spawn(TclInterpreter interp, string cls, string pos)
            => int.Parse(interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"").Trim());

        // ---- the eventgen decision table (representative classes) ----

        [Test]
        public void EvtGen_Pilz_PlainClick_Is_Harvest()
        {
            // eventgen.tcl:236-243: subclass Pilz + !alternate → evt_task_harvest -subject1, verb "dig".
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int p = Spawn(interp, "Pilz", "103 100 8");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, p, new Vector3(103, 100, 8), "-command");
            Assert.AreEqual("dig", verb.Trim());
            Assert.AreEqual("evt_task_harvest", ctx.EvtGen.EvtId);
            Assert.AreEqual(p.ToString(), ctx.EvtGen.Subject1);
            Assert.AreEqual(g, ctx.EvtGen.Object, "evtgen_attrib -object $objpar (eventgen.tcl:43) = raise target");
        }

        [Test]
        public void EvtGen_Stein_PlainClick_Is_Pickup()
        {
            // eventgen.tcl:245-304: subtype material, no special class → evt_task_pickup, verb "pickup".
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int s = Spawn(interp, "Stein", "101 100 8");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, s, new Vector3(101, 100, 8), "-command");
            Assert.AreEqual("pickup", verb.Trim());
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);
            Assert.AreEqual(s.ToString(), ctx.EvtGen.Subject1);
        }

        [Test]
        public void EvtGen_UsableItem_AlternateClick_Is_UseItem()
        {
            // eventgen.tcl:13: alternate + check_method <class> use → evt_task_useitem, verb "use".
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int h = Spawn(interp, "Heiltrank", "101 100 8");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, h, new Vector3(101, 100, 8), "-command -alternate");
            Assert.AreEqual("use", verb.Trim());
            Assert.AreEqual("evt_task_useitem", ctx.EvtGen.EvtId);
            Assert.AreEqual(h.ToString(), ctx.EvtGen.Subject1);
        }

        [Test]
        public void EvtGen_BoxedBuilding_Is_Pickup()
        {
            // eventgen.tcl:161-194: production subtype + get_boxed → evt_task_pickup — but the
            //:162 guard returns "select" first for an own target with neither -alternate nor
            // in-inventory. So: own crate plain-click = select; a world-owned crate (owner -1 ⇒
            // otherenemy,:53-57) or an alternate click picks it up.
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int f = Spawn(interp, "Feuerstelle", "102 100 8");
            // Spawn default is ownerless (-1, the CObj ctor law FUN_005c83f0:196) — assign the
            // player/crate owners the real init chains would, so the own-vs-world branches of
            // eventgen.tcl:53-57 actually diverge (with both at -1 every click read as "own").
            interp.Eval($"set_owner {g} 0");
            interp.Eval($"set_owner {f} 0");
            interp.Eval($"set_boxed {f} 1");
            Assert.AreEqual("select",
                DigglesTclEngine.EvtGenRun(ctx, g, f, new Vector3(102, 100, 8), "-command").Trim(),
                "own crate, plain click → select (eventgen.tcl:162)");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, f, new Vector3(102, 100, 8), "-command -alternate");
            Assert.AreEqual("pickup", verb.Trim(), "alternate click picks the crate up (:187-189)");
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);

            interp.Eval($"set_owner {f} -1");   // a crate lying in the world (no owner)
            string verb2 = DigglesTclEngine.EvtGenRun(ctx, g, f, new Vector3(102, 100, 8), "-command");
            Assert.AreEqual("pickup", verb2.Trim(), "world-owned crate, plain click → pickup");
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);
        }

        [Test]
        public void EvtGen_CarriedBoxedBuilding_PlacementAsprod_Is_Buildup()
        {
            // eventgen.tcl:165-179: boxed building in the gnome's inventory + -command -placement
            // + "asprod" in txtpar (+!alternate) → evt_task_buildup, verb "unpack". This is the
            // build-a-carried-crate path the placement UI drives.
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int f = Spawn(interp, "Feuerstelle", "102 100 8");
            interp.Eval($"set_boxed {f} 1");
            interp.Eval($"inv_add {g} {f}");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, f, new Vector3(104, 100, 8), "-command -placement asprod");
            Assert.AreEqual("unpack", verb.Trim());
            Assert.AreEqual("evt_task_buildup", ctx.EvtGen.EvtId);
            Assert.AreEqual("104 100 8", ctx.EvtGen.Pos1);

            // Same click without asprod → putdown (eventgen.tcl:176-178).
            string verb2 = DigglesTclEngine.EvtGenRun(ctx, g, f, new Vector3(104, 100, 8), "-command -placement");
            Assert.AreEqual("putdown", verb2.Trim());
            Assert.AreEqual("evt_task_putdown", ctx.EvtGen.EvtId);
        }

        [Test]
        public void EvtGen_OwnUnboxedBuilding_Is_Select_NoEvent()
        {
            // eventgen.tcl:161-162: own production building, plain click → "select", no event spec
            // (the original opens the building window; the port falls back to prod_cook for the
            // Feuerstelle until that window exists).
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int f = Spawn(interp, "Feuerstelle", "102 100 8");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, f, new Vector3(102, 100, 8), "-command");
            Assert.AreEqual("select", verb.Trim());
            Assert.AreEqual("", ctx.EvtGen.EvtId, "select must not leave a stale event spec");
            Assert.IsFalse(DigglesTclEngine.EvtGenFire(ctx, g));
        }

        [Test]
        public void EvtGen_OtherGnome_Alternate_Is_Walk()
        {
            // eventgen.tcl:143-159: own gnome target, alternate → evt_task_walk -pos1 (verb "walk");
            // plain → "select".
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int g2 = Spawn(interp, "Zwerg", "104 100 8");
            Assert.AreEqual("select",
                DigglesTclEngine.EvtGenRun(ctx, g, g2, new Vector3(104, 100, 8), "-command").Trim());
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, g2, new Vector3(104, 100, 8), "-command -alternate");
            Assert.AreEqual("walk", verb.Trim());
            Assert.AreEqual("evt_task_walk", ctx.EvtGen.EvtId);
            Assert.AreEqual("104 100 8", ctx.EvtGen.Pos1);
        }

        [Test]
        public void EvtGen_World_Is_Dig_Or_AlternateWalk()
        {
            // eventgen.tcl:132-139: subpar == 1 (the world object) → evt_task_dig -pos1;
            // alternate → walk. (The port's terrain clicks use the marquee/walk paths instead,
            // but the contract must hold for the native handle-1-is-world convention.)
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            string verb = DigglesTclEngine.EvtGenRun(ctx, g, 1, new Vector3(90, 100, 8), "-command");
            Assert.AreEqual("dig", verb.Trim());
            Assert.AreEqual("evt_task_dig", ctx.EvtGen.EvtId);
            string verb2 = DigglesTclEngine.EvtGenRun(ctx, g, 1, new Vector3(90, 100, 8), "-command -alternate");
            Assert.AreEqual("walk", verb2.Trim());
            Assert.AreEqual("evt_task_walk", ctx.EvtGen.EvtId);
        }

        [Test]
        public void EvtGen_Guards_Select_Standard_Multiselect_Trapped()
        {
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int s = Spawn(interp, "Stein", "101 100 8");
            // eventgen.tcl:17-23: no selection → select (over an object) / standard (over nothing).
            Assert.AreEqual("select", DigglesTclEngine.EvtGenRun(ctx, 0, s, default, "-command").Trim());
            Assert.AreEqual("standard", DigglesTclEngine.EvtGenRun(ctx, 0, 0, default, "-command").Trim());
            //:38-40: multiselect key → select.
            Assert.AreEqual("select", DigglesTclEngine.EvtGenRun(ctx, g, s, default, "-command -multiselect").Trim());
            //:63: a trapped gnome issues no commands.
            ctx.Entities[g].CurrentState = "trapped";
            Assert.AreEqual("", DigglesTclEngine.EvtGenRun(ctx, g, s, default, "-command").Trim());
            Assert.AreEqual("", ctx.EvtGen.EvtId);
        }

        [Test]
        public void EvtGenFire_EnqueuesTheSetEventShapedPayload()
        {
            // The UI consumption of the FUN_0063eb70 spec: raise EvtId on -object with the
            // payload keys the unchanged z_events.tcl handlers read (event_get this -subject1).
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int s = Spawn(interp, "Stein", "101 100 8");
            DigglesTclEngine.EvtGenRun(ctx, g, s, new Vector3(101, 100, 8), "-command");
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, g));
            var gnome = ctx.Entities[g];
            Assert.AreEqual(1, gnome.PendingEvents.Count);
            var evt = gnome.PendingEvents.Dequeue();
            Assert.AreEqual("evt_task_pickup", evt.Name);
            Assert.AreEqual(s.ToString(), evt.Payload!["-subject1"]);
            Assert.AreEqual("101 100 8", evt.Payload!["-pos1"]);
        }

        [Test]
        public void EventGenerator_Registrar_RequiresFourParamNames()
        {
            // FUN_0063e700:96-101 — "param list needs 4 names".
            var interp = new TclInterpreter();
            DigglesTclEngine.Register(interp);
            Assert.Throws<TclException>(() => interp.Eval("event_generator {a b c} {return x}"));
        }

        [Test]
        public void EvtGenAttrib_UnknownParameter_Errors()
        {
            // FUN_0063eb70:686 — "unknown parameter".
            var interp = new TclInterpreter();
            DigglesTclEngine.Register(interp);
            Assert.Throws<TclException>(() => interp.Eval("evtgen_attrib -bogus 1"));
        }

        // ---- visibility / containment semantics (the storage-shelf contract) ----

        [Test]
        public void Visibility_IsIndependentOfContainment_ShelfShape()
        {
            // lager.tcl store_item:734-744 = inv_add + set_visibility 1 + shelf set_pos +
            // set_instore 1: the item is contained-in-the-Lager yet rendered and queryable
            // with -flagpos {visible instore} (z_spare_procs.tcl:81).
            var (interp, ctx) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int l = Spawn(interp, "Lager", "110 100 8");
            int s = Spawn(interp, "Stein", "101 100 8");
            interp.Eval($"inv_add {l} {s}");
            Assert.AreEqual("0", interp.Eval($"get_visibility {s}").Trim(),
                "InvAdd clears the visible flag (FUN_0069bd30:132)");
            interp.Eval($"set_visibility {s} 1");
            interp.Eval($"set_pos {s} {{111 100 8}}");
            interp.Eval($"set_instore {s} 1");
            Assert.AreEqual("1", interp.Eval($"get_visibility {s}").Trim(),
                "set_visibility 1 must re-show a CONTAINED item (shelf display)");
            Assert.AreEqual(l, ctx.Entities[s].ContainedIn, "still contained in the Lager");
            string q = interp.Eval($"obj_query {g} \"-class Stein -flagpos {{visible instore}}\"");
            StringAssert.Contains(s.ToString(), q,
                "-flagpos instore must return Lager-shelved (contained) items");
        }

        [Test]
        public void GetVisibility_DefaultsVisible()
        {
            // Engine default = visible (CObj ctor flag word 0x01100101, bit 0 set); the old
            // port defaulted "0" — inverted.
            var (interp, _) = EvtGenHost();
            int s = Spawn(interp, "Stein", "101 100 8");
            Assert.AreEqual("1", interp.Eval($"get_visibility {s}").Trim());
        }

        [Test]
        public void InvRem_NoPos_DropsAtTheHolder()
        {
            // FUN_0069c0f0:97-99,173 — the drop position defaults to the holder's position
            // (retrieve_item lager.tcl:796 passes no pos and expects the item at the Lager).
            var (interp, ctx) = EvtGenHost();
            int l = Spawn(interp, "Lager", "110 100 8");
            int s = Spawn(interp, "Stein", "5 5 5");
            interp.Eval($"inv_add {l} {s}");
            interp.Eval($"inv_rem {l} {s}");
            var item = ctx.Entities[s];
            Assert.AreEqual(new Vector3(110, 100, 8), item.Position,
                "inv_rem without a pos must place the item at the holder");
            Assert.AreEqual(-1, item.ContainedIn);
            Assert.AreEqual("1", interp.Eval($"get_visibility {s}").Trim(),
                "InvRem re-sets the visible flag (FUN_0069c0f0:171-172)");
        }

        [Test]
        public void ProdEnabled_RoundTrip_DefaultEnabled()
        {
            // SetProdEnabled FUN_006950a0 / GetProdEnabled FUN_00694e80; default enabled —
            // the Lager toggles it from its storable scans (lager.tcl:145/154).
            var (interp, _) = EvtGenHost();
            int l = Spawn(interp, "Lager", "110 100 8");
            Assert.AreEqual("1", interp.Eval($"get_prod_enabled {l}").Trim(), "default = enabled");
            interp.Eval($"set_prod_enabled {l} 0");
            Assert.AreEqual("0", interp.Eval($"get_prod_enabled {l}").Trim());
            interp.Eval($"set_prod_enabled {l} 1");
            Assert.AreEqual("1", interp.Eval($"get_prod_enabled {l}").Trim());
        }

        [Test]
        public void IsWearingPannier_TransportItemInInventory()
        {
            // The native handler FUN_0069e930 → FUN_005cf820 answers 1 only when the carried
            // type-9 container's own inventory is non-empty (005cf820:53-55) — an empty worn
            // basket does not trigger the pannier walk (z_dignwalk.tcl:101-109). Port flat
            // model: content ⇔ items beyond ZwergBaseInvSlots. Full coverage lives in
            // InventoryPannierWalkTests; this pins the same law through the real-class host.
            var (interp, _) = EvtGenHost();
            int g = Spawn(interp, "Zwerg", "100 100 8");
            int k = Spawn(interp, "Holzkiepe", "101 100 8");
            int s = Spawn(interp, "Stein", "102 100 8");
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim());
            interp.Eval($"inv_add {g} {s}");
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(), "a Stein is not a pannier");
            interp.Eval($"inv_add {g} {k}");
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "an EMPTY worn basket must not trigger the pannier walk (FUN_005cf820:53-55)");
            int baseSlots = Diggles.Runtime.Tcl.Engine.DigglesTclEngine.ZwergBaseInvSlots;
            for (int n = 0; n < baseSlots - 1; n++)
                interp.Eval($"inv_add {g} {Spawn(interp, "Stein", "103 100 8")}");
            Assert.AreEqual("1", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "beyond base capacity the overflow lives in the basket → pannier walk");
            interp.Eval($"inv_rem {g} {k}");
            Assert.AreEqual("0", interp.Eval($"is_wearing_pannier {g}").Trim(),
                "no container carried → never the pannier walk");
        }

        [Test]
        public void GetNegBbox_NoModel_SafeZeroVector()
        {
            // from_wall (z_procs.tcl:431) does `lindex [get_negbbox $item] 2` — headless /
            // model-less must yield a number, not the old unknown-command "0" (whose lindex 2
            // is "").
            var (interp, _) = EvtGenHost();
            int s = Spawn(interp, "Stein", "101 100 8");
            Assert.AreEqual("0 0 0", interp.Eval($"get_negbbox {s}").Trim());
            Assert.AreEqual("0 0 0", interp.Eval($"get_posbbox {s}").Trim());
        }

        [Test]
        public void Lmsg_ResolvesTheTable_AndPassesUnknownKeysThrough()
        {
            // Native lmsg = locale text lookup in data/messages.txt, backed by GameLocalization.
            // The old passthrough survives as the fallback (unbound table / unknown key → the
            // key itself), so eventgen -cursor/-desc strings always read as words, never as the
            // bulk-stub literal "0".
            var (interp, _) = EvtGenHost();
            var previous = Diggles.Runtime.Localization.GameLocalization.Current;
            try
            {
                Diggles.Runtime.Localization.GameLocalization.Bind(null);
                Assert.AreEqual("pickup", interp.Eval("lmsg pickup").Trim());

                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "diggles-lmsg-test");
                System.IO.Directory.CreateDirectory(root);
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "messages.txt"),
                    "## \"pickup\"\r\nen \"Pick up\"\r\n");
                Diggles.Runtime.Localization.GameLocalization.Bind(
                    new Diggles.Runtime.Localization.GameLocalization(root));
                Assert.AreEqual("Pick up", interp.Eval("lmsg pickup").Trim());
                Assert.AreEqual("nosuchkey", interp.Eval("lmsg nosuchkey").Trim());
            }
            finally
            {
                Diggles.Runtime.Localization.GameLocalization.Bind(previous);
            }
        }
    }

    // The full pickup/putdown task chain against the real class load (WorkLoopTests.RealHost
    // pattern: txtinit/animinit/classinit → ~680 classes; classinit.tcl:111 sources the real
    // eventgen.tcl through the event_generator registrar). Heavy — run on demand.
    [Explicit]
    public sealed class ItemsWave5RealClassTests
    {
        private static string DataRoot => OriginalDataTestFixture.DataRoot;

        static (TclInterpreter interp, DigglesTclContext ctx, DigglesGameSim sim) RealHost()
        {
            var interp = new TclInterpreter { TolerateExprErrors = true, TolerateMissingVars = true };
            interp.UnknownCommandHandler = (i, a) => "0";
            var ctx = DigglesTclEngine.Register(interp);
            ctx.DataRoot = DataRoot;
            ctx.TolerateMissingEntities = true;
            ctx.TolerateMissingClass = true;
            ctx.TolerateMissingFiles = true;
            var sim = new DigglesGameSim(ctx);
            foreach (var s in new[] { "scripts/init/txtinit.tcl", "scripts/init/animinit.tcl", "scripts/init/classinit.tcl" })
                try { interp.Eval("call " + s); }
                catch (System.Exception e) { TestContext.WriteLine($"[init] {s}: {e.Message}"); }
            return (interp, ctx, sim);
        }

        static DigglesTclEntity Spawn(TclInterpreter interp, DigglesTclContext ctx, string cls, string pos)
        {
            string h = interp.Eval($"new {cls} \"\" \"{pos}\" \"0 0 0\"");
            return ctx.GetEntity(h)!;
        }

        [Test, Explicit]
        public void RealClasses_EventGenerator_IsRegisteredByClassinit()
        {
            var (interp, ctx, _) = RealHost();
            Assert.IsTrue(ctx.Classes.ContainsKey("Zwerg"), "class load failed");
            Assert.IsTrue(interp.CommandExists(DigglesTclEngine.EvtGenProcName),
                "classinit.tcl:111 `call scripts/init/eventgen.tcl` must register the generator body");
        }

        [Test, Explicit]
        public void RealClasses_DecisionTable_Pilz_Stein_BoxedBuilding()
        {
            var (interp, ctx, _) = RealHost();
            var g = Spawn(interp, ctx, "Zwerg", "100 100 8");
            var p = Spawn(interp, ctx, "Pilz", "103 100 8");
            var s = Spawn(interp, ctx, "Stein", "101 100 8");
            var f = Spawn(interp, ctx, "Feuerstelle", "105 100 8");
            interp.Eval($"set_boxed {f.Handle} 1");
            interp.Eval($"set_owner {f.Handle} -1");   // a crate lying in the world (the:162 own-select guard)

            Assert.AreEqual("dig",
                DigglesTclEngine.EvtGenRun(ctx, g.Handle, p.Handle, p.Position, "-command").Trim(),
                "Pilz → harvest verb (eventgen.tcl:236-243)");
            Assert.AreEqual("evt_task_harvest", ctx.EvtGen.EvtId);

            Assert.AreEqual("pickup",
                DigglesTclEngine.EvtGenRun(ctx, g.Handle, s.Handle, s.Position, "-command").Trim(),
                "Stein → pickup (eventgen.tcl:245-304)");
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);

            Assert.AreEqual("pickup",
                DigglesTclEngine.EvtGenRun(ctx, g.Handle, f.Handle, f.Position, "-command").Trim(),
                "world-owned boxed building → pickup (eventgen.tcl:164,187-194)");
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId);
        }

        // Click-pickup end-to-end: eventgen decides evt_task_pickup; the unchanged
        // z_events.tcl evt_task_pickup_proc (:406-433) clears the tasklist, runs pickup
        // (z_procs.tcl:446-489 → walk_near_item/rotate_towards/play_anim_pickup/take_item)
        // through `state task` (zwerg.tcl:322-333), and take_item (z_procs.tcl:592) beams the
        // item into inventory via native InvAdd. Headless "carried" = ContainedIn == gnome +
        // visible flag cleared. The Stein sits 0.5 from the gnome so the approach short-circuits
        // (walk_near_item first exit: dist−radius ≤ tolerance) — no Play-mode walk needed.
        [Test, Explicit]
        public void Pickup_DriveLoop_SteinEndsCarried()
        {
            var (interp, ctx, sim) = RealHost();
            var g = Spawn(interp, ctx, "Zwerg", "100 100 8");
            var s = Spawn(interp, ctx, "Stein", "100.5 100 8");
            interp.Eval($"set_worktime {g.Handle} 0 12");
            for (int i = 0; i < 10; i++) sim.Step(0.1);   // settle into idle

            string verb = DigglesTclEngine.EvtGenRun(ctx, g.Handle, s.Handle, s.Position, "-command");
            Assert.AreEqual("evt_task_pickup", ctx.EvtGen.EvtId, $"eventgen verb was '{verb}'");
            Assert.IsTrue(DigglesTclEngine.EvtGenFire(ctx, g.Handle));

            bool sawTask = false;
            for (int i = 0; i < 80 && s.ContainedIn != g.Handle; i++)
            {
                sim.Step(0.1);
                if (g.CurrentState == "task") sawTask = true;
                if (i % 8 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} tasklist={g.TaskList.Count} containedIn={s.ContainedIn}");
            }
            TestContext.WriteLine($"RESULT pickup: sawTask={sawTask} containedIn={s.ContainedIn} inv=[{string.Join(",", g.Inventory)}]");
            Assert.IsTrue(sawTask, "gnome never entered the task state after evt_task_pickup");
            Assert.AreEqual(g.Handle, s.ContainedIn, "the Stein never ended up carried (beamto_inv → inv_add)");
            Assert.IsTrue(g.Inventory.Contains(s.Handle));
            Assert.AreEqual("0", interp.Eval($"get_visibility {s.Handle}").Trim(),
                "a carried item is hidden (InvAdd clears the visible bit)");
            Assert.AreEqual(g.Owner, s.Owner, "beamto_inv transfers ownership (z_procs.tcl:283)");
        }

        // Putdown at a position: evt_task_putdown (z_events.tcl:735-812) → putdown_tasklist
        // (z_procs.tcl:703-733, pos branch) → walk_near_item/rotate_towards/benda/
        // beam_from_inv_to_pos/bendb. beam_from_inv_to_pos (z_procs.tcl:292-340) inv_rem's at
        // the target + set_posbottom + set_visibility 1 + set_hoverable 1. Target 0.6 away so
        // both the approach (radius 0.7) and the ≤2.0 beam gate pass without a real walk.
        [Test, Explicit]
        public void Putdown_DriveLoop_SteinPlacedVisibleAtTarget()
        {
            var (interp, ctx, sim) = RealHost();
            var g = Spawn(interp, ctx, "Zwerg", "100 100 8");
            var s = Spawn(interp, ctx, "Stein", "100.5 100 8");
            interp.Eval($"set_worktime {g.Handle} 0 12");
            for (int i = 0; i < 10; i++) sim.Step(0.1);

            // carry it first (the proven pickup path)
            DigglesTclEngine.EvtGenRun(ctx, g.Handle, s.Handle, s.Position, "-command");
            DigglesTclEngine.EvtGenFire(ctx, g.Handle);
            for (int i = 0; i < 80 && s.ContainedIn != g.Handle; i++) sim.Step(0.1);
            Assert.AreEqual(g.Handle, s.ContainedIn, "precondition: pickup must complete");

            // then the player putdown order (the inventory-window / placement click shape:
            // -subject1 item -pos1 the drop spot — z_events.tcl:731-734).
            ctx.This = g;
            interp.Eval($"set_event this evt_task_putdown -subject1 {s.Handle} -pos1 {{100.6 100 8}}");
            ctx.This = null;

            for (int i = 0; i < 100 && s.ContainedIn != -1; i++)
            {
                sim.Step(0.1);
                if (i % 8 == 0)
                    TestContext.WriteLine($"step {i}: state={g.CurrentState} tasklist={g.TaskList.Count} containedIn={s.ContainedIn}");
            }
            TestContext.WriteLine($"RESULT putdown: containedIn={s.ContainedIn} pos={s.Position} vis={interp.Eval($"get_visibility {s.Handle}").Trim()}");
            Assert.AreEqual(-1, s.ContainedIn, "the Stein never left the inventory");
            Assert.IsFalse(g.Inventory.Contains(s.Handle));
            Assert.AreEqual("1", interp.Eval($"get_visibility {s.Handle}").Trim(),
                "beam_from_inv_to_pos restores visibility (z_procs.tcl:324)");
            Assert.AreEqual(100.6f, s.Position.x, 0.05f, "placed at the ordered -pos1");
            Assert.AreEqual(100f, s.Position.y, 0.05f);
        }
    }
}
