#!/usr/bin/env bash
# 仮想の HMD のドライバを Windows 機でビルドし、SteamVR に登録して、VR の確認用の設定にする。
#   tools/vrsim-hmd/install.sh          ビルド・登録・設定（SteamVR と VRChat は止める）
#   tools/vrsim-hmd/install.sh restore  SteamVR の設定を入れる前（実機の VR）に戻す
# 前提: Windows 機に Visual Studio Build Tools（C++）と VMT（C:\vmt_driver）。詳しくは README の「仮想の VR で確かめる」
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
source "$HERE/../../scripts/win.env"
: "${HOST:?}"
OPENVR_TAG=v2.15.6
WIN_SRC='C:/mmdhmd-src'
WIN_DRIVER='C:/mmdhmd'
ps() { ssh -o BatchMode=yes "$HOST" "pwsh -NoProfile -Command -"; }

if [ "${1:-}" = restore ]; then
  ps <<'PS'
Get-Process VRChat,vrmonitor,vrserver,vrcompositor,vrdashboard,vrwebhelper -EA SilentlyContinue | Stop-Process -Force
$cfg = "C:/Program Files (x86)/Steam/config/steamvr.vrsettings"
Copy-Item "$cfg.before-vmt" $cfg -Force
"SteamVR の設定を戻した"
PS
  exit 0
fi

# OpenVR のドライバ用ヘッダー（BSD-3-Clause）。リポジトリには入れず、ビルドのたびに取ってくる
mkdir -p "$HERE/include"
if [ ! -s "$HERE/include/openvr_driver.h" ]; then
  gh api "repos/ValveSoftware/openvr/contents/headers/openvr_driver.h?ref=$OPENVR_TAG" -H "Accept: application/vnd.github.raw" > "$HERE/include/openvr_driver.h"
fi
ssh "$HOST" "pwsh -NoProfile -Command \"New-Item -ItemType Directory -Force $WIN_SRC | Out-Null\""
scp -q -r "$HERE/src" "$HERE/include" "$HERE/mmdhmd" "$HOST:$WIN_SRC/"

ps <<PS
\$ErrorActionPreference = "Stop"
Get-Process VRChat,vrmonitor,vrserver,vrcompositor,vrdashboard,vrwebhelper -EA SilentlyContinue | Stop-Process -Force
\$vs = & "\${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not \$vs) { throw "Visual Studio Build Tools（C++）が無い" }
Set-Location '$WIN_SRC'
New-Item -ItemType Directory -Force mmdhmd/bin/win64 | Out-Null
cmd /c "\`"\$vs\\VC\\Auxiliary\\Build\\vcvars64.bat\`" >nul && cl /nologo /LD /EHsc /O2 /std:c++17 /utf-8 /Iinclude src\\driver.cpp /Fe:mmdhmd\\bin\\win64\\driver_mmdhmd.dll /Fo:mmdhmd\\bin\\win64\\ ws2_32.lib"
if (\$LASTEXITCODE -ne 0) { throw "ビルドに失敗" }
Start-Sleep 2
if (Test-Path '$WIN_DRIVER') { Remove-Item -Recurse -Force '$WIN_DRIVER' }
Copy-Item -Recurse mmdhmd '$WIN_DRIVER'
\$reg = "C:/Program Files (x86)/Steam/steamapps/common/SteamVR/bin/win64/vrpathreg.exe"
& \$reg adddriver '$WIN_DRIVER'

\$cfg = "C:/Program Files (x86)/Steam/config/steamvr.vrsettings"
if (-not (Test-Path "\$cfg.before-vmt")) { Copy-Item \$cfg "\$cfg.before-vmt" }
\$j = Get-Content \$cfg -Raw | ConvertFrom-Json -AsHashtable
\$j["steamvr"]["forcedDriver"] = "mmdhmd"
\$j["steamvr"]["activateMultipleDrivers"] = \$true
\$j["steamvr"]["enableHomeApp"] = \$false
\$j.Remove("TrackingOverrides")
if (\$j.ContainsKey("driver_null")) { \$j["driver_null"]["enable"] = \$false }
\$p = if (\$j.ContainsKey("power")) { \$j["power"] } else { @{} }
\$p["pauseCompositorOnStandby"] = \$false
\$p["turnOffScreensTimeout"] = 86400.0
\$j["power"] = \$p
\$j | ConvertTo-Json -Depth 20 | Set-Content \$cfg -Encoding utf8NoBOM
"入れた: \$((Get-Item '$WIN_DRIVER/bin/win64/driver_mmdhmd.dll').Length) バイト"
PS
