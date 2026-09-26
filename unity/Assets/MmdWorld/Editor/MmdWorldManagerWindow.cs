using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// 曲・お手本のアバター・着替えの台・枠の数をまとめて扱うウィンドウ。
    /// .vmd と音声（エクスプローラーからでも Project からでも）をドロップすると曲が1つ増え、「ワールドを組み立て直す」でシーンに反映される。
    /// </summary>
    public sealed class MmdWorldManagerWindow : EditorWindow
    {
        Vector2 _scroll;
        string _message;
        MessageType _messageType;

        [MenuItem("MMD World/マネージャー", priority = 0)]
        public static void Open() => GetWindow<MmdWorldManagerWindow>("MMD World");

        void OnGUI()
        {
            var settings = MmdWorldSettings.LoadOrCreate();
            var songs = MmdWorldLibrary.Songs();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, _messageType);

            DrawSongs(songs);
            EditorGUILayout.Space(12);
            DrawDancers(settings, songs);
            EditorGUILayout.Space(12);
            DrawPedestals(settings);
            EditorGUILayout.Space(12);
            DrawWorld(settings);
            EditorGUILayout.EndScrollView();
        }

        // ---- 曲 ----

        void DrawSongs(List<DanceSong> songs)
        {
            EditorGUILayout.LabelField($"曲（{songs.Count}）", EditorStyles.boldLabel);
            DrawDropArea();

            for (int i = 0; i < songs.Count; i++)
            {
                var song = songs[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        string title = EditorGUILayout.TextField(song.title);
                        if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(song, "曲の題名"); song.title = title; EditorUtility.SetDirty(song); }
                        using (new EditorGUI.DisabledScope(i == 0))
                            if (GUILayout.Button("▲", GUILayout.Width(24))) { MmdWorldLibrary.Move(song, -1); GUIUtility.ExitGUI(); }
                        using (new EditorGUI.DisabledScope(i == songs.Count - 1))
                            if (GUILayout.Button("▼", GUILayout.Width(24))) { MmdWorldLibrary.Move(song, 1); GUIUtility.ExitGUI(); }
                        if (GUILayout.Button("削除", GUILayout.Width(44)) &&
                            EditorUtility.DisplayDialog("曲を消す", $"「{song.DisplayTitle}」を消します。Songs の下のこの曲のフォルダ（モーションと音声も）ごと消えます。", "消す", "やめる"))
                        {
                            MmdWorldLibrary.RemoveSong(song);
                            Show("曲を消しました。「ワールドを組み立て直す」でシーンに反映されます", MessageType.Info);
                            GUIUtility.ExitGUI();
                        }
                    }

                    EditorGUI.BeginChangeCheck();
                    var motion = (AnimationClip)EditorGUILayout.ObjectField("モーション", song.motion, typeof(AnimationClip), false);
                    var audio = (AudioClip)EditorGUILayout.ObjectField("音声", song.audio, typeof(AudioClip), false);
                    float offset = EditorGUILayout.FloatField(new GUIContent("音のずれ（秒）", "音をモーションより何秒遅らせるか。音が早いときは +"), song.audioOffset);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(song, "曲の設定");
                        song.motion = motion;
                        song.audio = audio;
                        song.audioOffset = offset;
                        EditorUtility.SetDirty(song);
                    }

                    var (m, a) = MmdWorldLibrary.Lengths(song);
                    string lengths = $"長さ: モーション {Format(m)}" + (song.audio != null ? $" / 音声 {Format(a)}" : " / 音声なし");
                    if (song.audio != null && Mathf.Abs(m - a - song.audioOffset) > 2f)
                        EditorGUILayout.HelpBox(lengths + "。長さが2秒以上違います。別の曲の音声か、音のずれの設定を確かめてください", MessageType.Warning);
                    else
                        EditorGUILayout.LabelField(lengths, EditorStyles.miniLabel);
                }
            }
            if (GUI.changed) AssetDatabase.SaveAssets();
        }

        void DrawDropArea()
        {
            var rect = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));
            GUI.Box(rect, ".vmd と音声（wav / mp3 / ogg）をここにドロップ\n1つの .vmd に1つの音声、または同じ名前どうしを組にして曲を足します", EditorStyles.helpBox);
            var e = Event.current;
            if (!rect.Contains(e.mousePosition) || (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)) return;

            var paths = DragAndDrop.paths.Select(ToFullPath).ToList();
            bool any = paths.Any(MmdWorldLibrary.IsVmdFile);
            DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && any)
            {
                DragAndDrop.AcceptDrag();
                AddDropped(paths);
                e.Use();
            }
        }

        void AddDropped(List<string> paths)
        {
            var added = new List<string>();
            try
            {
                foreach (var (vmd, audio) in PairFiles(paths))
                    added.Add(MmdWorldLibrary.AddSong(vmd, audio).DisplayTitle);
                Show($"曲を {added.Count} つ足しました（{string.Join("、", added)}）。「ワールドを組み立て直す」でシーンに反映されます", MessageType.Info);
            }
            catch (System.Exception ex)
            {
                Show("足せませんでした: " + ex.Message, MessageType.Error);
            }
        }

        /// <summary>.vmd と音声を組にする。.vmd と音声が1つずつならその2つ、そうでなければ拡張子を除いた名前が同じものどうし。</summary>
        public static List<(string vmd, string audio)> PairFiles(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            var vmds = list.Where(MmdWorldLibrary.IsVmdFile).ToList();
            var audios = list.Where(MmdWorldLibrary.IsAudioFile).ToList();
            if (vmds.Count == 1 && audios.Count == 1) return new List<(string, string)> { (vmds[0], audios[0]) };
            return vmds.Select(v => (v, audios.FirstOrDefault(a =>
                Path.GetFileNameWithoutExtension(a) == Path.GetFileNameWithoutExtension(v)))).ToList();
        }

        static string ToFullPath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

        // ---- お手本 ----

        void DrawDancers(MmdWorldSettings settings, List<DanceSong> songs)
        {
            EditorGUILayout.LabelField("お手本のアバター", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("舞台の奥で曲に合わせて踊るモデル（Humanoid の FBX や prefab）。空なら付属の人形。\n" +
                "購入したアバターをワールドに入れて公開すると、多くの規約で再配布にあたります。公開するワールドでは、規約で許されたものだけを使ってください。", MessageType.None);

            for (int i = 0; i < settings.previewDancers.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var model = (GameObject)EditorGUILayout.ObjectField(settings.previewDancers[i], typeof(GameObject), false);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(settings, "お手本"); settings.previewDancers[i] = model; Save(settings); }
                    if (GUILayout.Button("外す", GUILayout.Width(44))) { Undo.RecordObject(settings, "お手本"); settings.previewDancers.RemoveAt(i); Save(settings); GUIUtility.ExitGUI(); }
                }
                var check = MmdWorldLibrary.CheckAvatar(settings.previewDancers[i], songs);
                if (settings.previewDancers[i] != null) DrawAvatarCheck(check);
            }
            if (GUILayout.Button("＋ お手本を足す")) { Undo.RecordObject(settings, "お手本"); settings.previewDancers.Add(null); Save(settings); }
        }

        static void DrawAvatarCheck(MmdWorldLibrary.AvatarCheck check)
        {
            if (!check.IsHumanoid)
            {
                EditorGUILayout.HelpBox("Humanoid ではありません。モデルの Rig を Humanoid にしてください（このままでは置かれません）", MessageType.Error);
                return;
            }
            string face = !check.HasFaceMesh ? "表情のメッシュ（Body）が無いので表情は動きません"
                : $"表情 {check.MorphsFound}/{check.MorphsWanted}" + (check.Missing.Count > 0 ? $"（無いもの: {string.Join("、", check.Missing.Take(8))}{(check.Missing.Count > 8 ? " ほか" : "")}）" : "");
            if (check.IsLocalOnly) face += "\nAssets/LocalOnly/ のモデルです。リポジトリに入らないので、ほかの人の手元ではシーンから抜けます";
            EditorGUILayout.HelpBox(face, check.HasFaceMesh && check.MorphsFound == check.MorphsWanted ? MessageType.Info : MessageType.Warning);
        }

        // ---- 着替えの台 ----

        void DrawPedestals(MmdWorldSettings settings)
        {
            EditorGUILayout.LabelField("着替えの台（アバター ID）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("VRChat にアップロード済みで公開（Public）のアバターの ID（avtr_...）。触るとそのアバターに着替えられる台が置かれます。", MessageType.None);
            for (int i = 0; i < settings.pedestalAvatarIds.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    string id = EditorGUILayout.TextField(settings.pedestalAvatarIds[i]);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(settings, "着替えの台"); settings.pedestalAvatarIds[i] = id.Trim(); Save(settings); }
                    if (GUILayout.Button("外す", GUILayout.Width(44))) { Undo.RecordObject(settings, "着替えの台"); settings.pedestalAvatarIds.RemoveAt(i); Save(settings); GUIUtility.ExitGUI(); }
                }
                string current = settings.pedestalAvatarIds[i];
                if (!string.IsNullOrEmpty(current) && !MmdWorldLibrary.IsValidAvatarId(current))
                    EditorGUILayout.HelpBox("ID の形が違います（avtr_ と36文字の英数字・ハイフン）", MessageType.Warning);
            }
            if (GUILayout.Button("＋ 着替えの台を足す")) { Undo.RecordObject(settings, "着替えの台"); settings.pedestalAvatarIds.Add(""); Save(settings); }
        }

        // ---- ワールド ----

        void DrawWorld(MmdWorldSettings settings)
        {
            EditorGUILayout.LabelField("ワールド", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            int slots = EditorGUILayout.IntSlider("踊る人の枠", settings.slotCount, 1, 16);
            float countdown = EditorGUILayout.Slider("カウントダウン（秒）", settings.countdownSeconds, 0f, 10f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(settings, "ワールドの設定");
                settings.slotCount = slots;
                settings.countdownSeconds = countdown;
                Save(settings);
            }
            int stations = slots * MmdWorldLibrary.Songs().Count;
            EditorGUILayout.LabelField($"ステーションの数: 枠 {slots} × 曲 {MmdWorldLibrary.Songs().Count} = {stations}", EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            if (GUILayout.Button("ワールドを組み立て直す", GUILayout.Height(32)))
            {
                if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                WorldBuilder.Build();
                Show("組み立て直しました: " + WorldBuilder.ScenePath, MessageType.Info);
                GUIUtility.ExitGUI();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Play で確かめる")) PlayModeCheck.Begin(false);
                if (GUILayout.Button("VRChat で試す（Build & Test）")) DevCommands.BuildAndTest(false);
            }
        }

        static void Save(MmdWorldSettings settings)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        void Show(string message, MessageType type)
        {
            _message = message;
            _messageType = type;
            Repaint();
        }

        static string Format(float seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
    }
}
