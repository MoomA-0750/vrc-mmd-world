using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace MmdWorld
{
    /// <summary>
    /// 手元に浮かぶタブレット。踊っている間（ステーションに固定されている間）でも、再生・停止・シークなどを操作できる。
    /// 自分の手元でだけ動かす（同期しない。ほかの人には、その人のタブレットがその人の手元に出る）。
    ///
    /// VR: 左右どちらかの手のグリップを2回握ると、その手のひらの少し上・画面を目の方へ向けた位置に出し、そのときの手との位置関係のまま、手の位置と向きに付いていく。
    ///     押すのはもう一方の手の指先。
    ///     3回握ると、握った手の少し先に、目の方へ向けてワールドに止めて置く。押すのは握った手の指先（片手だけで出して押せる）。
    ///     踊っている間はアバターの手が踊りで動くので、アバターの手ではなく、実際の左右のコントローラーの位置を使う。右手の指先は目印の球で示す。
    ///     指先がボタンに近づくと明るくなり、面を押し込むと押したことになって、振動で知らせる。
    /// デスクトップ: T キーで出し入れする。踊ると視点が回ってマウスでは狙えないので、画面の下に固定して、ボタンに書いたキーで押す。
    /// どちらも、スティック・WASD で移動しようとしたら消す。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DanceTablet : UdonSharpBehaviour
    {
        public DanceSystem system;
        [Tooltip("出し入れする見た目の親（ボタン・文字・板）")]
        public GameObject body;
        [Tooltip("タブレットのボタン。押す判定は、DanceTablet から見たボタンの中心と、buttonSizes の大きさ（メートル）で行う")]
        public DanceButton[] buttons;
        [Tooltip("ボタンの見た目（近づくと明るく、押すとさらに明るくする）")]
        public Image[] buttonImages;
        public Vector2[] buttonSizes;
        public Color normalColor = new Color(0.25f, 0.45f, 0.8f);
        public Color hoverColor = new Color(0.4f, 0.62f, 1f);
        public Color pressColor = new Color(0.75f, 0.88f, 1f);
        [Tooltip("デスクトップで各ボタンを押すキー（Input.GetKeyDown に渡す名前: \"1\"、\"-\"、\"j\" など。空なら無し）。Udon は KeyCode の配列を扱えないので文字列で持つ")]
        public string[] desktopKeys;
        [Tooltip("ボタンのキーの表示（デスクトップのときだけ出す）")]
        public GameObject[] desktopKeyLabels;
        [Tooltip("VR で指先を示す小さな球")]
        public Transform pointer;
        public string desktopToggleKey = "t";

        [Header("置き方")]
        [Tooltip("VR: 左手のコントローラーから、どれだけ上（ワールドの上向き）に浮かべるか（メートル）")]
        public float palmHeight = 0.1f;
        [Tooltip("VR: 左手のコントローラーから、目の方へどれだけ寄せるか（メートル）")]
        public float palmTowardEyes = 0.03f;
        [Tooltip("VR での大きさ（1 で幅 38cm）")]
        public float vrScale = 0.55f;
        [Tooltip("グリップをこの秒数以内に続けて握ったら数える（2回で手に出す・消す、3回でワールドに止めて置く）")]
        public float doubleGripSeconds = 0.5f;
        [Tooltip("VR: 3回握ってワールドに置くとき、手から目と反対の向きへどれだけ離すか（メートル）")]
        public float fixedAhead = 0.12f;
        [Tooltip("スティック・WASD をこれより倒したら消す")]
        public float hideOnMove = 0.5f;
        [Tooltip("デスクトップ: 頭からどこに置くか（視点についてくる）")]
        public Vector3 desktopOffset = new Vector3(0f, -0.1f, 1.0f);
        [Tooltip("右手のコントローラーから指先までのずれ（コントローラーの向きのローカル座標、メートル）")]
        public Vector3 fingerOffset = new Vector3(0f, -0.02f, 0.07f);

        [Header("押し方")]
        [Tooltip("ボタンの面をこれだけ越えたら押したことにする（メートル）")]
        public float pressDepth = 0.004f;
        [Tooltip("面からこれだけ手前に戻したら離したことにする（メートル）")]
        public float releaseDistance = 0.025f;
        [Tooltip("指先が面からこれだけ手前まで来たら、ボタンを明るくする（メートル）")]
        public float hoverDistance = 0.04f;

        VRCPlayerApi _local;
        bool _vr;
        bool _visible;
        bool[] _pressed;
        /// <summary>ボタンの見た目の状態（0: ふつう、1: 近い、2: 押している）。変わったときだけ色を塗り直す</summary>
        int[] _shown;
        /// <summary>出したときの、左手から見たタブレットの位置と向き（VR）</summary>
        Vector3 _handOffset;
        Quaternion _handRotation;
        /// <summary>右手に持っているか（押すのは反対の手）。ワールドに置いたときは、右手で押すか</summary>
        bool _rightHand;
        /// <summary>ワールドに止めて置いているか（3回握ったとき）</summary>
        bool _fixed;
        float _lastGripLeft = -10f;
        float _lastGripRight = -10f;
        /// <summary>続けて握った回数</summary>
        int _gripsLeft;
        int _gripsRight;

        void Start()
        {
            _local = Networking.LocalPlayer;
            if (!Utilities.IsValid(_local)) return;
            _vr = _local.IsUserInVR();
            _pressed = new bool[buttons.Length];
            _shown = new int[buttons.Length];
            transform.localScale = Vector3.one * (_vr ? vrScale : 1f);
            foreach (var label in desktopKeyLabels)
                if (label != null) label.SetActive(!_vr);
            SetVisible(false);
        }

        /// <summary>
        /// グリップを続けて握った回数（前の握りから doubleGripSeconds 以内）で決める。1回だけだと、物を掴むなどふだんの操作で出てしまうため。
        /// 2回: その手に出す。その手に出しているなら消し、もう一方の手・ワールドに置いているなら、その手に持ち替える。
        /// 3回: 2回目で出した・消したのに続けて、握った手の少し先のワールドに止めて置き、その手で押すようにする。
        /// </summary>
        public override void InputGrab(bool value, UdonInputEventArgs args)
        {
            if (!_vr || !value) return;
            bool right = args.handType == HandType.RIGHT;
            float now = Time.time;
            float last = right ? _lastGripRight : _lastGripLeft;
            int grips = now - last > doubleGripSeconds ? 1 : (right ? _gripsRight : _gripsLeft) + 1;
            if (right) { _lastGripRight = now; _gripsRight = grips; }
            else { _lastGripLeft = now; _gripsLeft = grips; }
            if (grips == 2)
            {
                if (_visible && !_fixed && _rightHand == right)
                {
                    SetVisible(false);
                    return;
                }
                _rightHand = right;
                AttachToHand();
                SetVisible(true);
            }
            else if (grips == 3)
            {
                // 4回目の握りを、次の2回の1回目として数えない
                if (right) _lastGripRight = -10f; else _lastGripLeft = -10f;
                _rightHand = right;
                PlaceInWorld();
                SetVisible(true);
            }
        }

        /// <summary>出し入れする（タブレットの「閉じる」ボタン・デスクトップの T キーから呼ぶ）。VR では、前に出していた手に出す。</summary>
        public void Toggle()
        {
            if (!_visible && _vr) AttachToHand();
            SetVisible(!_visible);
        }

        /// <summary>握った手の少し先（目と反対の向き）に、画面を目の方へ向けてワールドに止めて置く。</summary>
        void PlaceInWorld()
        {
            _fixed = true;
            var head = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            var hand = _local.GetTrackingData(_rightHand ? VRCPlayerApi.TrackingDataType.RightHand : VRCPlayerApi.TrackingDataType.LeftHand).position;
            var away = hand - head;
            var position = hand + (away.sqrMagnitude > 1e-4f ? away.normalized * fixedAhead : Vector3.zero);
            var look = position - head;
            transform.SetPositionAndRotation(position, look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look, Vector3.up) : transform.rotation);
        }

        /// <summary>持つ手のひらの少し上・画面を目の方へ向けた位置に置き、そのときの手との位置関係を覚える。</summary>
        void AttachToHand()
        {
            _fixed = false;
            var head = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            var hand = _local.GetTrackingData(HoldingHand());
            var toEyes = head - hand.position;
            var position = hand.position + Vector3.up * palmHeight + (toEyes.sqrMagnitude > 1e-4f ? toEyes.normalized * palmTowardEyes : Vector3.zero);
            var look = position - head;
            var rotation = look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look, Vector3.up) : hand.rotation;
            var inverse = Quaternion.Inverse(hand.rotation);
            _handOffset = inverse * (position - hand.position);
            _handRotation = inverse * rotation;
        }

        /// <summary>右手で押すか（手に持っているなら反対の手、ワールドに置いたなら握った手）</summary>
        bool PressingRight()
        {
            return _fixed ? _rightHand : !_rightHand;
        }

        VRCPlayerApi.TrackingDataType HoldingHand()
        {
            return _rightHand ? VRCPlayerApi.TrackingDataType.RightHand : VRCPlayerApi.TrackingDataType.LeftHand;
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            if (_visible && Mathf.Abs(value) > hideOnMove) SetVisible(false);
        }

        public override void InputMoveVertical(float value, UdonInputEventArgs args)
        {
            if (_visible && Mathf.Abs(value) > hideOnMove) SetVisible(false);
        }

        public void Hide()
        {
            SetVisible(false);
        }


        void SetVisible(bool visible)
        {
            _visible = visible;
            if (body != null) body.SetActive(visible);
            if (pointer != null) pointer.gameObject.SetActive(visible && _vr);
            if (_pressed != null)
                for (int i = 0; i < _pressed.Length; i++)
                {
                    _pressed[i] = false;
                    SetShown(i, 0);
                }
        }

        void Update()
        {
            if (!Utilities.IsValid(_local)) return;
            if (!_vr)
            {
                UpdateDesktop();
                return;
            }
            if (!_visible) return;

            // 手に持っているなら、出したときの手との位置関係のまま、持つ手の位置と向きに付いていく。押すのはもう一方の手の指先
            if (!_fixed)
            {
                var hand = _local.GetTrackingData(HoldingHand());
                transform.SetPositionAndRotation(hand.position + hand.rotation * _handOffset, hand.rotation * _handRotation);
            }

            var finger = _local.GetTrackingData(PressingRight() ? VRCPlayerApi.TrackingDataType.RightHand : VRCPlayerApi.TrackingDataType.LeftHand);
            var tip = finger.position + finger.rotation * fingerOffset;
            if (pointer != null) pointer.position = tip;
            TouchButtons(transform.InverseTransformPoint(tip));
        }

        /// <summary>指先（タブレットのローカル座標）がボタンの面を押し込んだら押す。離れるまでは続けて押さない。</summary>
        void TouchButtons(Vector3 p)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var c = transform.InverseTransformPoint(button.transform.position);
                var half = buttonSizes[i] * 0.5f;
                bool over = Mathf.Abs(p.x - c.x) <= half.x && Mathf.Abs(p.y - c.y) <= half.y;
                // 面は z = 0（自分の側が -Z）。面を越えて奥へ入ったら押す
                if (!_pressed[i])
                {
                    if (over && p.z >= -pressDepth && p.z < 0.05f)
                    {
                        _pressed[i] = true;
                        Press(i);
                    }
                }
                else if (!over || p.z < -releaseDistance)
                {
                    _pressed[i] = false;
                }
                SetShown(i, _pressed[i] ? 2 : over && p.z > -hoverDistance && p.z < 0.05f ? 1 : 0);
            }
        }

        void UpdateDesktop()
        {
            if (Input.GetKeyDown(desktopToggleKey)) Toggle();
            if (!_visible) return;
            // 座っている間は VRChat が移動のイベントを渡さないことがあるので、WASD も直接見る
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D))
            {
                SetVisible(false);
                return;
            }
            // 視点についてくる（画面の下に固定）
            var head = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            transform.SetPositionAndRotation(head.position + head.rotation * desktopOffset, head.rotation);
            for (int i = 0; i < buttons.Length && i < desktopKeys.Length; i++)
                if (desktopKeys[i] != "" && buttons[i] != null && buttons[i].gameObject.activeInHierarchy && Input.GetKeyDown(desktopKeys[i]))
                {
                    Press(i);
                    // 押したボタンでページが変わると、同じキーの別のボタンが同じフレームで押されてしまうので、1フレームに1つだけ
                    break;
                }
        }

        void SetShown(int i, int state)
        {
            if (_shown == null || buttonImages == null || i >= buttonImages.Length || buttonImages[i] == null || _shown[i] == state) return;
            _shown[i] = state;
            buttonImages[i].color = state == 2 ? pressColor : state == 1 ? hoverColor : normalColor;
        }

        /// <summary>処理の名前（と一覧の番号。-1 なら番号なし）でボタンを探して押す（自動確認から。ボタンの並びが変わっても壊れないように）。</summary>
        public void PressByName(string eventName, int argument)
        {
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null && buttons[i].eventName == eventName && buttons[i].argument == argument)
                {
                    Press(i);
                    return;
                }
            Debug.Log("[MmdWorld] タブレット: ボタンが無い " + eventName + " " + argument);
        }

        /// <summary>i 番目のボタンを押す（指・キー・自動確認から）。</summary>
        public void Press(int i)
        {
            if (i < 0 || i >= buttons.Length || buttons[i] == null) return;
            if (_vr) _local.PlayHapticEventInHand(PressingRight() ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left, 0.08f, 0.6f, 120f);
            Debug.Log("[MmdWorld] タブレット: " + buttons[i].eventName);
            buttons[i].Press();
        }
    }
}
