using UdonSharp;

namespace MmdWorld
{
    /// <summary>押すと target の eventName を呼ぶだけのボタン。</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DanceButton : UdonSharpBehaviour
    {
        public UdonSharpBehaviour target;
        public string eventName;

        public override void Interact()
        {
            if (target != null) target.SendCustomEvent(eventName);
        }
    }
}
