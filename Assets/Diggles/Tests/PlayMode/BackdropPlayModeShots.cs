#nullable enable

namespace Diggles.Tests.PlayMode
{
    using System.Collections;
    using System.IO;
    using Diggles.Runtime;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.TestTools;
    using VContainer;
    using VContainer.Unity;

    // PLAY-mode VISUAL verification of the panorama backdrop (report:
    // vertical strip stacks, floating carpet, earth frame). Boots the real campaign like
    // CampaignPlayModeTests, poses the real DigglesCameraController at the report's three
    // camera poses and captures actual rendered frames to PNG for eyeball comparison.
    //
    // MUST run WITH graphics (batchmode WITHOUT -nographics) — WaitForEndOfFrame/capture
    // need a real device:
    // Unity.exe -batchmode -projectPath <proj> -runTests -testPlatform PlayMode \
    // -testFilter "Diggles.Tests.PlayMode.BackdropPlayModeShots"...
    public class BackdropPlayModeShots
    {
        const string ScenePath = "Assets/Diggles/Scenes/RuntimeWorldGeneration.unity";

        static string OutDir =>
            System.Environment.GetEnvironmentVariable("DIGGLES_SHOT_DIR")
            ?? Path.Combine(Application.temporaryCachePath, "backdrop_shots");

        DigglesWorldRuntime _runtime = null!;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Time.captureDeltaTime = 0.025f;
            LogAssert.ignoreFailingMessages = true;

            SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
            yield return null;

            _runtime = Object.FindFirstObjectByType<DigglesWorldRuntime>();
            Assert.IsNotNull(_runtime, "scene must contain a DigglesWorldRuntime");
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "menu world never became ready");
        }

        IEnumerator BootCampaign()
        {
            _runtime.campaignIntroCutscene = "";
            _runtime.NewGame();
            yield return null;
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "campaign world never ready after NewGame");
            _runtime.Context!.TriggerService!.SuppressSequence("urw_3");
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            Time.captureDeltaTime = 0f;
        }

        IEnumerator WaitUntil(System.Func<bool> cond, int maxFrames, string failMsg)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                if (cond()) yield break;
                yield return null;
            }
            Assert.Fail(failMsg);
        }

        // The MENU diorama with its own scripted camera — the player's view:
        // the diorama's rear cutout (block-59 bowl at ~x28-36/y38-42) against the zone
        // backdrop. No pose overrides: whatever the menu sequence frames is what the player sees.
        [UnityTest]
        public IEnumerator CaptureMenuDiorama()
        {
            Directory.CreateDirectory(OutDir);
            for (int i = 0; i < 60; i++) yield return null;    // let the menu cinematic settle
            yield return Capture("menu_diorama_a.png");
            for (int i = 0; i < 200; i++) yield return null;   // a later beat of the pan
            yield return Capture("menu_diorama_b.png");
        }

        // The third case: CAVES with rear cutouts, which only exist in a
        // played save. Loads the tutorial save, then finds interior load-time-59 clusters in
        // the world data (air clusters NOT connected to the sky bowl = authored rear cutouts)
        // and captures the two largest with the gameplay camera aimed at their centroids.
        [UnityTest]
        public IEnumerator CaptureTutorialSaveCaveCutouts()
        {
            yield return BootCampaign();
            var scope = Object.FindFirstObjectByType<LifetimeScope>();
            Assert.IsNotNull(scope, "scene must contain the game lifetime scope");
            var loader = scope!.Container.Resolve<Diggles.Runtime.Save.IOriginalSaveGameLoadService>();
            loader.LoadGame("save_tutorial");
            yield return null;
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "tutorial save world never became ready");
            for (int i = 0; i < 60; i++) yield return null;

            Directory.CreateDirectory(OutDir);
            var terr = _runtime.Context!.Terrain!;
            int W = terr.Width, H = terr.Height;
            bool Air(int tx, int ty) =>
                tx >= 0 && ty >= 0 && tx < W && ty < H
                && terr.BlockIdx[ty * W + tx] == 59 && !terr.RendersTerrain(tx, ty);

            // Cluster the air tiles (4-connected); the cluster containing the shallowest air
            // row is the SKY BOWL — every other cluster is an interior rear cutout.
            var cluster = new int[W * H];
            var sizes = new System.Collections.Generic.List<int> { 0 };   // id 0 = unvisited
            var sumX = new System.Collections.Generic.List<long> { 0 };
            var sumY = new System.Collections.Generic.List<long> { 0 };
            var minTy = new System.Collections.Generic.List<int> { int.MaxValue };
            var stack = new System.Collections.Generic.Stack<(int x, int y)>();
            for (int ty = 0; ty < H; ty++)
            for (int tx = 0; tx < W; tx++)
            {
                if (!Air(tx, ty) || cluster[ty * W + tx] != 0) continue;
                int id = sizes.Count;
                sizes.Add(0); sumX.Add(0); sumY.Add(0); minTy.Add(int.MaxValue);
                stack.Push((tx, ty));
                cluster[ty * W + tx] = id;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    sizes[id]++; sumX[id] += x; sumY[id] += y;
                    if (y < minTy[id]) minTy[id] = y;
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    {
                        if (Air(nx, ny) && cluster[ny * W + nx] == 0)
                        {
                            cluster[ny * W + nx] = id;
                            stack.Push((nx, ny));
                        }
                    }
                }
            }
            int skyId = 0; int skyTy = int.MaxValue;
            for (int id = 1; id < sizes.Count; id++)
                if (minTy[id] < skyTy) { skyTy = minTy[id]; skyId = id; }
            var cutouts = new System.Collections.Generic.List<(int size, float cx, float cy)>();
            for (int id = 1; id < sizes.Count; id++)
                if (id != skyId && sizes[id] >= 4)
                    cutouts.Add((sizes[id], (float)sumX[id] / sizes[id], (float)sumY[id] / sizes[id]));
            cutouts.Sort((a, b) => b.size.CompareTo(a.size));
            Debug.Log($"[BackdropPlayModeShots] interior air clusters: {cutouts.Count} " +
                      $"(sky id {skyId}, minTy {skyTy})");

            var cam = Object.FindFirstObjectByType<DigglesCameraController>();
            Assert.IsNotNull(cam, "camera controller must exist");
            int shots = 0;
            foreach (var c in cutouts)
            {
                if (shots >= 2) break;
                cam!.FocusX = c.cx + 0.5f; cam.FocusY = c.cy + 2f;   // look slightly below the hole
                cam.Dx = -0.25f; cam.Dy = 0f; cam.Zoom = 1.2f;
                yield return Capture($"cave_cutout_{shots}_at_{(int)c.cx}_{(int)c.cy}.png");
                shots++;
            }
            if (shots == 0)
                Debug.Log("[BackdropPlayModeShots] no interior cutouts found in save_tutorial");
        }

        // Headless stand-in for the player's F8 probe (rim hunt): loads the
        // tutorial save, aims at the largest interior cutout exactly like the capture
        // above, then probes a screen grid through RimProbeTool — every hit appends a
        // full record to <project>/RimProbe/rim_probe.jsonl. Answers "what IS the blue
        // floor / the rim collar" without a manual Play session.
        [UnityTest]
        public IEnumerator ProbeTutorialCaveGrid()
        {
            yield return BootCampaign();
            var scope = Object.FindFirstObjectByType<LifetimeScope>();
            Assert.IsNotNull(scope, "scene must contain the game lifetime scope");
            var loader = scope!.Container.Resolve<Diggles.Runtime.Save.IOriginalSaveGameLoadService>();
            loader.LoadGame("save_tutorial");
            yield return null;
            yield return WaitUntil(() => _runtime.WorldReady, 3000, "tutorial save world never became ready");
            for (int i = 0; i < 60; i++) yield return null;

            var cam = Object.FindFirstObjectByType<DigglesCameraController>();
            Assert.IsNotNull(cam, "camera controller must exist");
            // The (372,43) cutout pose of the capture runs.
            cam!.FocusX = 372.5f; cam.FocusY = 45f;
            cam.Dx = -0.25f; cam.Dy = 0f; cam.Zoom = 1.2f;
            for (int i = 0; i < 10; i++) yield return null;

            var probe = Object.FindFirstObjectByType<Diggles.Runtime.World.RimProbeTool>();
            if (probe == null)
            {
                // Save-loaded sessions may predate the BindSession wiring — attach on the
                // world root the camera controller tracks (rebound on every session swap).
                var rootTr = cam!.WorldRoot;
                Assert.IsNotNull(rootTr, "camera must know the world root");
                probe = rootTr!.gameObject.AddComponent<Diggles.Runtime.World.RimProbeTool>();
                probe.Init(_runtime.Context!.Terrain!, _runtime.Context.FogOfWar,
                           _runtime.Context.BaseLight, rootTr);
            }
            var uc = Camera.main;
            Assert.IsNotNull(uc, "main camera");
            int hits = 0;
            // 7×5 grid over the frame (skip the outermost margins).
            for (int gy = 1; gy <= 5; gy++)
            for (int gx = 1; gx <= 7; gx++)
            {
                var sp = new Vector3(Screen.width * gx / 8f, Screen.height * gy / 6f, 0f);
                if (probe!.ProbeScreenPoint(uc!, sp)) hits++;
            }
            Debug.Log($"[ProbeTutorialCaveGrid] {hits} probe hits recorded");
            Assert.Greater(hits, 0, "the probe grid must hit something");
        }

        [UnityTest]
        public IEnumerator CaptureBackdropPoses()
        {
            yield return BootCampaign();
            Directory.CreateDirectory(OutDir);

            var cam = Object.FindFirstObjectByType<DigglesCameraController>();
            Assert.IsNotNull(cam, "DigglesCameraController must exist in the campaign world");

            // Let the world settle a moment (presenters, first backdrop frame).
            for (int i = 0; i < 30; i++) yield return null;

            // Pose A — the default start view (whatever the campaign boot set), sanity anchor.
            yield return Capture("pose_a_default.png");

            // Pose B — near-level tilt between the trees (the_11-52 look).
            cam!.Dx = -0.06f; cam.Dy = 0f; cam.Zoom = 1.2f;
            yield return Capture("pose_b_level.png");

            // Pose C — strong sideways orbit (the_11-56 look: the stripe-stack repro).
            cam.Dx = -0.25f; cam.Dy = 0.9f; cam.Zoom = 1.4f;
            yield return Capture("pose_c_orbit.png");

            // Pose D — orbit the other way + pitched up, extreme V stress.
            cam.Dx = -0.03f; cam.Dy = -1.1f; cam.Zoom = 2.2f;
            yield return Capture("pose_d_extreme.png");

            // Pose E — close-up on the meadow bowl rim against the panorama (the
            //_12-44 report pose: near-level, slightly orbited).
            cam.FocusX = 242f; cam.FocusY = 30f;
            cam.Dx = -0.12f; cam.Dy = 0.35f; cam.Zoom = 1.0f;
            yield return Capture("pose_e_rim_west.png");

            // Pose F — the east rim from the other side.
            cam.FocusX = 252f; cam.FocusY = 30f;
            cam.Dx = -0.12f; cam.Dy = -0.35f; cam.Zoom = 1.0f;
            yield return Capture("pose_f_rim_east.png");

            var backdrop = Object.FindFirstObjectByType<Diggles.Runtime.World.BackdropPresenter>();
            Assert.IsNotNull(backdrop, "BackdropPresenter must be attached to the campaign world");
            Assert.IsTrue(backdrop!.PanoramaVisible,
                "panorama must be visible at the meadow start view (surface gate open)");
        }

        IEnumerator Capture(string name)
        {
            // batchmode never reaches WaitForEndOfFrame (no on-screen frames), so render the
            // camera EXPLICITLY into an offscreen RT — works under -batchmode with a device.
            var cam = Camera.main;
            Assert.IsNotNull(cam, "no main camera to capture");
            const int W = 1600, H = 900;
            // Aspect BEFORE the settle frames: BackdropPresenter.LateUpdate derives the layer
            // UVs from the live camera projection, which must match the capture RT.
            cam!.aspect = (float)W / H;
            for (int i = 0; i < 5; i++) yield return null;
            var rt = new RenderTexture(W, H, 24);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.aspect = (float)W / H;   // targetTexture assignment resets the override
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(OutDir, name), tex.EncodeToPNG());
                Debug.Log($"[BackdropPlayModeShots] wrote {Path.Combine(OutDir, name)} ({W}x{H})");
                Object.Destroy(tex);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                cam.ResetAspect();
                rt.Release();
                Object.Destroy(rt);
            }
        }
    }
}
