"""仮想の VR（SteamVR の null ドライバ + Virtual Motion Tracker）の頭と手を OSC で動かす。Windows 機で動かす（VMT は 127.0.0.1:39570 で受ける）。

前提（README の「仮想の VR で確かめる」）:
  steamvr.vrsettings で forcedDriver = null、TrackingOverrides で VMT_0 → /user/head。
  VMT の setting.json で AddCompatibleControllerOnStartup = true（VMT_1 が左、VMT_2 が右の Index 互換コントローラーになる）。

座標は Unity と同じ左手系（+Y が上、+Z が前、メートル）。向きは yaw pitch roll（度）。

  python vrsim.py setup                        初回だけ: VMT の Room Matrix を単位行列にする
  python vrsim.py stand                        頭を 1.5m、両手を体の前に置く
  python vrsim.py pose head|left|right X Y Z [YAW PITCH ROLL]
  python vrsim.py grip left|right [回数]        グリップを握って離す（回数ぶん、0.15 秒おき）
  python vrsim.py trigger left|right            トリガーを引いて離す
  python vrsim.py stick left|right X Y [秒]     スティックを倒す（秒たったら戻す）
  python vrsim.py play 台本.txt                 1行に1つ、上のコマンドか「wait 秒」「move left|right X Y Z 秒 [YAW PITCH ROLL]」
"""
import math
import socket
import struct
import sys
import time

ADDR = ("127.0.0.1", 39570)
DEVICES = {"head": (0, 1), "left": (1, 5), "right": (2, 6)}  # index, enable（1: トラッカー、5/6: Index 互換の左右）
GRIP = 1  # Index 互換のとき、TriggerIndex 1 が /input/grip/value
_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
_pose = {"head": [0, 1.5, 0, 0, 0, 0], "left": [-0.2, 1.1, 0.3, 0, 0, 0], "right": [0.2, 1.1, 0.3, 0, 0, 0]}


def _pad(b):
    return b + b"\0" * (4 - len(b) % 4)


def send(address, *args):
    tags = ","
    data = b""
    for a in args:
        if isinstance(a, int):
            tags += "i"
            data += struct.pack(">i", a)
        elif isinstance(a, float):
            tags += "f"
            data += struct.pack(">f", a)
        else:
            tags += "s"
            data += _pad(str(a).encode())
    _sock.sendto(_pad(address.encode()) + _pad(tags.encode()) + data, ADDR)


def quat(yaw, pitch, roll):
    """Unity の Quaternion.Euler(pitch, yaw, roll)（Z→X→Y の順に回す）"""
    x, y, z = (math.radians(v) / 2 for v in (pitch, yaw, roll))
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    return (cy * sx * cz + sy * cx * sz, sy * cx * cz - cy * sx * sz, cy * cx * sz - sy * sx * cz, cy * cx * cz + sy * sx * sz)


def pose(name, x, y, z, yaw=0.0, pitch=0.0, roll=0.0):
    _pose[name] = [x, y, z, yaw, pitch, roll]
    index, enable = DEVICES[name]
    qx, qy, qz, qw = quat(yaw, pitch, roll)
    send("/VMT/Raw/Unity", index, enable, 0.0, float(x), float(y), float(z), float(qx), float(qy), float(qz), float(qw))


def refresh():
    for name, p in _pose.items():
        pose(name, *p)


def wait(seconds):
    """待つ間も姿勢を送り続ける（送らないと途切れたとみなされることがあるため）"""
    end = time.time() + seconds
    while time.time() < end:
        refresh()
        time.sleep(1 / 30)


def move(name, x, y, z, seconds, yaw=None, pitch=None, roll=None):
    start = list(_pose[name])
    goal = [x, y, z, start[3] if yaw is None else yaw, start[4] if pitch is None else pitch, start[5] if roll is None else roll]
    steps = max(1, int(seconds * 30))
    for k in range(1, steps + 1):
        t = k / steps
        _pose[name] = [a + (b - a) * t for a, b in zip(start, goal)]
        refresh()
        time.sleep(seconds / steps)


def grip(name, count=1):
    index = DEVICES[name][0]
    for _ in range(count):
        send("/VMT/Input/Trigger", index, GRIP, 0.0, 1.0)
        wait(0.08)
        send("/VMT/Input/Trigger", index, GRIP, 0.0, 0.0)
        wait(0.07)


def trigger(name):
    index = DEVICES[name][0]
    send("/VMT/Input/Trigger", index, 0, 0.0, 1.0)
    wait(0.1)
    send("/VMT/Input/Trigger", index, 0, 0.0, 0.0)


def stick(name, x, y, seconds=0.5):
    index = DEVICES[name][0]
    send("/VMT/Input/Joystick", index, 1, 0.0, float(x), float(y))
    wait(seconds)
    send("/VMT/Input/Joystick", index, 1, 0.0, 0.0, 0.0)


def run(words):
    cmd, rest = words[0], words[1:]
    f = [float(v) for v in rest[1:]] if rest else []
    if cmd == "stand":
        refresh()
    elif cmd == "pose":
        pose(rest[0], *f)
    elif cmd == "move":
        move(rest[0], f[0], f[1], f[2], f[3], *f[4:])
    elif cmd == "grip":
        grip(rest[0], int(f[0]) if f else 1)
    elif cmd == "trigger":
        trigger(rest[0])
    elif cmd == "stick":
        stick(rest[0], *f)
    elif cmd == "wait":
        wait(float(rest[0]))
    elif cmd == "setup":
        # VMT は Room Matrix が無いと動かない（/VMT/Out/Unavailable）。null の HMD のルームはそのままなので単位行列にする（setting.json に保存される）
        send("/VMT/SetRoomMatrix", 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0)
    elif cmd == "reset":
        send("/VMT/Reset")
    elif cmd == "play":
        for line in open(rest[0], encoding="utf-8"):
            line = line.split("#")[0].strip()
            if line:
                print(">", line, flush=True)
                run(line.split())
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    run(sys.argv[1:])
