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
            Press();
        }

        /// <summary>押したときの処理（Interact のほか、タブレットの指先・キーからも呼ぶ）。</summary>
        public void Press()
        {
            if (target != null) target.SendCustomEvent(eventName);
        }
    }
}
