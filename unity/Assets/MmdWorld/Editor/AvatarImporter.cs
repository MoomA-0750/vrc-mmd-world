using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// PC のアバターのプロジェクト（VRChat のアバター用に改変したもの）から、アバターをお手本・枠のアバターとして取り込む。
    /// 1. アバターのプロジェクトのシーン・prefab から、VRC Avatar Descriptor の付いたアバターを探す（ファイルを読むだけ）
    /// 2. そのプロジェクトをバッチモードの Unity で開き、AvatarExporter~/MmdWorldAvatarExport.cs を一時的に置いて動かす。
    ///    NDMF の手動ベイクで Modular Avatar などを適用した完成形を作り、ワールドで使えない部品を外して prefab にし、使うファイルを manifest.json に書く
    /// 3. 使うファイルを GUID ごと Assets/LocalOnly/Avatars/&lt;名前&gt;/ に写す（ワールドに同じ GUID があれば写さない）。
    ///    パッケージ（lilToon など）のものは、ワールドに無ければ vrc-get で入れる（入れられなければフォルダごと写す）
    /// 4. アバターのプロジェクトに置いたもの・書き出しで増えたものを消し、お手本か枠のアバターに登録する
    /// Assets/LocalOnly はリポジトリに入らない（購入したアバターを公開しない）。
    /// </summary>
    public static class AvatarImporter
    {
        public const string ImportRoot = "Assets/LocalOnly/Avatars";
        const string DescriptorGuid = "67cc4cb7839cd3741b63733d5adf0442";
        // VRCSDK3A.dll の中の VRCAvatarDescriptor（同じ DLL のほかの部品と見分ける）
        const string DescriptorFileId = "542108242";
        const string ExporterSource = "Assets/MmdWorld/Editor/AvatarExporter~/MmdWorldAvatarExport.cs";
        const string ExporterDir = "Assets/MmdWorldAvatarExport";

        public enum Role { Preview, SlotAvatar }

        public sealed class Candidate
        {
            public string file;
            public string name;
            public override string ToString() => string.IsNullOrEmpty(name) ? file : $"{name}（{file}）";
        }

        [Serializable]
        public sealed class Package
        {
            public string name;
            public string version;
        }

        [Serializable]
        public sealed class Manifest
        {
            public string name;
            public string prefab;
            public List<string> files = new List<string>();
            public List<Package> packages = new List<Package>();
            public List<string> created = new List<string>();
            public List<string> removedComponents = new List<string>();
            public bool baked;
            public string error;
        }

        /// <summary>取り込みの途中経過（書き出しの Unity が動いている間）。</summary>
        public sealed class Job
        {
            public string project;
            public Candidate candidate;
            public Role role;
            public Process process;
            public string workDir;
            public DateTime started;
            public string Manifest => Path.Combine(workDir, "manifest.json");
            public string Log => Path.Combine(workDir, "export.log");
        }

        public sealed class Result
        {
            public GameObject prefab;
            public int copied, skipped;
            public readonly List<string> packages = new List<string>();
            public readonly List<string> messages = new List<string>();
            public string error;
        }

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        // ---- 1. 探す ----

        /// <summary>VRChat Creator Companion（ALCOM と共通）に登録されたプロジェクトのうち、アバターのもの（ワールドのプロジェクトを除く）。</summary>
        public static List<string> Projects()
        {
            var list = new List<string>();
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRChatCreatorCompanion", "settings.json");
            if (!File.Exists(settings)) return list;
            var m = Regex.Match(File.ReadAllText(settings), "\"userProjects\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success) return list;
            foreach (Match p in Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string path = Regex.Unescape(p.Groups[1].Value);
                if (IsAvatarProject(path) && Path.GetFullPath(path).TrimEnd('\\', '/') != Path.GetFullPath(ProjectRoot).TrimEnd('\\', '/')) list.Add(path);
            }
            return list;
        }

        public static bool IsAvatarProject(string path)
        {
            string manifest = Path.Combine(path, "Packages", "vpm-manifest.json");
            return File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"com.vrchat.avatars\"");
        }

        /// <summary>アバターのプロジェクトのシーン・prefab のうち、VRC Avatar Descriptor が付いたアバターを探す（ファイルの中身を読むだけで、Unity では開かない）。</summary>
        public static List<Candidate> FindAvatars(string project)
        {
            var result = new List<Candidate>();
            string assets = Path.Combine(project, "Assets");
            if (!Directory.Exists(assets)) return result;
            foreach (string full in Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".unity") || f.EndsWith(".prefab")))
            {
                string rel = "Assets" + full.Substring(assets.Length).Replace('\\', '/');
                if (rel.StartsWith("Assets/ZZZ_GeneratedAssets/") || rel.StartsWith(ExporterDir)) continue;
                string text;
                try { text = File.ReadAllText(full); } catch (IOException) { continue; }
                if (!text.Contains($"fileID: {DescriptorFileId}, guid: {DescriptorGuid}")) continue;
                var names = DescriptorObjectNames(text);
                if (names.Count == 0) result.Add(new Candidate { file = rel, name = "" });
                foreach (var n in names) result.Add(new Candidate { file = rel, name = n });
            }
            return result.OrderBy(c => c.file).ThenBy(c => c.name).ToList();
        }

        /// <summary>シーン・prefab の YAML から、VRC Avatar Descriptor が付いた GameObject の名前を拾う（prefab のインスタンスの中にあるものは拾えない）。</summary>
        static List<string> DescriptorObjectNames(string yaml)
        {
            var docs = Regex.Split(yaml, @"^--- !u!", RegexOptions.Multiline);
            var names = new Dictionary<string, string>();
            var owners = new List<string>();
            foreach (var doc in docs)
            {
                var head = Regex.Match(doc, @"^(\d+) &(-?\d+)");
                if (!head.Success) continue;
                if (head.Groups[1].Value == "1")
                {
                    var name = Regex.Match(doc, @"^\s*m_Name: (.*)$", RegexOptions.Multiline);
                    if (name.Success) names[head.Groups[2].Value] = name.Groups[1].Value.Trim();
                }
                else if (head.Groups[1].Value == "114" && doc.Contains($"m_Script: {{fileID: {DescriptorFileId}, guid: {DescriptorGuid}"))
                {
                    var go = Regex.Match(doc, @"m_GameObject: \{fileID: (-?\d+)\}");
                    if (go.Success) owners.Add(go.Groups[1].Value);
                }
            }
            return owners.Where(names.ContainsKey).Select(o => names[o]).Distinct().ToList();
        }

        // ---- 2. 書き出す ----

        /// <summary>アバターのプロジェクトが Unity で開かれているか（開いていると、バッチモードで開けない）。</summary>
        public static bool IsOpen(string project)
        {
            string lockFile = Path.Combine(project, "Temp", "UnityLockfile");
            if (!File.Exists(lockFile)) return false;
            try
            {
                using (File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        static string UnityFor(string project)
        {
            string version = File.ReadAllLines(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"))
                .Select(l => Regex.Match(l, @"^m_EditorVersion: (\S+)")).Where(m => m.Success).Select(m => m.Groups[1].Value).FirstOrDefault();
            if (version == Application.unityVersion) return EditorApplication.applicationPath;
            string hub = $"C:/Program Files/Unity/Hub/Editor/{version}/Editor/Unity.exe";
            if (File.Exists(hub)) return hub;
            throw new Exception($"アバターのプロジェクトの Unity {version} が見つからない（Unity Hub で入れる）");
        }

        /// <summary>書き出しの Unity を起動する（終わるのは Poll で待つ）。</summary>
        public static Job Start(string project, Candidate candidate, Role role)
        {
            if (!IsAvatarProject(project)) throw new Exception("VRChat のアバター用のプロジェクトではない: " + project);
            if (IsOpen(project)) throw new Exception("アバターのプロジェクトが Unity で開かれています。閉じてからもう一度押してください");
            string exe = UnityFor(project);
            // 書き出し用のスクリプトを一時的に置く
            string dir = Path.Combine(project, ExporterDir, "Editor");
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(ProjectRoot, ExporterSource), Path.Combine(dir, "MmdWorldAvatarExport.cs"), true);

            var job = new Job
            {
                project = project,
                candidate = candidate,
                role = role,
                workDir = Path.Combine(Path.GetTempPath(), "mmdworld-avatar-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")),
                started = DateTime.Now,
            };
            Directory.CreateDirectory(job.workDir);
            string args = $"-batchmode -quit -projectPath \"{project}\" -executeMethod MmdWorldAvatarExport.Run -logFile \"{job.Log}\" " +
                          $"-mmdFile \"{candidate.file}\" -mmdName \"{candidate.name}\" -mmdOut \"{job.Manifest}\"";
            job.process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            Debug.Log($"[MmdWorld] アバターの書き出しを始めた: {project} の {candidate}");
            return job;
        }

        // ---- 3・4. 取り込む ----

        /// <summary>書き出しが終わっていれば取り込んで結果を返す。まだなら null。</summary>
        public static Result Poll(Job job)
        {
            if (!job.process.HasExited) return null;
            var result = new Result();
            try
            {
                if (!File.Exists(job.Manifest))
                    throw new Exception($"書き出しに失敗しました（終了コード {job.process.ExitCode}）。ログ: {job.Log}");
                var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(job.Manifest));
                if (!string.IsNullOrEmpty(manifest.error)) throw new Exception("書き出しに失敗しました: " + manifest.error.Split('\n')[0] + $"（ログ: {job.Log}）");
                Import(job, manifest, result);
                CleanUp(job.project, manifest);
            }
            catch (Exception e)
            {
                result.error = e.Message;
                Debug.LogException(e);
                CleanUp(job.project, null);
            }
            return result;
        }

        static void Import(Job job, Manifest manifest, Result result)
        {
            string safe = MmdWorldLibrary.SafeName(manifest.name);
            string root = $"{ImportRoot}/{safe}";
            if (!manifest.baked) result.messages.Add("NDMF が無いプロジェクトなので、Modular Avatar などは適用せずにそのまま取り込んだ");

            // パッケージ（lilToon など）は、ワールドに無ければ vrc-get で入れる
            var copyPackages = new List<string>();
            foreach (var pkg in manifest.packages)
            {
                if (Directory.Exists(Path.Combine(ProjectRoot, "Packages", pkg.name))) continue;
                if (InstallPackage(pkg)) result.packages.Add($"{pkg.name} {pkg.version}");
                else copyPackages.Add(pkg.name);
            }

            foreach (string file in manifest.files)
            {
                string src = Path.Combine(job.project, file);
                if (file.StartsWith("Packages/"))
                {
                    // vrc-get で入れたものは写さない。入れられなかったパッケージは下でフォルダごと写す
                    continue;
                }
                string guid = MetaGuid(src + ".meta");
                if (guid != null && !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)) && File.Exists(Path.Combine(ProjectRoot, AssetDatabase.GUIDToAssetPath(guid))))
                {
                    result.skipped++;
                    continue;
                }
                // 書き出した prefab とベイクの生成物は root の直下に、それ以外は元の場所を root の下に再現する
                string rel = file.StartsWith("Assets/MmdWorldAvatarExportOut/") ? file.Substring("Assets/MmdWorldAvatarExportOut/".Length) : file.Substring("Assets/".Length);
                CopyWithMeta(src, Path.Combine(ProjectRoot, root, rel));
                result.copied++;
            }
            foreach (string pkg in copyPackages)
            {
                // vrc-get で入れられなかったパッケージは、フォルダごと Assets の下に写す（シェーダーの include などが相対パスなので、フォルダごと）
                CopyDirectory(Path.Combine(job.project, "Packages", pkg), Path.Combine(ProjectRoot, ImportRoot, "_Packages", pkg));
                result.messages.Add($"パッケージ {pkg} は vrc-get で入れられなかったので、{ImportRoot}/_Packages/{pkg} に写した");
            }

            if (result.packages.Count > 0) UnityEditor.PackageManager.Client.Resolve();
            AssetDatabase.Refresh();
            string prefabPath = $"{root}/{safe}.prefab";
            result.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (result.prefab == null) throw new Exception("取り込んだ prefab を読めない: " + prefabPath);

            var settings = MmdWorldSettings.LoadOrCreate();
            Undo.RecordObject(settings, "アバターの取り込み");
            var list = job.role == Role.Preview ? settings.previewDancers : settings.slotAvatars;
            if (!list.Contains(result.prefab)) list.Add(result.prefab);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            if (manifest.removedComponents.Count > 0)
                result.messages.Add("外した部品: " + string.Join("、", manifest.removedComponents.Select(n => n.Split('.').Last()).Distinct()));
        }

        static bool InstallPackage(Package pkg)
        {
            foreach (string args in new[] { $"install -y -p \"{ProjectRoot}\" {pkg.name} {pkg.version}", $"install -y -p \"{ProjectRoot}\" {pkg.name}" })
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo("vrc-get", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Debug.Log($"[MmdWorld] vrc-get {args}: {p.ExitCode}\n{output}");
                    if (p.ExitCode == 0) return true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MmdWorld] vrc-get を動かせない: " + e.Message);
                    return false;
                }
            }
            return false;
        }

        static string MetaGuid(string meta)
        {
            if (!File.Exists(meta)) return null;
            var m = Regex.Match(File.ReadAllText(meta), @"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        static void CopyWithMeta(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, true);
            if (File.Exists(src + ".meta")) File.Copy(src + ".meta", dst + ".meta", true);
        }

        static void CopyDirectory(string src, string dst)
        {
            foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(dst, file.Substring(src.Length).TrimStart('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        /// <summary>アバターのプロジェクトに置いた書き出し用のスクリプトと、書き出し・ベイクで増えたものを消す。</summary>
        static void CleanUp(string project, Manifest manifest)
        {
            var paths = new List<string> { ExporterDir };
            if (manifest != null) paths.AddRange(manifest.created);
            foreach (string rel in paths.Distinct())
            {
                string full = Path.Combine(project, rel);
                try
                {
                    if (Directory.Exists(full)) Directory.Delete(full, true);
                    if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MmdWorld] アバターのプロジェクトの {rel} を消せなかった: {e.Message}");
                }
            }
        }
    }
}
