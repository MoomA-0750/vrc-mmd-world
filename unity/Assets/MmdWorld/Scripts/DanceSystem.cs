using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRCStation = VRC.SDK3.Components.VRCStation;
using VRCAvatarPedestal = VRC.SDK3.Components.VRCAvatarPedestal;
using VRC.SDKBase;
using VRC.Udon.Common;

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
        [Tooltip("降りる前に座り直す、トラッキングを元に戻すだけの Controller（VRC Animator Tracking Control で全身を Tracking にする）。無ければそのまま降りる。1つだけの変数だと VRChat で読み込めなかったので配列（先頭を使う）")]
        public RuntimeAnimatorController[] restoreControllers;
        [Tooltip("トラッキングを戻す Controller に座ってから降りるまでの秒数")]
        public float restoreSeconds = 0.5f;

        [Header("選ぶページ（タブレットの「選ぶ」）")]
        [Tooltip("曲の一覧のボタンの文字（1ページぶん）")]
        public Text[] selectSongTexts;
        [Tooltip("枠のボタンの文字（先頭から枠1、枠2 …）")]
        public Text[] selectSlotTexts;
        [Tooltip("アバターの一覧のボタンの文字。先頭は「なし」、残りが1ページぶん")]
        public Text[] selectAvatarTexts;
        [Tooltip("DanceButton が押したボタンの番号を入れる")]
        public int pressedArgument;
        public Color selectTextColor = Color.white;
        public Color selectCurrentColor = new Color(1f, 0.8f, 0.3f);
        public Color selectDisabledColor = new Color(0.55f, 0.55f, 0.6f);

        [Header("その場で踊る")]
        [Tooltip("「その場で踊る」の切り替えボタンの文字（今の状態を出す）")]
        public Text[] inPlaceLabels;

        [Header("踊りながら動く")]
        [Tooltip("踊っている間、スティック・WASD で自分の枠（ステーションの親）ごと動けるようにする。前は、VR では頭の向き、デスクトップでは枠の向き")]
        public bool driveWhileDancing = false;
        [Tooltip("動く速さ（メートル/秒）")]
        public float driveSpeed = 1.5f;
        [Tooltip("枠の位置からどこまで離れられるか（メートル）")]
        public float driveRadius = 4f;

        [Header("体の軌跡（ワールドを組み立てるメニューが入れる）")]
        [Tooltip("全曲ぶんをつなげた軌跡（床の上の位置）。目の高さを 1 とした値。向きはステーションのクリップに入っているので持たない")]
        public float[] trajX;
        public float[] trajZ;
        [Tooltip("曲ごとの、軌跡の始まりの位置・数・1秒あたりのサンプル数")]
        public int[] trajStart;
        public int[] trajCount;
        public float[] trajRate;

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
        /// <summary>トラッキングを戻すために座り直したステーション（restoreSeconds 後に降りる）</summary>
        VRCStation _restoreStation;
        /// <summary>シークを受けたあと、次に区切りをまたいだところで、もう一方のステーションへ乗り換える</summary>
        bool _switchPending;
        bool _previewStarted;
        int _currentSegment = -2;
        int _handledSeekSeq;
        bool _localPreview;
        float _localPreviewStart;
        float _audioVolume = 1f;
        float _moveX;
        float _moveY;
        /// <summary>自分が「その場で踊る」を選んでいるか（枠に入ったとき・切り替えたときに枠へ入れる）</summary>
        bool _preferInPlace;
        int _songPage;
        int _avatarPage;
        int _selectSlot;
        /// <summary>自動確認で入れる、スティックを倒したことにする入力</summary>
        Vector2 _autoMove;

        void Start()
        {
            if (audioSource != null) _audioVolume = audioSource.volume;
            ShowIdle();
            ApplyInPlace();
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
        public void _AutoTestMoveForward() { _AutoTestMove(new Vector2(0f, 1f)); }
        public void _AutoTestMoveRight() { _AutoTestMove(new Vector2(1f, 0f)); }
        public void _AutoTestMoveBack() { _AutoTestMove(new Vector2(0f, -1f)); }
        public void _AutoTestMoveLeft() { _AutoTestMove(new Vector2(-1f, 0f)); }

        void _AutoTestMove(Vector2 move)
        {
            Debug.Log("[MmdWorld] 自動確認: スティック " + move.ToString("F0"));
            _autoMove = move;
            SendCustomEventDelayedSeconds(nameof(_AutoTestMoveRelease), 3f);
        }

        public void _AutoTestMoveRelease()
        {
            _autoMove = Vector2.zero;
        }

        public void _AutoTestLogPosition()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            string line = "[MmdWorld] 位置: 自分 " + local.GetPosition().ToString("F2") + (_localStation != null ? " 席" : " 立ち");
            var dancer = slots.Length > 0 && slots[0] != null ? slots[0].GetDancer() : null;
            if (Utilities.IsValid(dancer))
                line += " / 枠1 " + dancer.GetPosition().ToString("F2") + " 骨盤 " + dancer.GetBonePosition(HumanBodyBones.Hips).ToString("F2")
                    + " 腰の向き " + dancer.GetBoneRotation(HumanBodyBones.Hips).eulerAngles.y.ToString("F0");
            if (slots.Length > 0 && slots[0] != null) line += " / 席 " + slots[0].GetStationRoot().position.ToString("F2") + " 向き " + slots[0].GetStationRoot().eulerAngles.y.ToString("F0") + " 動かした分 " + slots[0].GetDrive().ToString("F2");
            line += " / 入力 " + _moveX.ToString("F1") + "," + _moveY.ToString("F1") + " 自動 " + _autoMove.ToString("F0")
                + " キー " + (Input.GetKey(KeyCode.W) ? "W" : "") + (Input.GetKey(KeyCode.A) ? "A" : "") + (Input.GetKey(KeyCode.S) ? "S" : "") + (Input.GetKey(KeyCode.D) ? "D" : "")
                + " 軸 " + Input.GetAxisRaw("Horizontal").ToString("F1") + "," + Input.GetAxisRaw("Vertical").ToString("F1");
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
            if (autoTestScenario == 3)
            {
                // 踊りながら動く: 始まって 8 秒から、前・右・後ろ・左へ 3 秒ずつスティックを倒したことにする。35 秒で止めて降りる
                Debug.Log("[MmdWorld] 自動確認: 踊りながら枠ごと動く");
                SendCustomEventDelayedSeconds(nameof(_AutoTestMoveForward), countdownSeconds + 8f);
                SendCustomEventDelayedSeconds(nameof(_AutoTestMoveRight), countdownSeconds + 14f);
                SendCustomEventDelayedSeconds(nameof(_AutoTestMoveBack), countdownSeconds + 20f);
                SendCustomEventDelayedSeconds(nameof(_AutoTestMoveLeft), countdownSeconds + 26f);
                SendCustomEventDelayedSeconds(nameof(_AutoTestStop), countdownSeconds + 35f);
                return;
            }
            // シークも試す: 始まって 20 秒で1つ先へ、35 秒で1つ前へ
            SendCustomEventDelayedSeconds(nameof(_AutoTestSeekForward), 23f);
            SendCustomEventDelayedSeconds(nameof(_AutoTestSeekBack), 38f);
        }

        public void _AutoTestTabletPlay()
        {
            if (tablet == null) return;
            tablet.Press(12); // 踊る / やめる
            tablet.Press(1);  // 再生
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletInPlace), 8f);
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletInPlace), 18f);
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletSeek), 23f);
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletStop), 38f);
        }

        /// <summary>「その場」を切り替える（8 秒でオン、18 秒でオフ。オンの間は席が動かないのを見る）</summary>
        public void _AutoTestTabletInPlace()
        {
            if (tablet != null) tablet.Press(13); // その場
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
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletSelect), 4f);
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletClose), 12f);
        }

        /// <summary>「選ぶ」のページを開き、枠2を選んでアバター「なし」、曲の1番目を押して、戻る。</summary>
        public void _AutoTestTabletSelect()
        {
            if (tablet == null) return;
            tablet.Press(14); // 選ぶ
            tablet.Press(20); // 枠2
            tablet.Press(18); // アバター: なし
            tablet.Press(16); // 曲の1番目
            SendCustomEventDelayedSeconds(nameof(_AutoTestTabletBack), 4f);
        }

        public void _AutoTestTabletBack()
        {
            // 選ぶページに出ている文字をログに出す
            string line = "[MmdWorld] 選ぶページ: 曲";
            foreach (var t in selectSongTexts) line += " [" + (t != null ? t.text : "") + "]";
            line += " / 枠";
            foreach (var t in selectSlotTexts) line += " [" + (t != null ? t.text.Replace("\n", " ") : "") + "]";
            line += " / アバター";
            foreach (var t in selectAvatarTexts) line += " [" + (t != null ? t.text.Replace("\n", " ") : "") + "]";
            Debug.Log(line);
            if (tablet != null) tablet.Press(32); // 戻る
        }

        public void _AutoTestTabletClose()
        {
            if (tablet != null) tablet.Press(15); // 閉じる
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

        // ---- 選ぶページ ----

        int SongsPerPage()
        {
            return selectSongTexts == null ? 0 : selectSongTexts.Length;
        }

        int AvatarsPerPage()
        {
            return selectAvatarTexts == null ? 0 : selectAvatarTexts.Length - 1;
        }

        /// <summary>曲の一覧の pressedArgument 番目を選ぶ（再生中は変えない）。</summary>
        public void SelectSongButton()
        {
            int index = _songPage * SongsPerPage() + pressedArgument;
            Debug.Log("[MmdWorld] 選ぶ: 曲 " + index);
            if (_playing || index < 0 || index >= SongCount() || index == _songIndex) return;
            TakeOwnership();
            _songIndex = index;
            ResetRange();
            Commit();
        }

        public void SongPageNext() { MoveSongPage(1); }
        public void SongPagePrev() { MoveSongPage(-1); }

        void MoveSongPage(int step)
        {
            int per = Mathf.Max(1, SongsPerPage());
            int pages = Mathf.Max(1, (SongCount() + per - 1) / per);
            _songPage = (_songPage + step + pages) % pages;
            RefreshSelectPage();
        }

        /// <summary>アバターを置く枠を pressedArgument 番目にする。</summary>
        public void SelectSlotButton()
        {
            if (pressedArgument < 0 || pressedArgument >= slots.Length) return;
            _selectSlot = pressedArgument;
            Debug.Log("[MmdWorld] 選ぶ: 枠" + (_selectSlot + 1));
            RefreshSelectPage();
        }

        /// <summary>選んでいる枠に、アバターの一覧の pressedArgument 番目を置く（0 は「なし」）。</summary>
        public void SelectAvatarButton()
        {
            if (_selectSlot < 0 || _selectSlot >= slots.Length || slots[_selectSlot] == null) return;
            int index = pressedArgument == 0 ? -1 : _avatarPage * AvatarsPerPage() + pressedArgument - 1;
            if (index >= SlotAvatarCount()) return;
            Debug.Log("[MmdWorld] 選ぶ: 枠" + (_selectSlot + 1) + " のアバター " + (index < 0 ? "なし" : SlotAvatarName(index)));
            slots[_selectSlot].SetAvatar(index);
            RefreshSelectPage();
        }

        public void AvatarPageNext() { MoveAvatarPage(1); }
        public void AvatarPagePrev() { MoveAvatarPage(-1); }

        void MoveAvatarPage(int step)
        {
            int per = Mathf.Max(1, AvatarsPerPage());
            int pages = Mathf.Max(1, (SlotAvatarCount() + per - 1) / per);
            _avatarPage = (_avatarPage + step + pages) % pages;
            RefreshSelectPage();
        }

        /// <summary>選ぶページの文字を、今の曲・枠・アバターに合わせる。今のものは黄色、選べないものは灰色。</summary>
        public void RefreshSelectPage()
        {
            if (selectSongTexts != null)
                for (int i = 0; i < selectSongTexts.Length; i++)
                {
                    var text = selectSongTexts[i];
                    if (text == null) continue;
                    int index = _songPage * SongsPerPage() + i;
                    bool has = index < SongCount();
                    text.text = has ? (index == _songIndex ? "▶ " : "") + songTitles[index] : "";
                    text.color = index == _songIndex ? selectCurrentColor : (_playing ? selectDisabledColor : selectTextColor);
                }
            if (selectSlotTexts != null)
                for (int i = 0; i < selectSlotTexts.Length; i++)
                {
                    var text = selectSlotTexts[i];
                    if (text == null) continue;
                    bool has = i < slots.Length && slots[i] != null;
                    text.text = has ? "枠" + (i + 1) + "\n" + slots[i].Describe() : "";
                    text.color = i == _selectSlot ? selectCurrentColor : selectTextColor;
                }
            if (selectAvatarTexts != null)
            {
                var slot = _selectSlot < slots.Length ? slots[_selectSlot] : null;
                bool person = slot != null && slot.HasDancer();
                int current = slot != null ? slot.GetAvatarIndex() : -1;
                for (int i = 0; i < selectAvatarTexts.Length; i++)
                {
                    var text = selectAvatarTexts[i];
                    if (text == null) continue;
                    if (i == 0)
                    {
                        text.text = person ? "人が踊る枠" : "なし";
                        text.color = person ? selectDisabledColor : (current < 0 ? selectCurrentColor : selectTextColor);
                        continue;
                    }
                    int index = _avatarPage * AvatarsPerPage() + i - 1;
                    if (index >= SlotAvatarCount())
                    {
                        text.text = i == 1 && SlotAvatarCount() == 0 ? "（ワールドに\nアバター無し）" : "";
                        text.color = selectDisabledColor;
                        continue;
                    }
                    text.text = SlotAvatarName(index);
                    bool usedElsewhere = IsSlotAvatarUsed(index, slot);
                    text.color = index == current ? selectCurrentColor : (person || usedElsewhere ? selectDisabledColor : selectTextColor);
                }
            }
        }

        /// <summary>
        /// 「その場で踊る」を切り替える（タブレットから）。オンなら、ステーションを振り付けの移動どおりに動かさない。
        /// VR では視点がステーションに付いているので、オンだと視点は動かない代わりに、体は移動せず足踏みになる。
        /// </summary>
        public void ToggleInPlace()
        {
            _preferInPlace = !_preferInPlace;
            Debug.Log("[MmdWorld] その場で踊る: " + _preferInPlace);
            ApplyInPlace();
        }

        /// <summary>自分の枠に「その場で踊る」を入れ、ボタンの文字を今の状態にする。</summary>
        public void ApplyInPlace()
        {
            foreach (var slot in slots)
                if (slot != null && slot.IsLocalDancer()) slot.SetLocalInPlace(_preferInPlace);
            if (inPlaceLabels == null) return;
            foreach (var label in inPlaceLabels)
                if (label != null) label.text = _preferInPlace ? "その場: オン" : "その場: オフ";
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
            DriveLocal();

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

        /// <summary>
        /// 自分をステーションから降ろす。VR ではステーションの Controller が VRC Animator Tracking Control で全身を踊りに任せているので、
        /// そのまま降りるとトラッキングが戻らない。先にもう一方のステーションへ、トラッキングを戻すだけの Controller で乗り換え、少し待ってから降りる。
        /// </summary>
        void LeaveLocalStation()
        {
            if (_localStation == null) return;
            var station = _localStation;
            _localStation = null;
            _switchPending = false;
            var other = OtherStation(station);
            var restore = restoreControllers != null && restoreControllers.Length > 0 ? restoreControllers[0] : null;
            if (restore != null && other != null)
            {
                Debug.Log("[MmdWorld] トラッキングを戻してから降りる");
                other.animatorController = restore;
                _restoreStation = other;
                other.UseStation(Networking.LocalPlayer);
                SendCustomEventDelayedSeconds(nameof(_FinishLeave), restoreSeconds);
                return;
            }
            Debug.Log("[MmdWorld] ステーションから降りる");
            station.ExitStation(Networking.LocalPlayer);
        }

        public void _FinishLeave()
        {
            if (_restoreStation == null) return;
            Debug.Log("[MmdWorld] ステーションから降りる");
            var station = _restoreStation;
            _restoreStation = null;
            station.ExitStation(Networking.LocalPlayer);
        }

        /// <summary>同じ枠のもう一方のステーション。</summary>
        VRCStation OtherStation(VRCStation station)
        {
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                for (int i = 0; i < slot.StationCount(); i++)
                    if (slot.GetStation(i) == station) return slot.GetStation(1 - i);
            }
            return null;
        }

        /// <summary>DanceStation から: 自分がステーションに入った。自分が座らせたのでなければ（ステーションを直接選んだなど）、すぐ降ろす。</summary>
        public void _OnLocalStationEntered(VRCStation station)
        {
            if (station == _localStation || station == _restoreStation) return;
            Debug.Log("[MmdWorld] 座らせていないのにステーションに入ったので、降ろす: " + station.gameObject.name);
            _localStation = station;
            LeaveLocalStation();
        }

        /// <summary>DanceStation から: 自分がステーションから出た（リスポーンなど、こちらが降ろしたのでない場合も含む）。</summary>
        public void _OnLocalStationExited(VRCStation station)
        {
            if (station == _localStation) _localStation = null;
        }

        /// <summary>
        /// 人が入っている枠のステーションを、曲の軌跡どおりに動かして回す。VRChat は座った人の体の位置と向きをステーションに固定するので、
        /// ステーションを動かさないと、歩いたり回ったりする振りがその場の足踏みになる。
        /// ほかの人のクライアントも座っている人を自分の手元のステーションの位置に出すので、全員の手元で同じように動かす。
        /// </summary>
        void MoveStations(int song, float t)
        {
            float x = 0f, z = 0f;
            if (trajCount != null && song < trajCount.Length && trajCount[song] >= 2)
            {
                float f = Mathf.Clamp(t * trajRate[song], 0f, trajCount[song] - 1.001f);
                int i = Mathf.FloorToInt(f);
                float a = f - i;
                int k = trajStart[song] + i;
                x = Mathf.Lerp(trajX[k], trajX[k + 1], a);
                z = Mathf.Lerp(trajZ[k], trajZ[k + 1], a);
            }
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                var dancer = slot.GetDancer();
                var station = slot.GetStationRoot();
                if (dancer == null || station == null) continue;
                // 軌跡は目の高さを 1 とした値なので、踊っている人のアバターの目の高さを掛ける
                float eye = dancer.GetAvatarEyeHeightAsMeters();
                // 踊っている人がスティック・WASD で動かした分を足す。「その場で踊る」なら軌跡では動かさない
                var along = slot.IsInPlace() ? Vector3.zero : new Vector3(x * eye, 0f, z * eye);
                station.transform.localPosition = along + slot.GetDrive();
                // 向きはステーションのクリップに入っているので回さない（回すと VR では視点も回る）
                station.transform.localRotation = Quaternion.identity;
            }
        }

        /// <summary>
        /// 自分が踊っている間、スティック・WASD の入力で自分の枠ごと動かす。座ったままなので、VRChat の歩きの代わりにステーションの親を動かす（乗り物と同じ）。
        /// 向きは頭の左右の向き。動かした分は枠が同期し、ほかの人の手元でも MoveStations が足す。
        /// </summary>
        void DriveLocal()
        {
            if (!driveWhileDancing || _localStation == null) return;
            var input = new Vector2(_moveX, _moveY) + _autoMove;
            // デスクトップは WASD も直接読む（座っている間に VRChat が移動のイベントを渡さない場合に備える）
            if (Input.GetKey(KeyCode.W)) input.y += 1f;
            if (Input.GetKey(KeyCode.S)) input.y -= 1f;
            if (Input.GetKey(KeyCode.D)) input.x += 1f;
            if (Input.GetKey(KeyCode.A)) input.x -= 1f;
            input = Vector2.ClampMagnitude(input, 1f);
            if (input.sqrMagnitude < 0.01f) return;
            DanceSlot mine = null;
            foreach (var slot in slots)
                if (slot != null && slot.IsLocalDancer()) mine = slot;
            if (mine == null || mine.GetStationRoot() == null) return;
            var parent = mine.GetStationRoot().parent;
            // VR は実際の頭の向きを前にする。デスクトップは視点が踊りで回るので、枠の向き（客席の方）を前にする
            var forward = Networking.LocalPlayer.IsUserInVR() || parent == null
                ? Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation * Vector3.forward
                : parent.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return;
            forward.Normalize();
            var right = new Vector3(forward.z, 0f, -forward.x);
            var world = (forward * input.y + right * input.x) * driveSpeed * Time.deltaTime;
            var local = parent != null ? parent.InverseTransformDirection(world) : world;
            var drive = mine.GetDrive() + local;
            drive.y = 0f;
            mine.SetLocalDrive(Vector3.ClampMagnitude(drive, driveRadius));
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            _moveX = value;
        }

        public override void InputMoveVertical(float value, UdonInputEventArgs args)
        {
            _moveY = value;
        }

        void ResetStations()
        {
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                slot.SetLocalDrive(Vector3.zero);
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
            RefreshSelectPage();
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
            RefreshSelectPage();
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
