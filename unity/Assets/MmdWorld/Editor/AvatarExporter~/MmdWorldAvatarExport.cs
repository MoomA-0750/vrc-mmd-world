// MMD World の「アバターのプロジェクトから取り込む」が、アバターのプロジェクトに一時的に置いてバッチモードで動かすスクリプト。
// 終わったら MMD World が消す。アバターのプロジェクトの中身は書き換えない（書き出したもの・ベイクで増えたものは、取り込みのあと MMD World が消す）。
//
// 1. 指定のシーン（か prefab）を開き、指定の名前の VRC Avatar Descriptor が付いたアバターを探す
// 2. NDMF があれば手動ベイク（AvatarProcessor.ManualProcessAvatar）で Modular Avatar などを適用した複製を作る。無ければそのまま複製する
// 3. ワールドで使えない部品（Avatar Descriptor、MA・AAO などのツールの残り）を外す。PhysBone・Contact は残す
// 4. prefab にして保存し、使っているファイルと、パッケージ（Packages/ の下）のものを manifest.json に書く
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MmdWorldAvatarExport
{
    const string OutDir = "Assets/MmdWorldAvatarExportOut";
    const string GeneratedDir = "Assets/ZZZ_GeneratedAssets";

    [Serializable]
    public class Package
    {
        public string name;
        public string version;
    }

    [Serializable]
    public class Manifest
    {
        public string name;
        public string prefab;
        public List<string> files = new List<string>();
        public List<Package> packages = new List<Package>();
        // このスクリプトが増やしたもの（取り込みのあと MMD World が消す）
        public List<string> created = new List<string>();
        public List<string> removedComponents = new List<string>();
        public bool baked;
        public string error;
    }

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Run()
    {
        var manifest = new Manifest();
        string outJson = Arg("-mmdOut");
        try
        {
            // 名前が空のときは「-」が来る（空の引数は Unity に渡らない）。そのファイルの最初のアバターを使う
            string name = Arg("-mmdName");
            Export(Arg("-mmdFile"), name == "-" ? "" : name, manifest);
        }
        catch (Exception e)
        {
            manifest.error = e.ToString();
            Debug.LogException(e);
        }
        File.WriteAllText(outJson, JsonUtility.ToJson(manifest, true));
        EditorApplication.Exit(string.IsNullOrEmpty(manifest.error) ? 0 : 1);
    }

    static Type FindType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(t => t != null);

    static void Export(string file, string name, Manifest manifest)
    {
        var descriptorType = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
        if (descriptorType == null) throw new Exception("VRChat のアバター SDK が無いプロジェクト");

        GameObject source;
        if (file.EndsWith(".unity"))
        {
            EditorSceneManager.OpenScene(file, OpenSceneMode.Single);
            source = UnityEngine.Object.FindObjectsOfType(descriptorType, true).Cast<Component>()
                .Select(c => c.gameObject).FirstOrDefault(g => string.IsNullOrEmpty(name) || g.name == name);
        }
        else
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(file);
            source = prefab != null ? (GameObject)PrefabUtility.InstantiatePrefab(prefab) : null;
            if (source != null && source.GetComponent(descriptorType) == null)
                source = source.GetComponentsInChildren(descriptorType, true).Select(c => ((Component)c).gameObject).FirstOrDefault(g => string.IsNullOrEmpty(name) || g.name == name);
        }
        if (source == null) throw new Exception($"{file} に {name} という名前のアバター（VRC Avatar Descriptor）が無い");
        manifest.name = string.IsNullOrEmpty(name) ? source.name : name;
        source.SetActive(true);

        bool generatedExisted = Directory.Exists(GeneratedDir);
        var generatedBefore = generatedExisted ? Directory.GetDirectories(GeneratedDir).ToList() : new List<string>();
        GameObject baked = null;
        var processor = FindType("nadena.dev.ndmf.AvatarProcessor");
        var manual = processor?.GetMethods().FirstOrDefault(m => m.Name == "ManualProcessAvatar");
        if (manual != null)
        {
            // メニューの「Manual bake avatar」と同じ。生成物は Assets/ZZZ_GeneratedAssets に保存される
            var parameters = manual.GetParameters();
            baked = (GameObject)manual.Invoke(null, parameters.Length == 1 ? new object[] { source } : new object[] { source, null });
            manifest.baked = true;
        }
        if (baked == null) baked = UnityEngine.Object.Instantiate(source);
        // ベイクで増えたフォルダ（ZZZ_GeneratedAssets が今回できたならそれごと）を覚えておく。取り込みのあと消す
        if (Directory.Exists(GeneratedDir))
        {
            if (!generatedExisted) manifest.created.Add(GeneratedDir);
            else manifest.created.AddRange(Directory.GetDirectories(GeneratedDir).Where(d => !generatedBefore.Contains(d)).Select(d => d.Replace('\\', '/')));
        }

        StripComponents(baked, manifest);

        Directory.CreateDirectory(OutDir);
        manifest.created.Add(OutDir);
        string safe = string.Concat(manifest.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        // ベイクで作られたまま保存されていないもの（メッシュ・マテリアルなど）があれば、prefab と一緒に保存する
        string container = $"{OutDir}/{safe}_assets";
        Directory.CreateDirectory(container);
        int n = 0;
        foreach (var obj in EditorUtility.CollectDependencies(new UnityEngine.Object[] { baked }))
        {
            if (obj == null || EditorUtility.IsPersistent(obj)) continue;
            string ext = obj is Material ? ".mat" : obj is AnimationClip ? ".anim" : obj is Mesh || obj is Texture || obj is Avatar || obj is AvatarMask || obj is ScriptableObject ? ".asset" : null;
            if (ext == null) continue;
            AssetDatabase.CreateAsset(obj, $"{container}/{n++}_{obj.GetType().Name}{ext}");
        }
        manifest.prefab = $"{OutDir}/{safe}.prefab";
        PrefabUtility.SaveAsPrefabAsset(baked, manifest.prefab);
        AssetDatabase.SaveAssets();

        foreach (var dep in AssetDatabase.GetDependencies(manifest.prefab, true))
        {
            if (dep.EndsWith(".cs") || dep.EndsWith(".dll") || dep.EndsWith(".asmdef")) continue;
            manifest.files.Add(dep);
            if (!dep.StartsWith("Packages/")) continue;
            string pkg = dep.Split('/')[1];
            if (manifest.packages.Any(p => p.name == pkg)) continue;
            string json = $"Packages/{pkg}/package.json";
            string version = File.Exists(json) ? JsonUtility.FromJson<Package>(File.ReadAllText(json)).version : null;
            manifest.packages.Add(new Package { name = pkg, version = version });
        }
    }

    /// <summary>ワールドで使えない部品を外す。Transform・描画・Animator・物理・PhysBone などは残す。</summary>
    static void StripComponents(GameObject root, Manifest manifest)
    {
        // 依存（RequireComponent）があると外せない順があるので、外せなくなるまで繰り返す
        for (int pass = 0; pass < 5; pass++)
        {
            bool removed = false;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || !(c is MonoBehaviour)) continue;
                string ns = c.GetType().Namespace ?? "";
                if (ns.StartsWith("VRC.SDK3.Dynamics") || ns.StartsWith("VRC.Dynamics")) continue;
                string label = c.GetType().FullName;
                try
                {
                    UnityEngine.Object.DestroyImmediate(c);
                    removed = true;
                    if (!manifest.removedComponents.Contains(label)) manifest.removedComponents.Add(label);
                }
                catch (Exception)
                {
                    // ほかの部品が使っているので、次の回で外す
                }
            }
            if (!removed) break;
        }
        // 見つからないスクリプト（Missing）も外す
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
    }
}
