using System;
using System.Collections.Generic;
using UnityEngine;

namespace MmdWorld.Vmd
{
    /// <summary>VMD から読んだモーション。座標と回転は MMD の座標系のまま（変換は MmdPoseSolver で行う）。</summary>
    public sealed class VmdMotion
    {
        public string ModelName = "";
        public readonly Dictionary<string, List<VmdBoneKey>> Bones = new Dictionary<string, List<VmdBoneKey>>();
        public readonly Dictionary<string, List<VmdMorphKey>> Morphs = new Dictionary<string, List<VmdMorphKey>>();
        public readonly List<VmdIkKey> IkKeys = new List<VmdIkKey>();
        public int CameraKeyCount;

        /// <summary>ボーンとモーフのキーのうち、いちばん後ろのフレーム。</summary>
        public int LastFrame
        {
            get
            {
                int last = 0;
                foreach (var keys in Bones.Values)
                    foreach (var k in keys) last = Math.Max(last, k.Frame);
                foreach (var keys in Morphs.Values)
                    foreach (var k in keys) last = Math.Max(last, k.Frame);
                return last;
            }
        }

        internal void SortKeys()
        {
            foreach (var keys in Bones.Values) keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            foreach (var keys in Morphs.Values) keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            IkKeys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        }
    }

    public struct VmdBoneKey
    {
        public int Frame;
        public Vector3 Position;
        public Quaternion Rotation;
        /// <summary>このキーへ向かう区間の補間曲線。X, Y, Z, 回転の順。</summary>
        public VmdBezier InterpX, InterpY, InterpZ, InterpR;
    }

    public struct VmdMorphKey
    {
        public int Frame;
        public float Weight;
    }

    public struct VmdIkKey
    {
        public int Frame;
        public Dictionary<string, bool> Enabled;
    }

    /// <summary>MMD の補間曲線。制御点は 0..127 を 0..1 にしたもの。始点 (0,0)、終点 (1,1)。</summary>
    public struct VmdBezier
    {
        public float X1, Y1, X2, Y2;

        public static readonly VmdBezier Linear = new VmdBezier { X1 = 20f / 127f, Y1 = 20f / 127f, X2 = 107f / 127f, Y2 = 107f / 127f };

        public bool IsLinear => Mathf.Approximately(X1, Y1) && Mathf.Approximately(X2, Y2);

        /// <summary>区間の中の経過割合 x (0..1) から、値の進み具合 y (0..1) を返す。</summary>
        public float Evaluate(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            if (IsLinear) return x;

            // x(t) は単調増加なので二分法で t を求める
            float lo = 0f, hi = 1f, t = x;
            for (int i = 0; i < 32; i++)
            {
                t = (lo + hi) * 0.5f;
                float bx = Cubic(t, X1, X2);
                if (Mathf.Abs(bx - x) < 1e-6f) break;
                if (bx < x) lo = t; else hi = t;
            }
            return Cubic(t, Y1, Y2);
        }

        static float Cubic(float t, float p1, float p2)
        {
            float s = 1f - t;
            return 3f * s * s * t * p1 + 3f * s * t * t * p2 + t * t * t;
        }
    }
}
