# Core Keeper スキン作成ツール / Core Keeper Skin Maker

Core Keeper の操作キャラクターの見た目を、好きな画像に差し替える Windows 用ツールです。
A Windows tool that replaces the look of your Core Keeper character with a picture of your
choosing.

**[日本語](#日本語) · [English](#english)**

> [!IMPORTANT]
> これは Pugstorm / Core Keeper の公式製品ではありません。有志が作った非公式のツールです。本ツールについて、ゲームの公式サポートへ問い合わせないでください。
>
> This is not an official Pugstorm or Core Keeper product. It is an unofficial, fan-made tool.
> Please do not contact the game's official support about it.

---

## 日本語

### これは何か

操作キャラクターの見た目（体・髪・目・服・ズボン・防具）を、自分で用意した画像に差し替えます。
差し替えはキャラクター単位で、ゲームのセーブデータそのものには手を加えません。

| 構成 | 内容 |
|---|---|
| GUI (`cks-gui.exe`) | 画像の取り込み、ドット編集、ゲームへの配置までを行う本体 |
| CLI (`cks.exe`) | 同じ変換をコマンドラインから行う道具。通常は使いません |
| MOD (`src/mod`) | ゲーム側で絵を差し替える PugMod 製の MOD。C# ソースのまま配布し、ゲームが読み込み時にコンパイルします |

### 免責事項

> [!WARNING]
> このツール、および導入される MOD の使用は、すべて利用者ご自身の責任において行ってください。
> 作者はいかなる損害についても責任を負いません。セーブデータの破損、ゲームの動作不良、ゲームの
> アップデートに伴う不具合、マルチプレイでの不都合などが起こり得ます。
> **重要なセーブデータは、使用前に必ず控えを取ってください。**

同じ内容をアプリの初回起動時にも表示します。同意された場合のみ使用できます。

### 動作環境

- Windows 10 / 11 (64bit)
- Steam 版 Core Keeper（1.3.0.4-511d で動作を確認済み。対応する版は [`data/supported-versions.json`](data/supported-versions.json) に記載）
- .NET のインストールは不要（配布物は自己完結ビルド）

このツールの版の番号は、対応する Core Keeper の版に合わせています。現在の版 v1.3.0 は Core Keeper 1.3.0 系用です。

### 入手と使い方

1. [Releases](../../releases) から zip をダウンロードし、展開して `cks-gui.exe` を実行します
2. 画像を開く・プリセットを選ぶ・ゲームから取得する、のいずれかでシートを作ります
3. 必要ならドット単位で描き直します
4. 右側でキャラクターにチェックを入れ、「ゲームへ配置」を押します

zip に同梱の `readme.txt` には、アプリを使い始める前に必要なこと（SmartScreen の警告への対処、
ゲームが見つからない場合の手順）とやめるときの手順を書いています。使い方のすべてはアプリ内の
「ヘルプ」にあります。

### できること

- **画像から作る** — ドラッグ＆ドロップで取り込み、背景の透過、切り抜き位置、ドット絵化、輪郭線、
  歩行と攻撃の動き付けを設定できます
- **プリセットから作る** — 6 カテゴリ 30 種。18 種はこのプロジェクトの画像生成ツール
  （[cksgen](https://github.com/utausnskareshi/corekeeper-image-generator)、下記）で作った絵、
  12 種は配色のレシピからツールが描くもので、ゲームの絵は使っていません
- **ゲーム内の見た目を取り込む** — MOD を導入したキャラクターの現在の見た目を、編集画面へ取り込みます
- **ドット単位の編集** — 左右対称に描く、全コマに反映、元に戻す／やり直す、シート内の色から選ぶ
- **動きの確認** — 待機・歩行・攻撃・溜め・運転・着席、正面・右・左・背面
- **キャラクターごとの配置と削除** — キャラクターごとに別の画像を持てます
- **MOD の導入・更新・削除** — アプリから行えます。Unity や .NET の導入は不要です
- **日本語 / English** の切り替え

### 取り込む絵を AI で描くには（別のツール）

取り込む元の絵は、別のツール「Core Keeper スキン生成ツール (cksgen)」で、画像生成 AI に描かせることも
できます。cksgen は別のリポジトリで公開しています。

- cksgen: https://github.com/utausnskareshi/corekeeper-image-generator

cksgen が書き出す PNG（背景が透明な絵）は、このツールの「画像を開く…」でそのまま開けます。cksgen で横と
後ろの絵も描いた場合は、「向き別の絵（任意）」の「右向きの絵…」と「背面の絵…」で開きます（横の絵は右を
向いている必要があります）。cksgen の動作には NVIDIA の GPU が必要です。詳しくは cksgen の README を
ご覧ください。

### 仕組み

ゲームのプレイヤーは 234×156 ピクセル・39 コマのスプライトシートで描かれています。このツールは
同じ配置のシートを作り、MOD が実行時にキャラクターのシートを差し替えます。コマの位置やパーツの
矩形は、ゲームから実測した [`data/`](data) の JSON に基づきます。

### 制限事項（主なもの）

- 左向きの専用コマはありません。ゲームが右向きを左右反転して表示するため、文字やロゴなど左右
  非対称な絵は左を向いたときに鏡文字になります
- 絵は最終的に 16×19 ピクセル程度まで縮小されます。細かい模様や小さな文字は判別できません
- 動きを付けられるのは歩行（6 コマ）と攻撃（2 コマ）だけです。待機・構え・運転・着席は、ゲーム側が
  それぞれ 1 コマしか持たないため静止したままです
- 手に持つ武器・ツール・盾・釣り竿は変更できません
- 差し替えた見た目が見えるのは、自分の画面の自分のキャラクターだけです。マルチプレイでは、ほかの
  参加者から見た自分のキャラクターは、相手が同じ MOD を導入していても通常の見た目のままです
- ゲームがアップデートされると MOD が動作しなくなる場合があります
- Windows 版のみに対応します（ゲームと設定フォルダの検出が Windows の仕組みに依存するため）

すべての制限はアプリ内の「ヘルプ」に記載しています。

### ソースからのビルド

必要なもの: [.NET 9 SDK](https://dotnet.microsoft.com/download) の 9.0.300 以降（GUI が使う Avalonia の
コード生成は、それより前の SDK のコンパイラでは動きません）

```bash
dotnet build src/gui/CoreKeeperSkinTool.Gui.csproj -c Release
dotnet test src/tool.tests/CoreKeeperSkinTool.Tests.csproj
dotnet test src/gui.tests/CoreKeeperSkinTool.Gui.Tests.csproj
```

GUI をビルドすると、Avalonia がビルド時の匿名の利用統計を avaloniaui.net へ送ります（配布物の実行時には
送りません）。送らないようにするには、環境変数 `AVALONIA_TELEMETRY_OPTOUT=1` を設定してからビルドしてください。

MOD の同梱データ `build/mod-payload/CustomPlayerSkin.zip` はリポジトリに含みません。これが無い
ままビルドすると、MOD 導入機能が使えないアプリになります（ビルド時に警告が出ます）。テストは
同梱データを使う `ModPayloadTests`（8 件）と `ModPayloadLinkTests`（4 件）だけが失敗し、残る 911 件は通ります。作り直すには
Unity 6000.0.59f2 と、Pugstorm が配布している Core Keeper Mod SDK が必要です。SDK 自体もこの
リポジトリには含まないため、`CoreKeeperModSDK/` に自分で配置してください。

```powershell
scripts/setup-dev.ps1           # .NET SDK・Unity・AssetRipper の確認と導入
scripts/setup-mod-project.ps1   # src/mod を SDK のプロジェクトへ接続
scripts/build-mod.ps1           # Unity バッチビルド（約 5 分）
scripts/package-release.ps1     # 配布用 zip の作成
```

`build-mod.ps1` はゲームの場所を自動で探します。見つからない場合は `-GamePath` か環境変数
`CKS_GAME_PATH` で指定してください。なお SDK の仕様上、このスクリプトはビルドした MOD を
ゲームのフォルダへ直接インストールします。

### リポジトリの構成

| パス | 内容 |
|---|---|
| `src/core` | 共通ロジック（画像処理・シート生成・導入・ローカライズ） |
| `src/tool` | CLI (`cks`) |
| `src/gui` | GUI (`cks-gui`、Avalonia) |
| `src/mod` | ゲーム内 MOD（PugMod。C# ソースで配布） |
| `src/sdk-editor` | Unity のバッチビルドに使うエディタスクリプト |
| `src/tool.tests`, `src/gui.tests` | テスト（923 件） |
| `data` | ゲームから実測した座標、プリセットの配色と絵、画像生成用のプロンプト |
| `scripts` | 開発・ビルド・配布用の PowerShell |
| `packaging` | 配布物に同梱する readme |

### ゲームのアセットについて

このリポジトリにゲームの画像・音声・アセンブリは一切含みません。`data/` にあるのは矩形座標などの
実測値と、このプロジェクトで用意した配色レシピ・プリセットの絵・画像生成用のプロンプトだけです。
Core Keeper 本体とそのアセット・商標は Pugstorm に帰属します。

### ライセンス

MIT License（[LICENSE](LICENSE)）。配布物に同梱するライブラリの著作権表示は
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) にあります。

---

## English

### What this is

It replaces how your character looks - body, hair, eyes, shirt, trousers and armour - with a
picture you supply. The replacement is per character, and the game's save data itself is left
untouched.

| Part | What it is |
|---|---|
| GUI (`cks-gui.exe`) | The application: import a picture, edit it pixel by pixel, put it in the game |
| CLI (`cks.exe`) | A command-line tool doing the same conversion. Not needed for normal use |
| Mod (`src/mod`) | A PugMod mod that swaps the artwork in the game. Shipped as C# source and compiled by the game as it loads |

### Disclaimer

> [!WARNING]
> You use this tool, and the mod it installs, entirely at your own risk. The author accepts no
> liability for any damage arising from its use: save data can be corrupted, the game can
> misbehave, a game update can break things, and multiplayer sessions can be affected.
> **Back up any save you care about before you start.**

The same notice appears the first time you run the application, and you can only proceed by
accepting it.

### Requirements

- Windows 10 / 11 (64-bit)
- Core Keeper on Steam, checked with 1.3.0.4-511d. The versions it supports are listed in
  [`data/supported-versions.json`](data/supported-versions.json)
- No .NET installation: the released build is self-contained

The version number of this tool follows the Core Keeper version it is made for: the current
version, v1.3.0, is for Core Keeper 1.3.0.x.

### Getting it, and using it

1. Download the zip from [Releases](../../releases), extract it and run `cks-gui.exe`
2. Build a sheet by opening a picture, picking a preset, or fetching what is already in the game
3. Redraw pixels by hand if you want to
4. Tick a character on the right and press "Apply to game"

The `readme.txt` inside the zip covers what you need before you can start using the application -
the SmartScreen warning and what to do when the game cannot be found - and how to remove it.
Everything about using it is in the application's help.

### What it does

- **From a picture** - drag and drop, then choose how the background is removed, where the crop
  sits, how it is reduced to pixels, whether it gets an outline, and how it moves when walking
  or swinging
- **From a preset** - 30 characters in 6 categories: 18 are pictures made for this project with
  its companion image generator ([cksgen](https://github.com/utausnskareshi/corekeeper-image-generator),
  see below), and the tool draws the other 12 from colour recipes; none of the game's own artwork
  is involved
- **From the game** - fetch a character's current appearance into the editor
- **Pixel editing** - mirrored drawing, apply to every frame, undo and redo, pick from the
  colours already in the sheet
- **Preview** - idle, walk, swing, aim, drive and sit, facing down, right, left and up
- **Per character** - apply to and remove from each character separately; each can have its own
  picture
- **Install, update and remove the mod** from inside the application. Neither Unity nor .NET is
  needed
- **Japanese and English** interface

### Drawing the picture with AI (a separate tool)

The picture to import can also be drawn by an image-generation AI, with a separate tool: Core Keeper
Image Generator (cksgen), published in a repository of its own. Its interface is in Japanese only.

- cksgen: https://github.com/utausnskareshi/corekeeper-image-generator

The PNG that cksgen writes - with a transparent background - opens here with "Open image…" as it is.
When cksgen has also drawn the side and back pictures, open them with "Right-facing art…" and
"Back-facing art…" under "Art per facing (optional)"; the side picture has to face right. cksgen
needs an NVIDIA GPU; its README has the details.

### How it works

The game draws the player from a 234x156 pixel sheet of 39 frames. This tool builds a sheet with
the same layout, and the mod swaps the character's sheet in at run time. Frame positions and part
rectangles come from the JSON in [`data/`](data), measured from the game.

### Limitations

- There are no frames for facing left: the game mirrors the right-facing ones, so lettering and
  anything else asymmetric reads backwards when the character turns left
- The picture ends up around 16x19 pixels. Fine detail and small text will not survive
- Only walking (6 frames) and swinging (2 frames) can be animated. Idle, aim, drive and sit hold
  a single frame each in the game, so they stay still
- Held weapons, tools, shields and fishing rods cannot be changed
- The replaced look is shown only for your own character on your own screen. In multiplayer, the
  other players see your character looking ordinary, even if they have installed the same mod
- A game update can stop the mod from working
- Windows only: finding the game and its settings folder relies on how Windows does things

The application's help lists all of them.

### Building from source

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download), version 9.0.300 or later (the
code generator of Avalonia, which the GUI uses, does not run on the compiler of earlier SDKs).

```bash
dotnet build src/gui/CoreKeeperSkinTool.Gui.csproj -c Release
dotnet test src/tool.tests/CoreKeeperSkinTool.Tests.csproj
dotnet test src/gui.tests/CoreKeeperSkinTool.Gui.Tests.csproj
```

Building the GUI makes Avalonia send anonymous build-time usage statistics to avaloniaui.net (the
released application sends nothing when it runs). To opt out, set the environment variable
`AVALONIA_TELEMETRY_OPTOUT=1` before building.

The bundled mod, `build/mod-payload/CustomPlayerSkin.zip`, is not in the repository. Building
without it produces an application whose install-mod feature cannot work, and the build says so.
The tests that use that bundle - 8 in `ModPayloadTests` and 4 in `ModPayloadLinkTests` - fail
without it; the other 911 pass.
Rebuilding it needs Unity 6000.0.59f2 and the Core Keeper Mod SDK that Pugstorm distributes. The
SDK is not in this repository either: put it in `CoreKeeperModSDK/` yourself.

```powershell
scripts/setup-dev.ps1           # check for and install the .NET SDK, Unity and AssetRipper
scripts/setup-mod-project.ps1   # attach src/mod to the SDK project
scripts/build-mod.ps1           # Unity batch build, about 5 minutes
scripts/package-release.ps1     # build the release zip
```

`build-mod.ps1` searches for the game itself; pass `-GamePath` or set `CKS_GAME_PATH` when it
cannot find it. Note that the SDK builds a mod straight into the game folder, so this script
installs it there.

### Repository layout

| Path | Contents |
|---|---|
| `src/core` | Shared logic: image processing, sheet building, installation, localisation |
| `src/tool` | The CLI (`cks`) |
| `src/gui` | The GUI (`cks-gui`, Avalonia) |
| `src/mod` | The in-game mod (PugMod, shipped as C# source) |
| `src/sdk-editor` | Editor script used by the Unity batch build |
| `src/tool.tests`, `src/gui.tests` | Tests (923 of them) |
| `data` | Measurements taken from the game, the preset colour recipes and pictures, and the image-generator prompt |
| `scripts` | PowerShell for development, building and packaging |
| `packaging` | The readme that ships inside the zip |

### About the game's assets

This repository contains none of the game's artwork, audio or assemblies. What `data/` holds is
measurements - rectangles and coordinates - plus the colour recipes, preset pictures and
image-generator prompt prepared for this project. Core Keeper itself, its assets and its
trademarks belong to Pugstorm.

### Licence

MIT License, in [LICENSE](LICENSE). Copyright notices for the libraries that ship inside the
released binaries are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
