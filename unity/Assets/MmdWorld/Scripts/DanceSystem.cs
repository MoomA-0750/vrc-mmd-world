using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRCStation = VRC.SDK3.Components.VRCStation;
using VRCAvatarPedestal = VRC.SDK3.Components.VRCAvatarPedestal;
using VRC.SDKBase;

namespace MmdWorld
{
    /// <summary>
    /// 曲の選択・再生・停止・シーク・範囲再生をみんなでそろえる。
    /// 同期するのは「どの曲か」「再生中か」「曲の 0 秒がサーバー時刻のいつか」「範囲とループ」「シークの回数」だけで、
    /// 音・お手本・ワールドのアバター・自分の踊り・ステーションの動きは、各自の手元で時刻から合わせる。
    ///
    /// ステーションのアニメーションは座った瞬間に先頭から始まり、途中へ飛ばせない。そこで曲を区切り（既定 10 秒ごと）に分け、
    /// 区切りの時刻から始まる Controller を区切りごとに用意しておく。区切りをまたぐたびに、全員の手元で枠のステーションの Controller を
    /// その区切りのものにそろえ、まだ座っていない踊り手（シークした直後や、途中から枠に入った人）はそこで座る。
    /// シークは、目的の区切りの少し手前へ時刻を飛ばして踊り手をいったん降ろし、区切りをまたいだところで座り直させる。
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
        [Tooltip("手元のタブレットの曲名と状態（パネルと同じものを出す）")]
        public Text tabletTitleText;
        public Text tabletStatusText;
        public DanceTablet tablet;
        [Tooltip("再生位置・範囲・区切りを示すバー（パネルとタブレット）")]
        public DanceSeekBar[] seekBars;

        [Header("体の軌跡（ワールドを組み立てるメニューが入れる）")]
        [Tooltip("全曲ぶんをつなげた軌跡。位置は目の高さを 1 とした値、向きは度")]
        public float[] trajX;
        public float[] trajZ;
        public float[] trajYaw;
        [Tooltip("曲ごとの、軌跡の始まりの位置・数・1秒あたりのサンプル数")]
        public int[] trajStart;
        public int[] trajCount;
        public float[] trajRate;
        [Tooltip("回る振りをステーションごと回して出す（踊る人の視点も回る）。オフなら位置だけ動かす")]
        public bool rotateStations = true;

        [Header("区切り（ワールドを組み立てるメニューが入れる）")]
        [Tooltip("全曲ぶんをつなげた、区切りの時刻（秒）と、その時刻から始まるステーション用の Controller")]
        public float[] segmentTimes;
        public RuntimeAnimatorController[] segmentControllers;
        [Tooltip("曲ごとの、区切りの始まりの位置と数")]
        public int[] segmentStart;
        public int[] segmentCount;
        [Tooltip("シークしたとき、目的の区切りのこの秒数だけ手前から流す（Controller の差し替えが全員に届いてから座り直すため）")]
        public float seekPreroll = 0.5f;
        [Tooltip("プレビューの音量（自分の手元だけ）")]
        public float previewVolume = 0.4f;

        [Header("枠で踊らせるアバター（ワールドに入れたもの）")]
        [Tooltip("1体ずつ置いたアバター。枠に割り当てられたときだけ、その枠に出て踊る。ステート名は Idle と Song0, Song1, ...")]
        public Animator[] slotAvatars;
        public string[] slotAvatarNames;

        [Header("調整")]
        public float countdownSeconds = 3f;
        [Tooltip("区切りをまたいでからこの秒数までなら、座って踊りに入る")]
        public float lateSeatWindow = 0.5f;
        [Tooltip("音がこの秒数以上ずれたら合わせ直す")]
        public float audioResyncThreshold = 0.15f;
        [Tooltip("0 より大きいと、入ってからこの秒数後に自分で枠1に入って再生する。Build & Test の自動確認用で、普段は 0")]
        public float autoTestDelay = 0f;
        [Tooltip("自動確認で、踊る前に着替える台（空なら着替えない）。終わったら autoTestRestorePedestal のアバターに戻す。" +
            "台のアバターは実行中に差し替えると読み込みが間に合わず前のアバターのまま着替えてしまうので、ビルドのときに ID を入れておく")]
        public VRCAvatarPedestal autoTestPedestal;
        public VRCAvatarPedestal autoTestRestorePedestal;
        [Tooltip("自動確認で、見る側のクライアントが立つ場所（枠1の正面）")]
        public Transform autoTestViewPoint;
        [Tooltip("自動確認の流れ。0: 再生とシーク　1: プレビュー・範囲再生・ループ・途中からの参加　2: 手元のタブレット")]
        public int autoTestScenario = 0;

        bool _autoTestSwitched;

        [UdonSynced] int _songIndex;
        [UdonSynced] bool _playing;
        /// <summary>曲の 0 秒にあたるサーバー時刻。シークはこれをずらす</summary>
        [UdonSynced] double _startTime;
        /// <summary>範囲の開始・終了の区切りの番号。終了が -1 なら曲の終わりまで</summary>
        [UdonSynced] int _rangeStart;
        [UdonSynced] int _rangeEnd = -1;
        [UdonSynced] bool _loop;
        /// <summary>シークするたびに増やす。受け取った側は、これが変わったら踊り手を降ろして座り直させる</summary>
        [UdonSynced] int _seekSeq;

        VRCStation _localStation;
        /// <summary>シークを受けたあと、次に区切りをまたいだところで、もう一方のステーションへ乗り換える</summary>
        bool _switchPending;
        bool _previewStarted;
        int _currentSegment = -2;
        int _handledSeekSeq;
        bool _localPreview;
        float _localPreviewStart;
        float _audioVolume = 1f;

        void Start()
        {
            if (audioSource != null) _audioVolume = audioSource.volume;
            ShowIdle();
            if (autoTestDelay > 0f) SendCustomEventDelayedSeconds(nameof(_AutoTest), autoTestDelay);
        }

        public void _AutoTest()
        {
            _AutoTestLogPosition();
            // 複数のクライアントで試すときは、最初に入った人だけが踊る。ほかの人は枠1の正面から見る
            if (!Networking.IsMaster)
            {
                if (autoTestViewPoint != null)
                    Networking.LocalPlayer.TeleportTo(autoTestViewPoint.position, autoTestViewPoint.rotation);
                // 途中からの参加: ループの途中で枠2に入る
                if (autoTestScenario == 1) SendCustomEventDelayedSeconds(nameof(_AutoTestViewerJoin), 63f);
                return;
            }
            if (autoTestPedestal != null)
            {
                Debug.Log("[MmdWorld] 自動確認: " + autoTestPedestal.blueprintId + " に着替える");
                autoTestPedestal.SetAvatarUse(Networking.LocalPlayer);
                _autoTestSwitched = true;
                // アバターの読み込みを待ってから踊る
                SendCustomEventDelayedSeconds(nameof(_AutoTestDance), 25f);
                return;
            }
            _AutoTestDance();
        }

        /// <summary>自動確認の間、2秒ごとに自分の位置と、枠1の踊り手の位置をログに出す（踊りながら動けるかの検証）。</summary>
        public void _AutoTestLogPosition()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            string line = "[MmdWorld] 位置: 自分 " + local.GetPosition().ToString("F2") + (_localStation != null ? " 席" : " 立ち");
            var dancer = slots.Length > 0 && slots[0] != null ? slots[0].GetDancer() : null;
            if (Utilities.IsValid(dancer))
                line += " / 枠1 " + dancer.GetPosition().ToString("F2") + " 骨盤 " + dancer.GetBonePosition(HumanBodyBones.Hips).ToString("F2");
            if (slots.Length > 0 && slots[0] != null) line += " / 席 " + slots[0].GetStationRoot().position.ToString("F2");
            line += " / 曲 " + (_playing ? CurrentTime().ToString("F1") : "-");
            Debug.Log(line);
            SendCustomEventDelayedSeconds(nameof(_AutoTestLogPosition), 2f);
        }

        public void _AutoTestDance()
        {
            if (autoTestScenario != 2 && slots.Length > 0 && slots[0] != null) slots[0].ClaimForLocal();
            if (autoTestScenario == 2)
            {
                // タブレットから操作する: 出す → 踊る → 再生 → 20 秒後に区切り ≫ → 35 秒後に停止 → 閉じる
                Debug.Log("[MmdWorld] 自動確認: タブレットを出して操作する");
                if (tablet != null) tablet.Toggle();
                SendCustomEventDelayedSeconds(nameof(_AutoTestTabletPlay), 3f);
                return;
            }
            if (autoTestScenario == 1)
            {
                // プレビューを 10 秒 → 範囲 0:20〜0:40・ループで再生 → 2 回ループしたら止める
                Debug.Log("[MmdWorld] 自動確認: プレビューを始める");
                TogglePreview();
                SendCustomEventDelayedSeconds(nameof(_AutoTestRangePlay), 10f);
                SendCustomEventDelayedSeconds(nameof(_AutoTestStop), 10f + countdownSeconds + 60f + 5f);
                return;
            }
            Play();
            // シークも試す: 始まって 20 秒で1つ先へ、35 秒で1つ前へ
            SendCustomEventDelayedSeconds(nameof(_AutoTestSeekForward), 23f);
            SendCustomEventDelayedSeconds(nameof(_AutoTestSeekBack), 38f);
        }

        public void _AutoTestTabletPlay()
        {
            if (tablet == null) return;
            tablet.Press(12); // 踊る / やめる
            tablet.Press(1);  // 再生
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletSeek), 23f);
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletStop), 38f);
        }

        public void _AutoTestTabletSeek()
        {
            if (tablet != null) tablet.Press(5); // 区切り ≫
        }

        public void _AutoTestTabletStop()
        {
            if (tablet == null) return;
            tablet.Press(2);  // 停止
            // 範囲を狭めて、シークバーの帯と再生位置（止まっているときは開始点）が動くのを見る
            tablet.Press(9);  // 開始 ▶
            tablet.Press(9);
            tablet.Press(10); // 終了 ◀
            tablet.Press(10);
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletClose), 8f);
        }

        public void _AutoTestTabletClose()
        {
            if (tablet != null) tablet.Press(13); // 閉じる
        }

        public void _AutoTestRangePlay()
        {
            Debug.Log("[MmdWorld] 自動確認: プレビューを止め、範囲 区切り2〜4・ループで再生");
            TogglePreview();
            SetRange(2, 4);
            if (!_loop) ToggleLoop();
            Play();
        }

        public void _AutoTestStop()
        {
            Debug.Log("[MmdWorld] 自動確認: 止める");
            Stop();
        }

        public void _AutoTestViewerJoin()
        {
            Debug.Log("[MmdWorld] 自動確認: 見る側が枠2に入る（t=" + CurrentTime() + "）");
            if (slots.Length > 1 && slots[1] != null) slots[1].ClaimForLocal();
        }

        public void _AutoTestSeekForward()
        {
            Debug.Log("[MmdWorld] 自動確認: 1つ先の区切りへ");
            SeekForward();
        }

        public void _AutoTestSeekBack()
        {
            Debug.Log("[MmdWorld] 自動確認: 1つ前の区切りへ");
            SeekBack();
        }

        void RestoreAutoTestAvatar()
        {
            if (!_autoTestSwitched || autoTestRestorePedestal == null) return;
            _autoTestSwitched = false;
            Debug.Log("[MmdWorld] 自動確認: " + autoTestRestorePedestal.blueprintId + " に戻す");
            autoTestRestorePedestal.SetAvatarUse(Networking.LocalPlayer);
        }

        // ---- ボタンから呼ぶ ----

        public void NextSong()
        {
            if (_playing || SongCount() == 0) return;
            TakeOwnership();
            _songIndex = (_songIndex + 1) % SongCount();
            ResetRange();
            Commit();
        }

        public void PrevSong()
        {
            if (_playing || SongCount() == 0) return;
            TakeOwnership();
            _songIndex = (_songIndex + SongCount() - 1) % SongCount();
            ResetRange();
            Commit();
        }

        /// <summary>範囲の開始の区切りから、カウントダウンのあとに流す。</summary>
        public void Play()
        {
            if (SongCount() == 0) return;
            _localPreview = false;
            TakeOwnership();
            _playing = true;
            _startTime = Networking.GetServerTimeInSeconds() + countdownSeconds - SegmentTime(_songIndex, _rangeStart);
            _seekSeq++;
            Debug.Log("[MmdWorld] 再生: 曲 " + _songIndex + "、区切り " + _rangeStart + " から、" + countdownSeconds + " 秒後に始まる");
            Commit();
        }

        public void Stop()
        {
            TakeOwnership();
            _playing = false;
            Commit();
        }

        /// <summary>1つ先の区切りへ（再生中だけ）。</summary>
        public void SeekForward()
        {
            SeekBy(1);
        }

        /// <summary>1つ前の区切りへ。いまの区切りに入ってすぐ（2秒以内）でなければ、いまの区切りの頭へ戻る（プレーヤーの「前へ」と同じ）。</summary>
        public void SeekBack()
        {
            if (!_playing) return;
            float t = CurrentTime();
            int k = SegmentAt(_songIndex, t);
            SeekTo(t - SegmentTime(_songIndex, k) > 2f ? k : k - 1);
        }

        void SeekBy(int delta)
        {
            if (!_playing) return;
            SeekTo(SegmentAt(_songIndex, CurrentTime()) + delta);
        }

        void SeekTo(int segment)
        {
            int last = LastSegment(_songIndex);
            segment = Mathf.Clamp(segment, 0, last);
            TakeOwnership();
            // 目的の区切りの少し手前から流す。区切りをまたいだところで、Controller を差し替えたステーションに座り直す
            _startTime = Networking.GetServerTimeInSeconds() - (SegmentTime(_songIndex, segment) - seekPreroll);
            _seekSeq++;
            Debug.Log("[MmdWorld] シーク: 区切り " + segment + "（" + FormatTime(SegmentTime(_songIndex, segment)) + "）へ");
            Commit();
        }

        // 範囲: 開始・終了を区切り単位で前後させる。終了を最後の区切りより後ろにすると「曲の終わりまで」

        public void RangeStartForward()
        {
            SetRange(_rangeStart + 1, _rangeEnd);
        }

        public void RangeStartBack()
        {
            SetRange(_rangeStart - 1, _rangeEnd);
        }

        public void RangeEndForward()
        {
            SetRange(_rangeStart, _rangeEnd < 0 ? -1 : _rangeEnd + 1);
        }

        public void RangeEndBack()
        {
            SetRange(_rangeStart, _rangeEnd < 0 ? LastSegment(_songIndex) : _rangeEnd - 1);
        }

        public void ToggleLoop()
        {
            TakeOwnership();
            _loop = !_loop;
            Debug.Log("[MmdWorld] ループ: " + _loop);
            Commit();
        }

        void SetRange(int start, int end)
        {
            if (SongCount() == 0) return;
            int last = LastSegment(_songIndex);
            start = Mathf.Clamp(start, 0, last);
            if (end > last) end = -1;
            // 終了は開始より後ろ（同じ区切りなら、その区切りの終わりまで）
            if (end >= 0 && end <= start) end = start + 1 > last ? -1 : start + 1;
            TakeOwnership();
            _rangeStart = start;
            _rangeEnd = end;
            Debug.Log("[MmdWorld] 範囲: 区切り " + start + "〜" + (end < 0 ? "終わり" : end.ToString()));
            Commit();
        }

        void ResetRange()
        {
            _rangeStart = 0;
            _rangeEnd = -1;
        }

        /// <summary>自分の手元だけで、お手本に範囲の頭から踊らせ、音を小さく流す。もう一度押すと止める。再生中は使えない。</summary>
        public void TogglePreview()
        {
            if (_playing) return;
            _localPreview = !_localPreview;
            _localPreviewStart = Time.time;
            Debug.Log("[MmdWorld] プレビュー: " + _localPreview);
            _previewStarted = false;
            if (!_localPreview) StopLocal();
            ShowIdle();
        }

        /// <summary>
        /// タブレットの「踊る / やめる」。自分が枠に入っていれば抜けて（踊っていればステーションから降りる）、
        /// 入っていなければ空いている枠（人もワールドのアバターもいない枠）に入る。再生中なら次の区切りから踊りに入る。
        /// </summary>
        public void ToggleLocalJoin()
        {
            foreach (var slot in slots)
            {
                if (slot == null || !slot.IsLocalDancer()) continue;
                slot.ReleaseIfLocal();
                LeaveLocalStation();
                return;
            }
            foreach (var slot in slots)
            {
                if (slot == null || slot.GetDancer() != null || slot.GetAvatarIndex() >= 0) continue;
                slot.ClaimForLocal();
                return;
            }
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
            if (_playing) _localPreview = false;
            if (!_playing && !_localPreview) StopLocal();
            if (_playing && _handledSeekSeq != _seekSeq)
            {
                // 再生の頭出し・シーク: 次に区切りをまたいだところで、Controller を入れたもう一方のステーションへ降りずに乗り換える
                _handledSeekSeq = _seekSeq;
                _currentSegment = -2;
                if (_localStation != null) _switchPending = true;
            }
            ShowIdle();
        }

        // ---- 毎フレーム：時刻から音・お手本・自分の席を合わせる ----

        void Update()
        {
            if (!_playing)
            {
                if (_localPreview) UpdateLocalPreview();
                return;
            }
            int song = _songIndex;
            if (song < 0 || song >= SongCount()) return;

            // 踊っている途中で枠から抜けたら（台やタブレットで）、ステーションから降りる
            if (_localStation != null && !IsLocalInAnySlot()) LeaveLocalStation();

            float t = CurrentTime();
            float start = SegmentTime(song, _rangeStart);
            float end = RangeEndTime(song);

            // 区切りをまたいだら、全員の手元で枠のステーションの Controller をその区切りのものにする（まだ座っていない踊り手はここで座る）
            UpdateSegment(song, t);

            if (t < start - seekPreroll - 0.01f)
            {
                SetStatus("はじまるまで " + Mathf.CeilToInt(start - t));
                return;
            }

            if (t >= end)
            {
                if (Networking.IsOwner(gameObject))
                {
                    if (_loop) SeekTo(_rangeStart);
                    else
                    {
                        _playing = false;
                        Commit();
                    }
                }
                else if (t > end + 1f) StopLocal();
                return;
            }

            if (_audioVolume > 0f && audioSource != null) audioSource.volume = _audioVolume;
            SyncAudio(song, t);
            SyncPreview(song, t, songLengths[song]);
            MoveStations(song, t);
            SetSeekBarTime(t);
            SetStatus(FormatTime(t) + " / " + FormatTime(songLengths[song]) + RangeLabel(song));
        }

        bool IsLocalInAnySlot()
        {
            foreach (var slot in slots)
                if (slot != null && slot.IsLocalDancer()) return true;
            return false;
        }

        /// <summary>いまの曲の時刻（秒）。</summary>
        float CurrentTime()
        {
            return (float)(Networking.GetServerTimeInSeconds() - _startTime);
        }

        void UpdateSegment(int song, float t)
        {
            int k = t < 0f ? -1 : SegmentAt(song, t);
            if (k == _currentSegment) return;
            _currentSegment = k;
            if (k < 0) return;
            var controller = segmentControllers[segmentStart[song] + k];
            // 両方のステーションに入れる（座っている人の踊りは、座り直すまで変わらない）
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                for (int i = 0; i < slot.StationCount(); i++)
                {
                    var station = slot.GetStation(i);
                    if (station != null) station.animatorController = controller;
                }
            }
            // 区切りの頭からあまり遅れていなければ、座っていない踊り手はここで座り、シークを受けた踊り手はもう一方へ乗り換える
            // （遅れた分だけ踊りが音より遅れるので、遅すぎたら次の区切りを待つ）
            if (t - SegmentTime(song, k) <= lateSeatWindow && (_localStation == null || _switchPending))
            {
                _switchPending = false;
                SeatLocalDancer(k);
            }
        }

        float SegmentTime(int song, int k)
        {
            if (segmentCount == null || song >= segmentCount.Length || segmentCount[song] == 0) return 0f;
            k = Mathf.Clamp(k, 0, segmentCount[song] - 1);
            return segmentTimes[segmentStart[song] + k];
        }

        int LastSegment(int song)
        {
            return segmentCount == null || song >= segmentCount.Length ? 0 : Mathf.Max(0, segmentCount[song] - 1);
        }

        /// <summary>時刻 t を含む区切りの番号（t 以下でいちばん後ろの区切り）。</summary>
        int SegmentAt(int song, float t)
        {
            int k = 0;
            for (int i = 0; i <= LastSegment(song); i++)
                if (SegmentTime(song, i) <= t + 0.001f) k = i;
            return k;
        }

        float RangeEndTime(int song)
        {
            return _rangeEnd < 0 ? songLengths[song] : SegmentTime(song, _rangeEnd);
        }

        string RangeLabel(int song)
        {
            if (_rangeStart == 0 && _rangeEnd < 0 && !_loop) return "";
            return "\n範囲 " + FormatTime(SegmentTime(song, _rangeStart)) + "〜" + FormatTime(RangeEndTime(song)) + (_loop ? "（ループ）" : "");
        }

        /// <summary>プレビュー: 自分の手元だけで、お手本を範囲の頭から踊らせ、音を小さく流す。範囲の終わりで頭に戻る。</summary>
        void UpdateLocalPreview()
        {
            int song = _songIndex;
            if (song < 0 || song >= SongCount()) return;
            float start = SegmentTime(song, _rangeStart);
            float end = RangeEndTime(song);
            float t = start + (Time.time - _localPreviewStart);
            if (t >= end)
            {
                _localPreviewStart = Time.time;
                _previewStarted = false;
                t = start;
            }
            if (audioSource != null) audioSource.volume = _audioVolume * previewVolume;
            SyncAudio(song, t);
            SyncPreview(song, t, songLengths[song]);
            SetSeekBarTime(t);
            SetStatus("プレビュー（自分だけ） " + FormatTime(t) + " / " + FormatTime(songLengths[song]) + RangeLabel(song));
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
            PlaySlotAvatars();
        }

        void SeatLocalDancer(int segment)
        {
            foreach (var slot in slots)
            {
                if (slot == null || !slot.IsLocalDancer()) continue;
                // 今座っているのと違う方へ（初めてなら A へ）。ステーションからステーションへは降りずに乗り換えられる
                var station = slot.GetStation(0);
                if (_localStation == station) station = slot.GetStation(1);
                if (station == null) return;
                _localStation = station;
                station.UseStation(Networking.LocalPlayer);
                Debug.Log("[MmdWorld] 自分を " + slot.transform.parent.name + " の " + station.gameObject.name + " に座らせた（区切り " + segment + "、" + station.animatorController.name + "）");
                return;
            }
        }

        void LeaveLocalStation()
        {
            if (_localStation == null) return;
            Debug.Log("[MmdWorld] ステーションから降りる");
            _localStation.ExitStation(Networking.LocalPlayer);
            _localStation = null;
        }

        /// <summary>
        /// 人が入っている枠のステーションを、曲の軌跡どおりに動かして回す。VRChat は座った人の体の位置と向きをステーションに固定するので、
        /// ステーションを動かさないと、歩いたり回ったりする振りがその場の足踏みになる。
        /// ほかの人のクライアントも座っている人を自分の手元のステーションの位置に出すので、全員の手元で同じように動かす。
        /// </summary>
        void MoveStations(int song, float t)
        {
            if (trajCount == null || song >= trajCount.Length || trajCount[song] < 2) return;
            float f = Mathf.Clamp(t * trajRate[song], 0f, trajCount[song] - 1.001f);
            int i = Mathf.FloorToInt(f);
            float a = f - i;
            int k = trajStart[song] + i;
            float x = Mathf.Lerp(trajX[k], trajX[k + 1], a);
            float z = Mathf.Lerp(trajZ[k], trajZ[k + 1], a);
            float yaw = Mathf.Lerp(trajYaw[k], trajYaw[k + 1], a);
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                var dancer = slot.GetDancer();
                var station = slot.GetStationRoot();
                if (dancer == null || station == null) continue;
                // 軌跡は目の高さを 1 とした値なので、踊っている人のアバターの目の高さを掛ける
                float eye = dancer.GetAvatarEyeHeightAsMeters();
                station.transform.localPosition = new Vector3(x * eye, 0f, z * eye);
                station.transform.localRotation = rotateStations ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.identity;
            }
        }

        void ResetStations()
        {
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                var root = slot.GetStationRoot();
                if (root == null) continue;
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
            }
        }

        void StopLocal()
        {
            ResetStations();
            _currentSegment = -2;
            _switchPending = false;
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.volume = _audioVolume;
            }
            if (_localStation != null)
            {
                LeaveLocalStation();
                RestoreAutoTestAvatar();
            }
            foreach (var dancer in previewDancers)
                if (dancer != null) dancer.Play("Idle", 0, 0f);
            PlaySlotAvatars();
        }

        // ---- 枠で踊らせるアバター ----

        public int SlotAvatarCount()
        {
            return slotAvatars == null ? 0 : slotAvatars.Length;
        }

        public string SlotAvatarName(int index)
        {
            return slotAvatarNames != null && index >= 0 && index < slotAvatarNames.Length ? slotAvatarNames[index] : "";
        }

        /// <summary>このアバターが except 以外の枠に割り当てられているか。</summary>
        public bool IsSlotAvatarUsed(int index, DanceSlot except)
        {
            foreach (var slot in slots)
                if (slot != null && slot != except && slot.GetAvatarIndex() == index) return true;
            return false;
        }

        /// <summary>
        /// 枠の割り当てに合わせて、アバターを枠の位置に出す・しまう。割り当てが変わったとき（どの枠からでも）呼ぶ。
        /// 再生中なら、出したアバターを今の時刻から踊らせる。
        /// </summary>
        public void RefreshSlotAvatars()
        {
            for (int i = 0; i < SlotAvatarCount(); i++)
            {
                var avatar = slotAvatars[i];
                if (avatar == null) continue;
                DanceSlot at = null;
                foreach (var slot in slots)
                    if (slot != null && slot.GetAvatarIndex() == i) at = slot;
                bool show = at != null;
                if (show)
                {
                    var anchor = at.transform.parent;
                    avatar.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
                }
                if (avatar.gameObject.activeSelf != show) avatar.gameObject.SetActive(show);
            }
            PlaySlotAvatars();
        }

        /// <summary>出ているアバターを、再生中なら今の曲の今の時刻から、止まっていれば Idle にする。</summary>
        void PlaySlotAvatars()
        {
            if (SlotAvatarCount() == 0) return;
            string state = "Idle";
            float normalized = 0f;
            if (_playing && _songIndex >= 0 && _songIndex < SongCount())
            {
                double t = CurrentTime();
                float length = songLengths[_songIndex];
                if (t >= 0 && t <= length && length > 0f)
                {
                    state = "Song" + _songIndex;
                    normalized = Mathf.Clamp01((float)(t / length));
                }
            }
            foreach (var avatar in slotAvatars)
                if (avatar != null && avatar.gameObject.activeInHierarchy) avatar.Play(state, 0, normalized);
        }

        // ---- 表示 ----

        void ShowIdle()
        {
            string title = SongCount() == 0 ? "曲がありません" : (_songIndex + 1) + "/" + SongCount() + "  " + songTitles[_songIndex];
            if (titleText != null) titleText.text = title;
            if (tabletTitleText != null) tabletTitleText.text = title;
            if (!_playing && !_localPreview) SetStatus("停止中" + (SongCount() > 0 ? RangeLabel(_songIndex) : ""));
            RefreshSeekBars();
        }

        /// <summary>バーに曲の長さ・区切り・範囲を入れ直す。止まっているときは再生位置を範囲の開始点に置く。</summary>
        void RefreshSeekBars()
        {
            if (seekBars == null || SongCount() == 0) return;
            int song = _songIndex;
            int start = segmentStart != null && song < segmentStart.Length ? segmentStart[song] : 0;
            int count = segmentCount != null && song < segmentCount.Length ? segmentCount[song] : 0;
            foreach (var bar in seekBars)
            {
                if (bar == null) continue;
                bar.SetSong(songLengths[song], segmentTimes, start, count);
                bar.SetRange(SegmentTime(song, _rangeStart), RangeEndTime(song));
                if (!_playing && !_localPreview) bar.SetTime(SegmentTime(song, _rangeStart));
            }
        }

        void SetSeekBarTime(float t)
        {
            if (seekBars == null) return;
            foreach (var bar in seekBars)
                if (bar != null) bar.SetTime(t);
        }

        void SetStatus(string s)
        {
            if (statusText != null) statusText.text = s;
            if (tabletStatusText != null) tabletStatusText.text = s;
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
