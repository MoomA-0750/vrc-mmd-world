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
            DrawTrackingControl();

            DrawSongs(songs);
            EditorGUILayout.Space(12);
            DrawDancers(settings, songs);
            EditorGUILayout.Space(12);
            DrawSlotAvatars(settings, songs);
            EditorGUILayout.Space(12);
            DrawPedestals(settings);
            EditorGUILayout.Space(12);
            DrawWorld(settings);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>VR で踊りを体に乗せる部品が無ければ、目立つように出して、入れるボタンを置く。</summary>
        void DrawTrackingControl()
        {
            if (MmdWorldLibrary.HasTrackingControl()) return;
            EditorGUILayout.HelpBox("VR 用の部品（アバター SDK の VRCSDK3A.dll）がありません。このままでは、VR で踊りが体に乗らず（その場で足踏みのようになる）、降りてもトラッキングが戻りません。\n" +
                                    "アバター用のプロジェクトの Packages/com.vrchat.avatars/Runtime/VRCSDK/Plugins/VRCSDK3A.dll を入れてから、ワールドを組み立て直してください。", MessageType.Error);
            if (GUILayout.Button("VRCSDK3A.dll を選んで入れる") && MmdWorldLibrary.InstallAvatarSdkDll())
                Show("VRCSDK3A.dll を入れました。スクリプトの読み込みが終わったら、ワールドを組み立て直してください", MessageType.Info);
            EditorGUILayout.Space(8);
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
                    var face = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent("表情（別の .vmd）", "表情だけの .vmd が別になっているとき。モーションの表情をこれで上書きする"), song.face, typeof(AnimationClip), false);
                    var audio = (AudioClip)EditorGUILayout.ObjectField("音声", song.audio, typeof(AudioClip), false);
                    float offset = EditorGUILayout.FloatField(new GUIContent("音のずれ（秒）", "音をモーションより何秒遅らせるか。音が早いときは +"), song.audioOffset);
                    float step = EditorGUILayout.FloatField(new GUIContent("区切りの間隔（秒）", "シーク・範囲再生・途中からの参加の区切り。細かいほどステーション用の Controller が増える。区切りの時刻を直接並べたいときは曲のアセットの seekPoints に入れる"), song.seekStep);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(song, "曲の設定");
                        song.motion = motion;
                        song.face = face;
                        song.audio = audio;
                        song.audioOffset = offset;
                        song.seekStep = Mathf.Max(2f, step);
                        EditorUtility.SetDirty(song);
                    }

                    if (!MmdWorldLibrary.IsDance(song))
                        EditorGUILayout.HelpBox("このモーションは踊りではありません（表情だけ・カメラなど）。組み立てでは飛ばします。消すか、踊りの曲の「表情」に入れてください", MessageType.Warning);
                    EditorGUILayout.LabelField($"区切り {MmdWorldLibrary.Segments(song).Count} 個" + (song.seekPoints != null && song.seekPoints.Count > 0 ? "（時刻を直接指定）" : ""), EditorStyles.miniLabel);
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
            GUI.Box(rect, ".vmd と音声（wav / mp3 / ogg）をここにドロップ（配布物をまとめてでもよい）\n踊りの .vmd ごとに曲を足す。表情だけの .vmd は踊りに重ね、カメラの .vmd は飛ばす。音声が1つなら全部に付ける", EditorStyles.helpBox);
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
                var plan = PlanDropped(paths, MmdWorldLibrary.Classify);
                foreach (var (vmd, audio, face) in plan.Songs)
                    added.Add(MmdWorldLibrary.AddSong(vmd, audio, null, face).DisplayTitle);
                string message = $"曲を {added.Count} つ足しました（{string.Join("、", added)}）。";
                if (plan.Skipped.Count > 0) message += $"\n曲にしなかったもの: {string.Join("、", plan.Skipped)}。";
                Show(message + "\n「ワールドを組み立て直す」でシーンに反映されます", added.Count > 0 ? MessageType.Info : MessageType.Warning);
            }
            catch (System.Exception ex)
            {
                Show("足せませんでした: " + ex.Message, MessageType.Error);
            }
        }

        public sealed class DropPlan
        {
            public readonly List<(string vmd, string audio, string face)> Songs = new List<(string, string, string)>();
            /// <summary>曲にしなかったファイルと理由（カメラ・キー無し・組む相手の無い表情）</summary>
            public readonly List<string> Skipped = new List<string>();
        }

        /// <summary>
        /// ドロップされたファイルから、足す曲を決める。MMD の配布物をまとめて落としてもよいように:
        /// - 踊りの .vmd ごとに1曲。表情だけの .vmd は踊りに重ねる（1つなら全部に、複数なら名前が近いものに）。カメラ・キー無しは飛ばす
        /// - 音声は、1つなら全部の曲に、複数なら拡張子を除いた名前が同じものに付ける
        /// </summary>
        public static DropPlan PlanDropped(IEnumerable<string> paths, System.Func<string, MmdWorldLibrary.VmdKind> classify)
        {
            var plan = new DropPlan();
            var list = paths.ToList();
            var audios = list.Where(MmdWorldLibrary.IsAudioFile).ToList();
            var dances = new List<string>();
            var faces = new List<string>();
            foreach (var vmd in list.Where(MmdWorldLibrary.IsVmdFile))
            {
                var kind = classify(vmd);
                if (kind == MmdWorldLibrary.VmdKind.Dance) dances.Add(vmd);
                else if (kind == MmdWorldLibrary.VmdKind.Face) faces.Add(vmd);
                else plan.Skipped.Add($"{Path.GetFileName(vmd)}（{MmdWorldLibrary.KindName(kind)}）");
            }
            string Name(string p) => Path.GetFileNameWithoutExtension(p);
            foreach (var dance in dances)
            {
                string audio = audios.Count == 1 ? audios[0] : audios.FirstOrDefault(a => Name(a) == Name(dance));
                string face = faces.Count == 1 ? faces[0] : faces.OrderByDescending(f => CommonPrefix(Name(f), Name(dance))).FirstOrDefault();
                plan.Songs.Add((dance, audio, face));
            }
            if (dances.Count == 0)
                foreach (var face in faces) plan.Skipped.Add($"{Path.GetFileName(face)}（表情だけのモーション。踊りの .vmd と一緒にドロップする）");
            return plan;
        }

        static int CommonPrefix(string a, string b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
            return n;
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

        // ---- 枠で踊らせるアバター ----

        void DrawSlotAvatars(MmdWorldSettings settings, List<DanceSong> songs)
        {
            EditorGUILayout.LabelField("枠で踊らせるアバター（ワールドに入れる）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("ワールドに入れておき、人と同じ枠で踊らせるアバター。どの枠で踊らせるかは、ワールドの中でタブレットの「選ぶ」から決めます（人が入っている枠には出ません）。\n" +
                "VRChat のアバターの prefab もそのまま使えます（Avatar Descriptor などワールドで使えない部品は組み立てのときに外し、揺れものは残します）。シェーダーが lilToon ならワールドのプロジェクトにも lilToon を入れてください。\n" +
                "ワールドを公開すると、入れたアバターのデータも来た人に配られます。購入したアバターの多くは規約でこれを禁じているので、公開するワールドでは規約で許されたものだけを使ってください。", MessageType.None);

            for (int i = 0; i < settings.slotAvatars.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var model = (GameObject)EditorGUILayout.ObjectField(settings.slotAvatars[i], typeof(GameObject), false);
                    if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(settings, "枠のアバター"); settings.slotAvatars[i] = model; Save(settings); }
                    if (GUILayout.Button("外す", GUILayout.Width(44))) { Undo.RecordObject(settings, "枠のアバター"); settings.slotAvatars.RemoveAt(i); Save(settings); GUIUtility.ExitGUI(); }
                }
                if (settings.slotAvatars[i] != null) DrawAvatarCheck(MmdWorldLibrary.CheckAvatar(settings.slotAvatars[i], songs));
            }
            if (GUILayout.Button("＋ 枠で踊らせるアバターを足す")) { Undo.RecordObject(settings, "枠のアバター"); settings.slotAvatars.Add(null); Save(settings); }
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
            int controllers = MmdWorldLibrary.Songs().Sum(sg => MmdWorldLibrary.Segments(sg).Count);
            EditorGUILayout.LabelField($"ステーション {slots} 個（枠ごとに1つ）、ステーション用の Controller {controllers} 個（曲 × 区切り）", EditorStyles.miniLabel);

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
