using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.Vmd
{
    /// <summary>
    /// 踊りの体の軌跡（床の上の位置と、左右の向き）。VRChat のステーションは座った人の体の位置と向きをステーションに固定するので、
    /// ステーションそのものをこの軌跡どおりに動かして、歩いたり回ったりする振りを出す。
    /// 位置は目の高さで割った値で持ち、使う側でアバターの目の高さを掛ける（体の大きさに合わせる）。
    /// </summary>
    public sealed class VmdTrajectory : ScriptableObject
    {
        /// <summary>1秒あたりのサンプル数。</summary>
        public float sampleRate = 15f;
        /// <summary>目の高さを 1 としたときの位置（クリップの原点から。+Z が前）。</summary>
        public float[] x;
        public float[] z;
        /// <summary>左右の向き（度、Y 軸まわり）。一回転を越えても続けて数える（補間で逆回りしないように）。</summary>
        public float[] yaw;
        /// <summary>このモーションを焼いた人形の目の高さ（メートル）。</summary>
        public float eyeHeight;

        public int Count => x != null ? x.Length : 0;
    }

    /// <summary>焼いたクリップから、その場で踊るクリップと軌跡を作る。</summary>
    public static class VmdInPlace
    {
        /// <param name="clip">VmdHumanoidBaker が焼いたクリップ（RootT/RootQ を持つ）</param>
        /// <param name="humanScale">焼いたリグの humanScale（RootT はこれで割った値）</param>
        /// <param name="eyeHeight">焼いたリグの目の高さ（メートル）</param>
        public static (AnimationClip inPlace, VmdTrajectory trajectory) Split(AnimationClip clip, float humanScale, float eyeHeight, float sampleRate = 15f)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var curves = new Dictionary<string, AnimationCurve>();
            foreach (var b in bindings)
                if (b.type == typeof(Animator)) curves[b.propertyName] = AnimationUtility.GetEditorCurve(clip, b);

            // 軌跡: 体の中心の床の上の位置と、体の左右の向き
            int count = Mathf.FloorToInt(clip.length * sampleRate) + 1;
            var trajectory = ScriptableObject.CreateInstance<VmdTrajectory>();
            trajectory.sampleRate = sampleRate;
            trajectory.eyeHeight = eyeHeight;
            trajectory.x = new float[count];
            trajectory.z = new float[count];
            trajectory.yaw = new float[count];
            float previousYaw = 0f;
            for (int i = 0; i < count; i++)
            {
                float t = Mathf.Min(i / sampleRate, clip.length);
                trajectory.x[i] = curves["RootT.x"].Evaluate(t) * humanScale / eyeHeight;
                trajectory.z[i] = curves["RootT.z"].Evaluate(t) * humanScale / eyeHeight;
                float yaw = Yaw(RootQ(curves, t));
                // 前のサンプルからの差を -180〜180 に収めて、続けて数える
                if (i > 0) yaw = previousYaw + Mathf.DeltaAngle(previousYaw, yaw);
                trajectory.yaw[i] = yaw;
                previousYaw = yaw;
            }

            // その場で踊るクリップ: 床の上の位置だけを 0 にする。体の向き（左右の回転）はクリップに残す。
            // 向きまでステーションで回すと、VR では視点（プレイエリア）もステーションごと回ってしまうため、体だけをアニメーションで回す
            var inPlace = Object.Instantiate(clip);
            var keysT = curves["RootT.x"].keys;
            var zeroX = new Keyframe[keysT.Length];
            var zeroZ = new Keyframe[keysT.Length];
            for (int i = 0; i < keysT.Length; i++)
            {
                zeroX[i] = new Keyframe(keysT[i].time, 0f);
                zeroZ[i] = new Keyframe(keysT[i].time, 0f);
            }
            Set(inPlace, "RootT.x", zeroX);
            Set(inPlace, "RootT.z", zeroZ);

            // ステーションでは、区切りの時刻から始める Controller をステートの cycleOffset で作る。
            // cycleOffset はループするクリップにしか効かないので、ループにする（曲の終わりで先頭へ戻るが、範囲の終わりでステーションから降ろす）
            var settings = AnimationUtility.GetAnimationClipSettings(inPlace);
            settings.loopTime = true;
            settings.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(inPlace, settings);
            return (inPlace, trajectory);
        }

        static Quaternion RootQ(Dictionary<string, AnimationCurve> curves, float t) =>
            new Quaternion(curves["RootQ.x"].Evaluate(t), curves["RootQ.y"].Evaluate(t), curves["RootQ.z"].Evaluate(t), curves["RootQ.w"].Evaluate(t)).normalized;

        /// <summary>体の前（+Z）が、上から見てどちらを向いているか（度）。</summary>
        public static float Yaw(Quaternion q)
        {
            var forward = q * Vector3.forward;
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        static void Set(AnimationClip clip, string property, Keyframe[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i].inTangent = i > 0 ? (keys[i].value - keys[i - 1].value) / Mathf.Max(1e-6f, keys[i].time - keys[i - 1].time) : 0f;
                keys[i].outTangent = i + 1 < keys.Length ? (keys[i + 1].value - keys[i].value) / Mathf.Max(1e-6f, keys[i + 1].time - keys[i].time) : 0f;
            }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), property), new AnimationCurve(keys));
        }
    }
}
