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

        void Start()
        {
            Refresh();
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
            RequestSerialization();
            Refresh();
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
        }
    }
}
