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
    ///                     preview を付けると、見る側はお手本の正面から見る
    ///                     mobile を付けると、このビルドの間だけステーションを PlayerMobility.Mobile にする（踊りながら歩けるかの検証）
    ///                     同上。クライアントを2つ起動し、先に入った方が12秒後に枠1に入って再生する（そのビルドだけ）。後の方は枠1の正面から見る。
    ///                     ID を渡すと、踊る前にそのアバターに着替え、終わったら戻す ID のアバターに着替え直す（自分がアップロードしたか公開のアバターだけ）
    ///   BuildAndTestVR    VR で1つ起動する（仮想の VR を scripts/vrsim.py で動かして確かめる。README の「仮想の VR で確かめる」）
    ///   SelfTest          マネージャーまわりの自己テスト（LibrarySelfTest）。結果はコンソールに [MmdWorld.SelfTest] で出る
    ///   OpenManager       マネージャーのウィンドウを開く
    ///   OpenAudioOffset   音のずれを合わせるウィンドウを開く
    ///   FindAvatars プロジェクト  アバターのプロジェクトのアバターを探して Temp/MmdAvatars.txt に書く（番号・ファイル・名前）
    ///   ImportAvatar プロジェクト 番号 [preview|slot]  FindAvatars の番号のアバターを取り込み、結果を Temp/MmdAvatarImport.txt に書く
    ///   InspectAvatar prefab  アバターの描画（SkinnedMeshRenderer）の骨が頭・体のどこに付いているかと、PhysBone の数を Temp/MmdAvatarInspect.txt に書く
    ///   AddPreview prefab  お手本に足す（取り込んだアバターを、組み立て直して確かめるとき）
    ///   SwitchPlatform android|windows  ビルドの対象を切り替える（アセットの取り込み直しで時間がかかる）
    ///   BuildOnly         今の対象（Windows か Android）でワールドをビルドだけして（アップロードしない）、結果と大きさを Temp/MmdBuild.txt に書く
    ///   UploadPrivate [名前]  今の対象（Windows か Android）で、ワールドを非公開（private）でアップロードする。最初の1回で新しいワールドを作り、
    ///                     ワールド ID は Assets/LocalOnly/WorldId.txt に覚える（シーンには残さない。リポジトリに入らない）。結果は Temp/MmdUpload.txt
    ///   Setting 名前 値   MmdWorldSettings の bool・int・float・string の欄を変えて保存する（試しの切り替え用）
    ///   ShowDetect [フォルダ]  マネージャーを開き、探すフォルダを変えて「フォルダから曲を探す」を押したところにする（登録はしない）
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
                            words.Contains("mobile"), viewPreview: words.Contains("preview"));
                        break;
                    case "BuildAndTestVR":
                        // BuildAndTestVR [auto] [immobilize]: auto なら入って 12 秒後に自分で枠1に入って再生する。immobilize ならこのビルドだけステーションを Immobilize にする
                        BuildAndTest(false, "", "", 0, vr: true, vrAuto: words.Contains("auto"), immobilize: words.Contains("immobilize"));
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
                    case "ShowDetect":
                        MmdWorldManagerWindow.Open();
                        EditorWindow.GetWindow<MmdWorldManagerWindow>().ShowDetected(words.Length > 1 ? string.Join(" ", words.Skip(1)) : null);
                        break;
                    case "FindAvatars":
                    {
                        var found = AvatarImporter.FindAvatars(words[1]);
                        File.WriteAllLines("Temp/MmdAvatars.txt", found.Select((c, i) => $"{i}\t{c.file}\t{c.name}"));
                        break;
                    }
                    case "ImportAvatar":
                    {
                        var candidate = AvatarImporter.FindAvatars(words[1])[int.Parse(words[2])];
                        var role = words.Length > 3 && words[3] == "slot" ? AvatarImporter.Role.SlotAvatar : AvatarImporter.Role.Preview;
                        File.WriteAllText("Temp/MmdAvatarImport.txt", "書き出し中");
                        var job = AvatarImporter.Start(words[1], candidate, role);
                        EditorApplication.CallbackFunction poll = null;
                        poll = () =>
                        {
                            var result = AvatarImporter.Poll(job);
                            if (result == null) return;
                            EditorApplication.update -= poll;
                            File.WriteAllText("Temp/MmdAvatarImport.txt", result.error != null ? "失敗: " + result.error
                                : $"成功: {AssetDatabase.GetAssetPath(result.prefab)}\n写した {result.copied}・元からある {result.skipped}\nパッケージ {string.Join(", ", result.packages)}\n{string.Join("\n", result.messages)}");
                        };
                        EditorApplication.update += poll;
                        break;
                    }
                    case "InspectAvatar":
                        File.WriteAllText("Temp/MmdAvatarInspect.txt", InspectAvatar(AssetDatabase.LoadAssetAtPath<GameObject>(string.Join(" ", words.Skip(1)))));
                        break;
                    case "AddPreview":
                    {
                        var settings = MmdWorldSettings.LoadOrCreate();
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(string.Join(" ", words.Skip(1)));
                        if (prefab != null && !settings.previewDancers.Contains(prefab)) settings.previewDancers.Add(prefab);
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssets();
                        break;
                    }
                    case "SwitchPlatform":
                    {
                        bool android = words.Length > 1 && words[1] == "android";
                        File.WriteAllText("Temp/MmdBuild.txt", "切り替え中");
                        bool ok = android
                            ? EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android)
                            : EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
                        File.WriteAllText("Temp/MmdBuild.txt", $"切り替え {(ok ? "済み" : "失敗")}: {EditorUserBuildSettings.activeBuildTarget}");
                        break;
                    }
                    case "BuildOnly":
                        BuildOnly();
                        break;
                    case "UploadPrivate":
                        UploadPrivate(words.Length > 1 ? string.Join(" ", words.Skip(1)) : "MMD World (test)");
                        break;
                    case "Setting":
                    {
                        var settings = MmdWorldSettings.LoadOrCreate();
                        var field = typeof(MmdWorldSettings).GetField(words[1]);
                        object value = field.FieldType == typeof(bool) ? (object)bool.Parse(words[2])
                            : field.FieldType == typeof(int) ? int.Parse(words[2])
                            : field.FieldType == typeof(float) ? float.Parse(words[2], System.Globalization.CultureInfo.InvariantCulture)
                            : string.Join(" ", words.Skip(2));
                        field.SetValue(settings, value);
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssets();
                        Debug.Log($"[MmdWorld.Command] 設定 {words[1]} = {value}");
                        break;
                    }
                    case "OpenAudioOffset":
                        AudioOffsetWindow.Open();
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

        public static void BuildAndTest(bool autoTest) => BuildAndTest(autoTest, "", "", 0);

        public static async void BuildAndTest(bool autoTest, string avatarId, string restoreAvatarId, int scenario, bool mobile = false, bool vr = false, bool vrAuto = false, bool immobilize = false, bool viewPreview = false)
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
                // preview なら、見る側はお手本の正面から見る（取り込んだアバターの確認用）
                var previewDancer = system.previewDancers != null ? system.previewDancers.FirstOrDefault(a => a != null) : null;
                if (viewPreview && previewDancer != null)
                {
                    var p = previewDancer.transform;
                    viewPoint.transform.SetPositionAndRotation(p.position + p.forward * 2.2f, Quaternion.LookRotation(-p.forward));
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
            // VR で確かめるビルドでは、視点の位置をログに出す。auto なら自分で枠1に入って再生する（終わったら戻す）
            if (system != null && vr)
            {
                system.logView = true;
                if (vrAuto) { system.autoTestDelay = 12f; system.autoTestScenario = 0; }
                UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(system);
                if (immobilize) SetStationMobility(VRC.SDKBase.VRCStation.Mobility.Immobilize);
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
                if (vr)
                {
                    SetTabletTouchLog(false);
                    var s = UnityEngine.Object.FindObjectsOfType<DanceSystem>(true).FirstOrDefault();
                    if (s != null)
                    {
                        s.logView = false;
                        s.autoTestDelay = 0f;
                        UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(s);
                    }
                    SetStationMobility(VRC.SDKBase.VRCStation.Mobility.ImmobilizeForVehicle);
                    var scene = EditorSceneManager.GetActiveScene();
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }
        }

        /// <summary>アバターの描画ごとに、根元の骨・骨の数・頭の骨の下の骨の数を、PhysBone ごとに根元を並べる（髪が頭に付いているかを見る）。</summary>
        static string InspectAvatar(GameObject prefab)
        {
            if (prefab == null) return "prefab が無い";
            var lines = new System.Collections.Generic.List<string>();
            var animator = prefab.GetComponent<Animator>();
            var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            string Path(Transform t) => t == null ? "(なし)" : AnimationUtility.CalculateTransformPath(t, prefab.transform);
            lines.Add($"Animator: {(animator != null ? (animator.isHuman ? "Humanoid" : "Generic") : "なし")} / 頭の骨: {Path(head)}");
            foreach (var r in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                int underHead = head != null ? r.bones.Count(b => b != null && b.IsChildOf(head)) : 0;
                int missing = r.bones.Count(b => b == null);
                lines.Add($"描画 {Path(r.transform)}: 根元 {Path(r.rootBone)}、骨 {r.bones.Length}（頭の下 {underHead}、見つからない {missing}）、有効 {r.gameObject.activeInHierarchy && r.enabled}");
            }
            foreach (var c in prefab.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name.Contains("PhysBone")))
            {
                var rootField = c.GetType().GetField("rootTransform");
                var root = rootField?.GetValue(c) as Transform;
                lines.Add($"{c.GetType().Name} {Path(c.transform)}: 根元 {Path(root != null ? root : c.transform)}（頭の下 {(head != null && (root != null ? root : c.transform).IsChildOf(head))}）");
            }
            // 一番上の階層のもの（MA が頭などに付けるために出したものがここに来る）の部品
            foreach (Transform child in prefab.transform)
                lines.Add($"一番上 {child.name}: " + string.Join("、", child.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().FullName)));
            foreach (var c in prefab.GetComponentsInChildren<Component>(true).Where(c => c != null && (c.GetType().Name.Contains("Constraint"))))
                lines.Add($"{c.GetType().FullName} {Path(c.transform)}");
            lines.Add("見つからないスクリプト: " + prefab.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)));
            return string.Join("\n", lines);
        }

        /// <summary>今の対象でワールドをビルドだけする（アップロードしない）。結果と大きさを Temp/MmdBuild.txt に書く。</summary>
        static async void BuildOnly()
        {
            File.WriteAllText("Temp/MmdBuild.txt", "ビルド中: " + EditorUserBuildSettings.activeBuildTarget);
            try
            {
                EditorSceneManager.OpenScene(WorldBuilder.ScenePath);
                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
                VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out var builder);
                string path = null;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (builder == null) throw new Exception("Open the SDK panel");
                        path = await builder.Build();
                        break;
                    }
                    catch (Exception e) when (attempt < 15 && e.Message.Contains("Open the SDK panel"))
                    {
                        await System.Threading.Tasks.Task.Delay(2000);
                        VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out builder);
                    }
                }
                long size = File.Exists(path) ? new FileInfo(path).Length : -1;
                File.WriteAllText("Temp/MmdBuild.txt", $"成功: {EditorUserBuildSettings.activeBuildTarget} {size / 1024f / 1024f:F2} MB {path}");
            }
            catch (Exception e)
            {
                File.WriteAllText("Temp/MmdBuild.txt", $"失敗: {EditorUserBuildSettings.activeBuildTarget} {e.GetType().Name}: {e.Message}");
                Debug.LogException(e);
            }
        }

        const string WorldIdFile = "Assets/LocalOnly/WorldId.txt";

        /// <summary>
        /// 今の対象でワールドを非公開でアップロードする。WorldIdFile にワールド ID があればそのワールドを更新し、無ければ新しく作って ID を覚える。
        /// ワールド ID は、アップロードの間だけシーンの PipelineManager に入れ、終わったら消す（シーンはリポジトリに入るので）。
        /// </summary>
        static async void UploadPrivate(string name)
        {
            File.WriteAllText("Temp/MmdUpload.txt", "アップロード中: " + EditorUserBuildSettings.activeBuildTarget);
            var scene = EditorSceneManager.OpenScene(WorldBuilder.ScenePath);
            var pipeline = UnityEngine.Object.FindObjectsOfType<VRC.Core.PipelineManager>(true).FirstOrDefault();
            try
            {
                if (pipeline == null) throw new Exception("シーンに PipelineManager が無い");
                string id = File.Exists(WorldIdFile) ? File.ReadAllText(WorldIdFile).Trim() : "";
                pipeline.blueprintId = id;
                // 新しいワールドは、SDK のパネルから押したときと同じように先に ID を割り当てて保存しておく
                // （アップロードの途中で SDK がシーンを読み込み直すので、保存していないと権利の確認を送るときに ID が空になって失敗した）
                bool creating = string.IsNullOrEmpty(id);
                if (creating) pipeline.AssignId(VRC.Core.PipelineManager.ContentType.world);
                // 権利の確認（Copyright ownership agreement）は本人の同意をもらってから送る（2026-10-05、本人「OKで進んでください」）。
                // SDK のダイアログで OK を押したときと同じ処理（内部の Agree）を呼ぶ。同じセッションで同意済みならダイアログは出ない
                var agree = typeof(VRC.SDKBase.VRCCopyrightAgreement).GetMethod("Agree", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                bool agreed = agree != null && await (System.Threading.Tasks.Task<bool>)agree.Invoke(null, new object[] { pipeline.blueprintId });
                File.AppendAllText("Temp/MmdUpload.txt", $"\nID {pipeline.blueprintId} / Agree {(agree != null ? "あり" : "なし")} / 結果 {agreed}");
                if (!agreed) throw new Exception("権利の確認を送れなかった（ID " + pipeline.blueprintId + "）");
                EditorUtility.SetDirty(pipeline);
                EditorSceneManager.SaveScene(scene);

                VRC.SDKBase.Editor.Api.VRCWorld world;
                string thumbnail = null;
                if (!string.IsNullOrEmpty(id))
                {
                    world = await VRC.SDKBase.Editor.Api.VRCApi.GetWorld(id, true);
                }
                else
                {
                    world = new VRC.SDKBase.Editor.Api.VRCWorld
                    {
                        Name = name,
                        Description = "MMD World の確かめ用（非公開）",
                        Capacity = 16,
                        RecommendedCapacity = 8,
                        Tags = new System.Collections.Generic.List<string>(),
                        ReleaseStatus = "private",
                    };
                    thumbnail = MakeThumbnail();
                }

                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
                VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out var builder);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (builder == null) throw new Exception("Open the SDK panel");
                        await builder.BuildAndUpload(world, thumbnailPath: thumbnail);
                        break;
                    }
                    catch (Exception e) when (attempt < 15 && e.Message.Contains("Open the SDK panel"))
                    {
                        await System.Threading.Tasks.Task.Delay(2000);
                        VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out builder);
                    }
                }
                // 新しく作ったワールドの ID は、アップロードで PipelineManager に入る
                pipeline = UnityEngine.Object.FindObjectsOfType<VRC.Core.PipelineManager>(true).FirstOrDefault();
                if (pipeline != null && !string.IsNullOrEmpty(pipeline.blueprintId)) File.WriteAllText(WorldIdFile, pipeline.blueprintId);
                File.WriteAllText("Temp/MmdUpload.txt", $"成功: {EditorUserBuildSettings.activeBuildTarget} {(pipeline != null ? pipeline.blueprintId : "")}");
            }
            catch (Exception e)
            {
                File.WriteAllText("Temp/MmdUpload.txt", $"失敗: {EditorUserBuildSettings.activeBuildTarget} {e.GetType().Name}: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                pipeline = UnityEngine.Object.FindObjectsOfType<VRC.Core.PipelineManager>(true).FirstOrDefault();
                if (pipeline != null)
                {
                    pipeline.blueprintId = "";
                    EditorUtility.SetDirty(pipeline);
                    EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
                }
            }
        }

        /// <summary>客席から舞台を撮ったサムネイル（1200×900）を Temp に書いて、そのパスを返す（新しいワールドを作るときに要る）。</summary>
        static string MakeThumbnail()
        {
            var go = new GameObject("ThumbnailCamera");
            try
            {
                var camera = go.AddComponent<Camera>();
                go.transform.SetPositionAndRotation(new Vector3(0f, 2.2f, -1.5f), Quaternion.LookRotation(new Vector3(0f, 1.2f, 4.5f) - new Vector3(0f, 2.2f, -1.5f)));
                camera.fieldOfView = 50f;
                var rt = new RenderTexture(1200, 900, 24);
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1200, 900, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1200, 900), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                camera.targetTexture = null;
                string path = Path.GetFullPath("Temp/MmdThumbnail.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
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
