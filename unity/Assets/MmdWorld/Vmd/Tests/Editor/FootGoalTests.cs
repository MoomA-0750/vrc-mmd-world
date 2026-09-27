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

        /// <summary>
        /// その場で踊るクリップをステーションと同じように「軌跡の位置と向きで動かした親」の下で流すと、元のクリップで流したときと
        /// 同じ場所・同じ向きに体が来るか。WAVEFILE の軌跡に体の移動と回る振りが入っているかも見る。
        /// </summary>
        [Test]
        public void InPlace_PlusTrajectoryMatchesOriginal()
        {
            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out var report);
            var (inPlace, trajectory) = VmdInPlace.Split(clip, report.HumanScale, report.EyeHeight);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);

            float minX = float.MaxValue, maxX = float.MinValue, minYaw = float.MaxValue, maxYaw = float.MinValue;
            for (int i = 0; i < trajectory.Count; i++)
            {
                minX = Mathf.Min(minX, trajectory.x[i] * trajectory.eyeHeight); maxX = Mathf.Max(maxX, trajectory.x[i] * trajectory.eyeHeight);
                minYaw = Mathf.Min(minYaw, trajectory.yaw[i]); maxYaw = Mathf.Max(maxYaw, trajectory.yaw[i]);
            }
            Debug.Log($"[VmdTests] 軌跡: 目の高さ {trajectory.eyeHeight:F2}m、左右 {minX * 100f:F0}〜{maxX * 100f:F0}cm、向き {minYaw:F0}〜{maxYaw:F0}°");
            Assert.That(maxX - minX, Is.GreaterThan(0.3f), "体の左右の移動が軌跡に入っていない");
            Assert.That(maxYaw - minYaw, Is.GreaterThan(180f), "回る振りが軌跡に入っていない");

            using (var a = new MmdHumanoidRig(skeleton, "Original"))
            using (var b = new MmdHumanoidRig(skeleton, "InPlace"))
            {
                var ra = a.Root.GetComponent<Animator>();
                var rb = b.Root.GetComponent<Animator>();
                if (ra == null) ra = a.Root.AddComponent<Animator>();
                if (rb == null) rb = b.Root.AddComponent<Animator>();
                ra.avatar = a.Avatar; rb.avatar = b.Avatar;
                ra.applyRootMotion = rb.applyRootMotion = false;
                float worst = 0f, worstAngle = 0f, worstFoot = 0f;
                using (var pa = new Player(ra, clip, true))
                using (var pb = new Player(rb, inPlace, true))
                {
                    for (int i = 0; i < trajectory.Count; i += 30)
                    {
                        float t = i / trajectory.sampleRate;
                        // ステーションと同じく、親（リグのルート）を軌跡の位置に置く（向きはクリップに残してある）
                        b.Root.transform.SetPositionAndRotation(
                            new Vector3(trajectory.x[i], 0f, trajectory.z[i]) * trajectory.eyeHeight,
                            Quaternion.identity);
                        pa.Evaluate(t);
                        pb.Evaluate(t);
                        var ha = ra.GetBoneTransform(HumanBodyBones.Hips);
                        var hb = rb.GetBoneTransform(HumanBodyBones.Hips);
                        worst = Mathf.Max(worst, Vector3.Distance(ha.position, hb.position));
                        worstAngle = Mathf.Max(worstAngle, Quaternion.Angle(ha.rotation, hb.rotation));
                        // 足も比べる（足の IK の目標が、その場にしたクリップで元の位置に取り残されていないか）
                        foreach (var bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                            worstFoot = Mathf.Max(worstFoot, Vector3.Distance(ra.GetBoneTransform(bone).position, rb.GetBoneTransform(bone).position));
                    }
                }
                Debug.Log($"[VmdTests] その場のクリップ + 軌跡 と元のクリップの腰のずれ: 最大 {worst * 100f:F1}cm {worstAngle:F1}°、足のずれ: 最大 {worstFoot * 100f:F1}cm");
                Assert.That(worstFoot, Is.LessThan(0.03f), "その場のクリップで足が取り残されている");
                Assert.That(worst, Is.LessThan(0.03f));
                Assert.That(worstAngle, Is.LessThan(3f));
            }
            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(inPlace);
            Object.DestroyImmediate(trajectory);
        }

        /// <summary>
        /// ステーション用の、区切りの時刻から始まる Controller（ステートの cycleOffset で開始位置をずらす）が、
        /// 座った瞬間にその時刻の姿勢から始まり、その後も時刻どおり進むか。
        /// </summary>
        [Test]
        public void SegmentController_StartsAtItsTime()
        {
            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out var report);
            var (inPlace, trajectory) = VmdInPlace.Split(clip, report.HumanScale, report.EyeHeight);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var sb = new StringBuilder("[VmdTests] 区切りの Controller:");
            float worst = 0f;
            using (var a = new MmdHumanoidRig(skeleton, "Reference"))
            using (var b = new MmdHumanoidRig(skeleton, "Segment"))
            {
                var ra = a.Root.GetComponent<Animator>(); if (ra == null) ra = a.Root.AddComponent<Animator>();
                var rb = b.Root.GetComponent<Animator>(); if (rb == null) rb = b.Root.AddComponent<Animator>();
                ra.avatar = a.Avatar; rb.avatar = b.Avatar;
                ra.applyRootMotion = rb.applyRootMotion = false;
                using (var pa = new Player(ra, inPlace, true))
                {
                    foreach (float start in new[] { 10f, 40f, 80f })
                    {
                        var controller = new UnityEditor.Animations.AnimatorController();
                        controller.AddLayer("Base");
                        var state = controller.layers[0].stateMachine.AddState("Dance");
                        state.motion = inPlace;
                        state.iKOnFeet = true;
                        state.cycleOffset = start / inPlace.length;
                        rb.runtimeAnimatorController = controller;
                        rb.Rebind();
                        rb.Update(0f);
                        foreach (float after in new[] { 0f, 1f, 3f })
                        {
                            if (after > 0f) rb.Update(after - (after == 3f ? 1f : 0f));
                            pa.Evaluate(start + after);
                            float d = 0f;
                            foreach (var bone in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.Head })
                                d = Mathf.Max(d, Vector3.Distance(ra.GetBoneTransform(bone).position, rb.GetBoneTransform(bone).position));
                            sb.Append($" {start}+{after}秒: {d * 100f:F1}cm");
                            worst = Mathf.Max(worst, d);
                        }
                        rb.runtimeAnimatorController = null;
                        Object.DestroyImmediate(controller);
                    }
                }
            }
            Debug.Log(sb.ToString());
            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(inPlace);
            Object.DestroyImmediate(trajectory);
            Assert.That(worst, Is.LessThan(0.02f), "区切りの Controller が、その時刻の姿勢から始まっていない");
        }

        /// <summary>
        /// 記録用: WAVEFILE の区切りの頭の前後で、両手が肩と同じ高さに広がる（T ポーズのように見える）振りがあるかを出す。
        /// VRChat でシーク直後に腕を横に広げた姿勢が写ったので、振り付けか座り直しの崩れかを見分けるのに使う。
        /// </summary>
        [Test]
        public void Record_ArmsAroundSegmentHeads()
        {
            var motion = VmdReader.Read(WavefilePath);
            var settings = new VmdBakeSettings();
            var clip = VmdHumanoidBaker.Bake(motion, settings, out _);
            var skeleton = new MmdSkeleton(settings.ArmAngle, settings.Scale);
            var sb = new StringBuilder("[VmdTests] 手の高さ − 肩の高さ（左/右, cm）と両手の間隔:");
            using (var rig = new MmdHumanoidRig(skeleton, "ArmRecord"))
            {
                var animator = rig.Root.GetComponent<Animator>(); if (animator == null) animator = rig.Root.AddComponent<Animator>();
                animator.avatar = rig.Avatar;
                animator.applyRootMotion = false;
                using (var player = new Player(animator, clip, true))
                {
                    foreach (float t in new[] { 29.5f, 30f, 30.3f, 30.6f, 31f, 39.5f, 40f, 40.3f, 40.6f })
                    {
                        player.Evaluate(t);
                        float sh = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).position.y;
                        var l = animator.GetBoneTransform(HumanBodyBones.LeftHand).position;
                        var r = animator.GetBoneTransform(HumanBodyBones.RightHand).position;
                        sb.Append($" {t}秒: {(l.y - sh) * 100f:F0}/{(r.y - sh) * 100f:F0} 間隔{Vector3.Distance(l, r) * 100f:F0}");
                    }
                }
            }
            Debug.Log(sb.ToString());
            Object.DestroyImmediate(clip);
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
