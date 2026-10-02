using System;
using System.Linq;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// ClientSim の Play モードで、枠に入る → 再生 を自動で行い、様子をログに出す。画面を人が触らずに動作を確かめるためのもの。
    /// メニューの「MMD World/確認: 枠に入って再生」か、コマンドラインの -mmdPlayCheck [dance] で Unity を開くと動く。
    /// dance を付けると自分も枠に入る（付けないとお手本の人形と音だけ）。
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeCheck
    {
        const string PendingKey = "MmdWorld.PlayModeCheck.Pending";
        const string DanceKey = "MmdWorld.PlayModeCheck.Dance";
        static double _startedAt;
        static double _avatarsAt;
        static Animator _localAvatar;
        static readonly string[] WatchedShapes = { "あ", "い", "う", "お", "まばたき", "笑い", "ウィンク" };
        static double _shotsFrom;
        static int _shot;
        const int ShotCount = 20;
        const double ShotInterval = 1.0;
        /// <summary>客席側から舞台を撮った画像の置き場所（プロジェクトの Temp の下）。</summary>
        public const string ShotDir = "Temp/MmdPlayCheck";
        static int _step;

        static PlayModeCheck()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-mmdPlayCheck");
            if (i >= 0 && !SessionState.GetBool("MmdWorld.PlayModeCheck.Launched", false))
            {
                SessionState.SetBool("MmdWorld.PlayModeCheck.Launched", true);
                bool dance = i + 1 < args.Length && args[i + 1] == "dance";
                // エディタの準備ができてから始める
                EditorApplication.delayCall += () => Begin(dance);
            }
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("MMD World/確認: 枠に入って再生")]
        static void MenuDance() => Begin(true);

        [MenuItem("MMD World/確認: お手本だけ再生")]
        static void MenuPreview() => Begin(false);

        public static void Begin(bool dance)
        {
            // Play 中なら、いったん止めて編集モードに戻ってからやり直す（同じフレームで止めて始めると、始める方が効かない）
            if (EditorApplication.isPlaying)
            {
                void Restart(PlayModeStateChange change)
                {
                    if (change != PlayModeStateChange.EnteredEditMode) return;
                    EditorApplication.playModeStateChanged -= Restart;
                    Begin(dance);
                }
                EditorApplication.playModeStateChanged += Restart;
                EditorApplication.isPlaying = false;
                return;
            }
            EditorSceneManager.OpenScene(WorldBuilder.ScenePath);
            SessionState.SetBool(PendingKey, true);
            SessionState.SetBool(DanceKey, dance);
            EditorApplication.isPlaying = true;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(PendingKey, false)) return;
            SessionState.SetBool(PendingKey, false);
            _startedAt = EditorApplication.timeSinceStartup;
            _step = 0;
            _shot = 0;
            _shotsFrom = 0;
            _avatarsAt = 0;
            _localAvatar = null;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.update -= Tick;
                return;
            }
            double t = EditorApplication.timeSinceStartup - _startedAt;
            // U# の代理コンポーネントは Play 中は無効になっていることがあるので、無効なものも含めて探す
            var system = Object.FindObjectsOfType<DanceSystem>(true).FirstOrDefault();
            var slots = Object.FindObjectsOfType<DanceSlot>(true).OrderBy(s => s.transform.parent.name).ToArray();
            if (system == null) return;

            // ClientSim がプレイヤーを出すまで少し待つ
            if (_step == 0 && t > 3 && Utilities.IsValid(Networking.LocalPlayer))
            {
                _step = 1;
                if (SessionState.GetBool(DanceKey, false) && slots.Length > 0)
                {
                    Send(slots[0], "_interact");
                    Log("枠1を押した");
                }
            }
            else if (_step == 1 && t > 4)
            {
                _step = 2;
                Send(system, "Play");
                Log("再生を押した");
                // 踊りが始まる瞬間に、手元のアバターをステーションと同じ Animator で踊らせる（VRChat でステーションに座ったときの再現）
                _avatarsAt = EditorApplication.timeSinceStartup + system.countdownSeconds;
            }
            if (_avatarsAt > 0 && EditorApplication.timeSinceStartup >= _avatarsAt)
            {
                _avatarsAt = 0;
                SpawnLocalAvatars(slots);
                _shotsFrom = EditorApplication.timeSinceStartup;
            }
            if (_shotsFrom > 0 && _shot < ShotCount && EditorApplication.timeSinceStartup >= _shotsFrom + _shot * ShotInterval)
                SaveShot(_shot++);
            else if (_step >= 2 && _step < 12 && t > 4 + (_step - 1) * 2)
            {
                _step++;
                var player = Networking.LocalPlayer;
                var audio = system.audioSource;
                var preview = system.previewDancers.FirstOrDefault();
                var hips = preview != null ? preview.GetBoneTransform(HumanBodyBones.Hips) : null;
                var hand = preview != null ? preview.GetBoneTransform(HumanBodyBones.RightHand) : null;
                Log($"状態 {system.statusText.text} / 音 {(audio.isPlaying ? audio.time.ToString("F2") + "秒" : "止まっている")}" +
                    $" / 自分の位置 {player.GetPosition()}" +
                    $" / お手本の腰 {(hips != null ? hips.position.ToString("F2") : "-")} 右手 {(hand != null ? hand.position.ToString("F2") : "-")}");
            }
            else if (_step == 12 && _shot >= ShotCount)
            {
                _step = 13;
                Log("確認を終えた");
                EditorApplication.update -= Tick;
            }
        }

        /// <summary>
        /// Assets/LocalOnly/ にある Humanoid のモデル（リポジトリに入れない手元のアバター）を、枠2から順に置き、
        /// 1曲目のステーション用の Animator で踊らせる。Play を止めれば消える。
        /// </summary>
        static void SpawnLocalAvatars(DanceSlot[] slots)
        {
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/MmdWorld/Generated/Station_Song0.controller");
            if (controller == null || !AssetDatabase.IsValidFolder("Assets/LocalOnly")) return;
            int slot = 1;
            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { "Assets/LocalOnly" }))
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                var modelAnimator = model != null ? model.GetComponent<Animator>() : null;
                if (modelAnimator == null || modelAnimator.avatar == null || !modelAnimator.avatar.isHuman) continue;
                if (slot >= slots.Length) break;
                var anchor = slots[slot++].transform.parent;
                var go = Object.Instantiate(model, anchor.position, anchor.rotation);
                go.name = "LocalOnly_" + model.name;
                var animator = go.GetComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (_localAvatar == null) _localAvatar = animator;
                Log($"手元のアバター {model.name} を {anchor.name} で踊らせた");
            }

        }

        /// <summary>
        /// 客席側から舞台を撮る。手元のアバターがいれば、顔のアップも撮り、顔の BlendShape の値をログに出す（表情が動いているかの確認）。
        /// </summary>
        static void SaveShot(int index)
        {
            System.IO.Directory.CreateDirectory(ShotDir);
            Render($"{ShotDir}/shot{index:00}.png", new Vector3(1.4f, 1.3f, 1.0f), new Vector3(1.4f, 0.8f, 4.5f), 55f);
            // 舞台から客席を見る（踊る人から見た景色）
            if (index == 0) Render($"{ShotDir}/from_stage.png", new Vector3(0f, 1.5f, 4.5f), new Vector3(0f, 1.3f, 0f), 60f);
            if (_localAvatar == null) return;

            var head = _localAvatar.GetBoneTransform(HumanBodyBones.Head);
            // アバターは客席（-Z）を向いているので、顔の前 0.6m から見る
            var face = head.position + new Vector3(0f, 0.08f, 0f);
            Render($"{ShotDir}/face{index:00}.png", face + _localAvatar.transform.forward * 0.6f, face, 30f);

            var body = _localAvatar.transform.Find(WorldBuilderFacePath);
            var smr = body != null ? body.GetComponent<SkinnedMeshRenderer>() : null;
            if (smr == null)
            {
                Log($"顔のメッシュ {WorldBuilderFacePath} がアバターの直下に無い");
                return;
            }
            var values = WatchedShapes.Select(n =>
            {
                int i = smr.sharedMesh.GetBlendShapeIndex(n);
                return i < 0 ? $"{n}=無し" : $"{n}={smr.GetBlendShapeWeight(i):F0}";
            });
            Log($"顔 {index:00}: " + string.Join(" ", values));
        }

        const string WorldBuilderFacePath = "Body";

        static void Render(string path, Vector3 from, Vector3 to, float fov)
        {
            var go = new GameObject("PlayCheckCamera");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(to - from));
                cam.nearClipPlane = 0.05f;
                cam.fieldOfView = fov;
                cam.clearFlags = CameraClearFlags.Skybox;
                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static void Send(Component proxy, string eventName)
        {
            var behaviour = UdonSharpEditorUtility.GetBackingUdonBehaviour((UdonSharp.UdonSharpBehaviour)proxy);
            behaviour.SendCustomEvent(eventName);
        }

        static void Log(string message) => Debug.Log("[MmdWorld.PlayCheck] " + message);
    }
}
