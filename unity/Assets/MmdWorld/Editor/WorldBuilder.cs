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
            _warnedTrackingControl = false;
            SetupVrchatLayers();
            EnsureFolder(GeneratedDir);
            EnsureFolder(RootDir + "/Scenes");
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            EnsureProgramAsset<DanceSystem>();
            EnsureProgramAsset<DanceSlot>();
            EnsureProgramAsset<DanceButton>();
            EnsureProgramAsset<DanceTablet>();
            EnsureProgramAsset<DanceSeekBar>();
            EnsureProgramAsset<DanceStation>();
            // batchmode では U# のコンパイルが走らないことがあり、新しいスクリプトのプログラムが未コンパイルのままだと
            // コンポーネントに値を入れられない（outdated behaviour version）。組み立ての前に必ず1回コンパイルする
            UdonSharpCompilerV1.CompileSync();

            // VR で踊りを体に乗せる部品（アバター SDK の VRCSDK3A.dll）が無いと、VR では足踏み・トラッキングが戻らないなどになる。
            // 警告がコンソールに埋もれて気付けなかったので、エディタから組み立てるときは確かめる
            if (FindTrackingControlType() == null && !Application.isBatchMode && !ConfirmWithoutTrackingControl()) return;

            var settings = MmdWorldSettings.LoadOrCreate();
            _slotCount = Mathf.Clamp(settings.slotCount, 1, 16);
            _headFollowsDance = settings.vrHeadFollowsDance;
            // 曲になっていない .vmd から曲を作るのは、マネージャーで入れたときだけ（以前は Assets の下を全部探していて、購入物の .vmd まで勝手に曲になった）
            if (settings.autoAddSongs) CreateMissingSongs(settings);
            // 表情だけ・カメラの .vmd を曲にしたもの、区切りが無い（短すぎる）ものは飛ばす
            var songs = new List<DanceSong>();
            foreach (var song in MmdWorldLibrary.Songs())
            {
                if (!MmdWorldLibrary.IsDance(song))
                    Debug.LogWarning($"[MmdWorld] 「{song.DisplayTitle}」は踊りのモーションではない（表情だけ・カメラなど）ので飛ばす。マネージャーで消すか、踊りの曲の「表情」に入れる");
                else if (MmdWorldLibrary.Segments(song).Count == 0)
                    Debug.LogWarning($"[MmdWorld] 「{song.DisplayTitle}」はモーションが短すぎる（区切りが無い）ので飛ばす");
                else songs.Add(song);
            }
            Debug.Log($"[MmdWorld] 曲 {songs.Count} 件: {string.Join(", ", songs.Select(s => s.DisplayTitle))}");

            // 表情が別の .vmd になっている曲は、モーションに表情を重ねたクリップを作る
            AssetDatabase.DeleteAsset(MergedDir);
            EnsureFolder(MergedDir);
            // 曲 × パート（複数人のモーションなら、枠1・枠2 … が踊るそれぞれの .vmd）を「トラック」として並べる。1人の曲は1トラック
            var trackSong = new List<int>();
            var trackClips = new List<AnimationClip>();
            var songPartStart = new List<int>();
            var songPartCount = new List<int>();
            for (int i = 0; i < songs.Count; i++)
            {
                var parts = MmdWorldLibrary.Parts(songs[i]);
                songPartStart.Add(trackClips.Count);
                songPartCount.Add(parts.Count);
                foreach (var part in parts)
                {
                    trackSong.Add(i);
                    trackClips.Add(part);
                }
            }
            // ステーションでは、振り付けの移動もクリップのまま踊る（ステーションは動かさない）。VR の視点はステーションに付いているので、体と頭が動いても視点は動かない。
            // 「その場」の枠には、移動を抜いたクリップを使う
            var fullClips = trackClips.Select((c, t) => WithFace(songs[trackSong[t]], c, $"Track{t}")).ToList();
            var inPlaceClips = trackClips.Select((c, t) => WithFace(songs[trackSong[t]], MmdWorldLibrary.InPlaceClip(c), $"Track{t}_InPlace")).ToList();

            // ステーション用の Controller は、曲 × 区切りの数だけ作り直す（前の分は丸ごと消す）
            AssetDatabase.DeleteAsset(StationDir);
            for (int i = 0; AssetDatabase.LoadAssetAtPath<Object>($"{GeneratedDir}/Station_Song{i}.controller") != null; i++)
                AssetDatabase.DeleteAsset($"{GeneratedDir}/Station_Song{i}.controller");
            EnsureFolder(StationDir);
            var segments = songs.Select(MmdWorldLibrary.Segments).ToList();
            // ステーション用の Controller はトラック × 区切り（区切りの時刻は曲ごとで、パートどうしは同じ）
            var segmentControllers = new List<RuntimeAnimatorController>();
            var inPlaceControllers = new List<RuntimeAnimatorController>();
            var trackSegmentStart = new List<int>();
            for (int t = 0; t < trackClips.Count; t++)
            {
                trackSegmentStart.Add(segmentControllers.Count);
                var times = segments[trackSong[t]];
                for (int k = 0; k < times.Count; k++)
                {
                    segmentControllers.Add(BuildStationController(fullClips[t], t, k, times[k], ""));
                    inPlaceControllers.Add(BuildStationController(inPlaceClips[t], t, k, times[k], "_InPlace"));
                }
            }
            Debug.Log($"[MmdWorld] ステーション用の Controller {segmentControllers.Count} 個 × 2（移動あり・その場）（区切り: {string.Join(" / ", segments.Select(g => g.Count + " 個"))}、" +
                      $"パート: {string.Join(" / ", songPartCount.Select(n => n + " 人"))}）");
            var previewController = BuildPreviewController(fullClips);
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

            int tickCount = segments.Count > 0 ? segments.Max(g => g.Count) : 0;
            var panelUi = BuildPanel(system, tickCount);
            var (tablet, tabletUi) = BuildTablet(system, tickCount);
            // 一覧の文字は、タブレットとパネルの2組を並べて渡す（DanceSystem が1ページの行数で折り返して両方に書く）
            var uis = new[] { tabletUi, panelUi };
            system.uiCopies = uis.Length;
            system.slotTexts = uis.SelectMany(u => u.slotTexts).ToArray();
            system.modelRowTexts = uis.SelectMany(u => u.modelRows).ToArray();
            system.songRowTexts = uis.SelectMany(u => u.songRows).ToArray();
            system.modelListHeaders = uis.Select(u => u.modelHeader).ToArray();
            system.songListHeaders = uis.Select(u => u.songHeader).ToArray();
            system.inPlaceLabels = uis.SelectMany(u => u.inPlaceLabels).ToArray();

            system.songTitles = songs.Select(s => s.DisplayTitle).ToArray();
            system.songAudio = songs.Select(s => s.audio).ToArray();
            foreach (var song in songs) SetAndroidAudio(song.audio);
            system.songLengths = songs.Select(s => s.motion.length).ToArray();
            system.audioOffsets = songs.Select(s => s.audioOffset).ToArray();
            system.songPartStart = songPartStart.ToArray();
            system.songPartCount = songPartCount.ToArray();
            system.trackNames = trackClips.Select(MmdWorldLibrary.PartName).ToArray();
            // パートごとの立ち位置のずれ（複数人のモーションだけ使う）
            system.trackOffsets = songs.SelectMany(sg => Enumerable.Range(0, MmdWorldLibrary.Parts(sg).Count)
                .Select(p => sg.partOffsets != null && p < sg.partOffsets.Count ? new Vector3(sg.partOffsets[p].x, 0f, sg.partOffsets[p].z) : Vector3.zero)).ToArray();
            system.trackLengths = fullClips.Select(c => c.length).ToArray();
            system.trackSegmentStart = trackSegmentStart.ToArray();
            system.stageOrigin = BuildStageOrigin();
            system.segmentTimes = segments.SelectMany(g => g).ToArray();
            system.segmentControllers = segmentControllers.ToArray();
            system.inPlaceControllers = inPlaceControllers.ToArray();
            system.segmentStart = segments.Select((g, i) => segments.Take(i).Sum(x => x.Count)).ToArray();
            system.segmentCount = segments.Select(g => g.Count).ToArray();
            system.slots = slots.ToArray();
            system.audioSource = audio;
            system.previewDancers = previewAnimators.ToArray();
            system.slotAvatars = slotAvatars.ToArray();
            system.slotAvatarNames = slotAvatars.Select(a => a.name).ToArray();
            system.countdownSeconds = settings.countdownSeconds;
            system.titleText = panelUi.title;
            system.statusText = panelUi.status;
            system.tabletTitleText = tabletUi.title;
            system.tabletStatusText = tabletUi.status;
            system.tablet = tablet;
            // 1つだけの変数に AnimatorController を入れると、VRChat でワールドを読み込めなくなった（エディタの型のまま保存されるらしい）。区切りの Controller と同じく配列で渡す
            var restore = BuildRestoreController();
            system.restoreControllers = restore != null ? new RuntimeAnimatorController[] { restore } : new RuntimeAnimatorController[0];
            system.seekBars = new[] { panelUi.seekBar, tabletUi.seekBar };
            UdonSharpEditorUtility.CopyProxyToUdon(system);

            PruneNetworkIds();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[MmdWorld] シーンを作りました: " + ScenePath);
        }

        /// <summary>
        /// settings.autoAddFolder の下の、まだ曲になっていない .vmd から曲を作る（MmdWorldLibrary.DetectSongs と同じ決め方。マネージャーの「フォルダから曲を探す」で先に一覧を見られる）。
        /// </summary>
        public static void CreateMissingSongs(MmdWorldSettings settings)
        {
            foreach (var planned in MmdWorldLibrary.DetectSongs(settings, out _))
            {
                var song = MmdWorldLibrary.CreateSongInPlace(planned);
                Debug.Log($"[MmdWorld] 曲を作りました: {AssetDatabase.GetAssetPath(song)}（音: {(song.audio != null ? song.audio.name : "なし")}、パート {MmdWorldLibrary.Parts(song).Count}）");
            }
        }

        // ---- 曲ごとのアニメーター ----

        /// <summary>曲 index の、時刻 startTime から踊り始めるステーション用の Controller。</summary>
        static AnimatorController BuildStationController(AnimationClip clip, int track, int segment, float startTime, string suffix)
        {
            string path = $"{StationDir}/Track{track}_Seg{segment}{suffix}.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            var dance = sm.AddState("Dance");
            // 移動ありのクリップ（ふだん）か、移動を抜いたクリップ（「その場」の枠）
            dance.motion = clip;
            // 区切りの時刻から始める（座った瞬間にこのステートが始まる）
            dance.cycleOffset = clip.length > 0f ? startTime / clip.length : 0f;
            dance.writeDefaultValues = false;
            // クリップには足の IK の目標（LeftFootT など）を焼いてあるので、体格の違うアバターでも足が MMD の位置に着く
            dance.iKOnFeet = true;
            // VR の視点はアバターの頭に付くので、頭も踊りに合わせると、振り付けの頭の動きで視点が揺れる。切り替えで頭だけトラッキングのままにできる
            AddTrackingControl(dance, "Animation", _headFollowsDance ? null : "trackingHead");
            sm.defaultState = dance;
            return controller;
        }

        /// <summary>
        /// 降りる前に座り直す Controller。VRC Animator Tracking Control で全身をトラッキングに戻すだけ（踊りの Controller が Animation にしたままだと、降りても戻らない）。
        /// トラッキング制御の部品が無ければ作らない（null）。
        /// </summary>
        static AnimatorController BuildRestoreController()
        {
            if (FindTrackingControlType() == null) return null;
            string path = $"{StationDir}/Restore.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            var restore = sm.AddState("Restore");
            restore.writeDefaultValues = false;
            AddTrackingControl(restore, "Tracking");
            sm.defaultState = restore;
            return controller;
        }

        static Type FindTrackingControlType() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl"))
            .FirstOrDefault(t => t != null);

        static readonly string[] TrackingParts =
        {
            "trackingHead", "trackingLeftHand", "trackingRightHand", "trackingHip", "trackingLeftFoot", "trackingRightFoot",
            "trackingLeftFingers", "trackingRightFingers", "trackingEyes", "trackingMouth",
        };

        /// <summary>
        /// VR では、座っていても頭・手・足がトラッキングのままで踊りを上書きする。座っている間は全身を踊りに任せるよう、VRC Animator Tracking Control を付ける。
        /// この部品はアバターの SDK（VRCSDK3A.dll）にしかなく、ワールドの SDK には無い。DLL をプロジェクトに入れてある（README）ときだけ付け、無ければ警告する。
        /// 降りてもトラッキングは自動では戻らないので、DanceSystem が降りる前に BuildRestoreController の Controller へ座り直す。
        /// </summary>
        /// <summary>踊りのステーションで、VR の頭も踊りに合わせるか（MmdWorldSettings.vrHeadFollowsDance）。</summary>
        static bool _headFollowsDance;

        /// <summary>state に VRCAnimatorTrackingControl を付け、体の各部を trackingType にする。keepTracking の部位だけはトラッキングのままにする。</summary>
        static void AddTrackingControl(AnimatorState state, string trackingType, string keepTracking = null)
        {
            var type = FindTrackingControlType();
            if (type == null)
            {
                if (!_warnedTrackingControl)
                    Debug.LogWarning("[MmdWorld] VRCAnimatorTrackingControl が無いので、VR では踊りが頭・手・足のトラッキングに上書きされる。アバターの SDK の VRCSDK3A.dll を Assets/LocalOnly/ に入れる（README）");
                _warnedTrackingControl = true;
                return;
            }
            var behaviour = state.AddStateMachineBehaviour(type);
            foreach (var part in TrackingParts)
            {
                var field = type.GetField(part);
                if (field != null) field.SetValue(behaviour, Enum.Parse(field.FieldType, part == keepTracking ? "Tracking" : trackingType));
            }
            EditorUtility.SetDirty(behaviour);
        }

        static bool _warnedTrackingControl;

        const string MergedDir = GeneratedDir + "/Merged";

        /// <summary>
        /// 曲に表情の .vmd（song.face）があれば、body のクリップに表情のカーブを重ねたクリップを作って返す（無ければ body のまま）。
        /// 表情の .vmd にあるモーフだけを上書きし、踊りの .vmd にしか無いモーフは残す。
        /// </summary>
        static AnimationClip WithFace(DanceSong song, AnimationClip body, string name)
        {
            if (song.face == null || body == null) return body;
            var merged = Object.Instantiate(body);
            merged.name = body.name + "（表情つき）";
            foreach (var binding in AnimationUtility.GetCurveBindings(song.face))
                if (binding.type == typeof(SkinnedMeshRenderer))
                    AnimationUtility.SetEditorCurve(merged, binding, AnimationUtility.GetEditorCurve(song.face, binding));
            AssetDatabase.CreateAsset(merged, $"{MergedDir}/{name}.anim");
            return merged;
        }

        /// <summary>VR で踊りを体に乗せる部品が無いまま組み立ててよいか、エディタで聞く。「DLL を選ぶ」を選んだら入れて続ける。</summary>
        static bool ConfirmWithoutTrackingControl()
        {
            int choice = EditorUtility.DisplayDialogComplex("VR 用の部品がありません",
                "アバター SDK の VRCSDK3A.dll がこのプロジェクトにありません。\n" +
                "このまま組み立てると、VR では踊りが体に乗らず（その場で足踏みのようになる）、降りてもトラッキングが戻りません。\n\n" +
                "アバター用のプロジェクトの Packages/com.vrchat.avatars/Runtime/VRCSDK/Plugins/VRCSDK3A.dll を選ぶと、Assets/LocalOnly/ に写して使います（リポジトリには入りません）。",
                "DLL を選んで入れる", "やめる", "このまま組み立てる");
            if (choice == 2) return true;
            if (choice == 1) return false;
            if (!MmdWorldLibrary.InstallAvatarSdkDll()) return false;
            EditorUtility.DisplayDialog("VR 用の部品", "VRCSDK3A.dll を入れました。スクリプトの読み込みが終わったら、もう一度組み立ててください。", "OK");
            return false;
        }

        static AnimatorController BuildPreviewController(List<AnimationClip> clips)
        {
            string path = GeneratedDir + "/Preview.controller";
            AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = controller.layers[0].stateMachine;
            // Idle は1曲目の最初のフレームで止めておく（何も再生しないと人形が T ポーズのままになる）
            var idle = sm.AddState("Idle");
            if (clips.Count > 0)
            {
                idle.motion = clips[0];
                idle.speed = 0f;
            }
            sm.defaultState = idle;
            for (int i = 0; i < clips.Count; i++)
            {
                var state = sm.AddState("Track" + i);
                state.motion = clips[i];
                state.iKOnFeet = true;
            }
            return controller;
        }

        /// <summary>曲ごとの軌跡を1本の配列につなげて DanceSystem に入れる（Udon は配列の配列を持てないので）。</summary>
        /// <summary>
        /// 複数人のモーションの立ち位置の原点（ステージの中央。枠の並びの真ん中で、枠と同じ向き）。
        /// MMD の複数人のモーションは、それぞれのパートに立ち位置が入っているので、全員この点からの位置で踊る。
        /// </summary>
        static Transform BuildStageOrigin()
        {
            var origin = new GameObject("StageOrigin");
            origin.transform.SetPositionAndRotation(new Vector3(0f, 0.02f, 4.5f), Quaternion.Euler(0f, 180f, 0f));
            return origin.transform;
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
            // 床は見えなくする（本人の希望）。当たり判定は残して、その上を歩く
            floor.GetComponent<Renderer>().enabled = false;

            var stage = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stage.name = "Stage";
            stage.transform.position = new Vector3(0f, 0.01f, 5f);
            stage.transform.localScale = new Vector3(stageWidth, 0.02f, 4f);
            stage.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Stage.mat", new Color(0.15f, 0.15f, 0.2f));

            var world = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCWorld.prefab"));
            world.transform.SetPositionAndRotation(new Vector3(0f, 0f, -1f), Quaternion.identity);

            // 客席の後ろの鏡は、本人の希望で置かない（2026-10-02）
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
            // 押しやすいように当たり判定だけ高くする。すり抜けられるようトリガーにする（ステーションから降りると台の上に立つので、ふつうの当たり判定だと高さ 1m の見えない箱の上に乗ってしまう）
            var capsule = pad.GetComponent<CapsuleCollider>();
            Object.DestroyImmediate(capsule);
            var box = pad.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 25f, 0f);
            box.size = new Vector3(1f, 50f, 1f);
            box.isTrigger = true;
            // 枠ごとに色を変えるので、台座ごとにマテリアルを持つ（Quest でも軽いシェーダー）
            pad.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("VRChat/Mobile/Standard Lite") ?? Shader.Find("Standard"));

            var slot = UdonSharpUndo.AddComponent<DanceSlot>(pad);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(slot).interactText = "ここで踊る / やめる";

            // ステーションは枠に2つ（A と B）。Controller は曲と区切りに合わせて DanceSystem が差し替え、シークでは交互に乗り換える。
            // 2つとも同じ親の下に置く（親は曲の間は動かさない。複数人のモーションではステージの中央へ置く）
            var stationRoot = new GameObject("Stations");
            stationRoot.transform.SetParent(root.transform, false);
            var stationList = new List<VRCStation>();
            foreach (string stationName in new[] { "StationA", "StationB" })
            {
                var stationGo = new GameObject(stationName);
                stationGo.transform.SetParent(stationRoot.transform, false);
                var station = stationGo.AddComponent<VRCStation>();
                // 踊りの軌跡とスティックでステーションごと動かすので、動くステーション向けの固定にする
                station.PlayerMobility = VRC.SDKBase.VRCStation.Mobility.ImmobilizeForVehicle;
                station.seated = false;
                station.canUseStationFromStation = true;
                // 歩く操作で降りないようにする（踊っている間は、スティック・WASD で DanceSystem が枠ごと動かす）。降りるのは枠の台・タブレットの「踊る / やめる」・停止から
                station.disableStationExit = true;
                station.animatorController = firstController;
                station.stationEnterPlayerLocation = stationGo.transform;
                station.stationExitPlayerLocation = root.transform;
                stationList.Add(station);
                // 誰が入った・出たかを DanceSystem に知らせる（座らせていないのに入ったら降ろす）
                var events = UdonSharpUndo.AddComponent<DanceStation>(stationGo);
                events.system = system;
                UdonSharpEditorUtility.CopyProxyToUdon(events);
            }

            // ワールドのアバターは、タブレットの「選ぶ」から枠に置く（以前の、押すたびに切り替える紫のボタンは、使い方が分かりにくかったので外した）

            var label = CreateText(root.transform, "Label", "空き", 60, new Vector2(500, 200));
            label.transform.parent.position = root.transform.position + new Vector3(0f, 2.2f, 0f);
            label.transform.parent.rotation = Quaternion.identity;

            slot.system = system;
            slot.stations = stationList.ToArray();
            slot.stationRoot = stationRoot.transform;
            slot.label = label;
            slot.pad = pad.GetComponent<Renderer>();
            UdonSharpEditorUtility.CopyProxyToUdon(slot);
            return slot;
        }

        /// <summary>操作の画面（手元のタブレットと舞台の横のパネルで同じもの）を組み立てた結果。DanceSystem・DanceTablet に渡す。</summary>
        sealed class ControlUi
        {
            public Text title, status, modelHeader, songHeader;
            public DanceSeekBar seekBar;
            public readonly List<DanceButton> buttons = new List<DanceButton>();
            public readonly List<Image> images = new List<Image>();
            public readonly List<Vector2> sizes = new List<Vector2>();
            public readonly List<string> keys = new List<string>();
            public readonly List<GameObject> keyLabels = new List<GameObject>();
            public readonly List<Text> slotTexts = new List<Text>();
            public List<Text> modelRows, songRows;
            public readonly List<Text> inPlaceLabels = new List<Text>();
            public readonly List<Collider> colliders = new List<Collider>();
        }

        /// <summary>
        /// 操作の画面を uGUI で組み立てる（1 ピクセル = scale メートルのワールド空間の Canvas を rootGo の子に作る）。
        /// 中央: 題名・状態・シークバー・枠の列（「空き」を押すとそこで踊る）・再生などのボタン。左: モデル（ワールドのアバター）の一覧。右: 曲の一覧。
        /// 面は Canvas の z = 0 で、見る側を -Z にする（DanceTablet が指先の位置と比べる）。
        /// tablet があれば手元のタブレット用（「閉じる」とキーの表示を付ける）。null なら舞台の横のパネル用で、ボタンに当たり判定を付けて Interact で押せるようにする。
        /// </summary>
        static ControlUi BuildControlUi(GameObject rootGo, DanceSystem system, DanceTablet tablet, int tickCount, float scale, Vector3 position)
        {
            const float centerW = 380f, listW = 190f, gap = 8f, height = 340f;
            float listX = centerW * 0.5f + gap + listW * 0.5f;
            bool onWorld = tablet == null;
            var ui = new ControlUi();

            var body = new GameObject("Body", typeof(RectTransform), typeof(Canvas));
            body.transform.SetParent(rootGo.transform, false);
            body.transform.localPosition = position;
            body.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var canvas = (RectTransform)body.transform;
            canvas.sizeDelta = new Vector2(centerW + (gap + listW) * 2f, height);
            canvas.localScale = Vector3.one * scale;

            var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            var boardColor = new Color(0.08f, 0.08f, 0.1f, onWorld ? 1f : 0.92f);
            UiImage(canvas, "ModelBoard", new Vector2(-listX, 0f), new Vector2(listW, height), boardColor, sprite);
            UiImage(canvas, "Board", Vector2.zero, new Vector2(centerW, height), boardColor, sprite);
            UiImage(canvas, "SongBoard", new Vector2(listX, 0f), new Vector2(listW, height), boardColor, sprite);
            ui.title = UiText(canvas, "Title", "曲", 16, new Vector2(0f, 150f), new Vector2(360f, 24f));
            ui.status = UiText(canvas, "Status", "停止中", 12, new Vector2(0f, 121f), new Vector2(360f, 32f));

            // 1つのボタンを作って、押す判定・キーの一覧に足す。label を返す
            Text AddButton(string text, string evt, KeyCode key, float x, float y, float w, float h, bool onTablet, int argument, int fontSize)
            {
                var image = UiImage(canvas, "Button_" + evt + (argument >= 0 ? "_" + argument : ""), new Vector2(x, y), new Vector2(w, h), new Color(0.25f, 0.45f, 0.8f), sprite);
                var db = UdonSharpUndo.AddComponent<DanceButton>(image.gameObject);
                db.target = onTablet ? (UdonSharpBehaviour)tablet : system;
                db.eventName = evt;
                db.argument = argument;
                // 舞台の横のパネルは、デスクトップでは視線で、VR ではレーザーで Interact して押す。人がぶつからないようにトリガーにする。
                // 手元のタブレットは、スマホ（タッチ）のときだけタップで押せるように当たり判定を付けておき、ふだんは切っておく（DanceTablet が入れる）
                var collider = image.gameObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(w, h, 10f);
                collider.isTrigger = true;
                collider.enabled = onWorld;
                ui.colliders.Add(collider);
                UdonSharpEditorUtility.GetBackingUdonBehaviour(db).interactText = string.IsNullOrEmpty(text) ? evt : text;
                UdonSharpEditorUtility.CopyProxyToUdon(db);
                ui.buttons.Add(db);
                ui.images.Add(image);
                // DanceTablet は、ボタンの中心を DanceTablet から見た位置（メートル）で、大きさをここの値（メートル）で比べる
                ui.sizes.Add(new Vector2(w, h) * 0.001f);
                ui.keys.Add(KeyInputName(key));
                var label = UiText(image.transform, "Label", text, fontSize, Vector2.zero, new Vector2(w - 6f, h));
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                if (key != KeyCode.None && !onWorld)
                {
                    var keyLabel = UiText(image.transform, "Key", "[" + KeyName(key) + "]", 8, new Vector2(3f, -2f), new Vector2(w, h));
                    keyLabel.alignment = TextAnchor.UpperLeft;
                    keyLabel.color = new Color(1f, 0.85f, 0.3f);
                    ui.keyLabels.Add(keyLabel.gameObject);
                }
                return label;
            }

            // 中央: 枠の列（ページ送りつき）。「空き」を押すとそこで踊り、自分の枠をもう一度押すとやめる
            var slotKeys = new[] { KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4 };
            AddButton("◀", "SlotPagePrev", KeyCode.F5, -172f, 55f, 28f, 44f, false, -1, 12);
            for (int i = 0; i < SlotRowsPerPage; i++)
                ui.slotTexts.Add(AddButton("", "SlotButton", slotKeys[i], -114f + 76f * i, 55f, 70f, 44f, false, i, 9));
            AddButton("▶", "SlotPageNext", KeyCode.F6, 172f, 55f, 28f, 44f, false, -1, 12);

            // 中央: 再生などのボタン（「閉じる」は手元のタブレットだけ）
            var defs = new (string text, string evt, KeyCode key, int col, int row, bool onTablet)[]
            {
                ("▶ 再生", "Play", KeyCode.Alpha1, 0, 0, false), ("■ 停止", "Stop", KeyCode.Alpha2, 1, 0, false),
                ("≪ 区切り", "SeekBack", KeyCode.Alpha3, 2, 0, false), ("区切り ≫", "SeekForward", KeyCode.Alpha4, 3, 0, false),
                ("プレビュー", "TogglePreview", KeyCode.Alpha5, 0, 1, false), ("ループ", "ToggleLoop", KeyCode.Alpha6, 1, 1, false),
                ("その場: オフ", "ToggleInPlace", KeyCode.K, 2, 1, false), ("閉じる", "Hide", KeyCode.None, 3, 1, true),
                ("開始 ◀", "RangeStartBack", KeyCode.Alpha7, 0, 2, false), ("開始 ▶", "RangeStartForward", KeyCode.Alpha8, 1, 2, false),
                ("終了 ◀", "RangeEndBack", KeyCode.Alpha9, 2, 2, false), ("終了 ▶", "RangeEndForward", KeyCode.Alpha0, 3, 2, false),
            };
            const float cellW = 86f, cellH = 52f, top = 5f;
            foreach (var d in defs)
            {
                if (d.onTablet && onWorld) continue;
                var label = AddButton(d.text, d.evt, d.key, -cellW * 1.5f + cellW * d.col, top - cellH * d.row, cellW - 8f, cellH - 10f, d.onTablet, -1, 12);
                if (d.evt == "ToggleInPlace") ui.inPlaceLabels.Add(label);
            }
            var hint = UiText(canvas, "Hint", "枠の「空き」を押すとそこで踊る（自分の枠をもう一度押すとやめる）。左でアバターを選んでから枠を押すと、その枠に置く", 9, new Vector2(0f, -148f), new Vector2(360f, 34f));
            hint.horizontalOverflow = HorizontalWrapMode.Wrap;
            hint.color = new Color(0.8f, 0.8f, 0.85f);

            // 左右の一覧（7 行とページ送り）
            List<Text> BuildList(float x, string evt, string prev, string next, KeyCode[] rowKeys, KeyCode prevKey, KeyCode nextKey, out Text header)
            {
                header = UiText(canvas, evt + "Header", "", 12, new Vector2(x, 150f), new Vector2(listW - 10f, 24f));
                var rows = new List<Text>();
                for (int i = 0; i < rowKeys.Length; i++)
                    rows.Add(AddButton("", evt, rowKeys[i], x, 115f - 38f * i, listW - 14f, 32f, false, i, 10));
                AddButton("▲", prev, prevKey, x - 44f, -150f, 80f, 28f, false, -1, 12);
                AddButton("▼", next, nextKey, x + 44f, -150f, 80f, 28f, false, -1, 12);
                return rows;
            }
            ui.modelRows = BuildList(-listX, "ModelRowButton", "ModelPagePrev", "ModelPageNext",
                new[] { KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.V, KeyCode.B, KeyCode.N, KeyCode.M }, KeyCode.Comma, KeyCode.Period, out ui.modelHeader);
            ui.songRows = BuildList(listX, "SongRowButton", "SongPagePrev", "SongPageNext",
                new[] { KeyCode.Q, KeyCode.E, KeyCode.R, KeyCode.Y, KeyCode.U, KeyCode.I, KeyCode.O }, KeyCode.LeftBracket, KeyCode.RightBracket, out ui.songHeader);

            // シークバー。body を隠すと中の UdonBehaviour が動かなくなるので、スクリプトは常に出ている根元に付け、見た目だけ body の中に置く
            ui.seekBar = UdonSharpUndo.AddComponent<DanceSeekBar>(rootGo);
            BuildUiSeekBar(ui.seekBar, canvas, new Vector2(0f, 94f), 340f, 9f, tickCount, sprite);
            return ui;
        }

        /// <summary>枠の列の1ページの数（手元のタブレットとパネルで同じ）。</summary>
        const int SlotRowsPerPage = 4;

        /// <summary>舞台の横の操作パネル。手元のタブレットと同じ画面を大きくして置く（以前は別に作っていて、機能が追いついていなかった）。</summary>
        static ControlUi BuildPanel(DanceSystem system, int tickCount)
        {
            var panel = new GameObject("Panel");
            panel.transform.position = new Vector3(-3.2f, 0f, 1.2f);
            // 幅 776 ピクセル × 2.5mm ≒ 1.9m。中心を目の高さより少し下に
            return BuildControlUi(panel, system, null, tickCount, 0.0025f, new Vector3(0f, 1.35f, 0f));
        }

        /// <summary>
        /// 手元に浮かぶタブレット（DanceTablet）。画面は BuildControlUi（1 ピクセル = 1mm）。
        /// VR では左右どちらかの手のグリップを2回握ってその手のひらの上に出し、もう一方の手の指先で押す。デスクトップでは T キーで出し、ボタンに書いたキーで押す。
        /// </summary>
        static (DanceTablet tablet, ControlUi ui) BuildTablet(DanceSystem system, int tickCount)
        {
            var rootGo = new GameObject("DanceTablet");
            var tablet = UdonSharpUndo.AddComponent<DanceTablet>(rootGo);
            var ui = BuildControlUi(rootGo, system, tablet, tickCount, 0.001f, Vector3.zero);

            // 指先の目印（タブレットとは別に置く。DanceTablet がコントローラーから割り出した位置へ動かす）
            var pointer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pointer.name = "DanceTabletPointer";
            Object.DestroyImmediate(pointer.GetComponent<Collider>());
            pointer.transform.localScale = Vector3.one * 0.012f;
            pointer.GetComponent<Renderer>().sharedMaterial = LoadOrCreateMaterial(GeneratedDir + "/Pointer.mat", new Color(1f, 0.6f, 0.1f));

            tablet.system = system;
            tablet.body = rootGo.transform.Find("Body").gameObject;
            tablet.buttons = ui.buttons.ToArray();
            tablet.buttonImages = ui.images.ToArray();
            tablet.buttonSizes = ui.sizes.ToArray();
            tablet.desktopKeys = ui.keys.ToArray();
            tablet.desktopKeyLabels = ui.keyLabels.ToArray();
            tablet.pointer = pointer.transform;
            tablet.buttonColliders = ui.colliders.ToArray();
            tablet.touchToggle = BuildTouchToggle(tablet);
            UdonSharpEditorUtility.CopyProxyToUdon(tablet);

            // プレイヤーの体とぶつからないように Walkthrough レイヤーにする
            int walkthrough = LayerMask.NameToLayer("Walkthrough");
            if (walkthrough >= 0)
            {
                foreach (var t in rootGo.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = walkthrough;
                pointer.layer = walkthrough;
                // スマホでタップして押すボタンは、Interact が届くようにふつうのレイヤーに戻す（当たり判定はトリガーなので、体はぶつからない）
                foreach (var c in ui.colliders) c.gameObject.layer = 0;
            }
            return (tablet, ui);
        }

        /// <summary>
        /// スマホ（タッチ）の人だけに出す、画面の隅の「メニュー」ボタン。タップでタブレットを出し入れする（DanceTablet が視点の前に置き続ける）。
        /// スマホには T キーも VR の握りも無く、踊っている間は舞台の横のパネルまで行けないため。
        /// </summary>
        static GameObject BuildTouchToggle(DanceTablet tablet)
        {
            var go = new GameObject("DanceTabletTouchToggle", typeof(RectTransform), typeof(Canvas));
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var canvas = (RectTransform)go.transform;
            canvas.sizeDelta = new Vector2(70f, 34f);
            canvas.localScale = Vector3.one * 0.001f;
            var sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            var image = UiImage(canvas, "Button_Toggle", Vector2.zero, new Vector2(70f, 34f), new Color(0.25f, 0.45f, 0.8f, 0.9f), sprite);
            UiText(image.transform, "Label", "メニュー", 14, Vector2.zero, new Vector2(66f, 34f));
            var collider = image.gameObject.AddComponent<BoxCollider>();
            collider.size = new Vector3(70f, 34f, 10f);
            collider.isTrigger = true;
            var db = UdonSharpUndo.AddComponent<DanceButton>(image.gameObject);
            db.target = tablet;
            db.eventName = nameof(DanceTablet.Toggle);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(db).interactText = "メニュー";
            UdonSharpEditorUtility.CopyProxyToUdon(db);
            // タップ（Interact）で押せるように、ふつうのレイヤーのまま（当たり判定はトリガーなので、体はぶつからない）
            go.SetActive(false);
            return go;
        }

        /// <summary>uGUI の Image を1つ置く（位置と大きさはピクセル。親の中心が原点）。</summary>
        static Image UiImage(Transform parent, string name, Vector2 position, Vector2 size, Color color, Sprite sprite = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
            var image = go.AddComponent<Image>();
            image.color = color;
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
            return image;
        }

        /// <summary>uGUI の Text を1つ置く（位置と大きさはピクセル。親の中心が原点）。</summary>
        static Text UiText(Transform parent, string name, string text, int fontSize, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = fontSize;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.text = text;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>
        /// uGUI のシークバーを作って seekBar に渡す。バーの根元を center（ピクセル）から左へ width/2 の位置に置き、根元の原点をバーの左端にする。
        /// 奥から 溝 → 範囲の帯 → 目盛り → 再生位置 の順に重ねる（uGUI は後の子が手前）。帯の幅は DanceSeekBar が localScale.x で決める。
        /// </summary>
        static void BuildUiSeekBar(DanceSeekBar seekBar, RectTransform parent, Vector2 center, float width, float height, int tickCount, Sprite sprite)
        {
            var barRoot = new GameObject("SeekBar", typeof(RectTransform));
            barRoot.transform.SetParent(parent, false);
            var rt = (RectTransform)barRoot.transform;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = center - new Vector2(width * 0.5f, 0f);

            UiImage(rt, "Track", new Vector2(width * 0.5f, 0f), new Vector2(width, height), new Color(0.22f, 0.22f, 0.26f), sprite);
            var fill = UiImage(rt, "Range", new Vector2(width * 0.5f, 0f), new Vector2(1f, height * 0.8f), new Color(0.25f, 0.6f, 0.95f));
            fill.transform.localScale = new Vector3(width, 1f, 1f);
            var ticks = new Transform[tickCount];
            for (int i = 0; i < tickCount; i++)
                ticks[i] = UiImage(rt, "Tick_" + i, Vector2.zero, new Vector2(1.5f, height * 1.3f), new Color(0.85f, 0.85f, 0.9f)).transform;
            var playhead = UiImage(rt, "Playhead", Vector2.zero, new Vector2(3f, height * 2.2f), new Color(1f, 0.6f, 0.1f)).transform;

            seekBar.width = width;
            seekBar.rangeFill = fill.transform;
            seekBar.playhead = playhead;
            seekBar.ticks = ticks;
            UdonSharpEditorUtility.CopyProxyToUdon(seekBar);
        }

        /// <summary>
        /// シーンの VRCWorld に登録されたネットワーク ID のうち、オブジェクトが無くなったものを消す。
        /// 自動確認で一時的に置いた着替えの台などの ID が残ると、VRChat で読み込むときに「Found N errors while configuring network IDs」となり、Udon が動かなくなる。
        /// </summary>
        public static void PruneNetworkIds()
        {
            foreach (var descriptor in Object.FindObjectsOfType<VRC.SDKBase.VRC_SceneDescriptor>(true))
            {
                var so = new SerializedObject(descriptor);
                var ids = so.FindProperty("NetworkIDs");
                if (ids == null || !ids.isArray) continue;
                int removed = 0;
                for (int i = ids.arraySize - 1; i >= 0; i--)
                {
                    var go = ids.GetArrayElementAtIndex(i).FindPropertyRelative("gameObject");
                    if (go != null && go.objectReferenceValue == null)
                    {
                        ids.DeleteArrayElementAtIndex(i);
                        removed++;
                    }
                }
                if (removed == 0) continue;
                so.ApplyModifiedProperties();
                Debug.Log($"[MmdWorld] 無くなったオブジェクトのネットワーク ID を {removed} 件消した");
            }
        }

        /// <summary>Input.GetKeyDown(string) に渡す名前。</summary>
        static string KeyInputName(KeyCode key) => key switch
        {
            KeyCode.None => "",
            KeyCode.Minus => "-",
            KeyCode.Equals => "=",
            KeyCode.LeftBracket => "[",
            KeyCode.RightBracket => "]",
            KeyCode.Comma => ",",
            KeyCode.Period => ".",
            _ => key.ToString().Replace("Alpha", "").ToLowerInvariant(),
        };

        static string KeyName(KeyCode key) => key switch
        {
            KeyCode.Minus => "-",
            KeyCode.Equals => "=",
            KeyCode.LeftBracket => "[",
            KeyCode.RightBracket => "]",
            KeyCode.Comma => ",",
            KeyCode.Period => ".",
            _ => key.ToString().Replace("Alpha", ""),
        };

        /// <summary>
        /// ワールド空間の Canvas に Text を1つ置く。1 ピクセル = 1mm。
        /// 返すのは中の Text なので、置く位置は Canvas（text.transform.parent）に設定する（Text の方に設定すると 1/1000 で効いて中央に固まる）。
        /// </summary>
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
            // Quest（Android）でも軽いように、VRChat の SDK のモバイル用のシェーダーにする（PC でも同じものを使う）
            var shader = Shader.Find("VRChat/Mobile/Standard Lite") ?? Shader.Find("Standard");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// 曲の音声に、Android（Quest・スマホ）向けの取り込みの設定を入れる（自分で入れたものがあればそのまま）。
        /// 曲の音声は全部ワールドに入っていて、最初に全部読み込まれるので、展開せずに圧縮したまま持つ（メモリが少ないため）。PC の設定は変えない。
        /// </summary>
        static void SetAndroidAudio(AudioClip clip)
        {
            if (clip == null) return;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as AudioImporter;
            if (importer == null || importer.ContainsSampleSettingsOverride("Android")) return;
            var settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.CompressedInMemory;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.6f;
            importer.SetOverrideSampleSettings("Android", settings);
            importer.SaveAndReimport();
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
