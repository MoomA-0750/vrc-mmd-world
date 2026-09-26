using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.Vmd
{
    public sealed class VmdBakeSettings
    {
        /// <summary>モーションを作ったモデルの腕が、初期姿勢で水平から何度下がっているか。</summary>
        public float ArmAngle = 35f;
        public float Scale = 0.08f;
        /// <summary>MMD は 30fps。</summary>
        public float FrameRate = 30f;
        public bool ImportMorphs = true;
        /// <summary>表情の BlendShape を持つメッシュの、Animator からの相対パス。MMD 対応アバターは慣例で Body。</summary>
        public string FaceMeshPath = "Body";
        /// <summary>
        /// 足の IK の目標（LeftFootT/Q・RightFootT/Q）も焼く。ステートの Foot IK を入れると、体格の違うアバターでも足が MMD の位置に着く。
        /// </summary>
        public bool BakeFootGoals = true;
    }

    public sealed class VmdBakeReport
    {
        public int Frames;
        public List<string> MatchedBones = new List<string>();
        public List<string> IgnoredBones = new List<string>();
        public List<string> Morphs = new List<string>();
        public bool OldStyleThumbs;

        public override string ToString() =>
            $"{Frames} フレーム。使ったボーン {MatchedBones.Count}（{string.Join(", ", MatchedBones)}）。" +
            $"使わなかったボーン {IgnoredBones.Count}（{string.Join(", ", IgnoredBones)}）。" +
            $"表情 {Morphs.Count}（{string.Join(", ", Morphs)}）。" +
            (OldStyleThumbs ? "親指０ が無いので、親指のキーを根元側へ1つずつ寄せた" : "");
    }

    /// <summary>VMD を Humanoid の AnimationClip（マッスルのカーブ）と、表情の BlendShape のカーブに焼く。</summary>
    public static class VmdHumanoidBaker
    {
        public static AnimationClip Bake(VmdMotion motion, VmdBakeSettings settings, out VmdBakeReport report)
        {
            report = new VmdBakeReport();
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var solver = new MmdPoseSolver(skeleton, motion);
            report.MatchedBones.AddRange(solver.MatchedBones);
            report.OldStyleThumbs = solver.OldStyleThumbs;
            foreach (var name in motion.Bones.Keys)
                if (skeleton[name] < 0) report.IgnoredBones.Add(name);

            var clip = new AnimationClip { frameRate = settings.FrameRate };
            var bindings = new List<EditorCurveBinding>();
            var curves = new List<AnimationCurve>();
            int lastFrame = motion.LastFrame;
            report.Frames = lastFrame + 1;

            using (var rig = new MmdHumanoidRig(skeleton))
            {
                var handler = new HumanPoseHandler(rig.Avatar, rig.Root.transform);
                var pose = new HumanPose();
                int muscleCount = HumanTrait.MuscleCount;
                var muscleKeys = new Keyframe[muscleCount][];
                for (int m = 0; m < muscleCount; m++) muscleKeys[m] = new Keyframe[lastFrame + 1];
                var rootKeys = new Keyframe[7][];
                for (int c = 0; c < 7; c++) rootKeys[c] = new Keyframe[lastFrame + 1];
                // 足の目標: 左右 × (位置 xyz + 回転 xyzw)
                var goalKeys = new Keyframe[14][];
                for (int c = 0; c < 14; c++) goalKeys[c] = new Keyframe[lastFrame + 1];
                float humanScale = rig.HumanScale;
                var feet = new[] { rig["LeftFoot"], rig["RightFoot"] };

                for (int f = 0; f <= lastFrame; f++)
                {
                    solver.Solve(f);
                    rig.Apply(solver);
                    handler.GetHumanPose(ref pose);
                    float t = f / settings.FrameRate;
                    for (int m = 0; m < muscleCount; m++) muscleKeys[m][f] = new Keyframe(t, pose.muscles[m]);
                    rootKeys[0][f] = new Keyframe(t, pose.bodyPosition.x);
                    rootKeys[1][f] = new Keyframe(t, pose.bodyPosition.y);
                    rootKeys[2][f] = new Keyframe(t, pose.bodyPosition.z);
                    // 四元数は符号が反転すると補間で一回転してしまうので、前のフレームと同じ側にそろえる
                    var q = pose.bodyRotation;
                    if (f > 0 && Dot(q, rootKeys, f - 1) < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    rootKeys[3][f] = new Keyframe(t, q.x);
                    rootKeys[4][f] = new Keyframe(t, q.y);
                    rootKeys[5][f] = new Keyframe(t, q.z);
                    rootKeys[6][f] = new Keyframe(t, q.w);

                    // 目標は、体の中心（RootT/RootQ）から見た足裏の点の位置と足の向きを、体の大きさ（humanScale）で割ったもの
                    var bodyWorld = rig.Root.transform.TransformPoint(pose.bodyPosition * humanScale);
                    var inv = Quaternion.Inverse(rig.Root.transform.rotation * pose.bodyRotation);
                    for (int side = 0; side < 2; side++)
                    {
                        var foot = feet[side];
                        var sole = foot.position + foot.rotation * new Vector3(0f, -rig.FootBottomHeight(side), 0f);
                        var gp = inv * (sole - bodyWorld) / humanScale;
                        var gq = inv * foot.rotation * MmdHumanoidRig.FootGoalAxis;
                        int o = side * 7;
                        if (f > 0 && gq.x * goalKeys[o + 3][f - 1].value + gq.y * goalKeys[o + 4][f - 1].value + gq.z * goalKeys[o + 5][f - 1].value + gq.w * goalKeys[o + 6][f - 1].value < 0)
                            gq = new Quaternion(-gq.x, -gq.y, -gq.z, -gq.w);
                        goalKeys[o + 0][f] = new Keyframe(t, gp.x);
                        goalKeys[o + 1][f] = new Keyframe(t, gp.y);
                        goalKeys[o + 2][f] = new Keyframe(t, gp.z);
                        goalKeys[o + 3][f] = new Keyframe(t, gq.x);
                        goalKeys[o + 4][f] = new Keyframe(t, gq.y);
                        goalKeys[o + 5][f] = new Keyframe(t, gq.z);
                        goalKeys[o + 6][f] = new Keyframe(t, gq.w);
                    }
                }
                handler.Dispose();

                string[] rootNames = { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
                for (int c = 0; c < 7; c++) AddCurve(bindings, curves, "", typeof(Animator), rootNames[c], rootKeys[c]);
                if (settings.BakeFootGoals)
                {
                    string[] goalNames = { "LeftFootT.x", "LeftFootT.y", "LeftFootT.z", "LeftFootQ.x", "LeftFootQ.y", "LeftFootQ.z", "LeftFootQ.w",
                        "RightFootT.x", "RightFootT.y", "RightFootT.z", "RightFootQ.x", "RightFootQ.y", "RightFootQ.z", "RightFootQ.w" };
                    for (int c = 0; c < 14; c++) AddCurve(bindings, curves, "", typeof(Animator), goalNames[c], goalKeys[c]);
                }
                for (int m = 0; m < muscleCount; m++)
                    AddCurve(bindings, curves, "", typeof(Animator), MusclePropertyName(HumanTrait.MuscleName[m]), muscleKeys[m]);
            }

            if (settings.ImportMorphs)
            {
                foreach (var pair in motion.Morphs)
                {
                    var keys = new List<Keyframe>();
                    foreach (var k in pair.Value) keys.Add(new Keyframe(k.Frame / settings.FrameRate, k.Weight * 100f));
                    AddCurve(bindings, curves, settings.FaceMeshPath, typeof(SkinnedMeshRenderer), "blendShape." + pair.Key, keys.ToArray());
                    report.Morphs.Add(pair.Key);
                }
            }

            // 1本ずつ SetEditorCurve すると遅いのでまとめて入れる
            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());

            // 位置と向きは姿勢に焼き込み、ステーションの位置を原点として踊らせる
            var clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
            clipSettings.loopTime = false;
            clipSettings.loopBlendOrientation = true;
            clipSettings.loopBlendPositionY = true;
            clipSettings.loopBlendPositionXZ = true;
            clipSettings.keepOriginalOrientation = true;
            clipSettings.keepOriginalPositionY = true;
            clipSettings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(clip, clipSettings);
            return clip;
        }

        static float Dot(Quaternion q, Keyframe[][] rootKeys, int f) =>
            q.x * rootKeys[3][f].value + q.y * rootKeys[4][f].value + q.z * rootKeys[5][f].value + q.w * rootKeys[6][f].value;

        static void AddCurve(List<EditorCurveBinding> bindings, List<AnimationCurve> curves, string path, System.Type type, string property, Keyframe[] keys)
        {
            // キーの間は直線でつなぐ（ボーンは全フレームにキーがあり、表情は MMD でも直線補間）。
            // SetKeyLeftTangentMode をキーごとに呼ぶと遅いので、傾きを自分で入れる
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i].inTangent = i > 0 ? Slope(keys[i - 1], keys[i]) : 0f;
                keys[i].outTangent = i + 1 < keys.Length ? Slope(keys[i], keys[i + 1]) : 0f;
            }
            bindings.Add(EditorCurveBinding.FloatCurve(path, type, property));
            curves.Add(new AnimationCurve(keys));
        }

        static float Slope(Keyframe a, Keyframe b) => b.time > a.time ? (b.value - a.value) / (b.time - a.time) : 0f;

        /// <summary>
        /// HumanTrait.MuscleName とアニメーションのプロパティ名は、指だけ書き方が違う。
        /// 例: "Left Thumb 1 Stretched" → "LeftHand.Thumb.1 Stretched"、"Left Index Spread" → "LeftHand.Index.Spread"
        /// </summary>
        public static string MusclePropertyName(string muscleName)
        {
            foreach (var side in new[] { "Left", "Right" })
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    string prefix = side + " " + finger + " ";
                    if (!muscleName.StartsWith(prefix)) continue;
                    string rest = muscleName.Substring(prefix.Length);
                    return side + "Hand." + finger + (rest == "Spread" ? ".Spread" : "." + rest);
                }
            return muscleName;
        }
    }
}
