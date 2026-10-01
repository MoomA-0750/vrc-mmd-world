using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// 曲の一覧（題名・モーション・音声・音のずれ・立ち位置のずれなど）の書き出しと読み込み。
    /// JSON は設定だけ（アセットはパスと GUID で指す）。同じプロジェクトで設定を戻す・見比べるのに使う。
    /// .unitypackage は曲の設定とモーション・音声をまとめたもの。別のプロジェクトに持っていける（スクリプトは入れない）。
    /// </summary>
    public static class MmdWorldExport
    {
        [Serializable]
        public sealed class AssetRef
        {
            public string path;
            public string guid;
        }

        [Serializable]
        public sealed class SongEntry
        {
            public string title;
            public int order;
            public AssetRef motion;
            public List<AssetRef> parts = new List<AssetRef>();
            public List<Vector3> partOffsets = new List<Vector3>();
            public AssetRef face;
            public AssetRef audio;
            public float audioOffset;
            public float seekStep;
            public List<float> seekPoints = new List<float>();
        }

        [Serializable]
        public sealed class Library
        {
            public int version = 1;
            public List<SongEntry> songs = new List<SongEntry>();
        }

        /// <summary>読み込んだ結果（足した曲・書き戻した曲・見つからなかったアセット）。</summary>
        public sealed class ImportResult
        {
            public int added, updated;
            public readonly List<string> missing = new List<string>();
        }

        static AssetRef Ref(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            string path = AssetDatabase.GetAssetPath(asset);
            return new AssetRef { path = path, guid = AssetDatabase.AssetPathToGUID(path) };
        }

        /// <summary>GUID で探し、無ければパスで探す（同じプロジェクトなら GUID、写したフォルダならパスで見つかる）。</summary>
        static T Load<T>(AssetRef r, ImportResult result, string what) where T : UnityEngine.Object
        {
            if (r == null || (string.IsNullOrEmpty(r.guid) && string.IsNullOrEmpty(r.path))) return null;
            string path = !string.IsNullOrEmpty(r.guid) ? AssetDatabase.GUIDToAssetPath(r.guid) : null;
            var asset = !string.IsNullOrEmpty(path) ? AssetDatabase.LoadAssetAtPath<T>(path) : null;
            if (asset == null && !string.IsNullOrEmpty(r.path)) asset = AssetDatabase.LoadAssetAtPath<T>(r.path);
            if (asset == null) result.missing.Add($"{what}: {r.path}");
            return asset;
        }

        public static Library ToLibrary(IEnumerable<DanceSong> songs)
        {
            var library = new Library();
            foreach (var s in songs)
                library.songs.Add(new SongEntry
                {
                    title = s.title,
                    order = s.order,
                    motion = Ref(s.motion),
                    parts = (s.parts ?? new List<AnimationClip>()).Where(c => c != null).Select(Ref).ToList(),
                    partOffsets = new List<Vector3>(s.partOffsets ?? new List<Vector3>()),
                    face = Ref(s.face),
                    audio = Ref(s.audio),
                    audioOffset = s.audioOffset,
                    seekStep = s.seekStep,
                    seekPoints = new List<float>(s.seekPoints ?? new List<float>()),
                });
            return library;
        }

        /// <summary>今の曲の一覧を JSON にして path に書く。</summary>
        public static void ExportJson(string path)
        {
            File.WriteAllText(path, JsonUtility.ToJson(ToLibrary(MmdWorldLibrary.Songs()), true), new System.Text.UTF8Encoding(false));
        }

        /// <summary>
        /// JSON の曲を読み込む。同じモーションの曲があれば設定を書き戻し、無ければ JSON が指すアセットで曲を作る（アセットは写さない）。
        /// モーションが見つからない曲は飛ばす（missing に入る）。
        /// </summary>
        public static ImportResult ImportJson(string path)
        {
            var library = JsonUtility.FromJson<Library>(File.ReadAllText(path));
            var result = new ImportResult();
            if (library?.songs == null) return result;
            var existing = AssetDatabase.FindAssets("t:" + nameof(DanceSong))
                .Select(g => AssetDatabase.LoadAssetAtPath<DanceSong>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null && s.motion != null)
                .ToList();
            foreach (var e in library.songs)
            {
                var motion = Load<AnimationClip>(e.motion, result, $"「{e.title}」のモーション");
                if (motion == null) continue;
                var song = existing.FirstOrDefault(s => s.motion == motion);
                bool isNew = song == null;
                if (isNew) song = ScriptableObject.CreateInstance<DanceSong>();
                else Undo.RecordObject(song, "曲の読み込み");
                song.title = e.title;
                song.order = e.order;
                song.motion = motion;
                song.parts = (e.parts ?? new List<AssetRef>()).Select(p => Load<AnimationClip>(p, result, $"「{e.title}」のパート")).Where(c => c != null).ToList();
                song.partOffsets = new List<Vector3>(e.partOffsets ?? new List<Vector3>());
                song.face = Load<AnimationClip>(e.face, result, $"「{e.title}」の表情");
                song.audio = Load<AudioClip>(e.audio, result, $"「{e.title}」の音声");
                song.audioOffset = e.audioOffset;
                song.seekStep = e.seekStep > 0f ? e.seekStep : 10f;
                song.seekPoints = new List<float>(e.seekPoints ?? new List<float>());
                if (isNew)
                {
                    // モーションと同じフォルダに置く（モーションを消すときに一緒に見つけやすい）
                    string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(motion)).Replace('\\', '/');
                    AssetDatabase.CreateAsset(song, AssetDatabase.GenerateUniqueAssetPath($"{dir}/{MmdWorldLibrary.SafeName(song.DisplayTitle)}.asset"));
                    result.added++;
                }
                else
                {
                    EditorUtility.SetDirty(song);
                    result.updated++;
                }
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>曲の設定と、それが使うモーション・音声（と .meta）を .unitypackage にまとめる。スクリプトや DLL は入れない。</summary>
        public static List<string> ExportPackage(string path)
        {
            var songPaths = MmdWorldLibrary.Songs().Select(AssetDatabase.GetAssetPath).ToArray();
            var assets = AssetDatabase.GetDependencies(songPaths, true)
                .Where(p => p.StartsWith("Assets/"))
                .Where(p => !p.EndsWith(".cs") && !p.EndsWith(".dll") && !p.EndsWith(".asmdef"))
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            AssetDatabase.ExportPackage(assets.ToArray(), path, ExportPackageOptions.Default);
            return assets;
        }
    }
}
