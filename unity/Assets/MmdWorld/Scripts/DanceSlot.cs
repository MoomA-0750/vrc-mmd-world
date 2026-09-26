using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRCStation = VRC.SDK3.Components.VRCStation;
using VRC.SDKBase;

namespace MmdWorld
{
    /// <summary>
    /// 踊る人の枠。床の台を押すと自分の枠になり、もう一度押すと空く。誰の枠かだけを同期する。
    /// 子に曲の数だけステーションを持ち、DanceSystem が曲の始まりにそのうち1つへ座らせる。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DanceSlot : UdonSharpBehaviour
    {
        public DanceSystem system;
        [Tooltip("曲の順に並んだステーション")]
        public VRCStation[] stations;
        public Text label;
        public Renderer pad;
        public Color freeColor = new Color(0.3f, 0.3f, 0.35f);
        public Color takenColor = new Color(0.2f, 0.4f, 0.9f);
        public Color mineColor = new Color(1.0f, 0.55f, 0.1f);

        [UdonSynced] int _dancerId = -1;

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
            if (IsTaken()) return;
            if (system != null) system.ReleaseLocalSlots();
            SetDancer(local.playerId);
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

        public VRCStation GetStation(int song)
        {
            if (stations == null || song < 0 || song >= stations.Length) return null;
            return stations[song];
        }

        public override void OnDeserialization()
        {
            Refresh();
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
                label.text = taken ? VRCPlayerApi.GetPlayerById(_dancerId).displayName : "空き\n（押して参加）";
            if (pad != null)
                pad.material.color = IsLocalDancer() ? mineColor : (taken ? takenColor : freeColor);
        }
    }
}
