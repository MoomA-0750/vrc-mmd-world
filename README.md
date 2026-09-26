# vrc-mmd-world

VRChat の MMD ワールド（曲に合わせて、参加した人のアバターが MMD のモーションで踊る）の土台。

- `.vmd` を Unity に置くと、Humanoid の AnimationClip として取り込まれる（MMD4Mecanim などの外部ツールは使わない）
- 「MMD World/ワールドを組み立てる」で、曲の一覧からステージ・踊る枠・ステーション・操作パネル・お手本の人形を作り直す
- 再生・停止・曲の選択と開始時刻だけを同期し、音・お手本・自分の踊りは各自の手元で時刻から合わせる

## 前提

- Unity 2022.3.22f1、VRChat SDK Worlds 3.10.5（UdonSharp・ClientSim 同梱）
- パッケージは `unity/Packages/vpm-manifest.json` から `vrc-get resolve` で入れる（`com.vrchat.*` はリポジトリに入れない）

## マネージャー

「MMD World/マネージャー」のウィンドウで、ワールドに入れるものをまとめて扱う。変えたら「ワールドを組み立て直す」でシーンに反映する。

| 項目 | できること |
|---|---|
| 曲 | `.vmd` と音声（wav / mp3 / ogg）をドロップすると曲が増える（エクスプローラーからでも Project からでもよい。`Assets/MmdWorld/Songs/<題名>/` に写す）。題名・音のずれの編集、並べ替え、削除。モーションと音声の長さが食い違うと警告する |
| お手本のアバター | Humanoid のモデルを登録すると、舞台の奥で曲に合わせて踊る。空なら付属の人形。VMD の表情のうちそのアバターにあるものの数も出る |
| 着替えの台 | VRChat にアップロード済みで公開（Public）のアバターの ID を登録すると、着替えの台が置かれる |
| ワールド | 踊る人の枠の数（1〜16）、カウントダウンの秒数 |

購入したアバターをお手本に入れてワールドを公開すると、多くの規約で再配布にあたる。公開するワールドでは、規約で許されたものだけを使う。

設定は `Assets/MmdWorld/Settings.asset` に入る。同じ操作は `MmdWorldLibrary`（`AddSong`・`RemoveSong`・`Move`・`CheckAvatar`）からも呼べる。

## 曲を足す（フォルダに置く方法）

1. `.vmd` と音声ファイルを、同じフォルダに入れて `unity/Assets/` のどこかに置く（1フォルダ1曲）
2. `MMD World/ワールドを組み立てる` を実行する
   - `.vmd` に対応する曲の設定（`DanceSong`）が無ければ、同じフォルダに作られる。音は、そのフォルダに音声が1つだけあれば自動で入る
   - 題名・音のずれ（`audioOffset`）・並び順（`order`）は `DanceSong` で直し、もう一度組み立てる
3. モーションの見た目が合わないときは、`.vmd` の Inspector で `armAngle`（元のモデルの腕の下がり具合）を変えて Apply する

シーン（`Assets/MmdWorld/Scenes/MmdWorld.unity`）は毎回作り直すので、手で置いたものは残らない。変えたいところは `Assets/MmdWorld/Editor/WorldBuilder.cs` の方を直す。

## しくみ

| 部分 | ファイル | 中身 |
|---|---|---|
| VMD の読み込み | `Vmd/Editor/VmdReader.cs` | ボーン・モーフ・IK の有効/無効を読む。カメラ・照明は読み飛ばす |
| MMD の姿勢 | `Vmd/Editor/MmdSkeleton.cs`, `MmdPoseSolver.cs` | 標準ボーン（全ての親〜指、腰キャンセル・肩C の回転付与）に VMD を当て、足IK・つま先IK を解く |
| Humanoid へ | `Vmd/Editor/MmdHumanoidRig.cs`, `VmdHumanoidBaker.cs` | 同じ寸法の T ポーズのリグへ姿勢を写し、HumanPoseHandler でマッスル値にして 30fps で焼く。足の IK の目標（LeftFootT/Q など）も焼き、ステートの Foot IK で体格の違うアバターでも足が MMD の位置に着くようにする。表情は `Body` の BlendShape のカーブにする |
| 取り込み | `Vmd/Editor/VmdImporter.cs` | `.vmd` 用の ScriptedImporter |
| ギミック（Udon） | `Scripts/DanceSystem.cs`, `DanceSlot.cs`, `DanceButton.cs` | 再生の同期、踊る枠、ボタン |
| 組み立て | `Editor/WorldBuilder.cs`, `MannequinBuilder.cs` | シーン・ステーション用の Animator・お手本の人形を作る |
| 管理 | `Editor/MmdWorldManagerWindow.cs`, `MmdWorldLibrary.cs`, `MmdWorldSettings.cs` | マネージャーのウィンドウと、その操作の本体。`LibrarySelfTest` が batchmode で操作を一通り試す |
| 外からの操作 | `Editor/DevCommands.cs` | 開いているエディタの `Temp/MmdCommand.txt` に処理名（BuildWorld・PlayCheckPreview・BuildAndTest・BuildAndTestAuto・OpenManager など）を書くと実行する |
| 動作確認 | `Editor/PlayModeCheck.cs` | ClientSim の Play モードで「枠に入る → 再生」を自動で行い、様子をログに出す。客席側から舞台を 1.5 秒おきに撮って `unity/Temp/MmdPlayCheck/` に書き出す |

### 踊らせ方

VRChat では、ステーションに座った人のアバターを、そのステーションの Animator Controller で動かせる。ところが Udon からはステーションの Animator Controller を差し替えられないので、**枠の数 × 曲の数**だけステーションを並べておき、曲が始まる瞬間に、枠を取っている人をその曲のステーションへ座らせる。

- ステーションのアニメーションは座った瞬間から始まり、途中へ飛ばせない。このため、始まってから `lateSeatWindow`（0.5秒）より後に来た人は、その曲では踊れない
- ほかの人の踊りは VRChat のアバターの同期で見えるので、少し遅れて見える
- 表情は、アバターの顔のメッシュが `Body` という名前で、MMD の表情名（あ・い・う・まばたき など）のシェイプキーを持っていれば動く（いわゆる MMD 対応アバター）。FX レイヤーの Write Defaults などの設定によっては動かない

### 焼き方を変えたら

Unity は ScriptedImporter の版番号が変わらないと `.vmd` を取り込み直さない。`VmdHumanoidBaker`・`MmdPoseSolver` などの焼き方を変えたら、`VmdImporter.Version` を上げる（上げないと、シーンは古いクリップのまま動く）。

## 手元のアバターで試す

購入したアバターなど再配布できないものは `unity/Assets/LocalOnly/` に FBX を置く（リポジトリには入らない）。置いたモデルは Humanoid として取り込まれ、次の2か所で使われる。

- テスト：`FootGoalTests.RealAvatar_FeetSlideLessWithFootIk` が、足の滑り・沈み込みと、VMD の表情のうちアバターにあるものの数を記録する（置いていなければ飛ばす）
- Play モードの確認：踊りが始まる瞬間に枠2から順に置かれ、ステーション用の Animator で踊る（VRChat でステーションに座ったときの再現）

## テスト用の素材

`Assets/MmdWorld/Songs/Wavefile/` に次の2つを置いている。

- `wavefile_v2.vmd`：hino 氏の WAVEFILE のモーション。three.js のサンプル（r171）に同梱されていたもの。規約は同じフォルダの `readme_wavefile.txt` にあり、改変・再配布は自由、営利目的の利用は連絡が必要。曲（ラマーズP）の音源は入っていない
- `click_120bpm.wav`：`scripts/make_click_track.py` で作ったクリック音。曲の代わりに、同期を確かめるために使う

## Windows で動かす（開発の流れ）

正本はこの Linux 側のリポジトリ。Unity は Windows 機で動かす。

機械ごとの値（ssh のホスト名・Windows 側の置き場所など）は `scripts/win.env` に書く（`scripts/win.env.example` を写す。リポジトリには入らない）。

```sh
scripts/win.sh setup     # 初回だけ
scripts/win.sh push      # 送る
scripts/win.sh resolve   # VPM パッケージを入れる
scripts/win.sh test      # EditMode のテスト（結果は out/）
scripts/win.sh exec MmdWorld.EditorTools.WorldBuilder.BuildFromCommandLine   # ワールドを組み立てる
scripts/win.sh pull      # Unity が作ったファイル（.meta・生成物）を取ってくる
scripts/wincua.py shot out/screen.png   # Windows の画面を撮る
```

batchmode と、画面付きの Unity を同時に開くことはできない（同じプロジェクトを2つ開けない）。

## ライセンス

MIT（`LICENSE`）。ただし次のものは除く。

- `unity/Assets/MmdWorld/Songs/Wavefile/wavefile_v2.vmd`：hino 氏のモーション。同じフォルダの `readme_wavefile.txt` の規約に従う（改変・再配布は自由、営利目的の利用は作者への連絡が必要）
- VRChat SDK・UdonSharp などのパッケージ：それぞれの規約に従う（リポジトリには入れず、`vrc-get resolve` で入れる）
