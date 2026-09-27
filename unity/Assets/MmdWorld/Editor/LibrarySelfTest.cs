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

            var pairs = MmdWorldManagerWindow.PairFiles(new[] { vmd, wav });
            Check(pairs.Count == 1 && pairs[0].audio == wav, "vmd と音声が1つずつなら組になる");
            var named = MmdWorldManagerWindow.PairFiles(new[] { "a/x.vmd", "a/y.vmd", "a/y.ogg", "a/z.wav" });
            Check(named.Count == 2 && named[0].audio == null && named[1].audio == "a/y.ogg", "複数なら同じ名前どうしが組になる");

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
                song = MmdWorldLibrary.AddSong(vmd, wav, "自己テスト/曲:1");
                // シーンを作り直すと、使われていないアセットは外されて参照が切れるので、パスで持っておく
                songPath = AssetDatabase.GetAssetPath(song);
                string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(song)).Replace('\\', '/');
                Check(MmdWorldLibrary.Songs().Count == before + 1, "曲が1つ増える");
                Check(dir.StartsWith(MmdWorldLibrary.SongsDir + "/") && !dir.Contains(":"), "曲は Songs の下の、使えない文字を除いた名前のフォルダに入る: " + dir);
                Check(song.motion != null && song.motion.humanMotion, "モーションが Humanoid として取り込まれる");
                Check(song.audio != null && song.audio.length > 90f, "音声が入る");
                Check(song.title == "自己テスト/曲:1", "題名はそのまま残る");
                Check(MmdWorldLibrary.Songs().Last() == song, "新しい曲は最後に並ぶ");

                MmdWorldLibrary.Move(song, -1);
                Check(MmdWorldLibrary.Songs().IndexOf(song) == before - 1, "▲で1つ前へ動く");

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
                WorldBuilder.Build();
                var slots = UnityEngine.Object.FindObjectsOfType<DanceSlot>(true);
                Check(slots.Length == 6, "枠が6つになる: " + slots.Length);
                int songCount = MmdWorldLibrary.Songs().Count;
                Check(slots.All(s => s.stations.Length == 2 && s.stations.All(st => st != null) && s.stationRoot != null), "どの枠にもステーションが2つ（交互に乗り換える用）ある");
                var pedestals = UnityEngine.Object.FindObjectsOfType<VRCAvatarPedestal>(true);
                Check(pedestals.Length == 1 && pedestals[0].blueprintId == TestAvatarId, "形の正しい ID の台だけが1つ置かれる");
                var system = UnityEngine.Object.FindObjectsOfType<DanceSystem>(true).First();
                int segTotal = MmdWorldLibrary.Songs().Sum(sg => MmdWorldLibrary.Segments(sg).Count);
                Check(system.segmentControllers.Length == segTotal && system.segmentControllers.All(c => c != null), $"区切りの数だけ Controller がある: {system.segmentControllers.Length}");
                Check(system.segmentCount.Length == songCount && system.segmentTimes.Length == segTotal && system.segmentTimes[system.segmentStart.Last()] == 0f, "区切りの表が曲ごとに 0 秒から並ぶ");
                Check(Mathf.Approximately(system.countdownSeconds, 5f), "カウントダウンの秒数が入る");
                Check(system.songTitles.Contains("自己テスト/曲:1"), "足した曲がパネルの曲に入る");
                int expectedSlotAvatars = local != null ? 2 : 1;
                Check(system.slotAvatars.Length == expectedSlotAvatars && system.slotAvatarNames.Length == expectedSlotAvatars, "枠で踊らせるアバターが置かれる（重複は1体）: " + system.slotAvatars.Length);
                Check(system.slotAvatars.All(a => a != null && !a.gameObject.activeSelf && a.runtimeAnimatorController != null), "枠のアバターは最初は隠れていて、踊りの Animator が付いている");
                Check(UnityEngine.Object.FindObjectsOfType<DanceButton>(true).Count(b => b.eventName == nameof(DanceSystem.SelectAvatarButton)) == 4
                      && system.selectAvatarTexts.Length == 4 && system.selectSlotTexts.Length == 4 && system.selectSongTexts.Length == 4,
                      "タブレットの「選ぶ」のページに、曲・枠・アバター（なし＋3体）のボタンがある");
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
