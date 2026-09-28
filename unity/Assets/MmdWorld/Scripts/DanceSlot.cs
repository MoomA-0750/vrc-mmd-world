using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRCStation = VRC.SDK3.Components.VRCStation;
using VRC.SDKBase;

namespace MmdWorld
{
    /// <summary>
    /// 踊る人の枠。床の台を押すと自分の枠になり、もう一度押すと空く。誰の枠かだけを同期する。
    /// 子にステーションを2つ持ち、DanceSystem が曲と区切りに合わせて Controller を差し替えて座らせる（シークでは交互に乗り換える）。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DanceSlot : UdonSharpBehaviour
    {
        public DanceSystem system;
        [Tooltip("この枠のステーション2つ（A と B）。シークでは今座っていない方に Controller を入れ、降りずに乗り換える（降りると一瞬立ち姿勢が見えるため）")]
        public VRCStation[] stations;
        [Tooltip("ステーションの親。踊りの軌跡どおりに DanceSystem がこれを動かす")]
        public Transform stationRoot;
        public Text label;
        public Renderer pad;
        public Color freeColor = new Color(0.3f, 0.3f, 0.35f);
        public Color takenColor = new Color(0.2f, 0.4f, 0.9f);
        public Color mineColor = new Color(1.0f, 0.55f, 0.1f);
        public Color avatarColor = new Color(0.6f, 0.3f, 0.8f);

        [UdonSynced] int _dancerId = -1;
        /// <summary>この枠で踊らせるワールドのアバターの番号（DanceSystem.slotAvatars）。-1 なら無し。人が入っている枠には割り当てない</summary>
        [UdonSynced] int _avatarIndex = -1;
        /// <summary>踊っている人がスティック・WASD で動かした分（ステーションの親のローカル座標、XZ、メートル）。踊っている人（この枠の持ち主）が書く</summary>
        [UdonSynced] Vector3 _drive;
        /// <summary>踊っている人が「その場で踊る」を選んでいるか。オンならステーションを軌跡どおりに動かさない（視点は動かないが、体は移動せず足踏みになる）</summary>
        [UdonSynced] bool _inPlace;
        /// <summary>表示に使う値。持ち主はそのまま、ほかの人は同期された値を滑らかに追う</summary>
        Vector3 _shownDrive;
        bool _driveDirty;
        float _lastDriveSend;
        const float DriveSendInterval = 0.2f;

        void Start()
        {
            Refresh();
        }

        void Update()
        {
            if (IsLocalDancer())
            {
                // 動かしている間は 0.2 秒おきに送り、止めたら最後の値を送る
                if (_driveDirty && Time.time - _lastDriveSend >= DriveSendInterval) SendDrive();
            }
            else
            {
                _shownDrive = Vector3.Lerp(_shownDrive, _drive, 1f - Mathf.Exp(-8f * Time.deltaTime));
            }
        }

        public bool IsInPlace()
        {
            return _inPlace;
        }

        /// <summary>自分が踊っている枠なら、「その場で踊る」を入れる。</summary>
        public void SetLocalInPlace(bool inPlace)
        {
            if (!IsLocalDancer() || _inPlace == inPlace) return;
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _inPlace = inPlace;
            RequestSerialization();
        }

        /// <summary>踊っている人が動かした分（ステーションの親のローカル座標）。</summary>
        public Vector3 GetDrive()
        {
            return _shownDrive;
        }

        /// <summary>自分が踊っている枠なら、動かした分を入れる（同期は間引いて送る）。</summary>
        public void SetLocalDrive(Vector3 drive)
        {
            if (!IsLocalDancer() || drive == _shownDrive) return;
            _shownDrive = drive;
            _drive = drive;
            _driveDirty = true;
            if (Time.time - _lastDriveSend >= DriveSendInterval) SendDrive();
        }

        void SendDrive()
        {
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _driveDirty = false;
            _lastDriveSend = Time.time;
            RequestSerialization();
        }

        public override void Interact()
        {
            var local = Networking.LocalPlayer;
            if (_dancerId == local.playerId)
            {
                SetDancer(-1);
                return;
            }
            // ワールドのアバターが踊る枠には人は入れない（先にアバターを外す）
            if (IsTaken() || _avatarIndex >= 0) return;
            if (system != null) system.ReleaseLocalSlots();
            SetDancer(local.playerId);
        }

        /// <summary>この枠に入っている人（いなければ null）。</summary>
        public VRCPlayerApi GetDancer()
        {
            if (_dancerId < 0) return null;
            var player = VRCPlayerApi.GetPlayerById(_dancerId);
            return Utilities.IsValid(player) ? player : null;
        }

        public int GetAvatarIndex()
        {
            return _avatarIndex;
        }

        /// <summary>
        /// この枠で踊らせるワールドのアバターを次へ送る（なし → 1体目 → 2体目 … → なし）。ほかの枠で踊っているアバターは飛ばす。
        /// 人が入っている枠では何もしない。枠の横のボタンから呼ぶ。
        /// </summary>
        public void NextAvatar()
        {
            if (system == null || IsTaken()) return;
            int count = system.SlotAvatarCount();
            int next = _avatarIndex;
            for (int i = 0; i <= count; i++)
            {
                next++;
                if (next >= count) { next = -1; break; }
                if (!system.IsSlotAvatarUsed(next, this)) break;
            }
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _avatarIndex = next;
            RequestSerialization();
            Refresh();
            system.RefreshSlotAvatars();
        }

        /// <summary>
        /// この枠で踊らせるワールドのアバターを index にする（-1 で外す）。タブレットの「選ぶ」から呼ぶ。
        /// 人が入っている枠と、ほかの枠で踊っているアバターには何もしない。
        /// </summary>
        public void SetAvatar(int index)
        {
            if (system == null || IsTaken() || index >= system.SlotAvatarCount() || index == _avatarIndex) return;
            if (index >= 0 && system.IsSlotAvatarUsed(index, this)) return;
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _avatarIndex = index;
            RequestSerialization();
            Refresh();
            system.RefreshSlotAvatars();
        }

        /// <summary>選ぶページに出す、この枠の今の状態（人の名前・ワールドのアバターの名前・空き）。</summary>
        public string Describe()
        {
            if (IsTaken()) return VRCPlayerApi.GetPlayerById(_dancerId).displayName;
            if (_avatarIndex >= 0 && system != null) return system.SlotAvatarName(_avatarIndex);
            return "空き";
        }

        public bool HasDancer()
        {
            return IsTaken();
        }

        /// <summary>空いていれば自分の枠にする（自動確認用）。</summary>
        public void ClaimForLocal()
        {
            if (IsLocalDancer() || IsTaken()) return;
            if (system != null) system.ReleaseLocalSlots();
            SetDancer(Networking.LocalPlayer.playerId);
        }

        public void ReleaseIfLocal()
        {
            if (IsLocalDancer()) SetDancer(-1);
        }

        public bool IsLocalDancer()
        {
            return Utilities.IsValid(Networking.LocalPlayer) && _dancerId == Networking.LocalPlayer.playerId;
        }

        public VRCStation GetStation(int index)
        {
            return stations != null && index >= 0 && index < stations.Length ? stations[index] : null;
        }

        public int StationCount()
        {
            return stations == null ? 0 : stations.Length;
        }

        public Transform GetStationRoot()
        {
            return stationRoot;
        }

        public override void OnDeserialization()
        {
            Refresh();
            if (system != null) system.RefreshSlotAvatars();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            // 抜けた人の枠は、表示の上で空きに戻す（誰かが押せばその人の枠になる）
            Refresh();
        }

        void SetDancer(int playerId)
        {
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _dancerId = playerId;
            _drive = Vector3.zero;
            _shownDrive = Vector3.zero;
            _inPlace = false;
            RequestSerialization();
            Refresh();
            // 入った人の「その場で踊る」の選択を枠に入れる
            if (playerId >= 0 && system != null) system.ApplyInPlace();
        }

        bool IsTaken()
        {
            if (_dancerId < 0) return false;
            var player = VRCPlayerApi.GetPlayerById(_dancerId);
            return Utilities.IsValid(player);
        }

        void Refresh()
        {
            bool taken = IsTaken();
            if (label != null)
            {
                if (taken) label.text = VRCPlayerApi.GetPlayerById(_dancerId).displayName;
                else if (_avatarIndex >= 0 && system != null) label.text = system.SlotAvatarName(_avatarIndex) + "\n（ワールドのアバター）";
                else label.text = "空き\n（押して参加）";
            }
            if (pad != null)
                pad.material.color = IsLocalDancer() ? mineColor : (taken ? takenColor : (_avatarIndex >= 0 ? avatarColor : freeColor));
            // 手元のパネルの枠の列も合わせる
            if (system != null) system.RefreshLists();
        }
    }
}
