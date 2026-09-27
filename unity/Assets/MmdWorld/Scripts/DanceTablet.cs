using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace MmdWorld
{
    /// <summary>
    /// 手元に浮かぶタブレット。踊っている間（ステーションに固定されている間）でも、再生・停止・シークなどを操作できる。
    /// 自分の手元でだけ動かす（同期しない。ほかの人には、その人のタブレットがその人の手元に出る）。
    ///
    /// VR: 左手のグリップで出し入れする。出したときの「目の前の少し下」に、プレイエリアに対して固定する（踊りで体ごと動いたり回ったりしても、自分から見た位置は変わらない）。
    ///     踊っている間はアバターの手が踊りで動くので、アバターの指ではなく、実際の右手のコントローラーの位置から指先を割り出し、目印の球で示す。
    ///     指先でボタンの面を押し込むと押したことになり、振動で知らせる。
    /// デスクトップ: T キーで出し入れする。踊ると視点が回ってマウスでは狙えないので、画面の下に固定して、ボタンに書いたキーで押す。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DanceTablet : UdonSharpBehaviour
    {
        public DanceSystem system;
        [Tooltip("出し入れする見た目の親（ボタン・文字・板）")]
        public GameObject body;
        [Tooltip("タブレットのボタン。位置と大きさは body の中のローカル座標（メートル）")]
        public DanceButton[] buttons;
        public Vector2[] buttonSizes;
        [Tooltip("デスクトップで各ボタンを押すキー（Input.GetKeyDown に渡す名前: \"1\"、\"-\"、\"j\" など。空なら無し）。Udon は KeyCode の配列を扱えないので文字列で持つ")]
        public string[] desktopKeys;
        [Tooltip("ボタンのキーの表示（デスクトップのときだけ出す）")]
        public GameObject[] desktopKeyLabels;
        [Tooltip("VR で指先を示す小さな球")]
        public Transform pointer;
        public string desktopToggleKey = "t";

        [Header("置き方")]
        [Tooltip("VR: 出したとき、頭からどこに置くか（頭の向きの前・下）")]
        public Vector3 vrOffset = new Vector3(0f, -0.28f, 0.38f);
        [Tooltip("デスクトップ: 頭からどこに置くか（視点についてくる）")]
        public Vector3 desktopOffset = new Vector3(0f, -0.14f, 0.8f);
        [Tooltip("右手のコントローラーから指先までのずれ（コントローラーの向きのローカル座標、メートル）")]
        public Vector3 fingerOffset = new Vector3(0f, -0.02f, 0.07f);

        [Header("押し方")]
        [Tooltip("ボタンの面をこれだけ越えたら押したことにする（メートル）")]
        public float pressDepth = 0.004f;
        [Tooltip("面からこれだけ手前に戻したら離したことにする（メートル）")]
        public float releaseDistance = 0.025f;

        VRCPlayerApi _local;
        bool _vr;
        bool _visible;
        Vector3 _pinnedPosition;
        Quaternion _pinnedRotation;
        bool[] _pressed;

        void Start()
        {
            _local = Networking.LocalPlayer;
            if (!Utilities.IsValid(_local)) return;
            _vr = _local.IsUserInVR();
            _pressed = new bool[buttons.Length];
            foreach (var label in desktopKeyLabels)
                if (label != null) label.SetActive(!_vr);
            SetVisible(false);
        }

        public override void InputGrab(bool value, UdonInputEventArgs args)
        {
            if (!_vr || !value || args.handType != HandType.LEFT) return;
            Toggle();
        }

        /// <summary>出し入れする（タブレットの「閉じる」ボタンからも呼ぶ）。</summary>
        public void Toggle()
        {
            if (!_visible) Pin();
            SetVisible(!_visible);
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
                for (int i = 0; i < _pressed.Length; i++) _pressed[i] = false;
        }

        /// <summary>今の頭の前・下の位置を、プレイエリアの原点から見た位置として覚える。</summary>
        void Pin()
        {
            var head = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            var origin = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Origin);
            // 頭の左右の向きだけを使う（うつむいていても水平に置く）
            var forward = head.rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            var yaw = Quaternion.LookRotation(forward.normalized, Vector3.up);
            var position = head.position + yaw * vrOffset;
            // 画面が自分の方を向くように（ボタンの面は -Z、つまり自分の側）
            var rotation = Quaternion.LookRotation(position - head.position, Vector3.up);
            var inverse = Quaternion.Inverse(origin.rotation);
            _pinnedPosition = inverse * (position - origin.position);
            _pinnedRotation = inverse * rotation;
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

            // プレイエリアの原点についていく（ステーションごと体が動いたり回ったりしても、自分から見た位置は変わらない）
            var origin = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Origin);
            transform.SetPositionAndRotation(origin.position + origin.rotation * _pinnedPosition, origin.rotation * _pinnedRotation);

            var right = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);
            var tip = right.position + right.rotation * fingerOffset;
            if (pointer != null) pointer.position = tip;
            TouchButtons(transform.InverseTransformPoint(tip));
        }

        /// <summary>指先（タブレットのローカル座標）がボタンの面を押し込んだら押す。離れるまでは続けて押さない。</summary>
        void TouchButtons(Vector3 p)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                if (button == null) continue;
                var c = button.transform.localPosition;
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
            }
        }

        void UpdateDesktop()
        {
            if (Input.GetKeyDown(desktopToggleKey)) Toggle();
            if (!_visible) return;
            // 視点についてくる（画面の下に固定）
            var head = _local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            transform.SetPositionAndRotation(head.position + head.rotation * desktopOffset, head.rotation);
            for (int i = 0; i < buttons.Length && i < desktopKeys.Length; i++)
                if (desktopKeys[i] != "" && Input.GetKeyDown(desktopKeys[i])) Press(i);
        }

        /// <summary>i 番目のボタンを押す（指・キー・自動確認から）。</summary>
        public void Press(int i)
        {
            if (i < 0 || i >= buttons.Length || buttons[i] == null) return;
            if (_vr) _local.PlayHapticEventInHand(VRC_Pickup.PickupHand.Right, 0.08f, 0.6f, 120f);
            Debug.Log("[MmdWorld] タブレット: " + buttons[i].eventName);
            buttons[i].Press();
        }
    }
}
