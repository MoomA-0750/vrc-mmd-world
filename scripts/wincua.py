#!/usr/bin/env python3
"""Windows 機の画面を cua computer-server の HTTP API で見る・操作する。動作確認用。

  wincua.py shot 出力.png [縮小率]      画面を撮る（既定は 1/2 に縮小）
  wincua.py click X Y [right]           画面の実座標でクリック
  wincua.py dclick X Y
  wincua.py type 文字列
  wincua.py key キー [キー...]          例: key ctrl p
  wincua.py run "コマンド"              ログオン中の画面側（セッション1）でコマンドを動かす
"""
import base64
import io
import json
import os
import sys
import urllib.request

def _url():
    # 画面操作サーバーの場所は環境変数 WINCUA_URL か scripts/win.env の WINCUA_URL で渡す
    url = os.environ.get("WINCUA_URL")
    env = os.path.join(os.path.dirname(os.path.abspath(__file__)), "win.env")
    if not url and os.path.exists(env):
        for line in open(env, encoding="utf-8"):
            if line.startswith("WINCUA_URL="):
                url = line.split("=", 1)[1].strip().strip("'\"")
    if not url:
        raise SystemExit("WINCUA_URL が決まっていません（scripts/win.env に書く）")
    return url.rstrip("/") + "/cmd"


URL = None


def cmd(_command, **params):
    req = urllib.request.Request(URL or _url(), data=json.dumps({"command": _command, "params": params}).encode(),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as r:
        body = r.read().decode()
    for line in body.splitlines():
        if line.startswith("data: "):
            result = json.loads(line[6:])
            if not result.get("success", True):
                raise SystemExit(f"{_command} に失敗: {result.get('error')}")
            return result
    raise SystemExit("応答が読めない: " + body[:300])


def main():
    a = sys.argv[1:]
    if not a:
        print(__doc__)
        return
    op = a[0]
    if op == "shot":
        r = cmd("screenshot")
        data = base64.b64decode(r["image_data"])
        scale = float(a[2]) if len(a) > 2 else 0.5
        try:
            from PIL import Image
            img = Image.open(io.BytesIO(data))
            if scale != 1.0:
                img = img.resize((int(img.width * scale), int(img.height * scale)))
            img.save(a[1])
        except ImportError:
            open(a[1], "wb").write(data)
        print(a[1])
    elif op == "click":
        cmd("right_click" if len(a) > 3 and a[3] == "right" else "left_click", x=int(a[1]), y=int(a[2]))
    elif op == "dclick":
        cmd("double_click", x=int(a[1]), y=int(a[2]))
    elif op == "type":
        cmd("type_text", text=a[1])
    elif op == "key":
        if len(a) > 2:
            cmd("hotkey", keys=a[1:])
        else:
            cmd("press_key", key=a[1])
    elif op == "run":
        r = cmd("run_command", command=a[1])
        print(r.get("stdout", ""), r.get("stderr", ""), sep="\n")
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
