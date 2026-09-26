#!/usr/bin/env bash
# Windows 機の Unity でこのプロジェクトを動かす。正本はこの Linux 側のリポジトリ。
#
#   win.sh setup            Windows 側に受け取り用のリポジトリを作る（初回だけ）
#   win.sh push             いまの HEAD を Windows 側へ送る（Windows 側の作業ツリーも更新される）
#   win.sh resolve          Windows 側で VPM パッケージを入れる
#   win.sh test             EditMode のテストを batchmode で流し、結果を out/ に取ってくる
#   win.sh exec <メソッド>  batchmode で -executeMethod を実行し、ログを out/ に取ってくる
#   win.sh pull             Unity が Windows 側で作ったファイル（.meta・生成物）を Linux 側の作業ツリーへ持ってくる
set -euo pipefail

REPO=$(cd "$(dirname "$0")/.." && pwd)
# 機械ごとの値（ssh のホスト名・Windows 側の置き場所・一時フォルダ）は scripts/win.env に書く（リポジトリには入れない）。
# 雛形は scripts/win.env.example
if [ ! -f "$REPO/scripts/win.env" ]; then
  echo "scripts/win.env がありません。scripts/win.env.example を写して書き換えてください" >&2; exit 1
fi
# shellcheck source=/dev/null
. "$REPO/scripts/win.env"
: "${HOST:?}" "${WIN_DIR:?}" "${WIN_TEMP:?}"
UNITY=${UNITY:-'C:/Program Files/Unity/Hub/Editor/2022.3.22f1/Editor/Unity.exe'}
OUT="$REPO/out"
mkdir -p "$OUT"

ps() { ssh -o BatchMode=yes "$HOST" "pwsh -NoProfile -Command -"; }

ensure_remote() {
  if ! git -C "$REPO" remote get-url win >/dev/null 2>&1; then
    git -C "$REPO" remote add win "$HOST:$WIN_DIR"
  fi
  # Windows の既定シェルは PowerShell なので、git-receive-pack ではなく git receive-pack として呼ばせる
  git -C "$REPO" config remote.win.receivepack 'git receive-pack'
  git -C "$REPO" config remote.win.uploadpack 'git upload-pack'
}

unity_batch() { # $1: ログ名 残り: Unity の引数（PowerShell の書き方で）
  local name=$1; shift
  ps <<PS
\$log = "\$env:TEMP\\vrc-mmd-world-$name.log"
\$p = Start-Process -FilePath '$UNITY' -ArgumentList @('-batchmode','-nographics','-projectPath','$WIN_DIR/unity','-logFile',\$log,$*) -Wait -PassThru -NoNewWindow
"exit=\$(\$p.ExitCode)"
PS
  scp -q "$HOST:$WIN_TEMP/vrc-mmd-world-$name.log" "$OUT/$name.log" || true
}

case "${1:-}" in
  setup)
    ps <<PS
New-Item -ItemType Directory -Force '$WIN_DIR' | Out-Null
Set-Location '$WIN_DIR'
if (-not (Test-Path .git)) { git init -q -b main }
git config receive.denyCurrentBranch updateInstead
"ok"
PS
    ensure_remote
    ;;
  push)
    ensure_remote
    git -C "$REPO" push -f win HEAD:main
    ;;
  resolve)
    ps <<PS
Set-Location '$WIN_DIR/unity'
vrc-get resolve
PS
    ;;
  test)
    unity_batch tests "'-runTests','-testPlatform','EditMode','-assemblyNames','MmdWorld.Vmd.Tests','-testResults','$WIN_DIR/out/tests.xml'"
    scp -q "$HOST:$WIN_DIR/out/tests.xml" "$OUT/tests.xml" && python3 - "$OUT/tests.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print("結果:", root.get("result"), "成功", root.get("passed"), "失敗", root.get("failed"), "全", root.get("total"))
for case in root.iter("test-case"):
    if case.get("result") != "Passed":
        msg = case.find("failure/message")
        print("  ✗", case.get("fullname"), (msg.text or "").strip()[:500] if msg is not None else "")
for out in root.iter("output"):
    for line in (out.text or "").splitlines():
        if line.startswith("[VmdTests]"): print(" ", line)
PY
    ;;
  exec)
    unity_batch "exec" "'-executeMethod','$2'"
    grep -E '^\[MmdWorld\]|error CS|Exception|exit=' "$OUT/exec.log" | head -50 || true
    ;;
  pull)
    ensure_remote
    # Windows 側の変更をいったんコミットして取ってきて、Linux 側では作業ツリーの変更として受け取る
    ps <<PS
Set-Location '$WIN_DIR'
git add -A
if (git status --porcelain) { git -c user.name=win-sync -c user.email=win-sync@localhost commit -q -m 'wip: generated on Windows' ; 'committed' } else { 'clean' }
PS
    git -C "$REPO" fetch -q win main
    # Windows 側で新しく変わったファイル（wip コミットの中身）だけを受け取る。
    # 作業ツリーごと取ると、Linux 側で先に進めた変更を Windows 側の古い版で上書きしてしまう
    # 件名の日本語は PowerShell を通ると化けるので、作者名で見分ける
    if [ "$(git -C "$REPO" log -1 --format=%an win/main)" != 'win-sync' ]; then
      echo "Windows 側に新しいファイルは無い"; exit 0
    fi
    git -C "$REPO" diff --stat win/main~1 win/main
    git -C "$REPO" diff --name-only --diff-filter=AM -z win/main~1 win/main | xargs -0 -r git -C "$REPO" checkout win/main --
    ;;
  *)
    sed -n 2,11p "$0"; exit 1 ;;
esac
