using UnityEditor.AssetImporters;
using UnityEngine;

namespace MmdWorld.Vmd
{
    /// <summary>
    /// .vmd を置くと Humanoid の AnimationClip として取り込む。設定は Inspector で変えて Apply すると焼き直す。
    /// Unity は取り込み処理の版番号（Version）が変わらないと取り込み直さないので、焼き方（VmdHumanoidBaker・MmdPoseSolver など）を変えたら Version を上げる。
    /// </summary>
    [ScriptedImporter(Version, "vmd")]
    public sealed class VmdImporter : ScriptedImporter
    {
        /// <summary>2: 足の IK の目標を焼く・親指０ の無い形に合わせる　3: その場で踊るクリップと軌跡を足す　4: その場で踊るクリップをループにする（cycleOffset を効かせる）</summary>
        public const int Version = 4;

        [Tooltip("モーションを作ったモデルの腕が、初期姿勢で水平から何度下がっているか")]
        public float armAngle = 35f;
        [Tooltip("MMD の 1 単位を何メートルにするか")]
        public float scale = 0.08f;
        public bool importMorphs = true;
        [Tooltip("表情の BlendShape を持つメッシュの、アバターのルートからのパス")]
        public string faceMeshPath = "Body";

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var motion = VmdReader.Read(ctx.assetPath);
            var clip = VmdHumanoidBaker.Bake(motion, new VmdBakeSettings
            {
                ArmAngle = armAngle,
                Scale = scale,
                ImportMorphs = importMorphs,
                FaceMeshPath = faceMeshPath,
            }, out var report);
            clip.name = System.IO.Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset("clip", clip);
            ctx.SetMainObject(clip);
            // ステーション用: 体の床の上の移動と左右の向きを抜いたクリップと、その軌跡（ステーションを動かすのに使う）
            var (inPlace, trajectory) = VmdInPlace.Split(clip, report.HumanScale, report.EyeHeight);
            inPlace.name = clip.name + "（その場）";
            trajectory.name = clip.name + " の軌跡";
            ctx.AddObjectToAsset("inplace", inPlace);
            ctx.AddObjectToAsset("trajectory", trajectory);

            // 何が使われ、何が捨てられたかをサブアセットとして残す（Project で .vmd を開くと見える）
            var reportAsset = new TextAsset(report.ToString()) { name = clip.name + " の取り込み結果" };
            ctx.AddObjectToAsset("report", reportAsset);
        }
    }
}
