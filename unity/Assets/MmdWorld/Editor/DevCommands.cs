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
    ///   BuildAndTestAuto [着替えるID] [戻すID] [features]
    ///                     features を付けると、再生とシークの代わりに、プレビュー・範囲再生・ループ・途中からの参加を試す。
    ///                     tablet を付けると、手元のタブレットを出してそのボタンから操作する
    ///                     drive を付けると、踊りながらスティックを倒したことにして、枠ごと動くかを見る
    ///                     mobile を付けると、このビルドの間だけステーションを PlayerMobility.Mobile にする（踊りながら歩けるかの検証）
    ///                     同上。クライアントを2つ起動し、先に入った方が12秒後に枠1に入って再生する（そのビルドだけ）。後の方は枠1の正面から見る。
    ///                     ID を渡すと、踊る前にそのアバターに着替え、終わったら戻す ID のアバターに着替え直す（自分がアップロードしたか公開のアバターだけ）
    ///   BuildAndTestVR    VR で1つ起動する（仮想の VR を scripts/vrsim.py で動かして確かめる。README の「仮想の VR で確かめる」）
    ///   SelfTest          マネージャーまわりの自己テスト（LibrarySelfTest）。結果はコンソールに [MmdWorld.SelfTest] で出る
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
            // コンパイル中や取り込み中に拾うと、U# が「コンパイル中なのでビルドしない」と中止するなど途中で失敗するので、落ち着くまで待つ
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.0;
            if (!File.Exists(CommandFile)) return;

            // 「処理名 引数1 引数2 …」の形。引数は空白で区切る
            string[] words = File.ReadAllText(CommandFile).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string command = words.Length > 0 ? words[0] : "";
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
                        // BuildAndTestAuto [着替えるアバターID] [戻すアバターID]
                        BuildAndTest(true, words.Length > 1 ? words[1] : "", words.Length > 2 ? words[2] : "",
                            words.Length > 3 ? (words[3] == "features" ? 1 : words[3] == "tablet" ? 2 : words[3] == "drive" ? 3 : 0) : 0,
                            words.Contains("mobile"));
                        break;
                    case "BuildAndTestVR":
                        BuildAndTest(false, "", "", 0, vr: true);
                        break;
                    case "SelfTest":
                    {
                        var failures = LibrarySelfTest.Run();
                        foreach (var f in failures) Debug.LogError("[MmdWorld.SelfTest] 失敗: " + f);
                        Debug.Log($"[MmdWorld.SelfTest] 結果: {(failures.Count == 0 ? "成功" : "失敗 " + failures.Count + " 件")}");
                        // ssh から読めるように、結果をファイルにも書く（開いているエディタの Editor.log は書き込みが遅れることがある）
                        System.IO.File.WriteAllLines("Temp/MmdSelfTest.txt", new[] { failures.Count == 0 ? "成功" : "失敗 " + failures.Count + " 件" }.Concat(failures));
                        break;
                    }
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

        public static void BuildAndTest(bool autoTest) => BuildAndTest(autoTest, "", "", 0);

        public static async void BuildAndTest(bool autoTest, string avatarId, string restoreAvatarId, int scenario, bool mobile = false, bool vr = false)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[MmdWorld.Command] Play モード中は Build & Test できない");
                return;
            }
            EditorSceneManager.OpenScene(WorldBuilder.ScenePath);
            SetVrchatSetting("ForceNoVR", !vr);
            // 自動確認のときは2つ起動し、1つ目が踊り、2つ目が客席から見る
            SetVrchatSetting("NumClients", autoTest ? 2 : 1);

            // VR で確かめるビルドでは、タブレットの指先の位置をログに出す（終わったら戻す）
            SetTabletTouchLog(vr);

            // SDK のパネルが開いていないと Builder を取れない
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out var builder))
            {
                Debug.LogError("[MmdWorld.Command] VRChat SDK の Builder を取れない（サインインしているか確認）");
                return;
            }
            // テストのビルドにだけ、入って数秒後に自分で枠1に入って再生する設定を入れる。終わったら戻して保存する
            var system = UnityEngine.Object.FindObjectsOfType<DanceSystem>(true).FirstOrDefault();
            // 着替えの台と見る位置も、このビルドの間だけ置く
            GameObject pedestalGo = null, restoreGo = null, viewPoint = null;
            if (system != null && autoTest)
            {
                system.autoTestDelay = 12f;
                system.autoTestScenario = scenario;
                viewPoint = new GameObject("AutoTestViewPoint");
                var slot1 = system.slots.Length > 0 ? system.slots[0].transform.parent : null;
                if (slot1 != null)
                {
                    // 踊りながら動く確認では、枠の前へ 4m まで出てくるので、離れて見る
                    float distance = scenario == 3 ? 7f : 1.5f;
                    viewPoint.transform.SetPositionAndRotation(slot1.position + slot1.forward * distance, Quaternion.LookRotation(-slot1.forward));
                }
                system.autoTestViewPoint = viewPoint.transform;
                if (MmdWorldLibrary.IsValidAvatarId(avatarId))
                {
                    pedestalGo = HiddenPedestal("AutoTestPedestal", avatarId, -20f);
                    system.autoTestPedestal = pedestalGo.GetComponentInChildren<VRC.SDK3.Components.VRCAvatarPedestal>();
                    if (MmdWorldLibrary.IsValidAvatarId(restoreAvatarId))
                    {
                        restoreGo = HiddenPedestal("AutoTestRestorePedestal", restoreAvatarId, -25f);
                        system.autoTestRestorePedestal = restoreGo.GetComponentInChildren<VRC.SDK3.Components.VRCAvatarPedestal>();
                    }
                }
                UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(system);
                if (mobile) SetStationMobility(VRC.SDKBase.VRCStation.Mobility.Mobile);
            }
            try
            {
                // 開いたばかりの SDK のパネルは準備に数秒かかり、その間は「パネルを開いて」で断られるので、待って呼び直す
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await builder.BuildAndTest();
                        break;
                    }
                    catch (Exception e) when (attempt < 15 && e.Message.Contains("Open the SDK panel"))
                    {
                        await System.Threading.Tasks.Task.Delay(2000);
                        VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out builder);
                    }
                }
                Debug.Log("[MmdWorld.Command] Build & Test を始めた" + (autoTest ? "（12秒後に自動で枠1に入って再生）" : ""));
            }
            catch (Exception e)
            {
                Debug.LogError("[MmdWorld.Command] Build & Test に失敗: " + e);
            }
            finally
            {
                // SDK はビルドのときにシーンを読み込み直すので、ビルド前の参照は切れている。今のシーンから探し直して後片付けする
                if (autoTest) CleanUpAutoTest();
                if (vr) SetTabletTouchLog(false);
            }
        }

        static void SetTabletTouchLog(bool on)
        {
            var tablet = UnityEngine.Object.FindObjectsOfType<DanceTablet>(true).FirstOrDefault();
            if (tablet == null || tablet.logTouch == on) return;
            tablet.logTouch = on;
            UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(tablet);
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        /// <summary>
        /// 自動確認のために入れたもの（隠した着替えの台・見る位置・自動再生の設定）をシーンから消して保存する。
        /// 着替えの台にはアカウントのアバターの ID が入るので、シーンに残してはいけない。
        /// </summary>
        public static void CleanUpAutoTest()
        {
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "AutoTestPedestal" || root.name == "AutoTestRestorePedestal" || root.name == "AutoTestViewPoint")
                    UnityEngine.Object.DestroyImmediate(root);
            var system = UnityEngine.Object.FindObjectsOfType<DanceSystem>(true).FirstOrDefault();
            if (system != null)
            {
                system.autoTestDelay = 0f;
                system.autoTestScenario = 0;
                system.autoTestPedestal = null;
                system.autoTestRestorePedestal = null;
                system.autoTestViewPoint = null;
                UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(system);
            }
            SetStationMobility(VRC.SDKBase.VRCStation.Mobility.ImmobilizeForVehicle);
            // 消した着替えの台のネットワーク ID がシーンに残ると、VRChat で読み込むときに失敗して Udon が動かなくなる
            WorldBuilder.PruneNetworkIds();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[MmdWorld.Command] 自動確認のために入れたものをシーンから消した");
        }

        static void SetStationMobility(VRC.SDKBase.VRCStation.Mobility mobility)
        {
            foreach (var slot in UnityEngine.Object.FindObjectsOfType<DanceSlot>(true))
                foreach (var station in slot.stations)
                {
                    if (station == null) continue;
                    if (station.PlayerMobility == mobility) continue;
                    station.PlayerMobility = mobility;
                    EditorUtility.SetDirty(station);
                }
            Debug.Log("[MmdWorld.Command] ステーションの PlayerMobility: " + mobility);
        }

        /// <summary>床の下に隠した着替えの台を置き、アバターの ID を入れる（自動確認のビルドの間だけ）。</summary>
        static GameObject HiddenPedestal(string name, string avatarId, float y)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/AvatarPedestal.prefab"));
            go.name = name;
            go.transform.position = new Vector3(0f, y, 0f);
            var so = new SerializedObject(go.GetComponentInChildren<VRC.SDK3.Components.VRCAvatarPedestal>());
            so.FindProperty("blueprintId").stringValue = avatarId;
            so.ApplyModifiedPropertiesWithoutUndo();
            return go;
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
