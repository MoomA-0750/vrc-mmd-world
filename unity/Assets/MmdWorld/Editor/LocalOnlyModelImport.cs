using UnityEditor;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// Assets/LocalOnly/ に置いたモデル（手元のアバターの FBX など）を、Humanoid として取り込む。
    /// 変換したダンスが実在のアバターでどう見えるかを確かめるためのもの。LocalOnly はリポジトリに入れない。
    /// </summary>
    public sealed class LocalOnlyModelImport : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith("Assets/LocalOnly/")) return;
            var importer = (ModelImporter)assetImporter;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        }
    }
}
