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
        const string StationDir = GeneratedDir + "/Station";
        public const string ScenePath = RootDir + "/Scenes/MmdWorld.unity";
        const float SlotSpacing = 1.6f;
        static int _slotCount;

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
            EnsureProgramAsset<DanceTablet>();
            // batchmode では U# のコンパイルが走らないことがあり、新しいスクリプトのプログラムが未コンパイルのままだと
            // コンポーネントに値を入れられない（outdated behaviour version）。組み立ての前に必ず1回コンパイルする
            UdonSharpCompilerV1.CompileSync();

            var settings = MmdWorldSettings.LoadOrCreate();
            _slotCount = Mathf.Clamp(settings.slotCount, 1, 16);
            CreateMissingSongs();
            var songs = MmdWorldLibrary.Songs();
            Debug.Log($"[MmdWorld] 曲 {songs.Count} 件: {string.Join(", ", songs.Select(s => s.DisplayTitle))}");

            // ステーション用の Controller は、曲 × 区切りの数だけ作り直す（前の分は丸ごと消す）
            AssetDatabase.DeleteAsset(StationDir);
            for (int i = 0; AssetDatabase.LoadAssetAtPath<Object>($"{GeneratedDir}/Station_Song{i}.controller") != null; i++)
                AssetDatabase.DeleteAsset($"{GeneratedDir}/Station_Song{i}.controller");
            EnsureFolder(StationDir);
            var segments = songs.Select(MmdWorldLibrary.Segments).ToList();
            var segmentControllers = new List<RuntimeAnimatorController>();
            for (int i = 0; i < songs.Count; i++)
                for (int k = 0; k < segments[i].Count; k++)
                    segmentControllers.Add(BuildStationController(songs[i], i, k, segments[i][k]));
            Debug.Log($"[MmdWorld] ステーション用の Controller {segmentControllers.Count} 個（区切り: {string.Join(" / ", segments.Select(g => g.Count + " 個"))}）");
            var previewController = BuildPreviewController(songs);
            var mannequinMaterial = LoadOrCreateMaterial(GeneratedDir + "/Mannequin.mat", new Color(0.85f, 0.85f, 0.9f));
            var mannequin = MannequinBuilder.BuildPrefab(GeneratedDir + "/Mannequin.prefab", GeneratedDir + "/MannequinAvatar.asset", mannequinMaterial);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            float stageWidth = Mathf.Max(9f, _slotCount * SlotSpacing + 2.6f);
            BuildEnvironment(stageWidth);
            BuildPedestals(settings.pedestalAvatarIds, stageWidth);

            var systemGo = new GameObject("DanceSystem");
            var audio = systemGo.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f;
            var system = UdonSharpUndo.AddComponent<DanceSystem>(systemGo);

            var slots = new List<DanceSlot>();
            for (int i = 0; i < _slotCount; i++)
                slots.Add(BuildSlot(i, system, segmentControllers.Count > 0 ? segmentControllers[0] : null));

            var previewAnimators = BuildPreviewDancers(settings.previewDancers, mannequin, previewController, stageWidth);
            var slotAvatars = BuildSlotAvatars(settings.slotAvatars, previewController);

            var (title, status) = BuildPanel(system);
            var (tablet, tabletTitle, tabletStatus) = BuildTablet(system);

            system.songTitles = songs.Select(s => s.DisplayTitle).ToArray();
            system.songAudio = songs.Select(s => s.audio).ToArray();
            system.songLengths = songs.Select(s => s.motion.length).ToArray();
            system.audioOffsets = songs.Select(s => s.audioOffset).ToArray();
            FillTrajectories(system, songs);
            system.segmentTimes = segments.SelectMany(g => g).ToArray();
            system.segmentControllers = segmentControllers.ToArray();
            system.segmentStart = segments.Select((g, i) => segments.Take(i).Sum(x => x.Count)).ToArray();
            system.segmentCount = segments.Select(g => g.Count).ToArray();
            system.slots = slots.ToArray();
            system.audioSource = audio;
            system.previewDancers = previewAnimators.ToArray();
            system.slotAvatars = slotAvatars.ToArray();
            system.slotAvatarNames = slotAvatars.Select(a => a.name).ToArray();
            system.countdownSeconds = settings.countdownSeconds;
            system.rotateStations = settings.rotateDancers;
            system.titleText = title;
            system.statusText = status;
            system.tabletTitleText = tabletTitle;
            system.tabletStatusText = tabletStatus;
            system.tablet = tablet;
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

        /// <summary>曲 index の、時刻 startTime から踊り始めるステーション用の Controller。</summary>
        static AnimatorController BuildStationController(DanceSong song, int index, int segment, float startTime)
        {
            string path = $"{StationDir}/Song{index}_Seg{segment}.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            var dance = sm.AddState("Dance");
            // 体の移動と向きは、DanceSystem がステーションごと動かして出すので、その場で踊るクリップを使う
            var clip = MmdWorldLibrary.InPlaceClip(song);
            dance.motion = clip;
            // 区切りの時刻から始める（座った瞬間にこのステートが始まる）
            dance.cycleOffset = clip.length > 0f ? startTime / clip.length : 0f;
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

        /// <summary>曲ごとの軌跡を1本の配列につなげて DanceSystem に入れる（Udon は配列の配列を持てないので）。</summary>
        static void FillTrajectories(DanceSystem system, List<DanceSong> songs)
        {
            var xs = new List<float>();
            var zs = new List<float>();
            var yaws = new List<float>();
            var starts = new List<int>();
            var counts = new List<int>();
            var rates = new List<float>();
            foreach (var song in songs)
            {
                var tr = MmdWorldLibrary.Trajectory(song);
                starts.Add(xs.Count);
                counts.Add(tr != null ? tr.Count : 0);
                rates.Add(tr != null ? tr.sampleRate : 1f);
                if (tr == null)
                {
                    Debug.LogWarning($"[MmdWorld] {song.DisplayTitle} に軌跡が無い（取り込み直すと作られる）。この曲ではステーションを動かさない");
                    continue;
                }
                xs.AddRange(tr.x);
                zs.AddRange(tr.z);
                yaws.AddRange(tr.yaw);
            }
            system.trajX = xs.ToArray();
            system.trajZ = zs.ToArray();
            system.trajYaw = yaws.ToArray();
            system.trajStart = starts.ToArray();
            system.trajCount = counts.ToArray();
            system.trajRate = rates.ToArray();
        }

        // ---- お手本・着替えの台 ----

        /// <summary>
        /// お手本を舞台の右端から外側へ並べる。設定に Humanoid のモデルが無ければ付属の人形を1体置く。
        /// Humanoid でないモデルは飛ばす（Animator の Avatar が Humanoid でないと踊れない）。
        /// </summary>
        static List<Animator> BuildPreviewDancers(List<GameObject> models, GameObject mannequin, RuntimeAnimatorController controller, float stageWidth)
        {
            var usable = models.Where(m => m != null && IsHumanoid(m)).ToList();
            foreach (var m in models.Where(m => m != null && !IsHumanoid(m)))
                Debug.LogWarning($"[MmdWorld] お手本の {m.name} は Humanoid ではないので置かない");
            if (usable.Count == 0) usable.Add(mannequin);

            var animators = new List<Animator>();
            for (int i = 0; i < usable.Count; i++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(usable[i]);
                go.name = "お手本" + (usable.Count > 1 ? (i + 1).ToString() : "") + "_" + usable[i].name;
                float x = stageWidth * 0.5f - 0.9f - i * 1.0f;
                // 枠と重ならないよう、舞台の奥の列に並べる
                go.transform.SetPositionAndRotation(new Vector3(x, 0f, 6.2f), Quaternion.Euler(0f, 180f, 0f));
                var animator = go.GetComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animators.Add(animator);
            }
            return animators;
        }

        /// <summary>
        /// 枠で踊らせるアバターを1体ずつ置く（最初は隠しておき、枠に割り当てられたら DanceSystem が枠の位置に出す）。
        /// VRChat のアバターの prefab をそのまま使えるよう、ワールドでは使えない部品（Avatar Descriptor など）を外す。揺れもの（PhysBone）は残す。
        /// </summary>
        static List<Animator> BuildSlotAvatars(List<GameObject> models, RuntimeAnimatorController controller)
        {
            var result = new List<Animator>();
            var usable = models.Where(m => m != null && IsHumanoid(m)).Distinct().ToList();
            foreach (var m in models.Where(m => m != null && !IsHumanoid(m)))
                Debug.LogWarning($"[MmdWorld] 枠で踊らせるアバターの {m.name} は Humanoid ではないので置かない");
            if (usable.Count == 0) return result;

            var root = new GameObject("SlotAvatars");
            foreach (var model in usable)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
                go.name = model.name;
                // prefab のインスタンスのままだと部品を外せないことがあるので、結び付きを解く（シーンは毎回作り直すので困らない）
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                int removed = StripAvatarOnlyComponents(go);
                if (removed > 0) Debug.Log($"[MmdWorld] {model.name} からワールドで使えない部品を {removed} 個外した");
                var animator = go.GetComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                go.SetActive(false);
                result.Add(animator);
            }
            return result;
        }

        /// <summary>アバター専用の部品（型の名前空間で見分ける）と、中身の無いスクリプトを外す。外した数を返す。</summary>
        public static int StripAvatarOnlyComponents(GameObject root)
        {
            string[] avatarOnly = { "VRC.SDK3.Avatars", "VRC.SDKBase.VRC_AvatarDescriptor", "VRC.Core.PipelineManager", "nadena.dev", "Anatawa12", "VRC.SDK3.Dynamics.Contact" };
            int removed = 0;
            // 依存関係のある部品は外す順番で失敗することがあるので、外せなくなるまで繰り返す
            for (int pass = 0; pass < 4; pass++)
            {
                bool any = false;
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform) continue;
                    string name = c.GetType().FullName ?? "";
                    if (!avatarOnly.Any(name.StartsWith)) continue;
                    Object.DestroyImmediate(c);
                    removed++;
                    any = true;
                }
                if (!any) break;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            return removed;
        }

        static bool IsHumanoid(GameObject model)
        {
            var animator = model.GetComponent<Animator>();
            return animator != null && animator.avatar != null && animator.avatar.isHuman;
        }

        /// <summary>着替えの台を、舞台の左の外に客席へ向けて並べる。形の正しくない ID は飛ばす。</summary>
        static void BuildPedestals(List<string> avatarIds, float stageWidth)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/AvatarPedestal.prefab");
            int n = 0;
            foreach (string raw in avatarIds)
            {
                string id = raw?.Trim();
                if (!MmdWorldLibrary.IsValidAvatarId(id))
                {
                    if (!string.IsNullOrEmpty(id)) Debug.LogWarning("[MmdWorld] アバター ID の形が違うので台を置かない: " + id);
                    continue;
                }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = "着替え_" + id;
                go.transform.SetPositionAndRotation(new Vector3(-stageWidth * 0.5f - 1.5f, 0f, 4.5f - n * 1.4f), Quaternion.Euler(0f, 90f, 0f));
                var pedestal = go.GetComponentInChildren<VRCAvatarPedestal>();
                var so = new SerializedObject(pedestal);
                so.FindProperty("blueprintId").stringValue = id;
                so.ApplyModifiedPropertiesWithoutUndo();
                n++;
            }
        }

        // ---- シーンの部品 ----

        static void BuildEnvironment(float stageWidth)
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
            stage.transform.localScale = new Vector3(stageWidth, 0.02f, 4f);
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

        static DanceSlot BuildSlot(int index, DanceSystem system, RuntimeAnimatorController firstController)
        {
            var root = new GameObject("Slot" + (index + 1));
            float x = (index - (_slotCount - 1) * 0.5f) * SlotSpacing;
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

            // ステーションは枠に2つ（A と B）。Controller は曲と区切りに合わせて DanceSystem が差し替え、シークでは交互に乗り換える。
            // 2つとも同じ親の下に置き、踊りの軌跡どおりに親を動かす
            var stationRoot = new GameObject("Stations");
            stationRoot.transform.SetParent(root.transform, false);
            var stationList = new List<VRCStation>();
            foreach (string stationName in new[] { "StationA", "StationB" })
            {
                var stationGo = new GameObject(stationName);
                stationGo.transform.SetParent(stationRoot.transform, false);
                var station = stationGo.AddComponent<VRCStation>();
                station.PlayerMobility = VRC.SDKBase.VRCStation.Mobility.Immobilize;
                station.seated = false;
                station.canUseStationFromStation = true;
                station.disableStationExit = false;
                station.animatorController = firstController;
                station.stationEnterPlayerLocation = stationGo.transform;
                station.stationExitPlayerLocation = root.transform;
                stationList.Add(station);
            }

            // ワールドのアバターを選ぶボタン（アバターが1体もいないワールドでは押しても何も起きない）
            var avatarButton = GameObject.CreatePrimitive(PrimitiveType.Cube);
            avatarButton.name = "AvatarButton";
            avatarButton.transform.SetParent(root.transform, false);
            avatarButton.transform.localPosition = new Vector3(0.6f, 0.15f, 0.5f);
            avatarButton.transform.localScale = new Vector3(0.18f, 0.3f, 0.18f);
            avatarButton.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/AvatarButton.mat", new Color(0.6f, 0.3f, 0.8f));
            var ab = UdonSharpUndo.AddComponent<DanceButton>(avatarButton);
            ab.target = slot;
            ab.eventName = nameof(DanceSlot.NextAvatar);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(ab).interactText = "この枠で踊るアバターを選ぶ";
            UdonSharpEditorUtility.CopyProxyToUdon(ab);

            var label = CreateText(root.transform, "Label", "空き", 60, new Vector2(500, 200));
            label.transform.position = root.transform.position + new Vector3(0f, 2.2f, 0f);
            label.transform.rotation = Quaternion.identity;

            slot.system = system;
            slot.stations = stationList.ToArray();
            slot.stationRoot = stationRoot.transform;
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
            board.transform.localPosition = new Vector3(0f, 1.2f, 0.03f);
            board.transform.localScale = new Vector3(1.9f, 1.35f, 0.02f);
            board.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Board.mat", new Color(0.08f, 0.08f, 0.1f));

            var title = CreateText(panel.transform, "Title", "曲", 60, new Vector2(850, 120));
            title.transform.localPosition = new Vector3(0f, 1.74f, 0f);
            var status = CreateText(panel.transform, "Status", "停止中", 44, new Vector2(880, 140));
            status.transform.localPosition = new Vector3(0f, 1.5f, 0f);

            // 3 段: 曲と再生 / シーク・プレビュー・ループ / 範囲
            var buttons = new[]
            {
                ("◀ 曲", "PrevSong"), ("▶ 再生", "Play"), ("■ 停止", "Stop"), ("曲 ▶", "NextSong"),
                ("≪ 区切り", "SeekBack"), ("区切り ≫", "SeekForward"), ("プレビュー", "TogglePreview"), ("ループ", "ToggleLoop"),
                ("開始 ◀", "RangeStartBack"), ("開始 ▶", "RangeStartForward"), ("終了 ◀", "RangeEndBack"), ("終了 ▶", "RangeEndForward"),
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                var (text, evt) = buttons[i];
                float bx = -0.66f + (i % 4) * 0.44f;
                float by = 1.24f - (i / 4) * 0.24f;
                var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
                button.name = "Button_" + evt;
                button.transform.SetParent(panel.transform, false);
                button.transform.localPosition = new Vector3(bx, by, 0f);
                button.transform.localScale = new Vector3(0.38f, 0.18f, 0.06f);
                button.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Button.mat", new Color(0.25f, 0.45f, 0.8f));
                var db = UdonSharpUndo.AddComponent<DanceButton>(button);
                db.target = system;
                db.eventName = evt;
                UdonSharpEditorUtility.GetBackingUdonBehaviour(db).interactText = text;
                UdonSharpEditorUtility.CopyProxyToUdon(db);

                var label = CreateText(panel.transform, "Label_" + evt, text, 36, new Vector2(200, 80));
                label.transform.localPosition = new Vector3(bx, by, -0.04f);
            }
            return (title, status);
        }

        /// <summary>
        /// 手元に浮かぶタブレット（DanceTablet）。VR では左手のグリップで出し、右手の指先で押す。デスクトップでは T キーで出し、キーで押す。
        /// ボタンの前面を z = 0 に置き、自分の側を -Z にする（DanceTablet が指先の位置と比べる）。
        /// </summary>
        static (DanceTablet tablet, Text title, Text status) BuildTablet(DanceSystem system)
        {
            var rootGo = new GameObject("DanceTablet");
            var body = new GameObject("Body");
            body.transform.SetParent(rootGo.transform, false);

            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "Board";
            Object.DestroyImmediate(board.GetComponent<Collider>());
            board.transform.SetParent(body.transform, false);
            board.transform.localPosition = new Vector3(0f, 0f, 0.012f);
            board.transform.localScale = new Vector3(0.38f, 0.32f, 0.008f);
            board.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Board.mat", new Color(0.08f, 0.08f, 0.1f));

            var title = CreateText(body.transform, "Title", "曲", 18, new Vector2(360, 26));
            title.transform.localPosition = new Vector3(0f, 0.135f, -0.001f);
            var status = CreateText(body.transform, "Status", "停止中", 13, new Vector2(360, 40));
            status.transform.localPosition = new Vector3(0f, 0.1f, -0.001f);

            var defs = new (string text, string evt, KeyCode key, int col, int row, int span, bool onTablet)[]
            {
                ("◀ 曲", "PrevSong", KeyCode.Alpha1, 0, 0, 1, false), ("▶ 再生", "Play", KeyCode.Alpha2, 1, 0, 1, false),
                ("■ 停止", "Stop", KeyCode.Alpha3, 2, 0, 1, false), ("曲 ▶", "NextSong", KeyCode.Alpha4, 3, 0, 1, false),
                ("≪ 区切り", "SeekBack", KeyCode.Alpha5, 0, 1, 1, false), ("区切り ≫", "SeekForward", KeyCode.Alpha6, 1, 1, 1, false),
                ("プレビュー", "TogglePreview", KeyCode.Alpha7, 2, 1, 1, false), ("ループ", "ToggleLoop", KeyCode.Alpha8, 3, 1, 1, false),
                ("開始 ◀", "RangeStartBack", KeyCode.Alpha9, 0, 2, 1, false), ("開始 ▶", "RangeStartForward", KeyCode.Alpha0, 1, 2, 1, false),
                ("終了 ◀", "RangeEndBack", KeyCode.Minus, 2, 2, 1, false), ("終了 ▶", "RangeEndForward", KeyCode.Equals, 3, 2, 1, false),
                ("踊る / やめる", "ToggleLocalJoin", KeyCode.J, 0, 3, 2, false), ("閉じる", "Hide", KeyCode.None, 3, 3, 1, true),
            };
            const float cellW = 0.086f, cellH = 0.052f, top = 0.045f;
            var buttons = new List<DanceButton>();
            var sizes = new List<Vector2>();
            var keys = new List<KeyCode>();
            var keyLabels = new List<GameObject>();
            var tablet = UdonSharpUndo.AddComponent<DanceTablet>(rootGo);
            foreach (var d in defs)
            {
                float w = cellW * d.span - 0.008f, h = cellH - 0.01f;
                float x = -cellW * 1.5f + cellW * d.col + cellW * (d.span - 1) * 0.5f;
                float y = top - cellH * d.row;
                var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
                button.name = "Button_" + d.evt;
                button.transform.SetParent(body.transform, false);
                // 前面を z = 0 に合わせる（厚み 8mm の中心は +4mm）
                button.transform.localPosition = new Vector3(x, y, 0.004f);
                button.transform.localScale = new Vector3(w, h, 0.008f);
                button.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Button.mat", new Color(0.25f, 0.45f, 0.8f));
                var db = UdonSharpUndo.AddComponent<DanceButton>(button);
                db.target = d.onTablet ? (UdonSharpBehaviour)tablet : system;
                db.eventName = d.evt;
                UdonSharpEditorUtility.GetBackingUdonBehaviour(db).interactText = d.text;
                UdonSharpEditorUtility.CopyProxyToUdon(db);
                buttons.Add(db);
                // DanceTablet は button.transform.localPosition を面の中心として使うので、z は 0 として扱う（厚みの分は判定の余裕になる）
                sizes.Add(new Vector2(w, h));
                keys.Add(d.key);

                var label = CreateText(body.transform, "Label_" + d.evt, d.text, 12, new Vector2(w * 1000f, h * 1000f));
                label.transform.localPosition = new Vector3(x, y, -0.001f);
                if (d.key != KeyCode.None)
                {
                    var keyLabel = CreateText(body.transform, "Key_" + d.evt, "[" + KeyName(d.key) + "]", 8, new Vector2(w * 1000f, h * 1000f));
                    keyLabel.alignment = TextAnchor.UpperLeft;
                    keyLabel.color = new Color(1f, 0.85f, 0.3f);
                    keyLabel.transform.localPosition = new Vector3(x + 0.003f, y - 0.002f, -0.0015f);
                    keyLabels.Add(keyLabel.transform.parent.gameObject);
                }
            }

            // 指先の目印（タブレットとは別に置く。DanceTablet が右手のコントローラーから割り出した位置へ動かす）
            var pointer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pointer.name = "DanceTabletPointer";
            Object.DestroyImmediate(pointer.GetComponent<Collider>());
            pointer.transform.localScale = Vector3.one * 0.012f;
            pointer.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Pointer.mat", new Color(1f, 0.6f, 0.1f));

            tablet.system = system;
            tablet.body = body;
            tablet.buttons = buttons.ToArray();
            tablet.buttonSizes = sizes.ToArray();
            tablet.desktopKeys = keys.ToArray();
            tablet.desktopKeyLabels = keyLabels.ToArray();
            tablet.pointer = pointer.transform;
            UdonSharpEditorUtility.CopyProxyToUdon(tablet);

            // プレイヤーの体とぶつからないように、ボタンも含めて Walkthrough レイヤーにする
            int walkthrough = LayerMask.NameToLayer("Walkthrough");
            if (walkthrough >= 0)
            {
                foreach (var t in rootGo.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = walkthrough;
                pointer.layer = walkthrough;
            }
            return (tablet, title, status);
        }

        static string KeyName(KeyCode key) => key switch
        {
            KeyCode.Minus => "-",
            KeyCode.Equals => "=",
            _ => key.ToString().Replace("Alpha", ""),
        };

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
