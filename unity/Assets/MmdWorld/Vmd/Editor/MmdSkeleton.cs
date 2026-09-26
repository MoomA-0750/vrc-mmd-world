using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MmdWorld.Vmd
{
    /// <summary>
    /// VMD を当てるための「標準的な MMD モデル」の骨格。位置は MMD 単位・MMD 座標系で持ち、使うときに Unity 座標（メートル）へ直す。
    /// 実在のモデルの値ではなく、身長 1.6m 前後の標準ボーン配置を手で置いたもの。モーションは回転が主なので、多少違っても見た目への影響は小さい。
    /// 腕だけは、MMD モデルの多くが初期姿勢で腕を斜め下に下ろしている（A ポーズ）ので、その角度を合わせる必要がある（armAngle）。
    /// </summary>
    public sealed class MmdSkeleton
    {
        public sealed class Bone
        {
            public string Name;
            public int Index;
            public int Parent = -1;
            /// <summary>Unity 座標・メートル。初期姿勢での位置。回転は全ボーンとも初期姿勢で単位回転（MMD のボーンは向きを持たない）。</summary>
            public Vector3 RestPosition;
            /// <summary>回転付与（腰キャンセル・肩C）。付与親の回転を Ratio 倍して足す。</summary>
            public int AppendParent = -1;
            public float AppendRatio;
        }

        public readonly List<Bone> Bones = new List<Bone>();
        readonly Dictionary<string, int> _byName = new Dictionary<string, int>();

        public float ArmAngle { get; }
        public float Scale { get; }

        public int this[string name] => _byName.TryGetValue(NormalizeName(name), out int i) ? i : -1;

        /// <param name="armAngleDegrees">初期姿勢で腕が水平から何度下がっているか</param>
        /// <param name="scale">MMD の 1 単位を何メートルにするか（慣例は 0.08）</param>
        public MmdSkeleton(float armAngleDegrees = 35f, float scale = 0.08f)
        {
            ArmAngle = armAngleDegrees;
            Scale = scale;

            Add("全ての親", null, 0, 0, 0);
            Add("センター", "全ての親", 0, 8.0f, 0);
            Add("グルーブ", "センター", 0, 8.2f, 0);
            Add("腰", "グルーブ", 0, 11.0f, 0.3f);
            Add("上半身", "腰", 0, 11.8f, 0.2f);
            Add("上半身2", "上半身", 0, 13.0f, 0.2f);
            Add("首", "上半身2", 0, 16.0f, 0.3f);
            Add("頭", "首", 0, 16.9f, 0.2f);
            Add("下半身", "腰", 0, 11.8f, 0.2f);

            foreach (var side in new[] { "左", "右" })
            {
                float s = side == "左" ? 1f : -1f;

                // 腕。A ポーズの角度で斜め下へ伸ばす
                var armRoot = new Vector3(1.3f * s, 15.3f, 0.4f);
                float a = armAngleDegrees * Mathf.Deg2Rad;
                var u = new Vector3(Mathf.Cos(a) * s, -Mathf.Sin(a), 0f);
                var elbow = armRoot + u * 3.1f;
                var wrist = elbow + u * 2.9f;

                Add(side + "肩P", "上半身2", 0.3f * s, 15.6f, 0.3f);
                Add(side + "肩", side + "肩P", 0.3f * s, 15.6f, 0.3f);
                Add(side + "肩C", side + "肩", armRoot, appendParent: side + "肩P", appendRatio: -1f);
                Add(side + "腕", side + "肩C", armRoot);
                Add(side + "腕捩", side + "腕", (armRoot + elbow) * 0.5f);
                Add(side + "ひじ", side + "腕捩", elbow);
                Add(side + "手捩", side + "ひじ", (elbow + wrist) * 0.5f);
                Add(side + "手首", side + "手捩", wrist);

                // 親指は手の前側から前へ、ほかの指は腕の向きに沿って並べる（MMD では前が -Z）
                var thumbDir = (u * 0.6f + new Vector3(0, 0, -0.8f)).normalized;
                var thumb0 = wrist + u * 0.15f + new Vector3(0, -0.1f, -0.25f);
                Add(side + "親指０", side + "手首", thumb0);
                Add(side + "親指１", side + "親指０", thumb0 + thumbDir * 0.35f);
                Add(side + "親指２", side + "親指１", thumb0 + thumbDir * 0.65f);
                AddFinger(side, "人指", wrist, u, -0.25f);
                AddFinger(side, "中指", wrist, u, -0.08f);
                AddFinger(side, "薬指", wrist, u, 0.10f);
                AddFinger(side, "小指", wrist, u, 0.27f);

                // 脚。腰キャンセルは腰の回転を打ち消して、腰を振っても脚の付け根が回らないようにする
                Add("腰キャンセル" + side, "下半身", 0.9f * s, 10.4f, 0.2f, appendParent: "腰", appendRatio: -1f);
                Add(side + "足", "腰キャンセル" + side, 0.9f * s, 10.4f, 0.2f);
                Add(side + "ひざ", side + "足", 0.95f * s, 5.9f, -0.1f);
                Add(side + "足首", side + "ひざ", 1.0f * s, 1.2f, 0.3f);
                Add(side + "つま先", side + "足首", 1.0f * s, 0.1f, -1.2f);

                Add(side + "足IK親", "全ての親", 1.0f * s, 0f, 0.3f);
                Add(side + "足ＩＫ", side + "足IK親", 1.0f * s, 1.2f, 0.3f);
                Add(side + "つま先ＩＫ", side + "足ＩＫ", 1.0f * s, 0.1f, -1.2f);
            }
        }

        void AddFinger(string side, string finger, Vector3 wrist, Vector3 u, float z)
        {
            var root = wrist + u * 0.8f + new Vector3(0, 0, z);
            Add(side + finger + "１", side + "手首", root);
            Add(side + finger + "２", side + finger + "１", root + u * 0.35f);
            Add(side + finger + "３", side + finger + "２", root + u * 0.6f);
        }

        void Add(string name, string parent, float x, float y, float z, string appendParent = null, float appendRatio = 0f)
            => Add(name, parent, new Vector3(x, y, z), appendParent, appendRatio);

        void Add(string name, string parent, Vector3 mmdPosition, string appendParent = null, float appendRatio = 0f)
        {
            var bone = new Bone
            {
                Name = name,
                Index = Bones.Count,
                Parent = parent == null ? -1 : this[parent],
                RestPosition = ToUnityPosition(mmdPosition),
                AppendParent = appendParent == null ? -1 : this[appendParent],
                AppendRatio = appendRatio,
            };
            Bones.Add(bone);
            _byName[NormalizeName(name)] = bone.Index;
        }

        /// <summary>MMD（左手系・モデルは -Z 向き）から Unity（左手系・+Z 向き）へ。Y 軸まわりに 180 度回すだけ。</summary>
        public Vector3 ToUnityPosition(Vector3 mmd) => new Vector3(-mmd.x, mmd.y, -mmd.z) * Scale;

        public static Quaternion ToUnityRotation(Quaternion mmd) => new Quaternion(-mmd.x, mmd.y, -mmd.z, mmd.w);

        /// <summary>全角の英数字（ＩＫ、１２３）と半角を同じ名前として扱う。</summary>
        public static string NormalizeName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(c >= '！' && c <= '～' ? (char)(c - 0xFEE0) : c);
            return sb.ToString();
        }
    }
}
