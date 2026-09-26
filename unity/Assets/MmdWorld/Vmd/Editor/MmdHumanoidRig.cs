using System;
using System.Collections.Generic;
using UnityEngine;

namespace MmdWorld.Vmd
{
    /// <summary>
    /// MmdSkeleton と同じ寸法で、T ポーズを初期姿勢にした Humanoid のリグ。
    /// MMD の骨格は腕を下ろした A ポーズが初期姿勢なので、腕から先は腕の付け根を中心に水平まで持ち上げて置く。
    /// MMD のワールド回転を、このリグのワールド回転へ写して（Apply）、HumanPoseHandler で筋肉の値に直す。
    /// </summary>
    public sealed class MmdHumanoidRig : IDisposable
    {
        /// <summary>Humanoid のボーン名（HumanTrait.BoneName）と、対応する MMD のボーン名。並びは親が先。</summary>
        public static readonly (string human, string mmd, string parentHuman)[] Map = BuildMap();

        static (string, string, string)[] BuildMap()
        {
            var map = new List<(string, string, string)>
            {
                ("Hips", "下半身", null),
                ("Spine", "上半身", "Hips"),
                ("Chest", "上半身2", "Spine"),
                ("Neck", "首", "Chest"),
                ("Head", "頭", "Neck"),
            };
            foreach (var (side, jp) in new[] { ("Left", "左"), ("Right", "右") })
            {
                map.Add((side + "Shoulder", jp + "肩", "Chest"));
                map.Add((side + "UpperArm", jp + "腕", side + "Shoulder"));
                map.Add((side + "LowerArm", jp + "ひじ", side + "UpperArm"));
                map.Add((side + "Hand", jp + "手首", side + "LowerArm"));
                map.Add((side + " Thumb Proximal", jp + "親指０", side + "Hand"));
                map.Add((side + " Thumb Intermediate", jp + "親指１", side + " Thumb Proximal"));
                map.Add((side + " Thumb Distal", jp + "親指２", side + " Thumb Intermediate"));
                foreach (var (finger, fjp) in new[] { ("Index", "人指"), ("Middle", "中指"), ("Ring", "薬指"), ("Little", "小指") })
                {
                    map.Add((side + " " + finger + " Proximal", jp + fjp + "１", side + "Hand"));
                    map.Add((side + " " + finger + " Intermediate", jp + fjp + "２", side + " " + finger + " Proximal"));
                    map.Add((side + " " + finger + " Distal", jp + fjp + "３", side + " " + finger + " Intermediate"));
                }
                map.Add((side + "UpperLeg", jp + "足", "Hips"));
                map.Add((side + "LowerLeg", jp + "ひざ", side + "UpperLeg"));
                map.Add((side + "Foot", jp + "足首", side + "LowerLeg"));
                map.Add((side + "Toes", jp + "つま先", side + "Foot"));
            }
            return map.ToArray();
        }

        public readonly GameObject Root;
        public readonly Avatar Avatar;
        readonly MmdSkeleton _skeleton;
        readonly Transform[] _nodes;
        readonly int[] _mmdIndex;
        /// <summary>リグの初期姿勢（T ポーズ）から MMD の初期姿勢（A ポーズ）への回転。腕から先だけ単位回転でない。</summary>
        readonly Quaternion[] _restCorrection;

        /// <param name="hideFlags">一時的に使うなら HideAndDontSave。マネキンの prefab に残すなら None</param>
        public MmdHumanoidRig(MmdSkeleton skeleton, string name = "MmdHumanoidRig", HideFlags hideFlags = HideFlags.HideAndDontSave)
        {
            _skeleton = skeleton;
            Root = new GameObject(name) { hideFlags = hideFlags };
            _nodes = new Transform[Map.Length];
            _mmdIndex = new int[Map.Length];
            _restCorrection = new Quaternion[Map.Length];

            // 腕を水平へ持ち上げる回転（左右別）
            var lift = new Dictionary<string, Quaternion>();
            foreach (var jp in new[] { "左", "右" })
            {
                Vector3 arm = skeleton.Bones[skeleton[jp + "腕"]].RestPosition;
                Vector3 elbow = skeleton.Bones[skeleton[jp + "ひじ"]].RestPosition;
                Vector3 down = elbow - arm;
                lift[jp] = Quaternion.FromToRotation(down, new Vector3(down.x, 0f, 0f));
            }

            var restWorld = new Vector3[Map.Length];
            for (int i = 0; i < Map.Length; i++)
            {
                var (human, mmd, parentHuman) = Map[i];
                int mi = skeleton[mmd];
                if (mi < 0) throw new InvalidOperationException("骨格に " + mmd + " がありません");
                _mmdIndex[i] = mi;

                Vector3 pos = skeleton.Bones[mi].RestPosition;
                _restCorrection[i] = Quaternion.identity;
                string armSide = ArmSide(human);
                if (armSide != null)
                {
                    Vector3 pivot = skeleton.Bones[skeleton[armSide + "腕"]].RestPosition;
                    pos = pivot + lift[armSide] * (pos - pivot);
                    _restCorrection[i] = Quaternion.Inverse(lift[armSide]);
                }
                restWorld[i] = pos;

                var go = new GameObject(human) { hideFlags = hideFlags };
                int parent = parentHuman == null ? -1 : Array.FindIndex(Map, m => m.human == parentHuman);
                go.transform.SetParent(parent < 0 ? Root.transform : _nodes[parent], false);
                go.transform.localPosition = pos - (parent < 0 ? Vector3.zero : restWorld[parent]);
                go.transform.localRotation = Quaternion.identity;
                _nodes[i] = go.transform;
            }

            Avatar = AvatarBuilder.BuildHumanAvatar(Root, BuildDescription());
            Avatar.name = name + "Avatar";
            Avatar.hideFlags = hideFlags;
            if (!Avatar.isValid || !Avatar.isHuman)
                throw new InvalidOperationException("Humanoid の Avatar を作れませんでした");
        }

        static string ArmSide(string human)
        {
            if (human.EndsWith("Shoulder")) return null;
            if (human.StartsWith("LeftUpperArm") || human.StartsWith("LeftLowerArm") || human.StartsWith("LeftHand") || human.StartsWith("Left ")) return "左";
            if (human.StartsWith("RightUpperArm") || human.StartsWith("RightLowerArm") || human.StartsWith("RightHand") || human.StartsWith("Right ")) return "右";
            return null;
        }

        HumanDescription BuildDescription()
        {
            var skeleton = new List<SkeletonBone>
            {
                new SkeletonBone { name = Root.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one },
            };
            var human = new List<HumanBone>();
            for (int i = 0; i < Map.Length; i++)
            {
                var t = _nodes[i];
                skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = Vector3.one });
                human.Add(new HumanBone { boneName = t.name, humanName = Map[i].human, limit = new HumanLimit { useDefaultValues = true } });
            }
            return new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };
        }

        /// <summary>Animator.humanScale と同じ値（体の大きさ。目標や RootT はこれで割った値で持つ）。</summary>
        public float HumanScale => ReadAnimator().humanScale;

        /// <summary>足首から足裏までの高さ（左, 右）。足の目標は足首ではなく足裏の点で表す。</summary>
        public float FootBottomHeight(int side) => side == 0 ? ReadAnimator().leftFeetBottomHeight : ReadAnimator().rightFeetBottomHeight;

        /// <summary>
        /// 足の目標の回転は、足首のボーンの向きそのものではなく Unity の Humanoid が内部で使う軸で表す。その差（足首のローカルで右から掛ける回転）。
        /// T ポーズで単位回転のこのリグで、目標を足首の向きそのままにして Foot IK で流すと、結果がどの姿勢でも左右とも
        /// Euler(90, 270, 0) だけ回っていた（FootGoalTests.FootGoals_Calibration）。その逆を掛ける。
        /// </summary>
        public static readonly Quaternion FootGoalAxis = Quaternion.Inverse(Quaternion.Euler(90f, 270f, 0f));

        Animator _animator;

        Animator ReadAnimator()
        {
            if (_animator != null) return _animator;
            // GetComponent が返す「無い」は Unity の偽の null なので ?? では拾えない
            _animator = Root.GetComponent<Animator>();
            if (_animator == null) _animator = Root.AddComponent<Animator>();
            _animator.avatar = Avatar;
            _animator.Rebind();
            return _animator;
        }

        public Transform this[string human] => _nodes[Array.FindIndex(Map, m => m.human == human)];

        /// <summary>解いた MMD の姿勢をリグへ写す。回転はワールドで合わせ、位置は腰（Hips）だけ合わせる。</summary>
        public void Apply(MmdPoseSolver pose)
        {
            _nodes[0].position = Root.transform.TransformPoint(pose.GlobalPosition[_mmdIndex[0]]);
            for (int i = 0; i < _nodes.Length; i++)
                _nodes[i].rotation = Root.transform.rotation * pose.GlobalRotation[_mmdIndex[i]] * _restCorrection[i];
        }

        public void Dispose()
        {
            if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
            if (Avatar != null && Avatar.hideFlags == HideFlags.HideAndDontSave) UnityEngine.Object.DestroyImmediate(Avatar);
        }
    }
}
