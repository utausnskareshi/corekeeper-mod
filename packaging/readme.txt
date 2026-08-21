===============================================================================
 Core Keeper スキン作成ツール
 Core Keeper Skin Maker
===============================================================================

Core Keeper の操作キャラクターの見た目を、好きな画像に差し替えるツールです。
A tool for replacing the look of your Core Keeper character with a picture of
your choosing.

これは Pugstorm / Core Keeper の公式製品ではありません。有志が作った非公式の
ツールです。本ツールについて、ゲームの公式サポートへ問い合わせないでください。
This is not an official Pugstorm or Core Keeper product. It is an unofficial,
fan-made tool. Please do not contact the game's official support about it.

配布元はここだけです。ソースコードも同じ場所で公開しています。
The one place this is published from, source code included:

  https://github.com/utausnskareshi/corekeeper-mod

ほかの場所で配られているものは、作者が用意したものではありません。
Anything handed out anywhere else did not come from the author.


-------------------------------------------------------------------------------
 はじめに / Before you begin
-------------------------------------------------------------------------------

このツールの使用は、すべて利用者ご自身の責任において行ってください。
作者は、本ツールおよび本ツールが導入する MOD の使用によって生じたいかなる損害
についても責任を負いません。セーブデータの破損、ゲームの動作不良、ゲームの
アップデートに伴う不具合、マルチプレイでの不都合などが起こり得ます。

重要なセーブデータは、使用前に必ず控えを取ってください。

You use this tool, and the mod it installs, entirely at your own risk. The
author accepts no liability for any damage arising from its use: save data can
be corrupted, the game can misbehave, a game update can break things, and
multiplayer sessions can be affected.

Back up any save you care about before you start.

同じ内容をアプリの初回起動時にも表示します。同意された場合のみ使用できます。
The same notice appears the first time you run the application, and you can
only proceed by accepting it.


-------------------------------------------------------------------------------
 動作環境 / Requirements
-------------------------------------------------------------------------------

  ・Windows 10 / 11 (64bit)
  ・Steam 版 Core Keeper がインストール済みであること

.NET や Unity、Visual Studio などをインストールする必要はありません。
必要なものはすべてこのフォルダに入っています。

  - Windows 10 / 11 (64-bit)
  - Core Keeper installed through Steam

No .NET, Unity or Visual Studio installation is required. Everything needed is
contained in this folder.


-------------------------------------------------------------------------------
 使い方 / How to use
-------------------------------------------------------------------------------

  1. この zip を、書き込みできる場所に展開します
     （デスクトップやドキュメントなど。ゲームのフォルダの中に置く必要は
     ありません）
  2. cks-gui.exe を実行します
  3. 画面上部の帯に「MOD が未導入です」と出ていたら「MOD を導入」を押します
  4. あとは画面の案内と、アプリ内の「ヘルプ」に従ってください

  1. Extract this zip somewhere you can write to (your desktop or documents
     folder is fine; it does not need to go inside the game folder)
  2. Run cks-gui.exe
  3. If the bar at the top says the mod is not installed, press "Install mod"
  4. From there, follow the screen and the built-in "Help"

くわしい説明はすべてアプリ内の「ヘルプ」にあります。
Full instructions are in the application's own "Help" window.


-------------------------------------------------------------------------------
 起動時に警告が出る場合 / If Windows warns you when starting it
-------------------------------------------------------------------------------

■ 「Windows によって PC が保護されました」と表示される

  ● なぜ出るのか

    このツールにはコード署名（発行元を証明する電子署名）を付けていません。
    署名の費用と更新を個人で負担しないためです。

    インターネットからダウンロードしたファイルには Windows が別の PC から
    来たという印を付けます。署名が無く、かつダウンロード数の少ない実行
    ファイルは、Windows SmartScreen が既定で実行を止めます。

    これは、このアプリが危険だと判定されたという意味ではありません。
    発行元を確認できなかった、という意味です。

  ● 対処その1（おすすめ）: 展開する前に zip のブロックを解除する

    先にこれをしておくと、以下の警告そのものが出なくなります。

      1. ダウンロードした zip ファイルを右クリック
      2. 「プロパティ」を選ぶ
      3. 「全般」タブのいちばん下に「セキュリティ: このファイルは他の
         コンピューターから取得したものです。…」と表示されていたら、
         その右にある「許可する」（環境により「ブロックの解除」）に
         チェックを入れる
      4. 「OK」を押してから、あらためて zip を展開する

    この表示が無ければ、すでにブロックされていないので何もしなくて構いません。

  ● 対処その2: 警告が出てしまった場合

      1. 「Windows によって PC が保護されました」の画面で「詳細情報」を
         クリックする（青い小さな文字です。押すまで実行ボタンは出ません）
      2. アプリ名と発行元（「不明な発行元」と出ます）の下に「実行」ボタンが
         現れるので、それを押す

    次回以降、同じファイルでは警告は出ません。

  ● どうしても不安な場合

    実行せずに、zip ファイルを VirusTotal（https://www.virustotal.com/）
    などのオンライン検査に掛けてからご判断ください。
    このツールは MOD をゲームのフォルダへ書き込むため、納得できないまま
    実行しないでください。

■ "Windows protected your PC"

  ● Why it happens

    This tool is not code-signed - there is no certificate proving who
    published it, because paying for and renewing one is not something an
    individual project takes on.

    Windows marks any file downloaded from the internet as having come from
    another computer. An unsigned executable that few people have downloaded
    is stopped by Windows SmartScreen by default.

    It does not mean the application was judged dangerous. It means the
    publisher could not be verified.

  ● Option 1 (recommended): unblock the zip before extracting it

    Doing this first stops the warning from appearing at all.

      1. Right-click the downloaded zip file
      2. Choose "Properties"
      3. At the bottom of the "General" tab, if it says "Security: This file
         came from another computer and might be blocked...", tick "Unblock"
         beside it
      4. Press "OK", then extract the zip

    If that line is not there, the file is not blocked and there is nothing
    to do.

  ● Option 2: if the warning has already appeared

      1. On the "Windows protected your PC" screen, click "More info"
         (small blue text - the run button does not appear until you do)
      2. A "Run anyway" button appears below the application name and
         publisher (which will read "Unknown publisher"). Press it.

    The warning will not appear again for the same file.

  ● If you would rather not take the risk

    Do not run it. Upload the zip to an online scanner such as VirusTotal
    (https://www.virustotal.com/) and decide from there. This tool writes a
    mod into your game folder, so please do not run it while unconvinced.

■ ウイルス対策ソフトが反応する / Your antivirus reacts

  必要なものを1つの実行ファイルにまとめた、署名の無いアプリという形のため、
  ウイルス対策ソフトに誤検知されることがあります。気になる場合は、この zip を
  VirusTotal などに掛けてからご判断ください。

  A single-file, unsigned application is a shape that antivirus products often
  flag by mistake. If you would rather check first, scan the zip with a service
  such as VirusTotal before running it.


-------------------------------------------------------------------------------
 ゲームが見つからないと言われる場合 / If it cannot find the game
-------------------------------------------------------------------------------

Steam のライブラリ設定を読み、登録されている全ライブラリの
steamapps\common\Core Keeper を調べても CoreKeeper.exe が見つからない場合、
起動時にその旨を表示して止まります。

その画面の「ゲームの場所を指定…」ボタンから、Core Keeper のインストール先
フォルダ（CoreKeeper.exe があるフォルダ）を直接指定してください。

If Steam's library list is read and no CoreKeeper.exe is found under
steamapps\common\Core Keeper in any registered library, the application says so
and stops at start-up.

Use the "Choose game folder…" button on that screen to point it at where Core
Keeper is installed - the folder that contains CoreKeeper.exe.


-------------------------------------------------------------------------------
 やめるとき / Removing it
-------------------------------------------------------------------------------

  1. キャラクターの一覧でチェックを入れてから、アプリの右下「ゲームから削除」を
     押します。チェックしたキャラクターの見た目が元に戻ります（1体もチェックして
     いないと、このボタンは押せません）
  2. アプリの右上「MOD を削除」で、ゲームから MOD 本体を取り除きます
  3. このフォルダごと削除します

設定はこのフォルダの中だけに保存されます（portable.txt があるため）。
上の1と2を済ませてからこのフォルダを消せば、パソコンには何も残りません。MOD 本体は
ゲームのフォルダ側にあるため、1と2を飛ばしてこのフォルダだけ消すと、ゲームに MOD が
残ったうえ、アプリから削除する手段も無くなります。

  1. "Remove from game" at the bottom right restores the original appearance of
     the ticked characters. Tick at least one, or the button stays disabled
  2. "Remove mod" at the top right takes the mod itself out of the game
  3. Delete this folder

Settings are kept inside this folder only (that is what portable.txt does), so
once steps 1 and 2 are done, deleting the folder leaves nothing behind. The mod
itself lives in the game folder, so deleting this folder alone would leave it
there with no way left to remove it.


-------------------------------------------------------------------------------
 同梱ファイル / What is in this folder
-------------------------------------------------------------------------------

  cks-gui.exe             本体。これを実行してください
                          The application. This is the one to run

  cks.exe                 同じ変換をコマンドラインから行うための道具です。
                          通常は使いません
                          A command-line tool doing the same conversion.
                          Not needed for normal use

  portable.txt            設定をこのフォルダ内に保存するための目印です。
                          削除しないでください
                          Marks this folder as the place to keep settings.
                          Please do not delete it

  LICENSE                 このツールの使用許諾
                          Licence for this tool

  THIRD-PARTY-NOTICES.md  利用しているライブラリの著作権表示
                          Copyright notices for the libraries used

  readme.txt              このファイル
                          This file
