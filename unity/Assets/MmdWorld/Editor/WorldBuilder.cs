using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using Object = UnityEngine.Object;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// DanceSong の一覧から、シーンを丸ごと作り直す。手で置いたものは残らないので、手を加えたい部分はこのコードの方を直す。
    /// batchmode からは -executeMethod MmdWorld.EditorTools.WorldBuilder.BuildFromCommandLine で呼べる。
    /// </summary>
    public static class WorldBuilder
    {
        const string RootDir = "Assets/MmdWorld";
        const string GeneratedDir = RootDir + "/Generated";
        const string ScriptsDir = RootDir + "/Scripts";
        public const string ScenePath = RootDir + "/Scenes/MmdWorld.unity";
        const int SlotCount = 4;

        static Font _font;

        [MenuItem("MMD World/ワールドを組み立てる")]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Build();
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        public static void Build()
        {
            SetupVrchatLayers();
            EnsureFolder(GeneratedDir);
            EnsureFolder(RootDir + "/Scenes");
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            EnsureProgramAsset<DanceSystem>();
            EnsureProgramAsset<DanceSlot>();
            EnsureProgramAsset<DanceButton>();

            CreateMissingSongs();
            var songs = AssetDatabase.FindAssets("t:" + nameof(DanceSong))
                .Select(g => AssetDatabase.LoadAssetAtPath<DanceSong>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null && s.motion != null)
                .OrderBy(s => s.order).ThenBy(s => s.name)
                .ToList();
            Debug.Log($"[MmdWorld] 曲 {songs.Count} 件: {string.Join(", ", songs.Select(s => s.DisplayTitle))}");

            var stationControllers = songs.Select((s, i) => BuildStationController(s, i)).ToList();
            var previewController = BuildPreviewController(songs);
            var mannequinMaterial = LoadOrCreateMaterial(GeneratedDir + "/Mannequin.mat", new Color(0.85f, 0.85f, 0.9f));
            var mannequin = MannequinBuilder.BuildPrefab(GeneratedDir + "/Mannequin.prefab", GeneratedDir + "/MannequinAvatar.asset", mannequinMaterial);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildEnvironment();

            var systemGo = new GameObject("DanceSystem");
            var audio = systemGo.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f;
            var system = UdonSharpUndo.AddComponent<DanceSystem>(systemGo);

            var slots = new List<DanceSlot>();
            for (int i = 0; i < SlotCount; i++)
                slots.Add(BuildSlot(i, system, stationControllers));

            var preview = (GameObject)PrefabUtility.InstantiatePrefab(mannequin);
            preview.name = "お手本";
            preview.transform.SetPositionAndRotation(new Vector3(3.6f, 0f, 4.5f), Quaternion.Euler(0f, 180f, 0f));
            var previewAnimator = preview.GetComponent<Animator>();
            previewAnimator.runtimeAnimatorController = previewController;

            var (title, status) = BuildPanel(system);

            system.songTitles = songs.Select(s => s.DisplayTitle).ToArray();
            system.songAudio = songs.Select(s => s.audio).ToArray();
            system.songLengths = songs.Select(s => s.motion.length).ToArray();
            system.audioOffsets = songs.Select(s => s.audioOffset).ToArray();
            system.slots = slots.ToArray();
            system.audioSource = audio;
            system.previewDancers = new[] { previewAnimator };
            system.titleText = title;
            system.statusText = status;
            UdonSharpEditorUtility.CopyProxyToUdon(system);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[MmdWorld] シーンを作りました: " + ScenePath);
        }

        /// <summary>
        /// DanceSong がまだ無い .vmd に、同じフォルダへ DanceSong を作る。音は同じフォルダに AudioClip が1つだけあればそれを使う。
        /// .vmd と音を1つのフォルダに入れて組み立て直せば、曲が1つ増える。
        /// </summary>
        public static void CreateMissingSongs()
        {
            var used = new HashSet<AnimationClip>(AssetDatabase.FindAssets("t:" + nameof(DanceSong))
                .Select(g => AssetDatabase.LoadAssetAtPath<DanceSong>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null && s.motion != null)
                .Select(s => s.motion));

            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".vmd", StringComparison.OrdinalIgnoreCase)) continue;
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null || used.Contains(clip)) continue;

                string dir = Path.GetDirectoryName(path).Replace('\\', '/');
                var audios = AssetDatabase.FindAssets("t:AudioClip", new[] { dir })
                    .Select(g => AssetDatabase.GUIDToAssetPath(g))
                    .Where(p => Path.GetDirectoryName(p).Replace('\\', '/') == dir)
                    .ToList();

                var song = ScriptableObject.CreateInstance<DanceSong>();
                song.title = Path.GetFileNameWithoutExtension(path);
                song.motion = clip;
                song.audio = audios.Count == 1 ? AssetDatabase.LoadAssetAtPath<AudioClip>(audios[0]) : null;
                string songPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{song.title}.asset");
                AssetDatabase.CreateAsset(song, songPath);
                Debug.Log($"[MmdWorld] 曲を作りました: {songPath}（音: {(song.audio != null ? song.audio.name : "なし")}）");
            }
            AssetDatabase.SaveAssets();
        }

        // ---- 曲ごとのアニメーター ----

        static AnimatorController BuildStationController(DanceSong song, int index)
        {
            string path = $"{GeneratedDir}/Station_Song{index}.controller";
            AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            var dance = sm.AddState("Dance");
            dance.motion = song.motion;
            dance.writeDefaultValues = false;
            // クリップには足の IK の目標（LeftFootT など）を焼いてあるので、体格の違うアバターでも足が MMD の位置に着く
            dance.iKOnFeet = true;
            sm.defaultState = dance;
            return controller;
        }

        static AnimatorController BuildPreviewController(List<DanceSong> songs)
        {
            string path = GeneratedDir + "/Preview.controller";
            AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            // Idle は1曲目の最初のフレームで止めておく（何も再生しないと人形が T ポーズのままになる）
            var idle = sm.AddState("Idle");
            if (songs.Count > 0)
            {
                idle.motion = songs[0].motion;
                idle.speed = 0f;
            }
            sm.defaultState = idle;
            for (int i = 0; i < songs.Count; i++)
            {
                var state = sm.AddState("Song" + i);
                state.motion = songs[i].motion;
                state.iKOnFeet = true;
            }
            return controller;
        }

        // ---- シーンの部品 ----

        static void BuildEnvironment()
        {
            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(3f, 1f, 3f);
            floor.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Floor.mat", new Color(0.35f, 0.37f, 0.4f));

            var stage = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stage.name = "Stage";
            stage.transform.position = new Vector3(0f, 0.01f, 5f);
            stage.transform.localScale = new Vector3(9f, 0.02f, 4f);
            stage.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Stage.mat", new Color(0.15f, 0.15f, 0.2f));

            var world = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCWorld.prefab"));
            world.transform.SetPositionAndRotation(new Vector3(0f, 0f, -1f), Quaternion.identity);

            // 客席の後ろの鏡。踊る人が自分の姿を見る（MMD ワールドの定番）。VRCMirror はそのままで +Z（舞台）側に映る面が向いている
            var mirror = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCMirror.prefab"));
            mirror.name = "Mirror";
            mirror.transform.SetPositionAndRotation(new Vector3(0f, 1.7f, -2.5f), Quaternion.identity);
            mirror.transform.localScale = new Vector3(8f, 3.4f, 1f);
        }

        static DanceSlot BuildSlot(int index, DanceSystem system, List<AnimatorController> controllers)
        {
            var root = new GameObject("Slot" + (index + 1));
            float x = (index - (SlotCount - 1) * 0.5f) * 1.6f;
            // 客席（-Z）の方を向いて踊る
            root.transform.SetPositionAndRotation(new Vector3(x, 0.02f, 4.5f), Quaternion.Euler(0f, 180f, 0f));

            var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pad.name = "Pad";
            pad.transform.SetParent(root.transform, false);
            pad.transform.localScale = new Vector3(0.9f, 0.02f, 0.9f);
            // 押しやすいように当たり判定だけ高くする
            var capsule = pad.GetComponent<CapsuleCollider>();
            Object.DestroyImmediate(capsule);
            var box = pad.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 25f, 0f);
            box.size = new Vector3(1f, 50f, 1f);
            pad.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard"));

            var slot = UdonSharpUndo.AddComponent<DanceSlot>(pad);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(slot).interactText = "ここで踊る / やめる";

            var stations = new List<VRCStation>();
            for (int s = 0; s < controllers.Count; s++)
            {
                var go = new GameObject("Station_Song" + s);
                go.transform.SetParent(root.transform, false);
                var station = go.AddComponent<VRCStation>();
                station.PlayerMobility = VRC.SDKBase.VRCStation.Mobility.Immobilize;
                station.seated = false;
                station.canUseStationFromStation = true;
                station.disableStationExit = false;
                station.animatorController = controllers[s];
                station.stationEnterPlayerLocation = go.transform;
                station.stationExitPlayerLocation = root.transform;
                stations.Add(station);
            }

            var label = CreateText(root.transform, "Label", "空き", 60, new Vector2(500, 200));
            label.transform.position = root.transform.position + new Vector3(0f, 2.2f, 0f);
            label.transform.rotation = Quaternion.identity;

            slot.system = system;
            slot.stations = stations.ToArray();
            slot.label = label;
            slot.pad = pad.GetComponent<Renderer>();
            UdonSharpEditorUtility.CopyProxyToUdon(slot);
            return slot;
        }

        static (Text title, Text status) BuildPanel(DanceSystem system)
        {
            var panel = new GameObject("Panel");
            panel.transform.position = new Vector3(-3.2f, 0f, 1.2f);

            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "Board";
            Object.DestroyImmediate(board.GetComponent<Collider>());
            board.transform.SetParent(panel.transform, false);
            board.transform.localPosition = new Vector3(0f, 1.35f, 0.03f);
            board.transform.localScale = new Vector3(1.8f, 0.7f, 0.02f);
            board.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Board.mat", new Color(0.08f, 0.08f, 0.1f));

            var title = CreateText(panel.transform, "Title", "曲", 60, new Vector2(850, 120));
            title.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            var status = CreateText(panel.transform, "Status", "停止中", 50, new Vector2(850, 100));
            status.transform.localPosition = new Vector3(0f, 1.25f, 0f);

            var buttons = new[] { ("◀ 前", "PrevSong"), ("▶ 再生", "Play"), ("■ 停止", "Stop"), ("次 ▶", "NextSong") };
            for (int i = 0; i < buttons.Length; i++)
            {
                var (text, evt) = buttons[i];
                var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
                button.name = "Button_" + evt;
                button.transform.SetParent(panel.transform, false);
                button.transform.localPosition = new Vector3(-0.66f + i * 0.44f, 0.85f, 0f);
                button.transform.localScale = new Vector3(0.38f, 0.18f, 0.06f);
                button.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Button.mat", new Color(0.25f, 0.45f, 0.8f));
                var db = UdonSharpUndo.AddComponent<DanceButton>(button);
                db.target = system;
                db.eventName = evt;
                UdonSharpEditorUtility.GetBackingUdonBehaviour(db).interactText = text;
                UdonSharpEditorUtility.CopyProxyToUdon(db);

                var label = CreateText(panel.transform, "Label_" + evt, text, 40, new Vector2(200, 80));
                label.transform.localPosition = new Vector3(-0.66f + i * 0.44f, 0.85f, -0.04f);
            }
            return (title, status);
        }

        /// <summary>ワールド空間の Canvas に Text を1つ置く。1 ピクセル = 1mm。</summary>
        static Text CreateText(Transform parent, string name, string text, int fontSize, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(parent, false);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * 0.001f;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            var t = textGo.AddComponent<Text>();
            t.font = _font;
            t.fontSize = fontSize;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.text = text;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        // ---- 下ごしらえ ----

        /// <summary>UdonSharp のスクリプトには、対になる UdonSharpProgramAsset が要る。無ければスクリプトの隣に作る。</summary>
        static void EnsureProgramAsset<T>() where T : UdonSharpBehaviour
        {
            if (UdonSharpEditorUtility.GetUdonSharpProgramAsset(typeof(T)) != null) return;
            string path = $"{ScriptsDir}/{typeof(T).Name}.asset";
            var asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            asset.sourceCsScript = AssetDatabase.LoadAssetAtPath<MonoScript>($"{ScriptsDir}/{typeof(T).Name}.cs");
            // 作りたては版が Unknown で、エディタの次の周回で U# が書き換えの要否を調べるまで使えない。
            // ここのスクリプトは今の書き方なので、書き換え不要として最新版にしておく
            asset.ScriptVersion = UdonSharpProgramVersion.CurrentVersion;
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            // 型からプログラムを引く表が古いままなので捨てさせてから、コンパイルし直す
            typeof(UdonSharpProgramAsset).GetMethod("ClearProgramAssetCache", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
            UdonSharpCompilerV1.CompileSync();
            if (UdonSharpEditorUtility.GetUdonSharpProgramAsset(typeof(T)) == null)
                throw new InvalidOperationException(typeof(T).Name + " の UdonSharpProgramAsset を作れませんでした");
        }

        /// <summary>VRChat 用のレイヤーと衝突設定。SDK のコントロールパネルのボタンと同じ処理を呼ぶ。</summary>
        static void SetupVrchatLayers()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UpdateLayers", false) ?? a.GetTypes().FirstOrDefault(t => t.Name == "UpdateLayers"))
                .FirstOrDefault(t => t != null);
            if (type == null)
            {
                Debug.LogWarning("[MmdWorld] UpdateLayers が見つからないので、レイヤーの設定は VRChat SDK のパネルから行ってください");
                return;
            }
            bool layers = (bool)type.GetMethod("AreLayersSetup").Invoke(null, null);
            bool matrix = (bool)type.GetMethod("IsCollisionLayerMatrixSetup").Invoke(null, null);
            if (!layers) type.GetMethod("SetupEditorLayers").Invoke(null, null);
            if (!matrix) type.GetMethod("SetupCollisionLayerMatrix").Invoke(null, null);
        }

        static Material LoadOrCreateMaterial(string path, Color color)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
