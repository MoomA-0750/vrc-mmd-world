using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MmdWorld.Vmd
{
    /// <summary>
    /// VMD の 1 フレームを MmdSkeleton に当てて、各ボーンのワールドの回転と位置（Unity 座標）を求める。
    /// 順に: キーの補間 → 回転付与 → 親から子へ積む → 足IK → つま先IK。
    /// IK は MMD の CCD をそのまま真似ず、同じ結果になりやすい解析解で解く（ひざは X 軸まわりだけ曲げる、太ももは FK の向きから最小の回転で振る）。
    /// </summary>
    public sealed class MmdPoseSolver
    {
        public readonly MmdSkeleton Skeleton;
        readonly VmdMotion _motion;
        readonly Track[] _tracks;

        public readonly Quaternion[] LocalRotation;
        public readonly Quaternion[] GlobalRotation;
        public readonly Vector3[] GlobalPosition;
        readonly Vector3[] _localTranslation;

        readonly LegIk[] _legs;

        sealed class LegIk
        {
            public string IkName, ToeIkName;
            public int Thigh, Knee, Ankle, Toe, Target, ToeTarget;
        }

        public MmdPoseSolver(MmdSkeleton skeleton, VmdMotion motion)
        {
            Skeleton = skeleton;
            _motion = motion;
            int n = skeleton.Bones.Count;
            LocalRotation = new Quaternion[n];
            GlobalRotation = new Quaternion[n];
            GlobalPosition = new Vector3[n];
            _localTranslation = new Vector3[n];

            _tracks = new Track[n];
            foreach (var pair in motion.Bones)
            {
                int i = skeleton[pair.Key];
                if (i >= 0) _tracks[i] = new Track(pair.Value, skeleton.Scale);
            }

            // 親指０ の無い古い標準ボーンでは、親指１ が付け根の関節、親指２ が真ん中の関節にあたる。
            // そのまま当てると1関節ずつ先へずれるので、親指０ にキーが無ければ1つずつ根元側へ寄せる
            foreach (var side in new[] { "左", "右" })
            {
                int t0 = skeleton[side + "親指０"], t1 = skeleton[side + "親指１"], t2 = skeleton[side + "親指２"];
                if (_tracks[t0] != null || (_tracks[t1] == null && _tracks[t2] == null)) continue;
                _tracks[t0] = _tracks[t1];
                _tracks[t1] = _tracks[t2];
                _tracks[t2] = null;
                OldStyleThumbs = true;
            }

            _legs = new[] { "左", "右" }.Select(side => new LegIk
            {
                IkName = MmdSkeleton.NormalizeName(side + "足ＩＫ"),
                ToeIkName = MmdSkeleton.NormalizeName(side + "つま先ＩＫ"),
                Thigh = skeleton[side + "足"],
                Knee = skeleton[side + "ひざ"],
                Ankle = skeleton[side + "足首"],
                Toe = skeleton[side + "つま先"],
                Target = skeleton[side + "足ＩＫ"],
                ToeTarget = skeleton[side + "つま先ＩＫ"],
            }).ToArray();
        }

        /// <summary>親指０ の無い古い形のモーションとして、親指のキーを根元側へ寄せたか。</summary>
        public bool OldStyleThumbs { get; private set; }

        /// <summary>VMD にキーがあった、骨格が知っているボーンの名前。</summary>
        public IEnumerable<string> MatchedBones
        {
            get
            {
                for (int i = 0; i < _tracks.Length; i++)
                    if (_tracks[i] != null) yield return Skeleton.Bones[i].Name;
            }
        }

        public void Solve(float frame)
        {
            var bones = Skeleton.Bones;
            for (int i = 0; i < bones.Count; i++)
            {
                if (_tracks[i] != null) _tracks[i].Sample(frame, out _localTranslation[i], out LocalRotation[i]);
                else { _localTranslation[i] = Vector3.zero; LocalRotation[i] = Quaternion.identity; }
            }

            // 回転付与は付与親の VMD の回転（ローカル）を使う。付与親が先に並んでいるので順番どおりでよい
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                if (b.AppendParent < 0) continue;
                var q = LocalRotation[b.AppendParent];
                if (b.AppendRatio < 0) q = Quaternion.Inverse(q);
                q = Quaternion.Slerp(Quaternion.identity, q, Mathf.Abs(b.AppendRatio));
                LocalRotation[i] = q * LocalRotation[i];
            }

            UpdateGlobals(0);

            foreach (var leg in _legs)
            {
                if (IkEnabled(leg.IkName, frame)) SolveLeg(leg);
                if (IkEnabled(leg.ToeIkName, frame)) SolveToe(leg);
            }
        }

        /// <summary>from 以降（並び順で後ろ）のボーンのワールドを積み直す。子は必ず親より後ろに並んでいる。</summary>
        void UpdateGlobals(int from)
        {
            var bones = Skeleton.Bones;
            for (int i = from; i < bones.Count; i++) UpdateGlobal(i);
        }

        void UpdateGlobal(int i)
        {
            var b = Skeleton.Bones[i];
            if (b.Parent < 0)
            {
                GlobalRotation[i] = LocalRotation[i];
                GlobalPosition[i] = b.RestPosition + _localTranslation[i];
                return;
            }
            var p = Skeleton.Bones[b.Parent];
            GlobalRotation[i] = GlobalRotation[b.Parent] * LocalRotation[i];
            GlobalPosition[i] = GlobalPosition[b.Parent] + GlobalRotation[b.Parent] * (b.RestPosition - p.RestPosition + _localTranslation[i]);
        }

        void UpdateSubtree(int root)
        {
            UpdateGlobal(root);
            var bones = Skeleton.Bones;
            for (int i = root + 1; i < bones.Count; i++)
                if (IsDescendant(i, root)) UpdateGlobal(i);
        }

        bool IsDescendant(int i, int ancestor)
        {
            for (int p = Skeleton.Bones[i].Parent; p >= 0; p = Skeleton.Bones[p].Parent)
                if (p == ancestor) return true;
            return false;
        }

        void SolveLeg(LegIk leg)
        {
            var bones = Skeleton.Bones;
            Vector3 r1 = bones[leg.Knee].RestPosition - bones[leg.Thigh].RestPosition;
            Vector3 r2 = bones[leg.Ankle].RestPosition - bones[leg.Knee].RestPosition;
            Vector3 thighPos = GlobalPosition[leg.Thigh];
            Vector3 target = GlobalPosition[leg.Target];

            float maxReach = (r1 + r2).magnitude;
            float want = Mathf.Min((target - thighPos).magnitude, maxReach * 0.9999f);

            // ひざを X 軸まわりに曲げて、付け根から足首までの距離を合わせる。曲げる向きは足首が後ろ（Unity の -Z）へ行く側
            float sign = (r1 + Quaternion.AngleAxis(30f, Vector3.right) * r2).z < (r1 + Quaternion.AngleAxis(-30f, Vector3.right) * r2).z ? 1f : -1f;
            float lo = 0f, hi = 180f;
            for (int i = 0; i < 40; i++)
            {
                float mid = (lo + hi) * 0.5f;
                float d = (r1 + Quaternion.AngleAxis(sign * mid, Vector3.right) * r2).magnitude;
                if (d > want) lo = mid; else hi = mid;
            }
            LocalRotation[leg.Knee] = Quaternion.AngleAxis(sign * (lo + hi) * 0.5f, Vector3.right);
            UpdateSubtree(leg.Knee);

            // 太ももを、今の足首の向きから目標の向きへ最小の回転で振る
            Vector3 current = GlobalPosition[leg.Ankle] - thighPos;
            Vector3 wanted = target - thighPos;
            if (current.sqrMagnitude > 1e-10f && wanted.sqrMagnitude > 1e-10f)
            {
                var swing = Quaternion.FromToRotation(current, wanted);
                var global = swing * GlobalRotation[leg.Thigh];
                var parent = GlobalRotation[bones[leg.Thigh].Parent];
                LocalRotation[leg.Thigh] = Quaternion.Inverse(parent) * global;
                UpdateSubtree(leg.Thigh);
            }
        }

        void SolveToe(LegIk leg)
        {
            var bones = Skeleton.Bones;
            Vector3 anklePos = GlobalPosition[leg.Ankle];
            Vector3 current = GlobalPosition[leg.Toe] - anklePos;
            Vector3 wanted = GlobalPosition[leg.ToeTarget] - anklePos;
            if (current.sqrMagnitude < 1e-10f || wanted.sqrMagnitude < 1e-10f) return;
            var global = Quaternion.FromToRotation(current, wanted) * GlobalRotation[leg.Ankle];
            LocalRotation[leg.Ankle] = Quaternion.Inverse(GlobalRotation[bones[leg.Ankle].Parent]) * global;
            UpdateSubtree(leg.Ankle);
        }

        bool IkEnabled(string normalizedName, float frame)
        {
            bool enabled = true;
            foreach (var key in _motion.IkKeys)
            {
                if (key.Frame > frame) break;
                foreach (var pair in key.Enabled)
                    if (MmdSkeleton.NormalizeName(pair.Key) == normalizedName) enabled = pair.Value;
            }
            return enabled;
        }

        /// <summary>1 ボーン分のキー列。位置は Unity 座標・メートルに直して持つ。</summary>
        sealed class Track
        {
            readonly VmdBoneKey[] _keys;
            readonly Vector3[] _pos;
            readonly Quaternion[] _rot;
            int _cursor;

            public Track(List<VmdBoneKey> keys, float scale)
            {
                _keys = keys.ToArray();
                _pos = new Vector3[_keys.Length];
                _rot = new Quaternion[_keys.Length];
                for (int i = 0; i < _keys.Length; i++)
                {
                    var p = _keys[i].Position;
                    _pos[i] = new Vector3(-p.x, p.y, -p.z) * scale;
                    _rot[i] = MmdSkeleton.ToUnityRotation(_keys[i].Rotation);
                }
            }

            public void Sample(float frame, out Vector3 position, out Quaternion rotation)
            {
                if (frame <= _keys[0].Frame || _keys.Length == 1)
                {
                    position = _pos[0]; rotation = _rot[0]; return;
                }
                int last = _keys.Length - 1;
                if (frame >= _keys[last].Frame)
                {
                    position = _pos[last]; rotation = _rot[last]; return;
                }

                // たいていは前のフレームの続きなので、前回の位置から探す
                if (_cursor >= last || _keys[_cursor].Frame > frame) _cursor = 0;
                while (_keys[_cursor + 1].Frame <= frame) _cursor++;

                int a = _cursor, b = _cursor + 1;
                float x = (frame - _keys[a].Frame) / (_keys[b].Frame - _keys[a].Frame);
                var kb = _keys[b];
                position = new Vector3(
                    Mathf.LerpUnclamped(_pos[a].x, _pos[b].x, kb.InterpX.Evaluate(x)),
                    Mathf.LerpUnclamped(_pos[a].y, _pos[b].y, kb.InterpY.Evaluate(x)),
                    Mathf.LerpUnclamped(_pos[a].z, _pos[b].z, kb.InterpZ.Evaluate(x)));
                rotation = Quaternion.Slerp(_rot[a], _rot[b], kb.InterpR.Evaluate(x));
            }
        }
    }
}
