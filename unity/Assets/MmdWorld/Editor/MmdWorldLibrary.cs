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

        /// <summary>VR で踊りを体に乗せる部品（アバター SDK の VRCAnimatorTrackingControl）がこのプロジェクトにあるか。</summary>
        public static bool HasTrackingControl() => AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl") != null);

        public const string AvatarSdkDir = "Assets/LocalOnly/VRChatAvatarSDK";

        /// <summary>
        /// アバター SDK の VRCSDK3A.dll をファイルの選択で選んでもらい、Assets/LocalOnly/VRChatAvatarSDK/ に写す（LocalOnly はリポジトリに入らない）。
        /// SDK の DLL は配れないので、各自のアバター用のプロジェクトから写す。
        /// </summary>
        public static bool InstallAvatarSdkDll()
        {
            string picked = EditorUtility.OpenFilePanel("アバター SDK の VRCSDK3A.dll を選ぶ（アバター用のプロジェクトの Packages/com.vrchat.avatars/Runtime/VRCSDK/Plugins/）",
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "dll");
            if (string.IsNullOrEmpty(picked)) return false;
            if (!string.Equals(Path.GetFileName(picked), "VRCSDK3A.dll", StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("VR 用の部品", "VRCSDK3A.dll を選んでください（選んだのは " + Path.GetFileName(picked) + "）", "OK");
                return false;
            }
            Directory.CreateDirectory(AvatarSdkDir);
            File.Copy(picked, $"{AvatarSdkDir}/VRCSDK3A.dll", true);
            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>.vmd の中身の種類。MMD の配布物には、踊りのほかに表情だけ・カメラだけの .vmd が入っていることが多い。</summary>
        public enum VmdKind { Dance, Face, Camera, Empty }

        /// <summary>体を動かすボーン。これにキーがあれば踊りのモーション。</summary>
        static readonly string[] BodyBones = { "全ての親", "センター", "グルーブ", "下半身", "上半身", "左足ＩＫ", "右足ＩＫ" };

        /// <summary>.vmd の中身を見て、踊り・表情だけ・カメラだけ・キー無しに分ける。</summary>
        public static VmdKind Classify(string vmdPath)
        {
            // 大きな .vmd は読むのに時間がかかるので、ファイルの更新時刻ごとに覚えておく（マネージャーは描き直すたびに呼ぶ）
            string key = Path.GetFullPath(vmdPath) + "|" + File.GetLastWriteTimeUtc(vmdPath).Ticks;
            if (KindCache.TryGetValue(key, out var cached)) return cached;
            var kind = ClassifyUncached(vmdPath);
            KindCache[key] = kind;
            return kind;
        }

        static readonly Dictionary<string, VmdKind> KindCache = new Dictionary<string, VmdKind>();

        static VmdKind ClassifyUncached(string vmdPath)
        {
            var motion = MmdWorld.Vmd.VmdReader.Read(vmdPath);
            int boneKeys = motion.Bones.Values.Sum(k => k.Count);
            bool body = BodyBones.Any(b => motion.Bones.TryGetValue(b, out var keys) && keys.Count >= 2);
            if (body || boneKeys >= 30) return VmdKind.Dance;
            if (motion.Morphs.Values.Sum(k => k.Count) > 0) return VmdKind.Face;
            if (motion.CameraKeyCount > 0) return VmdKind.Camera;
            return VmdKind.Empty;
        }

        public static string KindName(VmdKind kind) => kind switch
        {
            VmdKind.Face => "表情だけのモーション",
            VmdKind.Camera => "カメラのモーション",
            VmdKind.Empty => "キーの無いモーション",
            _ => "踊りのモーション",
        };

        /// <summary>曲のモーションが踊りのモーションか（表情だけ・カメラの .vmd を曲にしていないか）。</summary>
        public static bool IsDance(DanceSong song)
        {
            string path = song.motion != null ? AssetDatabase.GetAssetPath(song.motion) : null;
            return path != null && IsVmdFile(path) && File.Exists(path) ? Classify(path) == VmdKind.Dance : song.motion != null;
        }

        /// <summary>
        /// 曲を1つ足す。.vmd と音声（無くてもよい）を Songs/&lt;題名&gt;/ に写し、取り込んで DanceSong を作る。
        /// パスはプロジェクトの外（エクスプローラーからのドロップなど）でも、Assets の中でもよい。中のものも写す（元はそのまま残る）。
        /// </summary>
        public static DanceSong AddSong(string vmdPath, string audioPath = null, string title = null, string facePath = null, IList<string> partPaths = null)
        {
            if (!IsVmdFile(vmdPath) || !File.Exists(vmdPath)) throw new ArgumentException(".vmd が見つかりません: " + vmdPath);
            var kind = Classify(vmdPath);
            if (kind != VmdKind.Dance)
                throw new ArgumentException($"{Path.GetFileName(vmdPath)} は{KindName(kind)}なので、曲にはできません" +
                                            (kind == VmdKind.Face ? "（踊りの .vmd と一緒にドロップするか、曲の「表情」に入れる）" : ""));
            if (!string.IsNullOrEmpty(facePath) && (!IsVmdFile(facePath) || !File.Exists(facePath)))
                throw new ArgumentException("表情の .vmd が見つかりません: " + facePath);
            foreach (var part in partPaths ?? new string[0])
                if (!IsVmdFile(part) || !File.Exists(part)) throw new ArgumentException("パートの .vmd が見つかりません: " + part);
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
            string faceAsset = null;
            if (!string.IsNullOrEmpty(facePath))
            {
                faceAsset = $"{dir}/{Path.GetFileName(facePath)}";
                if (faceAsset == vmdAsset) faceAsset = $"{dir}/表情_{Path.GetFileName(facePath)}";
                File.Copy(facePath, faceAsset);
            }
            var partAssets = new List<string>();
            foreach (var part in partPaths ?? new string[0])
            {
                string asset = $"{dir}/{Path.GetFileName(part)}";
                if (asset == vmdAsset || asset == faceAsset || partAssets.Contains(asset)) continue;
                File.Copy(part, asset);
                partAssets.Add(asset);
            }
            AssetDatabase.Refresh();

            var song = ScriptableObject.CreateInstance<DanceSong>();
            song.title = title;
            song.motion = AssetDatabase.LoadAssetAtPath<AnimationClip>(vmdAsset);
            song.audio = audioAsset != null ? AssetDatabase.LoadAssetAtPath<AudioClip>(audioAsset) : null;
            song.face = faceAsset != null ? AssetDatabase.LoadAssetAtPath<AnimationClip>(faceAsset) : null;
            song.parts = partAssets.Select(AssetDatabase.LoadAssetAtPath<AnimationClip>).Where(c => c != null).ToList();
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
            else
            {
                // モーションは残るので、「フォルダの .vmd から曲を作る」を入れていても曲に戻さないように覚えておく
                var settings = MmdWorldSettings.LoadOrCreate();
                foreach (var clip in new[] { song.motion }.Concat(song.parts ?? new List<AnimationClip>()))
                    if (clip != null && !settings.ignoredMotions.Contains(clip)) settings.ignoredMotions.Add(clip);
                EditorUtility.SetDirty(settings);
                AssetDatabase.DeleteAsset(path);
            }
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
        /// <summary>曲のパート（1人目の motion と、2人目以降の parts）。1人で踊る曲なら motion だけ。</summary>
        public static List<AnimationClip> Parts(DanceSong song)
        {
            var list = new List<AnimationClip>();
            if (song.motion != null) list.Add(song.motion);
            if (song.parts != null) list.AddRange(song.parts.Where(p => p != null && p != song.motion));
            return list;
        }

        /// <summary>パートの名前（.vmd のファイル名）。</summary>
        public static string PartName(AnimationClip part) =>
            part == null ? "" : Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(part));

        /// <summary>ステーション用の、その場で踊るクリップ（.vmd の取り込みで作られる）。無ければ元のクリップ。</summary>
        public static AnimationClip InPlaceClip(DanceSong song) => InPlaceClip(song.motion);

        public static AnimationClip InPlaceClip(AnimationClip motion)
        {
            string path = AssetDatabase.GetAssetPath(motion);
            var inPlace = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => c != motion && c.name.EndsWith("（その場）"));
            return inPlace != null ? inPlace : motion;
        }

        /// <summary>体の軌跡（.vmd の取り込みで作られる）。無ければ null。</summary>
        public static MmdWorld.Vmd.VmdTrajectory Trajectory(DanceSong song) => Trajectory(song.motion);

        public static MmdWorld.Vmd.VmdTrajectory Trajectory(AnimationClip motion) =>
            AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(motion)).OfType<MmdWorld.Vmd.VmdTrajectory>().FirstOrDefault();

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
                foreach (var clip in new[] { song.motion, song.face })
                {
                    if (clip == null) continue;
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                        if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape."))
                            wanted.Add(b.propertyName.Substring("blendShape.".Length));
                }
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
