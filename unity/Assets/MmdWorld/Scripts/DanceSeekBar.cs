using UdonSharp;
using UnityEngine;

namespace MmdWorld
{
    /// <summary>
    /// 曲の長さを横幅にしたバー。範囲（開始点〜終了点）を帯で、区切りを目盛りで、現在の再生位置を縦線で示す。表示だけ（同期しない）。
    /// 子の位置はバーの左端を x = 0、右端を x = width とするローカル座標（メートル）。帯・縦線・目盛りは中心が位置になる立方体。
    /// DanceSystem が曲・範囲が変わったときに SetSong / SetRange を、毎フレーム SetTime を呼ぶ。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DanceSeekBar : UdonSharpBehaviour
    {
        public float width = 0.34f;
        [Tooltip("範囲を示す帯")]
        public Transform rangeFill;
        [Tooltip("現在の再生位置を示す縦線")]
        public Transform playhead;
        [Tooltip("区切りの目盛り（曲の区切りの数だけ使い、残りは隠す）")]
        public Transform[] ticks;

        float _length = 1f;

        /// <summary>曲の長さと区切りを入れる。times[start]〜times[start+count-1] がこの曲の区切り（秒）。</summary>
        public void SetSong(float length, float[] times, int start, int count)
        {
            _length = Mathf.Max(0.01f, length);
            for (int i = 0; i < ticks.Length; i++)
            {
                var tick = ticks[i];
                if (tick == null) continue;
                bool used = times != null && i < count && start + i < times.Length;
                // 0 秒の目盛りはバーの左端と重なるので出さない
                bool show = used && times[start + i] > 0.01f;
                if (tick.gameObject.activeSelf != show) tick.gameObject.SetActive(show);
                if (show) SetX(tick, times[start + i] / _length * width);
            }
        }

        /// <summary>範囲（秒）。帯をそこに伸ばす。</summary>
        public void SetRange(float from, float to)
        {
            if (rangeFill == null) return;
            float a = Mathf.Clamp01(from / _length) * width;
            float b = Mathf.Clamp01(to / _length) * width;
            var scale = rangeFill.localScale;
            rangeFill.localScale = new Vector3(Mathf.Max(0.001f, b - a), scale.y, scale.z);
            SetX(rangeFill, (a + b) * 0.5f);
        }

        /// <summary>現在の再生位置（秒）。</summary>
        public void SetTime(float t)
        {
            if (playhead != null) SetX(playhead, Mathf.Clamp01(t / _length) * width);
        }

        void SetX(Transform t, float x)
        {
            var p = t.localPosition;
            t.localPosition = new Vector3(x, p.y, p.z);
        }
    }
}
