using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// 曲・お手本のアバター・着替えの台を出し入れする操作。マネージャーのウィンドウと、テストや AI からの操作が同じものを使う。
    /// </summary>
    public static class MmdWorldLibrary
    {
        public const string SongsDir = "Assets/MmdWorld/Songs";
        static readonly string[] AudioExtensions = { ".wav", ".mp3", ".ogg", ".aiff", ".aif" };
        static readonly Regex AvatarId = new Regex("^avtr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");

        /// <summary>並び順（order、同じなら名前）どおりの曲の一覧。モーションの無いものは除く。</summary>
        public static List<DanceSong> Songs() =>
            AssetDatabase.FindAssets("t:" + nameof(DanceSong))
                .Select(g => AssetDatabase.LoadAssetAtPath<DanceSong>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null && s.motion != null)
                .OrderBy(s => s.order).ThenBy(s => s.name)
                .ToList();

        public static bool IsAudioFile(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        public static bool IsVmdFile(string path) => string.Equals(Path.GetExtension(path), ".vmd", StringComparison.OrdinalIgnoreCase);
        public static bool IsValidAvatarId(string id) => id != null && AvatarId.IsMatch(id.Trim());

        /// <summary>
        /// 曲を1つ足す。.vmd と音声（無くてもよい）を Songs/&lt;題名&gt;/ に写し、取り込んで DanceSong を作る。
        /// パスはプロジェクトの外（エクスプローラーからのドロップなど）でも、Assets の中でもよい。中のものも写す（元はそのまま残る）。
        /// </summary>
        public static DanceSong AddSong(string vmdPath, string audioPath = null, string title = null)
        {
            if (!IsVmdFile(vmdPath) || !File.Exists(vmdPath)) throw new ArgumentException(".vmd が見つかりません: " + vmdPath);
            if (!string.IsNullOrEmpty(audioPath) && (!IsAudioFile(audioPath) || !File.Exists(audioPath)))
                throw new ArgumentException("音声ファイル（wav / mp3 / ogg）が見つかりません: " + audioPath);

            title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(vmdPath) : title.Trim();
            string dir = AssetDatabase.GenerateUniqueAssetPath($"{SongsDir}/{SafeName(title)}");
            Directory.CreateDirectory(dir);

            string vmdAsset = $"{dir}/{Path.GetFileName(vmdPath)}";
            File.Copy(vmdPath, vmdAsset);
            string audioAsset = null;
            if (!string.IsNullOrEmpty(audioPath))
            {
                audioAsset = $"{dir}/{Path.GetFileName(audioPath)}";
                File.Copy(audioPath, audioAsset);
            }
            AssetDatabase.Refresh();

            var song = ScriptableObject.CreateInstance<DanceSong>();
            song.title = title;
            song.motion = AssetDatabase.LoadAssetAtPath<AnimationClip>(vmdAsset);
            song.audio = audioAsset != null ? AssetDatabase.LoadAssetAtPath<AudioClip>(audioAsset) : null;
            song.order = Songs().Select(s => s.order).DefaultIfEmpty(-1).Max() + 1;
            if (song.motion == null) throw new InvalidOperationException(".vmd を取り込めませんでした: " + vmdAsset);
            AssetDatabase.CreateAsset(song, $"{dir}/{SafeName(title)}.asset");
            AssetDatabase.SaveAssets();
            return song;
        }

        /// <summary>
        /// 曲を消す。Songs/ の下の、その曲だけのフォルダ（AddSong が作ったもの）ならフォルダごと消す。それ以外は DanceSong だけ消す（モーションや音声は残す）。
        /// </summary>
        public static void RemoveSong(DanceSong song)
        {
            string path = AssetDatabase.GetAssetPath(song);
            string dir = Path.GetDirectoryName(path).Replace('\\', '/');
            bool ownFolder = dir.StartsWith(SongsDir + "/") && AssetDatabase.FindAssets("t:" + nameof(DanceSong), new[] { dir }).Length == 1;
            if (ownFolder) AssetDatabase.DeleteAsset(dir);
            else AssetDatabase.DeleteAsset(path);
        }

        /// <summary>曲の並びを入れ替える（order を 0 から振り直す）。</summary>
        public static void Move(DanceSong song, int delta)
        {
            var songs = Songs();
            int i = songs.IndexOf(song);
            int j = Mathf.Clamp(i + delta, 0, songs.Count - 1);
            if (i < 0 || i == j) return;
            songs.RemoveAt(i);
            songs.Insert(j, song);
            for (int k = 0; k < songs.Count; k++)
            {
                if (songs[k].order == k) continue;
                songs[k].order = k;
                EditorUtility.SetDirty(songs[k]);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>ステーション用の、その場で踊るクリップ（.vmd の取り込みで作られる）。無ければ元のクリップ。</summary>
        public static AnimationClip InPlaceClip(DanceSong song)
        {
            string path = AssetDatabase.GetAssetPath(song.motion);
            var inPlace = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => c != song.motion && c.name.EndsWith("（その場）"));
            return inPlace != null ? inPlace : song.motion;
        }

        /// <summary>体の軌跡（.vmd の取り込みで作られる）。無ければ null。</summary>
        public static MmdWorld.Vmd.VmdTrajectory Trajectory(DanceSong song) =>
            AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(song.motion)).OfType<MmdWorld.Vmd.VmdTrajectory>().FirstOrDefault();

        /// <summary>曲の区切りの時刻（秒、0 から昇順）。seekPoints があればそれ、無ければ seekStep ごと。曲の長さより前のものだけ。</summary>
        public static List<float> Segments(DanceSong song)
        {
            float length = song.motion != null ? song.motion.length : 0f;
            var points = new List<float> { 0f };
            if (song.seekPoints != null && song.seekPoints.Count > 0)
                points.AddRange(song.seekPoints);
            else
            {
                float step = Mathf.Max(2f, song.seekStep);
                for (float t = step; t < length - 1f; t += step) points.Add(t);
            }
            return points.Where(t => t >= 0f && t < length - 0.5f).Distinct().OrderBy(t => t).ToList();
        }

        /// <summary>モーションと音声の長さ（秒）。音声が無ければ音声は 0。</summary>
        public static (float motion, float audio) Lengths(DanceSong song) =>
            (song.motion != null ? song.motion.length : 0f, song.audio != null ? song.audio.length : 0f);

        /// <summary>
        /// モデルが Humanoid か、表情のメッシュ（Body）があるか、曲の表情のうちいくつがそのモデルにあるか。
        /// お手本や着替えの台に使うアバターで、表情まで動くかの目安にする。
        /// </summary>
        public static AvatarCheck CheckAvatar(GameObject model, IEnumerable<DanceSong> songs, string faceMeshPath = "Body")
        {
            var check = new AvatarCheck();
            if (model == null) return check;
            var animator = model.GetComponent<Animator>();
            check.IsHumanoid = animator != null && animator.avatar != null && animator.avatar.isHuman;
            var face = model.transform.Find(faceMeshPath);
            var smr = face != null ? face.GetComponent<SkinnedMeshRenderer>() : null;
            check.HasFaceMesh = smr != null && smr.sharedMesh != null;
            var shapes = new HashSet<string>();
            if (check.HasFaceMesh)
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) shapes.Add(smr.sharedMesh.GetBlendShapeName(i));

            var wanted = new HashSet<string>();
            foreach (var song in songs)
            {
                if (song.motion == null) continue;
                foreach (var b in AnimationUtility.GetCurveBindings(song.motion))
                    if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape."))
                        wanted.Add(b.propertyName.Substring("blendShape.".Length));
            }
            check.MorphsWanted = wanted.Count;
            check.MorphsFound = wanted.Count(shapes.Contains);
            check.Missing = wanted.Where(w => !shapes.Contains(w)).OrderBy(w => w).ToList();
            check.IsLocalOnly = AssetDatabase.GetAssetPath(model).StartsWith("Assets/LocalOnly/");
            return check;
        }

        public sealed class AvatarCheck
        {
            public bool IsHumanoid;
            public bool HasFaceMesh;
            public int MorphsWanted;
            public int MorphsFound;
            public List<string> Missing = new List<string>();
            /// <summary>Assets/LocalOnly/ のもの（リポジトリに入らないので、ほかの人の手元では抜ける）。</summary>
            public bool IsLocalOnly;
        }

        /// <summary>フォルダ名やアセット名に使えない文字を _ にする。</summary>
        public static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
            var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            string safe = new string(chars).Trim().TrimEnd('.');
            return string.IsNullOrEmpty(safe) ? "Song" : safe;
        }
    }
}
