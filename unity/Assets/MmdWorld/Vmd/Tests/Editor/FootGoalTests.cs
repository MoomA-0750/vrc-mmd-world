using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MmdWorld.Vmd.Tests
{
    /// <summary>
    /// 足の IK の目標を焼いたクリップを、Foot IK を入れて実際に Animator で流し、足の着き方を測る。
    /// 実在のアバター（Assets/LocalOnly/ に置いた、リポジトリに入れないもの）があればそれでも測る。
    /// </summary>
    public class FootGoalTests
    {
        const string WavefilePath = "Assets/MmdWorld/Songs/Wavefile/wavefile_v2.vmd";
        /// <summary>手元のアバター（Assets/LocalOnly/ に置いた、リポジトリに入れないもの）のうち、最初の Humanoid のモデル。</summary>
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

        sealed class Player : System.IDisposable
        {
            readonly PlayableGraph _graph;
            readonly AnimationClipPlayable _playable;

            public Player(Animator animator, AnimationClip clip, bool footIk)
            {
                _graph = PlayableGraph.Create("FootGoalTest");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(_graph, "out", animator);
                _playable = AnimationClipPlayable.Create(_graph, clip);
                _playable.SetApplyFootIK(footIk);
                output.SetSourcePlayable(_playable);
                _graph.Play();
            }

            public void Evaluate(float t)
            {
                _playable.SetTime(t);
                _graph.Evaluate(0f);
            }

            public void Dispose() => _graph.Destroy();
        }

        [Test]
        public void FootGoals_PlaceFeetWhereMmdDoesOnSameRig()
        {
            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out _);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var solver = new MmdPoseSolver(skeleton, motion);

            using (var rig = new MmdHumanoidRig(skeleton, "FootGoalRig"))
            {
                var animator = rig.Root.GetComponent<Animator>();
                if (animator == null) animator = rig.Root.AddComponent<Animator>();
                animator.avatar = rig.Avatar;
                animator.applyRootMotion = false;
                var errors = new Dictionary<bool, (float pos, float angle)>();
                foreach (bool ik in new[] { false, true })
                {
                    float worstPos = 0f, worstAngle = 0f;
                    using (var player = new Player(animator, clip, ik))
                    {
                        for (int f = 0; f <= motion.LastFrame; f += 15)
                        {
                            player.Evaluate(f / settings.FrameRate);
                            solver.Solve(f);
                            foreach (var (human, mmd, toe) in new[] { ("LeftFoot", "左足首", "左つま先"), ("RightFoot", "右足首", "右つま先") })
                            {
                                var foot = animator.GetBoneTransform(human == "LeftFoot" ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                                worstPos = Mathf.Max(worstPos, Vector3.Distance(foot.position, solver.GlobalPosition[skeleton[mmd]]));
                                // 足首から見たつま先の向きで、足の向きのずれを見る
                                var want = solver.GlobalPosition[skeleton[toe]] - solver.GlobalPosition[skeleton[mmd]];
                                var got = FindToes(foot).position - foot.position;
                                worstAngle = Mathf.Max(worstAngle, Vector3.Angle(want, got));
                            }
                        }
                    }
                    errors[ik] = (worstPos, worstAngle);
                }
                Debug.Log($"[VmdTests] 同じ体格の人形で足首のずれ: Foot IK なし 最大 {errors[false].pos * 100f:F1}cm {errors[false].angle:F1}° / あり 最大 {errors[true].pos * 100f:F1}cm {errors[true].angle:F1}°");
                Assert.That(errors[true].pos, Is.LessThan(0.03f), "Foot IK を入れると足首が MMD の位置から 3cm 以上ずれる");
                Assert.That(errors[true].angle, Is.LessThan(10f), "Foot IK を入れると足の向きが 10° 以上ずれる");
            }
            Object.DestroyImmediate(clip);
        }

        /// <summary>
        /// 目標の書き方を Unity の実際の挙動から逆算するための記録。目標を「足首の位置・向きそのまま」で書いて Foot IK で流し、
        /// 結果の足首の向き・位置が狙いとどれだけ違うかを、足首のローカルで表して出す。いくつかの姿勢で同じ値なら定数で補正できる。
        /// </summary>
        [Test]
        public void FootGoals_Calibration()
        {
            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out _);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var solver = new MmdPoseSolver(skeleton, motion);
            var sb = new StringBuilder("[VmdTests] 目標の逆算:");
            using (var rig = new MmdHumanoidRig(skeleton, "CalibRig"))
            {
                var animator = rig.Root.GetComponent<Animator>();
                if (animator == null) animator = rig.Root.AddComponent<Animator>();
                animator.avatar = rig.Avatar;
                animator.applyRootMotion = false;
                sb.Append($" humanScale={rig.HumanScale:F4}");
                using (var player = new Player(animator, clip, true))
                {
                    foreach (int f in new[] { 0, 600, 1200, 2400 })
                    {
                        player.Evaluate(f / settings.FrameRate);
                        solver.Solve(f);
                        foreach (var (bone, mmd, name) in new[] { (HumanBodyBones.LeftFoot, "左足首", "L"), (HumanBodyBones.RightFoot, "右足首", "R") })
                        {
                            var foot = animator.GetBoneTransform(bone);
                            var wantRot = solver.GlobalRotation[skeleton[mmd]];
                            var wantPos = solver.GlobalPosition[skeleton[mmd]];
                            var rotErr = Quaternion.Inverse(wantRot) * foot.rotation;
                            var posErr = Quaternion.Inverse(foot.rotation) * (wantPos - foot.position);
                            sb.Append($" | f{f}{name} 回転差(足首ローカル) {rotErr.eulerAngles.ToString("F1")} 位置差(結果の足首ローカル) {(posErr * 100f).ToString("F1")}cm");
                        }
                    }
                }
            }
            Debug.Log(sb.ToString());
            Object.DestroyImmediate(clip);
        }

        /// <summary>
        /// 同じクリップを、PlayableGraph で直接流した場合と、Animator Controller のステート（Foot IK あり・なし）で流した場合とで、
        /// 足首の高さ（床からの高さ）を比べる。Play モードでは Controller 経由で浮いて見えたので、その差を調べるためのもの。
        /// </summary>
        [Test]
        public void FootGoals_ControllerMatchesPlayable()
        {
            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out _);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var solver = new MmdPoseSolver(skeleton, motion);
            var sb = new StringBuilder("[VmdTests] 足首の高さ（MMD / Playable IKあり / Controller IKなし / Controller IKあり / 舞台の置き方で IKあり）:");
            var controllers = new List<UnityEditor.Animations.AnimatorController>();
            foreach (bool ik in new[] { false, true })
            {
                var c = new UnityEditor.Animations.AnimatorController();
                c.AddLayer("Base");
                var state = c.layers[0].stateMachine.AddState("Dance");
                state.motion = clip;
                state.iKOnFeet = ik;
                c.layers[0].stateMachine.defaultState = state;
                controllers.Add(c);
            }
            float worst = 0f;
            using (var rig = new MmdHumanoidRig(skeleton, "ControllerRig"))
            {
                var animator = rig.Root.GetComponent<Animator>();
                if (animator == null) animator = rig.Root.AddComponent<Animator>();
                animator.avatar = rig.Avatar;
                animator.applyRootMotion = false;
                var foot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var frames = new[] { 0, 300, 600, 900, 1200, 1800, 2400 };
                var byPlayable = new Dictionary<int, float>();
                using (var player = new Player(animator, clip, true))
                    foreach (int f in frames) { player.Evaluate(f / settings.FrameRate); byPlayable[f] = foot.position.y; }

                // 舞台と同じく、原点から離して 180° 回した置き方でも測る（k=2 は Foot IK あり）
                var byController = new Dictionary<int, float>[3];
                for (int k = 0; k < 3; k++)
                {
                    if (k == 2) rig.Root.transform.SetPositionAndRotation(new Vector3(3.6f, 0f, 4.5f), Quaternion.Euler(0f, 180f, 0f));
                    byController[k] = new Dictionary<int, float>();
                    animator.runtimeAnimatorController = controllers[Mathf.Min(k, 1)];
                    animator.Rebind();
                    animator.Update(0f);
                    int now = 0;
                    foreach (int f in frames)
                    {
                        while (now < f) { animator.Update(1f / settings.FrameRate); now++; }
                        byController[k][f] = foot.position.y;
                    }
                    animator.runtimeAnimatorController = null;
                }
                foreach (int f in frames)
                {
                    solver.Solve(f);
                    float mmd = solver.GlobalPosition[skeleton["左足首"]].y;
                    sb.Append($" f{f}: {mmd * 100f:F1} / {byPlayable[f] * 100f:F1} / {byController[0][f] * 100f:F1} / {byController[1][f] * 100f:F1} / {byController[2][f] * 100f:F1}cm");
                    worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(byController[1][f] - mmd), Mathf.Abs(byController[2][f] - mmd)));
                }
            }
            Debug.Log(sb.ToString());
            foreach (var c in controllers) Object.DestroyImmediate(c);
            Object.DestroyImmediate(clip);
            Assert.That(worst, Is.LessThan(0.03f), "Controller の Foot IK ありで、足首の高さが MMD から 3cm 以上ずれる");
        }

        static Transform FindToes(Transform foot)
        {
            foreach (Transform c in foot)
                if (c.name == "LeftToes" || c.name == "RightToes") return c;
            return foot.GetChild(0);
        }

        /// <summary>
        /// 実在のアバターで、MMD の足が止まっている間にアバターの足がどれだけ滑るか・床にめり込むかを、Foot IK のあり・なしで比べる。
        /// 表情のシェイプキーがどれだけ見つかるかも出す。アバターが置かれていなければ飛ばす。
        /// </summary>
        [Test]
        public void RealAvatar_FeetSlideLessWithFootIk()
        {
            var model = FindLocalAvatar();
            if (model == null) Assert.Ignore("Assets/LocalOnly/ に Humanoid のモデルが無いので飛ばす");

            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out var report);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var solver = new MmdPoseSolver(skeleton, motion);

            var go = (GameObject)Object.Instantiate(model);
            try
            {
                var animator = go.GetComponent<Animator>();
                Assert.That(animator.avatar != null && animator.avatar.isHuman, "Humanoid として取り込まれていない");
                animator.applyRootMotion = false;
                // 取り込んだモデルの既定は「映っていなければ動かさない」なので、画面の無いテストでは常に動かす
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var sb = new StringBuilder($"[VmdTests] {model.name}: ");

                foreach (bool ik in new[] { false, true })
                {
                    float slide = 0f, minHeight = float.MaxValue;
                    int planted = 0;
                    using (var player = new Player(animator, clip, ik))
                    {
                        var prev = new Vector3[2];
                        var prevMmd = new Vector3[2];
                        for (int f = 0; f <= motion.LastFrame; f++)
                        {
                            player.Evaluate(f / settings.FrameRate);
                            solver.Solve(f);
                            for (int side = 0; side < 2; side++)
                            {
                                var foot = animator.GetBoneTransform(side == 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot).position;
                                var mmd = solver.GlobalPosition[skeleton[side == 0 ? "左足首" : "右足首"]];
                                minHeight = Mathf.Min(minHeight, foot.y);
                                // MMD の足首が床の近くで止まっている（1フレーム 3mm 未満）フレームだけ、アバターの足の水平の動きを足す
                                if (f > 0 && mmd.y < 0.15f && Vector3.Distance(mmd, prevMmd[side]) < 0.003f)
                                {
                                    var d = foot - prev[side];
                                    slide += new Vector2(d.x, d.z).magnitude;
                                    planted++;
                                }
                                prev[side] = foot;
                                prevMmd[side] = mmd;
                            }
                        }
                    }
                    sb.Append($"Foot IK {(ik ? "あり" : "なし")}: 足が止まっているはずの間の滑り 平均 {slide / Mathf.Max(1, planted) * 1000f:F2}mm/フレーム（{planted} フレーム分）、足首の最低の高さ {minHeight * 100f:F1}cm。 ");
                }

                var smr = go.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.sharedMesh != null).ToArray();
                var shapes = new HashSet<string>(smr.SelectMany(r => Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(i => r.sharedMesh.GetBlendShapeName(i))));
                var found = report.Morphs.Where(shapes.Contains).ToList();
                var body = smr.FirstOrDefault(r => r.name == settings.FaceMeshPath);
                sb.Append($"顔のメッシュ {settings.FaceMeshPath}: {(body != null ? "ある" : "無い（" + string.Join(", ", smr.Select(r => r.name)) + "）")}。");
                sb.Append($"VMD の表情 {report.Morphs.Count} 個のうち、アバターにあるもの {found.Count} 個（{string.Join(", ", found)}）");
                Debug.Log(sb.ToString());
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(clip);
            }
        }
    }
}
