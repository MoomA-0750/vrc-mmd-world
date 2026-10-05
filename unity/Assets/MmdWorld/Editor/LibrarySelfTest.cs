using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Components;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// マネージャーの操作（曲の出し入れ・設定の反映）を、実際にアセットを作って確かめる。
    /// エディタの既定のアセンブリにあるのでテスト用のアセンブリから呼べず、batchmode から
    /// -executeMethod MmdWorld.EditorTools.LibrarySelfTest.RunFromCommandLine で流す。終わると元の状態に戻す。
    /// </summary>
    public static class LibrarySelfTest
    {
        const string SourceVmd = "Assets/MmdWorld/Songs/Wavefile/wavefile_v2.vmd";
        const string SourceAudio = "Assets/MmdWorld/Songs/Wavefile/click_120bpm.wav";
        const string TestAvatarId = "avtr_00000000-0000-0000-0000-000000000000";

        public static void RunFromCommandLine()
        {
            var failures = Run();
            foreach (var f in failures) Debug.LogError("[MmdWorld.SelfTest] 失敗: " + f);
            Debug.Log($"[MmdWorld.SelfTest] 結果: {(failures.Count == 0 ? "成功" : "失敗 " + failures.Count + " 件")}");
            EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        /// <summary>ボーンのキーの無い .vmd を書く（表情「あ」を morphKeys 個、カメラのキーを cameraKeys 個）。</summary>
        static void WriteVmd(string path, int morphKeys, int cameraKeys)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                var header = new byte[30];
                System.Text.Encoding.ASCII.GetBytes("Vocaloid Motion Data 0002").CopyTo(header, 0);
                w.Write(header);
                w.Write(new byte[20]);
                w.Write(0u); // ボーン
                w.Write((uint)morphKeys);
                for (int i = 0; i < morphKeys; i++)
                {
                    var name = new byte[15];
                    name[0] = 0x82; name[1] = 0xA0; // 「あ」（Shift_JIS）
                    w.Write(name);
                    w.Write((uint)(i * 30));
                    w.Write(i == 0 ? 0f : 0.77f);
                }
                w.Write((uint)cameraKeys);
                w.Write(new byte[61 * cameraKeys]);
            }
        }

        public static List<string> Run()
        {
            var failures = new List<string>();
            void Check(bool ok, string what) { if (!ok) failures.Add(what); else Debug.Log("[MmdWorld.SelfTest] ok: " + what); }

            // プロジェクトの外に置いたファイルから足す（エクスプローラーからのドロップと同じ）
            string temp = Path.Combine(Path.GetTempPath(), "MmdWorldSelfTest");
            Directory.CreateDirectory(temp);
            string vmd = Path.Combine(temp, "selftest.vmd");
            string wav = Path.Combine(temp, "selftest.wav");
            File.Copy(SourceVmd, vmd, true);
            File.Copy(SourceAudio, wav, true);
            // 複数人のモーションのパートの代わり（同じ踊りを写したもの）
            string vmdLeft = Path.Combine(temp, "selftest_left.vmd");
            string vmdRight = Path.Combine(temp, "selftest_right.vmd");
            File.Copy(SourceVmd, vmdLeft, true);
            File.Copy(SourceVmd, vmdRight, true);

            // 表情だけ・カメラだけの .vmd（MMD の配布物によく一緒に入っている）
            string faceVmd = Path.Combine(temp, "selftest_face.vmd");
            string cameraVmd = Path.Combine(temp, "selftest_camera.vmd");
            WriteVmd(faceVmd, 2, 0);
            WriteVmd(cameraVmd, 0, 3);
            Check(MmdWorldLibrary.Classify(vmd) == MmdWorldLibrary.VmdKind.Dance, "踊りの .vmd は踊りと分かる");
            Check(MmdWorldLibrary.Classify(faceVmd) == MmdWorldLibrary.VmdKind.Face, "表情だけの .vmd は表情と分かる");
            Check(MmdWorldLibrary.Classify(cameraVmd) == MmdWorldLibrary.VmdKind.Camera, "カメラの .vmd はカメラと分かる");

            MmdWorldLibrary.VmdKind Fake(string p) => p.Contains("face") ? MmdWorldLibrary.VmdKind.Face
                : p.Contains("camera") ? MmdWorldLibrary.VmdKind.Camera : MmdWorldLibrary.VmdKind.Dance;
            var single = MmdWorldManagerWindow.PlanDropped(new[] { "a/x.vmd", "a/song.wav" }, Fake);
            Check(single.Songs.Count == 1 && single.Songs[0].audio == "a/song.wav", "vmd と音声が1つずつなら組になる");
            var bundle = MmdWorldManagerWindow.PlanDropped(new[] { "a/center.vmd", "a/left.vmd", "a/face.vmd", "a/camera.vmd", "a/song.wav" }, Fake);
            Check(bundle.Songs.Count == 2 && bundle.Songs.All(t => t.audio == "a/song.wav" && t.face == "a/face.vmd") && bundle.Skipped.Count == 1,
                  "配布物をまとめて落とすと、踊りごとに曲になり、音声と表情は全部に付き、カメラは飛ばす");
            var multi = MmdWorldManagerWindow.PlanDropped(new[] { "a/m_right.vmd", "a/m_center.vmd", "a/m_left.vmd", "a/other.vmd", "a/song1.vmd", "a/song2.vmd", "a/song.wav" }, Fake);
            var m = multi.Songs.FirstOrDefault(t => t.title == "m");
            Check(m != null && m.vmd == "a/m_center.vmd" && m.parts.SequenceEqual(new[] { "a/m_left.vmd", "a/m_right.vmd" })
                  && multi.Songs.Count(t => t.title != "m") == 3,
                  "名前の末尾だけが違う .vmd は、センター・左・右の順のパートで1曲にまとまる（区切り文字の無い song1・song2 は別の曲）");
            var named = MmdWorldManagerWindow.PlanDropped(new[] { "a/x.vmd", "a/y.vmd", "a/y.ogg", "a/z.wav" }, Fake);
            Check(named.Songs.Count == 2 && named.Songs[0].audio == null && named.Songs[1].audio == "a/y.ogg", "音声が複数なら同じ名前どうしが組になる");
            var faceOnly = MmdWorldManagerWindow.PlanDropped(new[] { "a/face.vmd" }, Fake);
            Check(faceOnly.Songs.Count == 0 && faceOnly.Skipped.Count == 1, "表情だけを落としても曲にはしない");
            bool rejected = false;
            try { MmdWorldLibrary.AddSong(cameraVmd); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "カメラの .vmd は曲として足せない");

            var settings = MmdWorldSettings.LoadOrCreate();
            var saved = (settings.slotCount, settings.countdownSeconds, new List<GameObject>(settings.previewDancers), new List<string>(settings.pedestalAvatarIds));
            var savedSlotAvatars = new List<GameObject>(settings.slotAvatars);
            int before = MmdWorldLibrary.Songs().Count;
            // 並べ替えで変わる既存の曲の順番も、終わったら戻す
            var orders = MmdWorldLibrary.Songs().ToDictionary(s => AssetDatabase.GetAssetPath(s), s => s.order);
            DanceSong song = null;
            string songPath = null;
            try
            {
                song = MmdWorldLibrary.AddSong(vmd, wav, "自己テスト/曲:1", faceVmd, new[] { vmdLeft, vmdRight });
                // シーンを作り直すと、使われていないアセットは外されて参照が切れるので、パスで持っておく
                songPath = AssetDatabase.GetAssetPath(song);
                string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(song)).Replace('\\', '/');
                Check(MmdWorldLibrary.Songs().Count == before + 1, "曲が1つ増える");
                Check(dir.StartsWith(MmdWorldLibrary.SongsDir + "/") && !dir.Contains(":"), "曲は Songs の下の、使えない文字を除いた名前のフォルダに入る: " + dir);
                Check(song.motion != null && song.motion.humanMotion, "モーションが Humanoid として取り込まれる");
                Check(song.audio != null && song.audio.length > 90f, "音声が入る");
                Check(song.face != null, "表情の .vmd が曲の表情に入る");
                Check(song.parts.Count == 2 && MmdWorldLibrary.Parts(song).Count == 3, "複数人のモーションは2人目以降がパートに入る");
                Check(song.title == "自己テスト/曲:1", "題名はそのまま残る");
                Check(MmdWorldLibrary.Songs().Last() == song, "新しい曲は最後に並ぶ");

                MmdWorldLibrary.Move(song, -1);
                Check(MmdWorldLibrary.Songs().IndexOf(song) == before - 1, "▲で1つ前へ動く");

                // アバターのプロジェクトから取り込む: アバターのプロジェクトか、シーンの中の Avatar Descriptor の付いた GameObject の名前を拾えるか（同じ DLL のほかの部品は拾わない）
                string fakeProject = Path.Combine(Path.GetTempPath(), "mmdworld-selftest-avatar");
                Directory.CreateDirectory(Path.Combine(fakeProject, "Packages"));
                Directory.CreateDirectory(Path.Combine(fakeProject, "Assets", "Scene"));
                File.WriteAllText(Path.Combine(fakeProject, "Packages", "vpm-manifest.json"), "{\"dependencies\":{\"com.vrchat.avatars\":{\"version\":\"3.10.5\"}}}");
                File.WriteAllText(Path.Combine(fakeProject, "Assets", "Scene", "a.unity"),
                    "%YAML 1.1\n--- !u!1 &100\nGameObject:\n  m_Name: テストのアバター\n--- !u!114 &101\nMonoBehaviour:\n  m_GameObject: {fileID: 100}\n  m_Script: {fileID: 542108242, guid: 67cc4cb7839cd3741b63733d5adf0442, type: 3}\n" +
                    "--- !u!1 &200\nGameObject:\n  m_Name: エフェクト\n--- !u!114 &201\nMonoBehaviour:\n  m_GameObject: {fileID: 200}\n  m_Script: {fileID: -1122756469, guid: 67cc4cb7839cd3741b63733d5adf0442, type: 3}\n");
                var avatars = AvatarImporter.FindAvatars(fakeProject);
                Check(AvatarImporter.IsAvatarProject(fakeProject) && avatars.Count == 1 && avatars[0].name == "テストのアバター" && avatars[0].file == "Assets/Scene/a.unity",
                      "アバターのプロジェクトのシーンから、Avatar Descriptor の付いたアバターだけを見つける: " + string.Join(", ", avatars));
                Directory.Delete(fakeProject, true);

                // 音のずれを合わせるウィンドウが使う、モーションの動きの大きさ
                var activity = AudioOffsetWindow.MotionActivity(song.motion);
                Check(activity.Length > 60 * 60 && activity.Max() <= 1f && activity.Count(v => v > 0.3f) > 100, $"モーションの動きの大きさが読める（{activity.Length} 個、山 {activity.Count(v => v > 0.3f)} 個）");

                // 書き出しと読み込み: JSON に書いてから題名・音のずれ・立ち位置を変え、読み込むと元に戻る（同じモーションの曲に書き戻す）
                string json = Path.Combine(Path.GetTempPath(), "mmdworld-selftest.json");
                song.audioOffset = 0.12f;
                song.partOffsets = new List<Vector3> { Vector3.zero, new Vector3(1f, 0f, 2f) };
                EditorUtility.SetDirty(song);
                MmdWorldExport.ExportJson(json);
                int songsBeforeImport = MmdWorldLibrary.Songs().Count;
                song.title = "変えた題名";
                song.audioOffset = 9f;
                song.partOffsets = new List<Vector3>();
                var imported = MmdWorldExport.ImportJson(json);
                Check(imported.added == 0 && imported.updated == songsBeforeImport && imported.missing.Count == 0 && MmdWorldLibrary.Songs().Count == songsBeforeImport,
                      $"JSON から読み込むと、同じモーションの曲に書き戻す（足した {imported.added}・書き戻した {imported.updated}・見つからない {imported.missing.Count}）");
                Check(song.title == "自己テスト/曲:1" && Mathf.Approximately(song.audioOffset, 0.12f) && song.partOffsets.Count == 2 && song.partOffsets[1] == new Vector3(1f, 0f, 2f)
                      && song.parts.Count == 2 && song.face != null && song.audio != null, "JSON の往復で題名・音のずれ・立ち位置・パート・表情・音声が戻る");
                File.Delete(json);
                string package = Path.Combine(Path.GetTempPath(), "mmdworld-selftest.unitypackage");
                var packed = MmdWorldExport.ExportPackage(package);
                Check(File.Exists(package) && packed.Contains(AssetDatabase.GetAssetPath(song)) && packed.Contains(AssetDatabase.GetAssetPath(song.motion))
                      && packed.Contains(AssetDatabase.GetAssetPath(song.audio)) && !packed.Any(p => p.EndsWith(".cs") || p.EndsWith(".dll")),
                      $"素材ごとの書き出しに、曲の設定・モーション・音声が入り、スクリプトは入らない（{packed.Count} ファイル）");
                File.Delete(package);
                song.audioOffset = 0f;
                song.partOffsets = new List<Vector3>();
                EditorUtility.SetDirty(song);

                var mannequin = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/MmdWorld/Generated/Mannequin.prefab");
                var mc = MmdWorldLibrary.CheckAvatar(mannequin, new[] { song });
                Check(mc.IsHumanoid && !mc.HasFaceMesh && mc.MorphsWanted > 20, $"人形は Humanoid・表情のメッシュ無し・曲の表情は {mc.MorphsWanted} 個");

                Check(MmdWorldLibrary.IsValidAvatarId(TestAvatarId) && !MmdWorldLibrary.IsValidAvatarId("avtr_xyz"), "アバター ID の形を見分ける");

                // 設定がシーンに反映されるか
                settings.slotCount = 6;
                settings.countdownSeconds = 5f;
                settings.pedestalAvatarIds = new List<string> { TestAvatarId, "not-an-id" };
                var local = FindLocalAvatar();
                settings.previewDancers = local != null ? new List<GameObject> { local, null } : new List<GameObject>();
                // 枠で踊らせるアバター: 人形の prefab（と、あれば手元のアバター）。同じものを2回入れても1体だけ置く
                var mannequinPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/MmdWorld/Generated/Mannequin.prefab");
                settings.slotAvatars = local != null ? new List<GameObject> { mannequinPrefab, local, mannequinPrefab } : new List<GameObject> { mannequinPrefab, mannequinPrefab };
                EditorUtility.SetDirty(settings);
                // 2人目の立ち位置を右に 0.5m・前に 0.25m ずらす（y は使わない）
                song.partOffsets = new List<Vector3> { Vector3.zero, new Vector3(0.5f, 9f, 0.25f) };
                EditorUtility.SetDirty(song);
                WorldBuilder.Build();
                var slots = UnityEngine.Object.FindObjectsOfType<DanceSlot>(true);
                Check(slots.Length == 6, "枠が6つになる: " + slots.Length);
                {
                    // 足した曲のステーションのクリップに、表情の .vmd の「あ」（30 フレームで 0.77。ブレンドシェイプの重みは 0〜100 なので 77）が重なっている
                    var sys = UnityEngine.Object.FindObjectsOfType<DanceSystem>(true).First();
                    int index = Array.IndexOf(sys.songTitles, "自己テスト/曲:1");
                    var controller = index >= 0 ? sys.segmentControllers[sys.trackSegmentStart[sys.songPartStart[index]]] as UnityEditor.Animations.AnimatorController : null;
                    Check(index >= 0 && sys.songPartCount[index] == 3 && sys.trackNames[sys.songPartStart[index] + 1] == "selftest_left" && sys.stageOrigin != null,
                          "複数人の曲はパートの数だけトラックがあり、立ち位置の原点がある");
                    Check(index >= 0 && sys.TrackFor(index, 4) == sys.songPartStart[index] + 1, "枠5 は 2人目のパートを踊る（枠の順に割り当てて繰り返す）");
                    Check(index >= 0 && sys.trackOffsets.Length == sys.trackNames.Length && sys.trackOffsets[sys.songPartStart[index] + 1] == new Vector3(0.5f, 0f, 0.25f)
                          && sys.trackOffsets[sys.songPartStart[index] + 2] == Vector3.zero, "パートごとの立ち位置のずれがトラックに入る（y は 0、書いていないパートは 0）");
                    var clip = controller != null ? controller.layers[0].stateMachine.defaultState.motion as AnimationClip : null;
                    var binding = clip != null ? AnimationUtility.GetCurveBindings(clip).FirstOrDefault(b => b.propertyName == "blendShape.あ") : default;
                    var curve = clip != null && binding.propertyName != null ? AnimationUtility.GetEditorCurve(clip, binding) : null;
                    Check(curve != null && Mathf.Abs(curve.Evaluate(1f) - 77f) < 0.5f,
                          $"表情の .vmd がステーションのクリップに重なる（クリップ {(clip != null ? clip.name : "なし")}、1秒の「あ」= {(curve != null ? curve.Evaluate(1f).ToString("F1") : "無し")}）");
                }
                int songCount = MmdWorldLibrary.Songs().Count;
                Check(slots.All(s => s.stations.Length == 2 && s.stations.All(st => st != null) && s.stationRoot != null), "どの枠にもステーションが2つ（交互に乗り換える用）ある");
                var pedestals = UnityEngine.Object.FindObjectsOfType<VRCAvatarPedestal>(true);
                Check(pedestals.Length == 1 && pedestals[0].blueprintId == TestAvatarId, "形の正しい ID の台だけが1つ置かれる");
                var system = UnityEngine.Object.FindObjectsOfType<DanceSystem>(true).First();
                int segTotal = MmdWorldLibrary.Songs().Sum(sg => MmdWorldLibrary.Segments(sg).Count * MmdWorldLibrary.Parts(sg).Count);
                Check(system.segmentControllers.Length == segTotal && system.segmentControllers.All(c => c != null), $"区切り × パートの数だけ Controller がある: {system.segmentControllers.Length}");
                Check(system.inPlaceControllers.Length == segTotal && system.inPlaceControllers.All(c => c != null) && system.inPlaceControllers[0] != system.segmentControllers[0],
                      "「その場」用の Controller も同じ数だけある（移動ありとは別）");
                int timesTotal = MmdWorldLibrary.Songs().Sum(sg => MmdWorldLibrary.Segments(sg).Count);
                Check(system.segmentCount.Length == songCount && system.segmentTimes.Length == timesTotal && system.segmentTimes[system.segmentStart.Last()] == 0f, "区切りの表が曲ごとに 0 秒から並ぶ");
                Check(Mathf.Approximately(system.countdownSeconds, 5f), "カウントダウンの秒数が入る");
                Check(system.songTitles.Contains("自己テスト/曲:1"), "足した曲がパネルの曲に入る");
                int expectedSlotAvatars = local != null ? 2 : 1;
                Check(system.slotAvatars.Length == expectedSlotAvatars && system.slotAvatarNames.Length == expectedSlotAvatars, "枠で踊らせるアバターが置かれる（重複は1体）: " + system.slotAvatars.Length);
                Check(system.slotAvatars.All(a => a != null && !a.gameObject.activeSelf && a.runtimeAnimatorController != null), "枠のアバターは最初は隠れていて、踊りの Animator が付いている");
                Check(system.uiCopies == 2 && system.slotTexts.Length == 8 && system.songRowTexts.Length == 14 && system.modelRowTexts.Length == 14
                      && UnityEngine.Object.FindObjectsOfType<DanceButton>(true).Count(b => b.eventName == nameof(DanceSystem.SlotButton)) == 8,
                      "タブレットと舞台の横のパネルの両方に、枠の列（4つ）と左右の一覧（7行ずつ）がある");
                var tablet = UnityEngine.Object.FindObjectsOfType<DanceTablet>(true).First();
                var panelButtons = UnityEngine.Object.FindObjectsOfType<DanceButton>(true).Where(b => !tablet.buttons.Contains(b)).ToList();
                Check(panelButtons.Count == tablet.buttons.Length - 1 && panelButtons.All(b => b.GetComponent<Collider>() != null && b.target == system),
                      "舞台の横のパネルは、タブレットと同じボタン（「閉じる」以外）を、当たり判定つきで持つ");
                Check(panelButtons.All(b => b.GetComponent<Collider>().isTrigger) && GameObject.Find("Panel").GetComponentsInChildren<Collider>(true).All(c => c.isTrigger),
                      "舞台の横のパネルの当たり判定はトリガーだけ（体当たりしてもすり抜ける）");
                Check(GameObject.Find("Mirror") == null, "客席の鏡は置かない");
                Check(tablet.buttonColliders.Length == tablet.buttons.Length && tablet.buttonColliders.All(c => c != null && c.isTrigger && !c.enabled)
                      && tablet.touchToggle != null && !tablet.touchToggle.activeSelf && tablet.touchToggle.GetComponentInChildren<DanceButton>(true).eventName == nameof(DanceTablet.Toggle),
                      "スマホ用: タブレットのボタンに（ふだんは切った）当たり判定があり、「メニュー」ボタンがある");
                Check(!GameObject.Find("Floor").GetComponent<Renderer>().enabled && GameObject.Find("Floor").GetComponent<Collider>() != null, "床は見えないが、当たり判定はある");
                Check(slots.All(s => s.pad.sharedMaterial != null && s.pad.sharedMaterial.shader.name == "VRChat/Mobile/Standard Lite"), "台座などのマテリアルは Quest 向けの軽いシェーダー");
                Check(UnityEngine.Object.FindObjectsOfType<DanceButton>(true).All(b => b.eventName != nameof(DanceSlot.NextAvatar)), "枠ごとの、アバターを順に切り替えるボタンは無い");
                if (local != null)
                {
                    var placed = system.slotAvatars.First(a => a.name == local.name).gameObject;
                    int avatarParts = placed.GetComponentsInChildren<Component>(true).Count(c => c != null && (c.GetType().FullName ?? "").StartsWith("VRC.SDK3.Avatars"));
                    Check(avatarParts == 0, "手元のアバターからアバター専用の部品が外れている");
                }
                if (local != null)
                    Check(system.previewDancers.Length == 1 && system.previewDancers[0].name.Contains(local.name), "お手本に手元のアバターが入る（空の欄は飛ばす）");
                else
                    Check(system.previewDancers.Length == 1 && system.previewDancers[0].name.Contains("Mannequin"), "お手本が空なら付属の人形");
            }
            catch (Exception e)
            {
                failures.Add("例外: " + e);
            }
            finally
            {
                if (songPath != null)
                {
                    string dir = Path.GetDirectoryName(songPath).Replace('\\', '/');
                    MmdWorldLibrary.RemoveSong(AssetDatabase.LoadAssetAtPath<DanceSong>(songPath));
                    Check(!AssetDatabase.IsValidFolder(dir), "消すと曲のフォルダごと無くなる");
                    Check(MmdWorldLibrary.Songs().Count == before, "曲の数が元に戻る");
                }
                // シーンを作り直すと設定のアセットも外されて参照が切れるので、読み直してから戻す
                settings = MmdWorldSettings.LoadOrCreate();
                (settings.slotCount, settings.countdownSeconds, settings.previewDancers, settings.pedestalAvatarIds) = saved;
                settings.slotAvatars = savedSlotAvatars;
                foreach (var pair in orders)
                {
                    var s = AssetDatabase.LoadAssetAtPath<DanceSong>(pair.Key);
                    if (s == null || s.order == pair.Value) continue;
                    s.order = pair.Value;
                    EditorUtility.SetDirty(s);
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                WorldBuilder.Build();
                Directory.Delete(temp, true);
            }
            return failures;
        }

        static GameObject FindLocalAvatar()
        {
            if (!AssetDatabase.IsValidFolder("Assets/LocalOnly")) return null;
            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { "Assets/LocalOnly" }))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                var animator = go != null ? go.GetComponent<Animator>() : null;
                if (animator != null && animator.avatar != null && animator.avatar.isHuman) return go;
            }
            return null;
        }
    }
}
