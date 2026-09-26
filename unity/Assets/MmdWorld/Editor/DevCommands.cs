using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Editor;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// 開いているエディタに外から処理を頼むための仕組み。プロジェクトの Temp/MmdCommand.txt に処理名を1行書くと、エディタが拾って実行し、ファイルを消す。
    /// 画面のメニューをクリックして回らなくても、ssh からファイルを書くだけで操作できる（AI が動作確認するときに使う）。
    ///
    ///   BuildWorld        ワールドを組み立て直す
    ///   PlayCheckPreview  Play モードでお手本だけ再生する確認
    ///   PlayCheckDance    Play モードで枠に入って再生する確認
    ///   BuildAndTest      VRChat SDK の Build &amp; Test（VR ではなくデスクトップで1つ起動する）
    ///   BuildAndTestAuto  同上。クライアントを2つ起動し、先に入った方が12秒後に枠1に入って再生する（そのビルドだけ）。後の方は客席から見る
    ///   OpenManager       マネージャーのウィンドウを開く
    ///   Refresh           AssetDatabase.Refresh
    /// </summary>
    [InitializeOnLoad]
    public static class DevCommands
    {
        public const string CommandFile = "Temp/MmdCommand.txt";
        static double _nextPoll;

        static DevCommands()
        {
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (!File.Exists(CommandFile)) return;

            string command = File.ReadAllText(CommandFile).Trim();
            File.Delete(CommandFile);
            Debug.Log("[MmdWorld.Command] " + command);
            try
            {
                switch (command)
                {
                    case "BuildWorld":
                        WorldBuilder.Build();
                        break;
                    case "PlayCheckPreview":
                        PlayModeCheck.Begin(false);
                        break;
                    case "PlayCheckDance":
                        PlayModeCheck.Begin(true);
                        break;
                    case "BuildAndTest":
                        BuildAndTest(false);
                        break;
                    case "BuildAndTestAuto":
                        BuildAndTest(true);
                        break;
                    case "OpenManager":
                        MmdWorldManagerWindow.Open();
                        break;
                    case "Refresh":
                        AssetDatabase.Refresh();
                        break;
                    default:
                        Debug.LogWarning("[MmdWorld.Command] 知らない処理: " + command);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        [MenuItem("MMD World/VRChat で試す（Build & Test）")]
        public static void BuildAndTestMenu() => BuildAndTest(false);

        public static async void BuildAndTest(bool autoTest)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[MmdWorld.Command] Play モード中は Build & Test できない");
                return;
            }
            EditorSceneManager.OpenScene(WorldBuilder.ScenePath);
            SetVrchatSetting("ForceNoVR", true);
            // 自動確認のときは2つ起動し、1つ目が踊り、2つ目が客席から見る
            SetVrchatSetting("NumClients", autoTest ? 2 : 1);

            // SDK のパネルが開いていないと Builder を取れない
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out var builder))
            {
                Debug.LogError("[MmdWorld.Command] VRChat SDK の Builder を取れない（サインインしているか確認）");
                return;
            }
            // テストのビルドにだけ、入って数秒後に自分で枠1に入って再生する設定を入れる。終わったら戻して保存する
            var system = UnityEngine.Object.FindObjectsOfType<DanceSystem>(true).FirstOrDefault();
            if (system != null && autoTest)
            {
                system.autoTestDelay = 12f;
                UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(system);
            }
            try
            {
                await builder.BuildAndTest();
                Debug.Log("[MmdWorld.Command] Build & Test を始めた" + (autoTest ? "（12秒後に自動で枠1に入って再生）" : ""));
            }
            catch (Exception e)
            {
                Debug.LogError("[MmdWorld.Command] Build & Test に失敗: " + e);
            }
            finally
            {
                if (system != null && autoTest)
                {
                    system.autoTestDelay = 0f;
                    UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(system);
                    EditorSceneManager.SaveScene(system.gameObject.scene);
                }
            }
        }

        /// <summary>SDK の設定（VRCSettings）を名前で変える。クラスのある場所が SDK の版で変わるので、名前で探す。</summary>
        static void SetVrchatSetting(string name, object value)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetTypes().FirstOrDefault(t => t.Name == "VRCSettings"); } catch { return null; } })
                .FirstOrDefault(t => t != null);
            var prop = type?.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (prop == null)
            {
                Debug.LogWarning("[MmdWorld.Command] VRCSettings." + name + " が見つからない");
                return;
            }
            prop.SetValue(null, value);
        }
    }
}
