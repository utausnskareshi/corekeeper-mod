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
- Steam 版 Core Keeper（動作を確認したバージョンは [`data/supported-versions.json`](data/supported-versions.json) に記載。現在は 1.2.1）
- .NET のインストールは不要（配布物は自己完結ビルド）

### 入手と使い方

1. [Releases](../../releases) から zip をダウンロードし、展開して `cks-gui.exe` を実行します
2. 画像を開く・プリセットを選ぶ・ゲームから取得する、のいずれかでシートを作ります
3. 必要ならドット単位で描き直します
4. 右側でキャラクターにチェックを入れ、「ゲームへ配置」を押します

zip に同梱の `readme.txt` に、SmartScreen の警告への対処やゲームが見つからない場合の手順まで含めた
詳しい説明があります。アプリ内の「ヘルプ」にも同じ内容があります。

### できること

- **画像から作る** — ドラッグ＆ドロップで取り込み、背景の透過、切り抜き位置、ドット絵化、輪郭線、
  歩行と攻撃の動き付けを設定できます
- **プリセットから作る** — 6 カテゴリ 30 種。すべてこのプロジェクトで描いたもので、ゲームの絵は
  使っていません
- **ゲーム内の見た目を取り込む** — MOD を導入したキャラクターの現在の見た目を、編集画面へ取り込みます
- **ドット単位の編集** — 左右対称に描く、全コマに反映、元に戻す／やり直す、シート内の色から選ぶ
- **動きの確認** — 待機・歩行・攻撃・溜め・運転・着席、正面・右・左・背面
- **キャラクターごとの配置と削除** — キャラクターごとに別の画像を持てます
- **MOD の導入・更新・削除** — アプリから行えます。Unity や .NET の導入は不要です
- **日本語 / English** の切り替え

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
- マルチプレイでは、参加者全員が同じ MOD と同じ画像を導入している必要があります。導入していない人
  からは通常の見た目に見えます
- ゲームがアップデートされると MOD が動作しなくなる場合があります
- Windows 版のみに対応します（ゲームと設定フォルダの検出が Windows の仕組みに依存するため）

すべての制限はアプリ内の「ヘルプ」に記載しています。

### ソースからのビルド

必要なもの: [.NET 9 SDK](https://dotnet.microsoft.com/download)

```bash
dotnet build src/gui/CoreKeeperSkinTool.Gui.csproj -c Release
dotnet test src/tool.tests/CoreKeeperSkinTool.Tests.csproj
dotnet test src/gui.tests/CoreKeeperSkinTool.Gui.Tests.csproj
```

MOD の同梱データ `build/mod-payload/CustomPlayerSkin.zip` はリポジトリに含みません。これが無い
ままビルドすると、MOD 導入機能が使えないアプリになります（ビルド時に警告が出ます）。テストは
同梱データを検証する `ModPayloadTests` の 8 件だけが失敗し、残る 508 件は通ります。作り直すには
Unity 6000.0.59f2 と、Pugstorm が配布している Core Keeper Mod SDK が必要です。SDK 自体もこの
リポジトリには含まないため、`CoreKeeperModSDK/` に自分で配置してください。

```powershell
scripts/setup-dev.ps1           # .NET SDK・Unity・AssetRipper の確認と導入
scripts/setup-mod-project.ps1   # src/mod を SDK のプロジェクトへ接続
scripts/build-mod.ps1           # Unity バッチビルド（約 4 分）
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
| `src/tool.tests`, `src/gui.tests` | テスト（516 件） |
| `data` | ゲームから実測した座標と、プリセットの配色 |
| `scripts` | 開発・ビルド・配布用の PowerShell |
| `packaging` | 配布物に同梱する readme |

### ゲームのアセットについて

このリポジトリにゲームの画像・音声・アセンブリは一切含みません。`data/` にあるのは矩形座標などの
実測値と、このプロジェクトで書いた配色レシピだけです。Core Keeper 本体とそのアセット・商標は
Pugstorm に帰属します。

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
- Core Keeper on Steam. The versions this has been checked against are listed in
  [`data/supported-versions.json`](data/supported-versions.json) - currently 1.2.1
- No .NET installation: the released build is self-contained

### Getting it, and using it

1. Download the zip from [Releases](../../releases), extract it and run `cks-gui.exe`
2. Build a sheet by opening a picture, picking a preset, or fetching what is already in the game
3. Redraw pixels by hand if you want to
4. Tick a character on the right and press "Apply to game"

The `readme.txt` inside the zip goes through everything in detail, including the SmartScreen
warning and what to do when the game cannot be found. The same text is in the application's help.

### What it does

- **From a picture** - drag and drop, then choose how the background is removed, where the crop
  sits, how it is reduced to pixels, whether it gets an outline, and how it moves when walking
  or swinging
- **From a preset** - 30 characters in 6 categories, all drawn for this project; none of the
  game's own artwork is involved
- **From the game** - fetch a character's current appearance into the editor
- **Pixel editing** - mirrored drawing, apply to every frame, undo and redo, pick from the
  colours already in the sheet
- **Preview** - idle, walk, swing, aim, drive and sit, facing down, right, left and up
- **Per character** - apply to and remove from each character separately; each can have its own
  picture
- **Install, update and remove the mod** from inside the application. Neither Unity nor .NET is
  needed
- **Japanese and English** interface

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
- In multiplayer everyone needs the same mod and the same picture. To anyone without them, your
  character looks ordinary
- A game update can stop the mod from working
- Windows only: finding the game and its settings folder relies on how Windows does things

The application's help lists all of them.

### Building from source

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build src/gui/CoreKeeperSkinTool.Gui.csproj -c Release
dotnet test src/tool.tests/CoreKeeperSkinTool.Tests.csproj
dotnet test src/gui.tests/CoreKeeperSkinTool.Gui.Tests.csproj
```

The bundled mod, `build/mod-payload/CustomPlayerSkin.zip`, is not in the repository. Building
without it produces an application whose install-mod feature cannot work, and the build says so.
The 8 tests in `ModPayloadTests`, which check that bundle, fail without it; the other 508 pass.
Rebuilding it needs Unity 6000.0.59f2 and the Core Keeper Mod SDK that Pugstorm distributes. The
SDK is not in this repository either: put it in `CoreKeeperModSDK/` yourself.

```powershell
scripts/setup-dev.ps1           # check for and install the .NET SDK, Unity and AssetRipper
scripts/setup-mod-project.ps1   # attach src/mod to the SDK project
scripts/build-mod.ps1           # Unity batch build, about 4 minutes
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
| `src/tool.tests`, `src/gui.tests` | Tests (516 of them) |
| `data` | Measurements taken from the game, and the preset colour recipes |
| `scripts` | PowerShell for development, building and packaging |
| `packaging` | The readme that ships inside the zip |

### About the game's assets

This repository contains none of the game's artwork, audio or assemblies. What `data/` holds is
measurements - rectangles and coordinates - and colour recipes written for this project. Core
Keeper itself, its assets and its trademarks belong to Pugstorm.

### Licence

MIT License, in [LICENSE](LICENSE). Copyright notices for the libraries that ship inside the
released binaries are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
