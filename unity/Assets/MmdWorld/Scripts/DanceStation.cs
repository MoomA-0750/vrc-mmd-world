using UdonSharp;
using UnityEngine;
using VRCStation = VRC.SDK3.Components.VRCStation;
using VRC.SDKBase;

namespace MmdWorld
{
    /// <summary>
    /// 枠のステーション（VRCStation と同じ GameObject）に付け、誰が入った・出たかをログに出し、自分のことなら DanceSystem に知らせる。
    /// DanceSystem は、自分が座らせたのではないのに座った（ステーションを直接選んだなど）ときに降ろし、トラッキングを戻す。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DanceStation : UdonSharpBehaviour
    {
        public DanceSystem system;

        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player)) return;
            Debug.Log("[MmdWorld] ステーションに入った: " + transform.parent.parent.name + " の " + gameObject.name + "（" + player.displayName + "）");
            if (player.isLocal && system != null) system._OnLocalStationEntered(GetComponent<VRCStation>());
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player)) return;
            Debug.Log("[MmdWorld] ステーションから出た: " + transform.parent.parent.name + " の " + gameObject.name + "（" + player.displayName + "）");
            if (player.isLocal && system != null) system._OnLocalStationExited(GetComponent<VRCStation>());
        }
    }
}
