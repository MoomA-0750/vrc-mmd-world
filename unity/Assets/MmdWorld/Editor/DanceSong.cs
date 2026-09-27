using System.Collections.Generic;
using UnityEngine;

namespace MmdWorld.EditorTools
{
    /// <summary>1 曲ぶんの設定。これを置いてから「MMD World/ワールドを組み立てる」を実行すると、ステーションやボタンに反映される。</summary>
    [CreateAssetMenu(menuName = "MMD World/曲", fileName = "NewDanceSong")]
    public sealed class DanceSong : ScriptableObject
    {
        public string title;
        [Tooltip(".vmd を取り込んだ AnimationClip")]
        public AnimationClip motion;
        public AudioClip audio;
        [Tooltip("音をモーションより何秒遅らせるか（音が早いときは +）")]
        public float audioOffset;
        [Tooltip("並び順（小さいほど先）")]
        public int order;
        [Tooltip("シーク・範囲再生の区切りの間隔（秒）。区切りの時刻から踊りを始め直すので、細かいほど Controller が増える")]
        public float seekStep = 10f;
        [Tooltip("区切りの時刻（秒）を直接並べる（フレーズの頭など）。空なら seekStep ごと。0 秒は必ず入る")]
        public List<float> seekPoints = new List<float>();

        public string DisplayTitle => string.IsNullOrEmpty(title) ? name : title;
    }
}
