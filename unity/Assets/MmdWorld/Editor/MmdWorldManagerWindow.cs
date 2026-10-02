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
            DrawExport(songs);

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
                    // 複数人のモーションの 2人目以降。最後の空欄に入れると増え、空にすると減る
                    var parts = new List<AnimationClip>(song.parts ?? new List<AnimationClip>());
                    bool partsChanged = false;
                    for (int p = 0; p <= parts.Count; p++)
                    {
                        var current = p < parts.Count ? parts[p] : null;
                        var picked = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent($"{p + 2}人目のパート", "複数人のモーションのとき。枠1 がモーション、枠2 がここの1つ目 …と順に踊る。立ち位置は .vmd に入っている位置"), current, typeof(AnimationClip), false);
                        if (picked == current) continue;
                        partsChanged = true;
                        if (p < parts.Count) parts[p] = picked; else parts.Add(picked);
                    }
                    // 複数人のモーションの立ち位置のずれ（パートが2つ以上のときだけ）。0 番目が1人目（モーション）
                    var offsets = new List<Vector3>(song.partOffsets ?? new List<Vector3>());
                    bool offsetsChanged = false;
                    if (parts.Count(c => c != null) > 0)
                    {
                        EditorGUILayout.LabelField(new GUIContent("立ち位置のずれ（m）", "配布物の立ち位置がきれいに合っていないときに直す。右: 踊る人から見て右が +、前: 客席の方が +"), EditorStyles.miniBoldLabel);
                        int partCount = 1 + parts.Count(c => c != null);
                        for (int p = 0; p < partCount; p++)
                        {
                            var current = p < offsets.Count ? offsets[p] : Vector3.zero;
                            var v = EditorGUILayout.Vector2Field($"　{p + 1}人目（右・前）", new Vector2(current.x, current.z));
                            if (Mathf.Approximately(v.x, current.x) && Mathf.Approximately(v.y, current.z)) continue;
                            offsetsChanged = true;
                            while (offsets.Count <= p) offsets.Add(Vector3.zero);
                            offsets[p] = new Vector3(v.x, 0f, v.y);
                        }
                    }
                    var audio = (AudioClip)EditorGUILayout.ObjectField("音声", song.audio, typeof(AudioClip), false);
                    float offset;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        offset = EditorGUILayout.FloatField(new GUIContent("音のずれ（秒）", "音をモーションより何秒遅らせるか。音が早いときは +"), song.audioOffset);
                        using (new EditorGUI.DisabledScope(song.audio == null))
                            if (GUILayout.Button(new GUIContent("合わせる…", "音声の波形とモーションの動きを並べて見ながら、再生して合わせるウィンドウを開く"), GUILayout.Width(72)))
                                AudioOffsetWindow.Open(song);
                    }
                    float step = EditorGUILayout.FloatField(new GUIContent("区切りの間隔（秒）", "シーク・範囲再生・途中からの参加の区切り。細かいほどステーション用の Controller が増える。区切りの時刻を直接並べたいときは曲のアセットの seekPoints に入れる"), song.seekStep);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(song, "曲の設定");
                        song.motion = motion;
                        song.face = face;
                        if (partsChanged) song.parts = parts.Where(c => c != null).ToList();
                        if (offsetsChanged) song.partOffsets = offsets;
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

        // フォルダから見つけた曲の候補（「フォルダから曲を探す」を押したときに作り、登録するかやめるまで出す）
        List<PlannedSong> _detected;
        List<bool> _detectedPicked;
        List<string> _detectedSkipped;

        /// <summary>探すフォルダを変えて（null なら今のまま）、「フォルダから曲を探す」を押したところにする（DevCommands から）。</summary>
        public void ShowDetected(string folder)
        {
            var settings = MmdWorldSettings.LoadOrCreate();
            if (!string.IsNullOrEmpty(folder)) { settings.autoAddFolder = folder; Save(settings); }
            _detected = MmdWorldLibrary.DetectSongs(settings, out _detectedSkipped);
            _detectedPicked = _detected.Select(_ => true).ToList();
            Repaint();
        }

        /// <summary>
        /// フォルダの .vmd から曲を作る: 探すフォルダ、「フォルダから曲を探す」（登録する前に、どう登録するかの一覧を見せる）、組み立てのときに自動で作るか（初期は切る）。
        /// </summary>
        void DrawDetect(MmdWorldSettings settings)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("フォルダの .vmd から曲を作る", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            string folder = EditorGUILayout.TextField(new GUIContent("探すフォルダ", "この下だけを探す（Assets/ から書く）"), settings.autoAddFolder);
            bool autoAdd = EditorGUILayout.Toggle(new GUIContent("組み立てのときに自動で作る", "組み立てのとき、探すフォルダの、まだ曲になっていない .vmd から曲を作る（下の「フォルダから曲を探す」と同じ決め方）。切っておけば、自分で足した曲だけになる"), settings.autoAddSongs);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(settings, "ワールドの設定");
                settings.autoAddFolder = folder;
                settings.autoAddSongs = autoAdd;
                Save(settings);
                _detected = null;
            }
            if (GUILayout.Button(new GUIContent("フォルダから曲を探す", "探すフォルダの、まだ曲になっていない .vmd から作れる曲を一覧にする。登録は一覧を見てから選ぶ")))
            {
                _detected = MmdWorldLibrary.DetectSongs(settings, out _detectedSkipped);
                _detectedPicked = _detected.Select(_ => true).ToList();
            }
            if (_detected == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_detected.Count == 0) EditorGUILayout.LabelField("まだ曲になっていない踊りの .vmd はありませんでした", EditorStyles.wordWrappedMiniLabel);
                for (int i = 0; i < _detected.Count; i++)
                {
                    var d = _detected[i];
                    _detectedPicked[i] = EditorGUILayout.ToggleLeft(d.title + (d.parts.Count > 0 ? $"（{d.parts.Count + 1}人）" : ""), _detectedPicked[i], EditorStyles.boldLabel);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.LabelField("モーション", d.vmd, EditorStyles.miniLabel);
                        for (int p = 0; p < d.parts.Count; p++) EditorGUILayout.LabelField($"{p + 2}人目", d.parts[p], EditorStyles.miniLabel);
                        EditorGUILayout.LabelField("表情", string.IsNullOrEmpty(d.face) ? "なし" : d.face, EditorStyles.miniLabel);
                        EditorGUILayout.LabelField("音声", string.IsNullOrEmpty(d.audio) ? "なし（同じフォルダに音声が無いか、2つ以上ある）" : d.audio, EditorStyles.miniLabel);
                    }
                }
                if (_detectedSkipped != null && _detectedSkipped.Count > 0)
                    EditorGUILayout.LabelField("曲にしないもの: " + string.Join("、", _detectedSkipped), EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!_detectedPicked.Any(x => x)))
                        if (GUILayout.Button($"選んだ {_detectedPicked.Count(x => x)} 曲を登録する"))
                        {
                            var added = _detected.Where((_, k) => _detectedPicked[k]).Select(d => MmdWorldLibrary.CreateSongInPlace(d).DisplayTitle).ToList();
                            Show($"曲を {added.Count} つ登録しました（{string.Join("、", added)}）。ファイルはその場所のまま使います。「ワールドを組み立て直す」でシーンに反映されます", MessageType.Info);
                            _detected = null;
                            GUIUtility.ExitGUI();
                        }
                    if (GUILayout.Button("閉じる")) _detected = null;
                }
            }
        }

        /// <summary>曲の一覧の書き出し・読み込み（JSON は設定だけ、.unitypackage は素材ごと）。</summary>
        void DrawExport(List<DanceSong> songs)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(songs.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("JSON に書き出す", "題名・モーション・音声・音のずれ・立ち位置のずれなどを書き出す。素材はパスと GUID で指すだけ")))
                    {
                        string path = EditorUtility.SaveFilePanel("曲の一覧を書き出す", "", "mmd-world-songs.json", "json");
                        if (!string.IsNullOrEmpty(path))
                        {
                            MmdWorldExport.ExportJson(path);
                            Show($"{songs.Count} 曲を書き出しました: {path}", MessageType.Info);
                        }
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button(new GUIContent("素材ごと書き出す", "曲の設定とモーション・音声を .unitypackage にまとめる。別のプロジェクトで Import すれば曲がそのまま入る（スクリプトは入れない）")))
                    {
                        string path = EditorUtility.SaveFilePanel("曲を素材ごと書き出す", "", "mmd-world-songs.unitypackage", "unitypackage");
                        if (!string.IsNullOrEmpty(path))
                        {
                            var assets = MmdWorldExport.ExportPackage(path);
                            Show($"{songs.Count} 曲（{assets.Count} ファイル）を書き出しました: {path}", MessageType.Info);
                        }
                        GUIUtility.ExitGUI();
                    }
                }
                if (GUILayout.Button(new GUIContent("JSON から読み込む", "書き出した JSON の設定を戻す。同じモーションの曲は書き戻し、無ければ JSON が指す素材で曲を作る")))
                {
                    string path = EditorUtility.OpenFilePanel("曲の一覧を読み込む", "", "json");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var r = MmdWorldExport.ImportJson(path);
                        Show($"読み込みました: 足した曲 {r.added}、書き戻した曲 {r.updated}" +
                             (r.missing.Count > 0 ? $"\n見つからなかった素材 {r.missing.Count} 件:\n" + string.Join("\n", r.missing.Take(10)) : ""),
                             r.missing.Count > 0 ? MessageType.Warning : MessageType.Info);
                    }
                    GUIUtility.ExitGUI();
                }
            }
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
                foreach (var song in plan.Songs)
                    added.Add(MmdWorldLibrary.AddSong(song.vmd, song.audio, song.title, song.face, song.parts).DisplayTitle + (song.parts.Count > 0 ? $"（{song.parts.Count + 1}人）" : ""));
                string message = $"曲を {added.Count} つ足しました（{string.Join("、", added)}）。";
                if (plan.Skipped.Count > 0) message += $"\n曲にしなかったもの: {string.Join("、", plan.Skipped)}。";
                Show(message + "\n「ワールドを組み立て直す」でシーンに反映されます", added.Count > 0 ? MessageType.Info : MessageType.Warning);
            }
            catch (System.Exception ex)
            {
                Show("足せませんでした: " + ex.Message, MessageType.Error);
            }
        }

        public sealed class PlannedSong
        {
            public string title;
            /// <summary>1人目（センター）のパート</summary>
            public string vmd;
            /// <summary>2人目以降のパート</summary>
            public List<string> parts = new List<string>();
            public string audio;
            public string face;
        }

        public sealed class DropPlan
        {
            public readonly List<PlannedSong> Songs = new List<PlannedSong>();
            /// <summary>曲にしなかったファイルと理由（カメラ・キー無し・組む相手の無い表情）</summary>
            public readonly List<string> Skipped = new List<string>();
        }

        /// <summary>
        /// ドロップされたファイルから、足す曲を決める。MMD の配布物をまとめて落としてもよいように:
        /// - 踊りの .vmd は、名前の末尾（_center・_left・_right・_1・_A・左・右 など）だけが違うものを、複数人のモーションの1曲にまとめる（センターが1人目）。
        ///   それ以外は .vmd ごとに1曲
        /// - 表情だけの .vmd は踊りに重ねる（1つなら全部に、複数なら名前が近いものに）。カメラ・キー無しは飛ばす
        /// - 音声は、1つなら全部の曲に、複数なら拡張子を除いた名前（パートの末尾を除いたもの）が同じものに付ける
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
            // 名前の末尾のパートを除いた名前ごとにまとめる。並び順はセンター → 左 → 右 → そのほか（名前順）
            foreach (var group in dances.GroupBy(d => PartBase(Name(d))))
            {
                var members = group.OrderBy(d => PartOrder(Name(d))).ThenBy(Name).ToList();
                // まとめたときの題名は、末尾を除いた名前。1人ならファイル名のまま
                string title = members.Count > 1 ? group.Key : Name(members[0]);
                string audio = audios.Count == 1 ? audios[0]
                    : audios.FirstOrDefault(a => Name(a) == Name(members[0]) || Name(a) == group.Key);
                string face = faces.Count == 1 ? faces[0] : faces.OrderByDescending(f => CommonPrefix(Name(f), Name(members[0]))).FirstOrDefault();
                plan.Songs.Add(new PlannedSong { title = title, vmd = members[0], parts = members.Skip(1).ToList(), audio = audio, face = face });
            }
            if (dances.Count == 0)
                foreach (var face in faces) plan.Skipped.Add($"{Path.GetFileName(face)}（表情だけのモーション。踊りの .vmd と一緒にドロップする）");
            return plan;
        }

        // 英数字の末尾（_center・-L・ 2 など）は区切り文字があるときだけ、日本語の末尾（センター・左・2人目 など）は区切り文字が無くてもパートとみなす
        static readonly System.Text.RegularExpressions.Regex PartSuffix = new System.Text.RegularExpressions.Regex(
            @"(?:[_\-\s]+(center|centre|left|right|[lrc]|\d{1,2}|[a-f])|[_\-\s]*(センター|中央|左|右|[0-9０-９]人目))$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static string PartToken(string name)
        {
            var m = PartSuffix.Match(name);
            if (!m.Success) return null;
            return (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant();
        }

        /// <summary>名前から、末尾のパート（_center・_left・_1・左 など）を除いたもの。末尾がパートでなければそのまま。</summary>
        public static string PartBase(string name)
        {
            var m = PartSuffix.Match(name);
            string rest = m.Success ? name.Substring(0, m.Index) : name;
            return rest.Length > 0 ? rest : name;
        }

        /// <summary>パートの並び: センター・中央・1 → 左・2 → 右・3 → そのほか。</summary>
        static int PartOrder(string name)
        {
            string p = PartToken(name);
            if (p == null) return 0;
            if (p == "center" || p == "centre" || p == "c" || p == "センター" || p == "中央" || p == "1" || p == "01" || p == "a" || p == "1人目" || p == "１人目") return 0;
            if (p == "left" || p == "l" || p == "左" || p == "2" || p == "02" || p == "b" || p == "2人目" || p == "２人目") return 1;
            if (p == "right" || p == "r" || p == "右" || p == "3" || p == "03" || p == "3人目" || p == "３人目") return 2;
            return 3;
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
            bool headFollows = EditorGUILayout.Toggle(new GUIContent("VR で頭も踊りに合わせる", "入れると頭も振り付けどおりに動く（今は視点も一緒に揺れる）。切ると、頭（視点）はヘッドセットのまま、体だけ踊る"), settings.vrHeadFollowsDance);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(settings, "ワールドの設定");
                settings.slotCount = slots;
                settings.countdownSeconds = countdown;
                settings.vrHeadFollowsDance = headFollows;
                Save(settings);
            }
            DrawDetect(settings);
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
