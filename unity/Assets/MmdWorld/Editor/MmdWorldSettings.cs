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

        [Tooltip("ワールドに入れて、人と同じ枠で踊らせるアバター（Humanoid のモデルや VRChat のアバターの prefab）。どの枠で踊らせるかはタブレットの「選ぶ」で決める")]
        public List<GameObject> slotAvatars = new List<GameObject>();

        [Tooltip("VR で踊っているとき、頭も踊りに合わせるか（初期は入れる）。今は、入れると振り付けの頭の動きで視点も揺れる。切ると、頭（視点）はヘッドセットのまま、体だけ踊る")]
        public bool vrHeadFollowsDance = true;

        [Tooltip("組み立てのとき、まだ曲になっていない .vmd を autoAddFolder の下から探して、曲を自動で作るか。切っておけば、マネージャーで足した曲だけになる")]
        public bool autoAddSongs;

        [Tooltip("autoAddSongs で .vmd を探すフォルダ（この下だけを探す）")]
        public string autoAddFolder = "Assets/MmdWorld/Songs";

        [Tooltip("マネージャーで消した曲のモーション。autoAddSongs を入れていても、これは曲に戻さない")]
        public List<AnimationClip> ignoredMotions = new List<AnimationClip>();

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
