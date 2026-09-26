using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MmdWorld.EditorTools
{
    /// <summary>
    /// ワールド全体の設定。マネージャーのウィンドウで編集し、「ワールドを組み立てる」がこれを読む。
    /// 曲は1曲ずつ DanceSong に持つ（ここには入れない）。
    /// </summary>
    public sealed class MmdWorldSettings : ScriptableObject
    {
        public const string AssetPath = "Assets/MmdWorld/Settings.asset";

        [Tooltip("踊る人の枠の数")]
        [Range(1, 16)] public int slotCount = 4;

        [Tooltip("再生を押してから始まるまでの秒数")]
        [Range(0f, 10f)] public float countdownSeconds = 3f;

        [Tooltip("お手本として舞台の横で踊らせる Humanoid のモデル。空なら付属の人形")]
        public List<GameObject> previewDancers = new List<GameObject>();

        [Tooltip("着替え用の台に置くアバターの ID（avtr_...）。VRChat にアップロード済みで、公開（Public）のアバターだけが使える")]
        public List<string> pedestalAvatarIds = new List<string>();

        public static MmdWorldSettings LoadOrCreate()
        {
            var settings = AssetDatabase.LoadAssetAtPath<MmdWorldSettings>(AssetPath);
            if (settings != null) return settings;
            settings = CreateInstance<MmdWorldSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}
