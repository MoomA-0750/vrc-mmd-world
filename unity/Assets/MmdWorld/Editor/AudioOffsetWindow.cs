using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// 曲の「音のずれ」（DanceSong.audioOffset）を合わせるウィンドウ。
    /// 上に音声の波形、下にモーションの動きの大きさ（Humanoid の筋肉のカーブの変化の合計）を、モーションの時刻でそろえて並べる。
    /// ずれを変えると音声の波形が横に動くので、音の山と動きの山を見比べて合わせる。
    /// 再生すると、音声とシーンのモデルの踊りを、ずれを入れたまま一緒に流す（耳と目で確かめる）。
    /// 音のずれの意味は DanceSong と同じ: 音をモーションより何秒遅らせるか（モーションの時刻 m で鳴る音声の位置は m − ずれ）。
    /// </summary>
    public sealed class AudioOffsetWindow : EditorWindow
    {
        DanceSong _song;
        float _offset;
        GameObject _model;

        // 表示: 見えている範囲の始まり（モーションの時刻）と幅（秒）
        float _viewStart;
        float _viewLength = 8f;

        // 波形（音声の絶対値の最大を EnvelopeRate 個/秒にまとめたもの）と、モーションの動きの大きさ（MotionRate 個/秒、0〜1）
        const int EnvelopeRate = 200;
        const int MotionRate = 60;
        float[] _envelope;
        float[] _motion;
        DanceSong _analyzed;
        string _problem;

        // 再生: 始めたときのエディタの時刻と、そのときのモーションの時刻
        bool _playing;
        double _playStartedAt;
        float _playFrom;
        float _cursor;
        bool _audioStarted;

        [MenuItem("MMD World/音のずれを合わせる", priority = 2)]
        public static void Open() => Open(null);

        public static void Open(DanceSong song)
        {
            var window = GetWindow<AudioOffsetWindow>("音のずれ");
            window.minSize = new Vector2(720, 480);
            if (song != null) window.SetSong(song);
            window.Show();
        }

        void SetSong(DanceSong song)
        {
            Stop();
            _song = song;
            _offset = song != null ? song.audioOffset : 0f;
            _viewStart = 0f;
            _cursor = 0f;
        }

        void OnDisable() => Stop();

        void OnGUI()
        {
            var songs = MmdWorldLibrary.Songs();
            if (songs.Count == 0)
            {
                EditorGUILayout.HelpBox("曲がありません。マネージャーで曲を足してください", MessageType.Info);
                return;
            }
            if (_song == null || !songs.Contains(_song)) SetSong(songs[0]);

            using (new EditorGUILayout.HorizontalScope())
            {
                int index = songs.IndexOf(_song);
                int picked = EditorGUILayout.Popup("曲", index, songs.Select(s => s.DisplayTitle.Replace("/", "／")).ToArray());
                if (picked != index) SetSong(songs[picked]);
            }
            if (_song.audio == null)
            {
                EditorGUILayout.HelpBox("この曲には音声がありません", MessageType.Info);
                return;
            }
            if (_analyzed != _song) Analyze();
            if (_problem != null) EditorGUILayout.HelpBox(_problem, MessageType.Warning);

            DrawOffset();
            DrawTimeline();
            DrawTransport();

            if (_playing) Repaint();
        }

        void DrawOffset()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                float value = EditorGUILayout.FloatField(new GUIContent("音のずれ（秒）", "音をモーションより何秒遅らせるか。音が早いときは +"), _offset);
                foreach (var step in new[] { -0.1f, -0.01f, 0.01f, 0.1f })
                    if (GUILayout.Button((step > 0 ? "+" : "−") + Mathf.Abs(step).ToString("0.00"), GUILayout.Width(48))) value += step;
                if (EditorGUI.EndChangeCheck())
                {
                    _offset = (float)Math.Round(value, 3);
                    if (_playing) Play(CurrentTime());
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool changed = !Mathf.Approximately(_offset, _song.audioOffset);
                EditorGUILayout.LabelField(changed ? $"曲に入っている値: {_song.audioOffset:0.000} 秒" : "曲に入っている値と同じ", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(!changed))
                {
                    if (GUILayout.Button("元に戻す", GUILayout.Width(80))) _offset = _song.audioOffset;
                    if (GUILayout.Button("曲に入れる", GUILayout.Width(80)))
                    {
                        Undo.RecordObject(_song, "音のずれ");
                        _song.audioOffset = _offset;
                        EditorUtility.SetDirty(_song);
                        AssetDatabase.SaveAssets();
                    }
                }
            }
        }

        void DrawTimeline()
        {
            float length = Mathf.Max(_song.motion.length, _song.audio.length + Mathf.Max(0f, _offset));
            using (new EditorGUILayout.HorizontalScope())
            {
                _viewLength = EditorGUILayout.Slider("見える幅（秒）", _viewLength, 1f, 60f);
            }
            var rect = GUILayoutUtility.GetRect(100, 220, GUILayout.ExpandWidth(true));
            _viewStart = GUILayout.HorizontalScrollbar(_viewStart, _viewLength, 0f, Mathf.Max(length, _viewLength));

            EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.15f));
            float half = rect.height * 0.5f;
            var audioRect = new Rect(rect.x, rect.y, rect.width, half - 1);
            var motionRect = new Rect(rect.x, rect.y + half + 1, rect.width, half - 1);
            float secPerPixel = _viewLength / rect.width;

            for (int x = 0; x < (int)rect.width; x++)
            {
                float t0 = _viewStart + x * secPerPixel, t1 = t0 + secPerPixel;
                // 音声: モーションの時刻 t で鳴っている音声の位置は t − ずれ
                float a = Peak(_envelope, EnvelopeRate, t0 - _offset, t1 - _offset);
                if (a > 0f)
                {
                    float h = a * (audioRect.height - 4);
                    EditorGUI.DrawRect(new Rect(audioRect.x + x, audioRect.center.y - h * 0.5f, 1, Mathf.Max(1, h)), new Color(0.35f, 0.7f, 1f));
                }
                float m = Peak(_motion, MotionRate, t0, t1);
                if (m > 0f)
                {
                    float h = m * (motionRect.height - 4);
                    EditorGUI.DrawRect(new Rect(motionRect.x + x, motionRect.yMax - 2 - h, 1, Mathf.Max(1, h)), new Color(1f, 0.65f, 0.25f));
                }
            }
            // 目盛り。文字が重ならないように、間隔を 70 ピクセル以上にする
            float tick = new[] { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 30f }.FirstOrDefault(v => v / secPerPixel >= 70f);
            if (tick <= 0f) tick = 60f;
            for (float s = Mathf.Ceil(_viewStart / tick) * tick; s < _viewStart + _viewLength; s += tick)
            {
                float x = rect.x + (s - _viewStart) / secPerPixel;
                EditorGUI.DrawRect(new Rect(x, rect.y, 1, rect.height), new Color(1f, 1f, 1f, 0.12f));
                GUI.Label(new Rect(x + 2, rect.y, 60, 16), Format(s), EditorStyles.miniLabel);
            }
            GUI.Label(new Rect(audioRect.x + 4, audioRect.yMax - 16, 300, 16), "音声（ずれを入れた位置）", EditorStyles.miniLabel);
            GUI.Label(new Rect(motionRect.x + 4, motionRect.y, 300, 16), "モーションの動きの大きさ", EditorStyles.miniLabel);

            // 再生位置。クリック・ドラッグで動かす
            float cursor = CurrentTime();
            float cx = rect.x + (cursor - _viewStart) / secPerPixel;
            if (cx >= rect.x && cx <= rect.xMax) EditorGUI.DrawRect(new Rect(cx, rect.y, 2, rect.height), new Color(1f, 0.3f, 0.3f));
            var e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && rect.Contains(e.mousePosition))
            {
                float t = Mathf.Clamp(_viewStart + (e.mousePosition.x - rect.x) * secPerPixel, 0f, length);
                if (_playing) Play(t); else { _cursor = t; SampleModel(t); }
                e.Use();
            }
            // 再生中は再生位置が見えるように送る
            if (_playing && (cursor > _viewStart + _viewLength * 0.9f || cursor < _viewStart)) _viewStart = Mathf.Max(0f, cursor - _viewLength * 0.1f);
        }

        void DrawTransport()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_playing ? "■ 止める" : "▶ ここから再生", GUILayout.Width(120), GUILayout.Height(24)))
                {
                    if (_playing) Stop(); else Play(_cursor);
                }
                GUILayout.Label($"{Format(CurrentTime())} / {Format(_song.motion.length)}", GUILayout.Width(120));
                EditorGUI.BeginChangeCheck();
                _model = (GameObject)EditorGUILayout.ObjectField(new GUIContent("踊らせるモデル", "シーンの Humanoid のモデル（お手本など）。再生中・再生位置を動かしたときに、そのときのポーズにする。空ならシーンのお手本を探す"), _model, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck()) SampleModel(CurrentTime());
            }
            EditorGUILayout.HelpBox("音の山（拍）と、動きの山（振りの区切り）がそろうように、ずれを変える。音が動きより早く聞こえるときは + にする。" +
                                    "再生すると、シーンのモデルが踊り、音声もずれを入れて鳴る（Scene ビューか Game ビューで見る）。", MessageType.None);
        }

        float CurrentTime() => _playing ? _playFrom + (float)(EditorApplication.timeSinceStartup - _playStartedAt) : _cursor;

        void Play(float from)
        {
            Stop();
            _playing = true;
            _playFrom = Mathf.Max(0f, from);
            _playStartedAt = EditorApplication.timeSinceStartup;
            _audioStarted = false;
            EditorApplication.update += Tick;
            Tick();
        }

        void Stop()
        {
            if (_playing) _cursor = CurrentTime();
            _playing = false;
            EditorApplication.update -= Tick;
            StopAudio();
            if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        }

        void Tick()
        {
            if (!_playing || _song == null) return;
            float t = CurrentTime();
            if (t > _song.motion.length) { Stop(); Repaint(); return; }
            // 音声はモーションの時刻 = ずれ のところから始まる（ずれが + なら、音はそのぶん後から鳴る）
            float audioTime = t - _offset;
            if (!_audioStarted && audioTime >= 0f && audioTime < _song.audio.length)
            {
                PlayAudio(_song.audio, audioTime);
                _audioStarted = true;
            }
            SampleModel(t);
        }

        GameObject Model()
        {
            if (_model != null) return _model;
            // 組み立てたシーンのお手本（DanceSystem.previewDancers）を使う
            var system = FindObjectsOfType<DanceSystem>(true).FirstOrDefault();
            var dancer = system != null && system.previewDancers != null ? system.previewDancers.FirstOrDefault(a => a != null) : null;
            return dancer != null ? dancer.gameObject : null;
        }

        void SampleModel(float t)
        {
            var model = Model();
            if (model == null || _song?.motion == null) return;
            if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(model, _song.motion, Mathf.Clamp(t, 0f, _song.motion.length));
            AnimationMode.EndSampling();
            SceneView.RepaintAll();
        }

        // ---- 波形とモーションの動き ----

        void Analyze()
        {
            _analyzed = _song;
            _problem = null;
            _envelope = AudioEnvelope(_song.audio, out string audioProblem);
            _motion = MotionActivity(_song.motion);
            if (audioProblem != null) _problem = audioProblem;
            else if (_motion.Length == 0) _problem = "モーションの動きを読めませんでした（Humanoid のクリップではない）";
        }

        /// <summary>音声の絶対値の最大を、EnvelopeRate 個/秒にまとめる（0〜1）。</summary>
        static float[] AudioEnvelope(AudioClip clip, out string problem)
        {
            problem = null;
            if (clip.loadType != AudioClipLoadType.DecompressOnLoad)
            {
                problem = "音声の波形を読むには、音声の Import Settings で Load Type を Decompress On Load にする必要があります（再生はできます）";
                return new float[0];
            }
            if (clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
            var data = new float[clip.samples * clip.channels];
            if (!clip.GetData(data, 0))
            {
                problem = "音声の波形を読めませんでした（再生はできます）";
                return new float[0];
            }
            int perBin = Mathf.Max(1, clip.frequency / EnvelopeRate);
            var env = new float[clip.samples / perBin + 1];
            for (int i = 0; i < clip.samples; i++)
            {
                float v = 0f;
                for (int c = 0; c < clip.channels; c++) v = Mathf.Max(v, Mathf.Abs(data[i * clip.channels + c]));
                int bin = i / perBin;
                if (v > env[bin]) env[bin] = v;
            }
            float max = env.DefaultIfEmpty(0f).Max();
            if (max > 0f) for (int i = 0; i < env.Length; i++) env[i] /= max;
            return env;
        }

        /// <summary>
        /// モーションの動きの大きさ: Humanoid の筋肉のカーブ（指と根元を除く）の、1 フレームごとの変化の絶対値の合計（MotionRate 個/秒、0〜1）。
        /// 振りの区切り（止め・跳ね）が山になるので、音の拍と見比べられる。
        /// </summary>
        public static float[] MotionActivity(AnimationClip clip)
        {
            var curves = AnimationUtility.GetCurveBindings(clip)
                // 筋肉のカーブは「Spine Front-Back」のような名前。指（LeftHand.Index.1 …）・根元や IK の目標（RootT.x、LeftFootQ.w …）は「.」を含むので除く
                .Where(b => b.type == typeof(Animator) && !b.propertyName.Contains("."))
                .Select(b => AnimationUtility.GetEditorCurve(clip, b))
                .Where(c => c != null && c.length > 1)
                .ToList();
            int count = Mathf.CeilToInt(clip.length * MotionRate);
            if (curves.Count == 0 || count <= 1) return new float[0];
            var result = new float[count];
            var previous = curves.Select(c => c.Evaluate(0f)).ToArray();
            for (int i = 1; i < count; i++)
            {
                float t = i / (float)MotionRate, sum = 0f;
                for (int k = 0; k < curves.Count; k++)
                {
                    float v = curves[k].Evaluate(t);
                    sum += Mathf.Abs(v - previous[k]);
                    previous[k] = v;
                }
                result[i] = sum;
            }
            // 大きな外れ値で全体がつぶれないように、上位 1% で割って 1 で頭を切る
            var sorted = result.OrderBy(v => v).ToArray();
            float top = sorted[Mathf.Clamp((int)(sorted.Length * 0.99f), 0, sorted.Length - 1)];
            if (top > 0f) for (int i = 0; i < result.Length; i++) result[i] = Mathf.Min(1f, result[i] / top);
            return result;
        }

        static float Peak(float[] values, int rate, float t0, float t1)
        {
            if (values == null || values.Length == 0) return 0f;
            int i0 = Mathf.FloorToInt(t0 * rate), i1 = Mathf.CeilToInt(t1 * rate);
            if (i1 < 0 || i0 >= values.Length) return 0f;
            float peak = 0f;
            for (int i = Mathf.Max(0, i0); i <= Mathf.Min(values.Length - 1, i1); i++) peak = Mathf.Max(peak, values[i]);
            return peak;
        }

        static string Format(float seconds) => $"{(int)(seconds / 60)}:{seconds % 60:00.00}";

        // ---- エディタで音を鳴らす（UnityEditor.AudioUtil は公開されていないので、名前で呼ぶ） ----

        static Type AudioUtil => typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");

        static void PlayAudio(AudioClip clip, float from)
        {
            int start = Mathf.Clamp((int)(from * clip.frequency), 0, Mathf.Max(0, clip.samples - 1));
            var method = AudioUtil?.GetMethod("PlayPreviewClip", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);
            method?.Invoke(null, new object[] { clip, start, false });
        }

        static void StopAudio()
        {
            AudioUtil?.GetMethod("StopAllPreviewClips", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
        }
    }
}
