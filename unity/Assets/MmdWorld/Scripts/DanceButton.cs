using UdonSharp;

namespace MmdWorld
{
    /// <summary>押すと target の eventName を呼ぶだけのボタン。一覧のボタンは、何番目かを argument で渡す。</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DanceButton : UdonSharpBehaviour
    {
        public UdonSharpBehaviour target;
        public string eventName;
        [Tooltip("0 以上なら、呼ぶ前に target の pressedArgument に入れる（一覧の何番目のボタンか）")]
        public int argument = -1;

        public override void Interact()
        {
            Press();
        }

        /// <summary>押したときの処理（Interact のほか、タブレットの指先・キーからも呼ぶ）。</summary>
        public void Press()
        {
            if (target == null) return;
            if (argument >= 0) target.SetProgramVariable("pressedArgument", argument);
            target.SendCustomEvent(eventName);
        }
    }
}
