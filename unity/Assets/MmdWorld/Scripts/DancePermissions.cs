using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace MmdWorld
{
    /// <summary>
    /// 操作の権限（決定 2026-10-14。イメージは YamaPlayer の権限の仕組み）。
    /// 権限は3つ: 見るだけ（再生などを操作できない）・操作（再生・停止・シーク・範囲・曲の選択・ワールドのアバターを置く）・管理（操作に加えて、ほかの人の権限と初期の権限を変えられる）。
    /// インスタンスのマスターはいつも管理。ほかの人は、管理の人が決めた権限か、決めていなければ初期の権限（初めは操作）。
    /// 自分が枠に入って踊る・その場・プレビュー（自分の手元だけ）は、権限に関係なく誰でもできる。
    /// 同期するのは「権限を決めた人の playerId と権限」と「初期の権限」だけ。書くのは管理の人（書くときに持ち主になる）。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DancePermissions : UdonSharpBehaviour
    {
        public const int Viewer = 0;
        public const int Operator = 1;
        public const int Manager = 2;

        public DanceSystem system;

        [Tooltip("権限を決めていない人の権限（0: 見るだけ、1: 操作、2: 管理）。初めは操作")]
        [UdonSynced] public int defaultLevel = Operator;
        [UdonSynced] int[] _ids = new int[0];
        [UdonSynced] int[] _levels = new int[0];

        /// <summary>その人の権限。マスターはいつも管理。</summary>
        public int LevelOf(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player)) return Viewer;
            if (player.isMaster) return Manager;
            for (int i = 0; i < _ids.Length; i++)
                if (_ids[i] == player.playerId) return _levels[i];
            return defaultLevel;
        }

        public bool CanOperate(VRCPlayerApi player)
        {
            return LevelOf(player) >= Operator;
        }

        public bool CanManage(VRCPlayerApi player)
        {
            return LevelOf(player) >= Manager;
        }

        public bool LocalCanOperate()
        {
            return CanOperate(Networking.LocalPlayer);
        }

        public bool LocalCanManage()
        {
            return CanManage(Networking.LocalPlayer);
        }

        public string LevelName(int level)
        {
            return level >= Manager ? "管理" : level >= Operator ? "操作できる" : "見るだけ";
        }

        /// <summary>その人の権限を次へ送る（操作 → 見るだけ → 管理 → 操作）。自分が管理でなければ、マスターには何もしない。</summary>
        public void CycleLevel(VRCPlayerApi player)
        {
            if (!LocalCanManage() || !Utilities.IsValid(player) || player.isMaster) return;
            int level = LevelOf(player);
            int next = level == Operator ? Viewer : level == Viewer ? Manager : Operator;
            SetLevel(player.playerId, next);
            Debug.Log("[MmdWorld] 権限: " + player.displayName + " を " + LevelName(next) + " にした");
        }

        /// <summary>初期の権限（決めていない人の権限）を、操作と見るだけで切り替える。管理の人だけ。</summary>
        public void ToggleDefault()
        {
            if (!LocalCanManage()) return;
            TakeOwnership();
            defaultLevel = defaultLevel >= Operator ? Viewer : Operator;
            Debug.Log("[MmdWorld] 権限: 初期を " + LevelName(defaultLevel) + " にした");
            Commit();
        }

        void SetLevel(int playerId, int level)
        {
            TakeOwnership();
            for (int i = 0; i < _ids.Length; i++)
                if (_ids[i] == playerId)
                {
                    _levels[i] = level;
                    Commit();
                    return;
                }
            var ids = new int[_ids.Length + 1];
            var levels = new int[_levels.Length + 1];
            for (int i = 0; i < _ids.Length; i++)
            {
                ids[i] = _ids[i];
                levels[i] = _levels[i];
            }
            ids[_ids.Length] = playerId;
            levels[_ids.Length] = level;
            _ids = ids;
            _levels = levels;
            Commit();
        }

        /// <summary>抜けた人の分を消す（playerId は使い回されないが、表が伸び続けないように）。持ち主だけが書く。</summary>
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (Networking.IsOwner(gameObject) && Utilities.IsValid(player))
            {
                int keep = 0;
                for (int i = 0; i < _ids.Length; i++)
                    if (_ids[i] != player.playerId) keep++;
                if (keep != _ids.Length)
                {
                    var ids = new int[keep];
                    var levels = new int[keep];
                    int at = 0;
                    for (int i = 0; i < _ids.Length; i++)
                    {
                        if (_ids[i] == player.playerId) continue;
                        ids[at] = _ids[i];
                        levels[at] = _levels[i];
                        at++;
                    }
                    _ids = ids;
                    _levels = levels;
                    RequestSerialization();
                }
            }
            if (system != null) system.RefreshLists();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (system != null) system.RefreshLists();
        }

        public override void OnDeserialization()
        {
            if (system != null) system.RefreshLists();
        }

        void TakeOwnership()
        {
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }

        void Commit()
        {
            RequestSerialization();
            if (system != null) system.RefreshLists();
        }
    }
}
