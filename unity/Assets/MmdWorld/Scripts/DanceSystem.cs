using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRCStation = VRC.SDK3.Components.VRCStation;
using VRC.SDKBase;

namespace MmdWorld
{
    /// <summary>
    /// 曲の選択・再生・停止をみんなでそろえる。
    /// 同期するのは「どの曲か」「再生中か」「いつ始まるか（サーバー時刻）」だけで、音とお手本の人形と自分の踊りは各自の手元で時刻から合わせる。
    /// 踊る人は、始まる瞬間にその曲用のステーションへ座る。ステーションのアニメーションは Udon から差し替えられないので、枠 × 曲の数だけステーションを並べてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DanceSystem : UdonSharpBehaviour
    {
        [Header("曲（ワールドを組み立てるメニューが入れる）")]
        public string[] songTitles;
        public AudioClip[] songAudio;
        [Tooltip("モーションの長さ（秒）")]
        public float[] songLengths;
        [Tooltip("音をモーションより何秒遅らせるか")]
        public float[] audioOffsets;

        [Header("場所")]
        public DanceSlot[] slots;
        public AudioSource audioSource;
        [Tooltip("お手本の人形。ステート名は Idle と Song0, Song1, ...")]
        public Animator[] previewDancers;
        public Text titleText;
        public Text statusText;

        [Header("調整")]
        public float countdownSeconds = 3f;
        [Tooltip("始まってからこの秒数までなら、遅れて座っても踊りに入る")]
        public float lateSeatWindow = 0.5f;
        [Tooltip("音がこの秒数以上ずれたら合わせ直す")]
        public float audioResyncThreshold = 0.15f;
        [Tooltip("0 より大きいと、入ってからこの秒数後に自分で枠1に入って再生する。Build & Test の自動確認用で、普段は 0")]
        public float autoTestDelay = 0f;

        [UdonSynced] int _songIndex;
        [UdonSynced] bool _playing;
        [UdonSynced] double _startTime;

        VRCStation _localStation;
        bool _previewStarted;
        bool _seatAttempted;

        void Start()
        {
            ShowIdle();
            if (autoTestDelay > 0f) SendCustomEventDelayedSeconds(nameof(_AutoTest), autoTestDelay);
        }

        public void _AutoTest()
        {
            // 複数のクライアントで試すときは、最初に入った人だけが踊る（ほかの人は客席から見る）
            if (!Networking.IsMaster) return;
            if (slots.Length > 0 && slots[0] != null) slots[0].ClaimForLocal();
            Play();
        }

        // ---- ボタンから呼ぶ ----

        public void NextSong()
        {
            if (_playing || SongCount() == 0) return;
            TakeOwnership();
            _songIndex = (_songIndex + 1) % SongCount();
            Commit();
        }

        public void PrevSong()
        {
            if (_playing || SongCount() == 0) return;
            TakeOwnership();
            _songIndex = (_songIndex + SongCount() - 1) % SongCount();
            Commit();
        }

        public void Play()
        {
            if (SongCount() == 0) return;
            TakeOwnership();
            _playing = true;
            _startTime = Networking.GetServerTimeInSeconds() + countdownSeconds;
            Debug.Log("[MmdWorld] 再生: 曲 " + _songIndex + "、" + countdownSeconds + " 秒後に始まる");
            Commit();
        }

        public void Stop()
        {
            TakeOwnership();
            _playing = false;
            Commit();
        }

        /// <summary>自分が取っている枠を全部空ける（別の枠を取るときに DanceSlot から呼ぶ）。</summary>
        public void ReleaseLocalSlots()
        {
            foreach (var slot in slots)
                if (slot != null) slot.ReleaseIfLocal();
        }

        // ---- 同期 ----

        public override void OnDeserialization()
        {
            OnStateChanged();
        }

        void TakeOwnership()
        {
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }

        void Commit()
        {
            RequestSerialization();
            OnStateChanged();
        }

        void OnStateChanged()
        {
            _previewStarted = false;
            _seatAttempted = false;
            if (!_playing) StopLocal();
            ShowIdle();
        }

        // ---- 毎フレーム：時刻から音・お手本・自分の席を合わせる ----

        void Update()
        {
            if (!_playing) return;
            int song = _songIndex;
            if (song < 0 || song >= SongCount()) return;

            double t = Networking.GetServerTimeInSeconds() - _startTime;
            if (t < 0)
            {
                SetStatus("はじまるまで " + Mathf.CeilToInt((float)-t));
                return;
            }

            float length = songLengths[song];
            if (t > length + 1.0)
            {
                StopLocal();
                if (Networking.IsOwner(gameObject))
                {
                    _playing = false;
                    Commit();
                }
                return;
            }

            SyncAudio(song, (float)t);
            SyncPreview(song, (float)t, length);
            SeatLocalDancer(song, (float)t);
            SetStatus(FormatTime((float)t) + " / " + FormatTime(length));
        }

        void SyncAudio(int song, float t)
        {
            var clip = songAudio[song];
            if (audioSource == null || clip == null) return;
            float at = t - audioOffsets[song];
            if (at < 0f || at >= clip.length)
            {
                if (audioSource.isPlaying) audioSource.Stop();
                return;
            }
            if (!audioSource.isPlaying || audioSource.clip != clip)
            {
                audioSource.clip = clip;
                audioSource.time = at;
                audioSource.Play();
                Debug.Log("[MmdWorld] 音を " + at + " 秒の位置から鳴らす");
            }
            else if (Mathf.Abs(audioSource.time - at) > audioResyncThreshold)
            {
                audioSource.time = at;
            }
        }

        void SyncPreview(int song, float t, float length)
        {
            if (_previewStarted) return;
            _previewStarted = true;
            float normalized = length > 0f ? Mathf.Clamp01(t / length) : 0f;
            foreach (var dancer in previewDancers)
                if (dancer != null) dancer.Play("Song" + song, 0, normalized);
        }

        void SeatLocalDancer(int song, float t)
        {
            if (_seatAttempted) return;
            _seatAttempted = true;
            // 遅れて来た人は途中から踊れない（ステーションのアニメーションは座った瞬間から始まり、途中へ飛ばせない）
            if (t > lateSeatWindow) return;
            foreach (var slot in slots)
            {
                if (slot == null || !slot.IsLocalDancer()) continue;
                var station = slot.GetStation(song);
                if (station == null) return;
                _localStation = station;
                station.UseStation(Networking.LocalPlayer);
                Debug.Log("[MmdWorld] 自分を " + station.gameObject.name + "（" + slot.transform.parent.name + "）に座らせた");
                return;
            }
        }

        void StopLocal()
        {
            if (audioSource != null) audioSource.Stop();
            if (_localStation != null)
            {
                Debug.Log("[MmdWorld] ステーションから降りる");
                _localStation.ExitStation(Networking.LocalPlayer);
                _localStation = null;
            }
            foreach (var dancer in previewDancers)
                if (dancer != null) dancer.Play("Idle", 0, 0f);
        }

        // ---- 表示 ----

        void ShowIdle()
        {
            if (titleText != null)
                titleText.text = SongCount() == 0 ? "曲がありません" : (_songIndex + 1) + "/" + SongCount() + "  " + songTitles[_songIndex];
            SetStatus(_playing ? "" : "停止中");
        }

        void SetStatus(string s)
        {
            if (statusText != null) statusText.text = s;
        }

        int SongCount()
        {
            return songTitles == null ? 0 : songTitles.Length;
        }

        string FormatTime(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return (s / 60) + ":" + (s % 60).ToString("00");
        }
    }
}
