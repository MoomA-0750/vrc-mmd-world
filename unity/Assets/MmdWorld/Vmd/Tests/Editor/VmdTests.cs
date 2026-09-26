using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.Vmd.Tests
{
    public class VmdTests
    {
        const string WavefilePath = "Assets/MmdWorld/Songs/Wavefile/wavefile_v2.vmd";

        // ---- 補間曲線 ----

        [Test]
        public void Bezier_LinearIsIdentity()
        {
            foreach (float x in new[] { 0f, 0.25f, 0.5f, 0.9f, 1f })
                Assert.That(VmdBezier.Linear.Evaluate(x), Is.EqualTo(x).Within(1e-4f));
        }

        [Test]
        public void Bezier_EaseInIsSlowAtStart()
        {
            var easeIn = new VmdBezier { X1 = 0.8f, Y1 = 0f, X2 = 1f, Y2 = 1f };
            Assert.That(easeIn.Evaluate(0.3f), Is.LessThan(0.1f));
            Assert.That(easeIn.Evaluate(1f), Is.EqualTo(1f));
        }

        // ---- 読み込み ----

        [Test]
        public void Reader_ReadsSyntheticFile()
        {
            var w = new VmdWriter();
            w.Bone("センター", 0, new Vector3(0, 0, 0), Quaternion.identity);
            w.Bone("センター", 30, new Vector3(1, 2, 3), Quaternion.Euler(0, 90, 0));
            w.Bone("左足ＩＫ", 10, new Vector3(0, 1, 0), Quaternion.identity);
            w.Morph("あ", 5, 0.5f);
            w.Ik(0, ("左足ＩＫ", true), ("右足ＩＫ", false));
            var motion = VmdReader.Read(w.ToBytes());

            Assert.That(motion.ModelName, Is.EqualTo("テスト"));
            Assert.That(motion.Bones["センター"].Count, Is.EqualTo(2));
            Assert.That(motion.Bones["センター"][1].Position, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(motion.Bones.ContainsKey("左足ＩＫ"));
            Assert.That(motion.Morphs["あ"][0].Weight, Is.EqualTo(0.5f));
            Assert.That(motion.IkKeys[0].Enabled["右足ＩＫ"], Is.False);
            Assert.That(motion.LastFrame, Is.EqualTo(30));
        }

        [Test]
        public void Reader_AcceptsBoneOnlyFile()
        {
            var w = new VmdWriter { WriteOptionalSections = false };
            w.Bone("頭", 0, Vector3.zero, Quaternion.identity);
            var motion = VmdReader.Read(w.ToBytes());
            Assert.That(motion.Bones["頭"].Count, Is.EqualTo(1));
            Assert.That(motion.Morphs.Count, Is.EqualTo(0));
        }

        [Test]
        public void Reader_RejectsNonVmd()
        {
            Assert.Throws<InvalidDataException>(() => VmdReader.Read(new byte[64]));
        }

        [Test]
        public void Reader_ReadsWavefile()
        {
            var motion = VmdReader.Read(WavefilePath);
            int boneKeys = 0;
            foreach (var keys in motion.Bones.Values) boneKeys += keys.Count;
            int morphKeys = 0;
            foreach (var keys in motion.Morphs.Values) morphKeys += keys.Count;

            Assert.That(boneKeys, Is.EqualTo(14160));
            Assert.That(morphKeys, Is.EqualTo(1279));
            Assert.That(motion.LastFrame, Is.EqualTo(2809));
            Assert.That(motion.Bones["左足ＩＫ"].Count, Is.EqualTo(292));
            Assert.That(motion.Morphs["あ"].Count, Is.EqualTo(428));
        }

        // ---- 姿勢 ----

        [Test]
        public void Solver_NoKeysGivesRestPose()
        {
            var skeleton = new MmdSkeleton();
            var solver = new MmdPoseSolver(skeleton, new VmdMotion());
            solver.Solve(0);
            // 足IK は伸びきらないように到達距離をわずかに縮めて解くので、ひざから下は 1mm 未満ずれる
            foreach (var bone in skeleton.Bones)
                Assert.That(Vector3.Distance(solver.GlobalPosition[bone.Index], bone.RestPosition), Is.LessThan(1e-3f), bone.Name);
        }

        [Test]
        public void Solver_LegIkReachesTarget()
        {
            // 足IK を 3 単位（24cm）上げてしゃがませる
            var w = new VmdWriter();
            w.Bone("左足ＩＫ", 0, new Vector3(0, 3, 0), Quaternion.identity);
            var skeleton = new MmdSkeleton();
            var solver = new MmdPoseSolver(skeleton, VmdReader.Read(w.ToBytes()));
            solver.Solve(0);

            var ankle = solver.GlobalPosition[skeleton["左足首"]];
            var target = solver.GlobalPosition[skeleton["左足ＩＫ"]];
            var knee = solver.GlobalPosition[skeleton["左ひざ"]];
            var thigh = solver.GlobalPosition[skeleton["左足"]];
            Assert.That(Vector3.Distance(ankle, target), Is.LessThan(0.002f));
            // ひざは前（Unity の +Z）へ出る
            Assert.That(knee.z, Is.GreaterThan((thigh.z + ankle.z) * 0.5f + 0.02f));
        }

        [Test]
        public void Solver_WaistCancelKeepsLegs()
        {
            var w = new VmdWriter();
            w.Bone("腰", 0, Vector3.zero, Quaternion.Euler(0, 0, 20));
            w.Ik(0, ("左足ＩＫ", false), ("右足ＩＫ", false));
            var skeleton = new MmdSkeleton();
            var solver = new MmdPoseSolver(skeleton, VmdReader.Read(w.ToBytes()));
            solver.Solve(0);
            // 腰を回しても太ももの向きは変わらない（位置は付け根ごと動く）
            var thigh = solver.GlobalRotation[skeleton["左足"]];
            Assert.That(Quaternion.Angle(thigh, Quaternion.identity), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(solver.GlobalRotation[skeleton["上半身"]], Quaternion.identity), Is.GreaterThan(19f));
        }

        [Test]
        public void Solver_OldStyleThumbsShiftToRoot()
        {
            // 親指０ の無いモーションでは、親指１ の曲げが付け根（親指０）の関節で起きる
            var w = new VmdWriter();
            w.Bone("左親指１", 0, Vector3.zero, Quaternion.Euler(0, 40, 0));
            var skeleton = new MmdSkeleton();
            var solver = new MmdPoseSolver(skeleton, VmdReader.Read(w.ToBytes()));
            solver.Solve(0);
            Assert.That(solver.OldStyleThumbs, Is.True);
            Assert.That(Quaternion.Angle(solver.LocalRotation[skeleton["左親指０"]], Quaternion.identity), Is.GreaterThan(39f));
            Assert.That(Quaternion.Angle(solver.LocalRotation[skeleton["左親指１"]], Quaternion.identity), Is.LessThan(0.01f));
        }

        [Test]
        public void Solver_NewStyleThumbsStayAsIs()
        {
            var w = new VmdWriter();
            w.Bone("左親指０", 0, Vector3.zero, Quaternion.identity);
            w.Bone("左親指１", 0, Vector3.zero, Quaternion.Euler(0, 40, 0));
            var skeleton = new MmdSkeleton();
            var solver = new MmdPoseSolver(skeleton, VmdReader.Read(w.ToBytes()));
            solver.Solve(0);
            Assert.That(solver.OldStyleThumbs, Is.False);
            Assert.That(Quaternion.Angle(solver.LocalRotation[skeleton["左親指１"]], Quaternion.identity), Is.GreaterThan(39f));
        }

        // ---- Humanoid への焼き込み ----

        [Test]
        public void Baker_MakesHumanoidClipWithMorphs()
        {
            var w = new VmdWriter();
            w.Bone("左腕", 0, Vector3.zero, Quaternion.identity);
            w.Bone("左腕", 30, Vector3.zero, Quaternion.Euler(0, 0, 30));
            w.Morph("あ", 0, 0f);
            w.Morph("あ", 15, 1f);
            var clip = VmdHumanoidBaker.Bake(VmdReader.Read(w.ToBytes()), new VmdBakeSettings(), out var report);

            Assert.That(clip.humanMotion, Is.True);
            Assert.That(clip.length, Is.EqualTo(1f).Within(1e-3f));
            var morph = AnimationUtility.GetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.あ"));
            Assert.That(morph, Is.Not.Null);
            Assert.That(morph.Evaluate(0.5f), Is.EqualTo(100f).Within(1e-3f));
            Assert.That(report.Morphs, Does.Contain("あ"));
            Object.DestroyImmediate(clip);
        }

        [Test]
        public void Baker_FingerMuscleNamesMatchAnimationProperties()
        {
            Assert.That(VmdHumanoidBaker.MusclePropertyName("Left Thumb 1 Stretched"), Is.EqualTo("LeftHand.Thumb.1 Stretched"));
            Assert.That(VmdHumanoidBaker.MusclePropertyName("Right Little Spread"), Is.EqualTo("RightHand.Little.Spread"));
            Assert.That(VmdHumanoidBaker.MusclePropertyName("Spine Front-Back"), Is.EqualTo("Spine Front-Back"));
        }

        /// <summary>
        /// 焼いたクリップを同じ寸法の人形に流して、手首と足首が MMD の計算どおりの位置に来るかを見る。
        /// Humanoid の筋肉の値は関節の可動域で丸められるので、ぴったりにはならない。ずれの大きさをログに出す。
        /// </summary>
        [Test]
        public void Baker_WavefileRoundTripKeepsHandsAndFeet()
        {
            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out _);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var solver = new MmdPoseSolver(skeleton, motion);

            using (var rig = new MmdHumanoidRig(skeleton, "RoundTrip"))
            {
                var handler = new HumanPoseHandler(rig.Avatar, rig.Root.transform);
                var pose = new HumanPose();
                var bindings = AnimationUtility.GetCurveBindings(clip);
                var curves = new Dictionary<string, AnimationCurve>();
                foreach (var b in bindings)
                    if (b.type == typeof(Animator)) curves[b.propertyName] = AnimationUtility.GetEditorCurve(clip, b);

                var pairs = new[] { ("LeftHand", "左手首"), ("RightHand", "右手首"), ("LeftFoot", "左足首"), ("RightFoot", "右足首"), ("Head", "頭") };
                var worst = new Dictionary<string, float>();
                var sb = new StringBuilder();
                for (int f = 0; f <= motion.LastFrame; f += 60)
                {
                    float t = f / settings.FrameRate;
                    pose.bodyPosition = new Vector3(curves["RootT.x"].Evaluate(t), curves["RootT.y"].Evaluate(t), curves["RootT.z"].Evaluate(t));
                    pose.bodyRotation = new Quaternion(curves["RootQ.x"].Evaluate(t), curves["RootQ.y"].Evaluate(t), curves["RootQ.z"].Evaluate(t), curves["RootQ.w"].Evaluate(t)).normalized;
                    pose.muscles = new float[HumanTrait.MuscleCount];
                    for (int m = 0; m < pose.muscles.Length; m++)
                        pose.muscles[m] = curves[VmdHumanoidBaker.MusclePropertyName(HumanTrait.MuscleName[m])].Evaluate(t);
                    handler.SetHumanPose(ref pose);

                    solver.Solve(f);
                    foreach (var (human, mmd) in pairs)
                    {
                        float d = Vector3.Distance(rig[human].position, solver.GlobalPosition[skeleton[mmd]]);
                        worst[human] = Mathf.Max(worst.TryGetValue(human, out float w) ? w : 0f, d);
                    }
                }
                handler.Dispose();

                foreach (var pair in worst) sb.Append($"{pair.Key}: 最大 {pair.Value * 100f:F1}cm  ");
                Debug.Log("[VmdTests] WAVEFILE の往復のずれ " + sb);
                foreach (var pair in worst)
                    Assert.That(pair.Value, Is.LessThan(0.06f), pair.Key + " が 6cm 以上ずれた。" + sb);
            }
            Object.DestroyImmediate(clip);
        }

        /// <summary>テスト用に VMD を組み立てる。</summary>
        sealed class VmdWriter
        {
            public bool WriteOptionalSections = true;
            readonly List<(string name, int frame, Vector3 pos, Quaternion rot)> _bones = new List<(string, int, Vector3, Quaternion)>();
            readonly List<(string name, int frame, float weight)> _morphs = new List<(string, int, float)>();
            readonly List<(int frame, (string name, bool on)[] items)> _iks = new List<(int, (string, bool)[])>();

            public void Bone(string name, int frame, Vector3 pos, Quaternion rot) => _bones.Add((name, frame, pos, rot));
            public void Morph(string name, int frame, float weight) => _morphs.Add((name, frame, weight));
            public void Ik(int frame, params (string name, bool on)[] items) => _iks.Add((frame, items));

            public byte[] ToBytes()
            {
                var sjis = VmdReader.ShiftJis;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    Fixed(w, Encoding.ASCII.GetBytes("Vocaloid Motion Data 0002"), 30);
                    Fixed(w, sjis.GetBytes("テスト"), 20);
                    w.Write((uint)_bones.Count);
                    foreach (var b in _bones)
                    {
                        Fixed(w, sjis.GetBytes(b.name), 15);
                        w.Write((uint)b.frame);
                        w.Write(b.pos.x); w.Write(b.pos.y); w.Write(b.pos.z);
                        w.Write(b.rot.x); w.Write(b.rot.y); w.Write(b.rot.z); w.Write(b.rot.w);
                        var ip = new byte[64];
                        for (int c = 0; c < 4; c++) { ip[c] = 20; ip[c + 4] = 20; ip[c + 8] = 107; ip[c + 12] = 107; }
                        w.Write(ip);
                    }
                    if (!WriteOptionalSections) return ms.ToArray();
                    w.Write((uint)_morphs.Count);
                    foreach (var m in _morphs)
                    {
                        Fixed(w, sjis.GetBytes(m.name), 15);
                        w.Write((uint)m.frame);
                        w.Write(m.weight);
                    }
                    w.Write(0u); // カメラ
                    w.Write(0u); // 照明
                    w.Write(0u); // セルフ影
                    w.Write((uint)_iks.Count);
                    foreach (var k in _iks)
                    {
                        w.Write((uint)k.frame);
                        w.Write((byte)1);
                        w.Write((uint)k.items.Length);
                        foreach (var item in k.items)
                        {
                            Fixed(w, sjis.GetBytes(item.name), 20);
                            w.Write((byte)(item.on ? 1 : 0));
                        }
                    }
                    return ms.ToArray();
                }
            }

            static void Fixed(BinaryWriter w, byte[] bytes, int length)
            {
                var buf = new byte[length];
                System.Array.Copy(bytes, buf, Mathf.Min(bytes.Length, length));
                w.Write(buf);
            }
        }
    }
}
