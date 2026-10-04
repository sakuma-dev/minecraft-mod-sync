# 開発環境のセットアップと確認

更新日：2026-10-03。Issue #11の開発基盤は.NET SDK **10.0.401**とWPFを使う。対象はWindows 11の64ビット環境。現在の導入スクリプトとCIはx64 SDKを使い、ARM64 PCでの追試はまだ行っていない。

2026-10-03のユーザー承認により、#11はこのPCでの確認と対象版のCI成功を完了条件とする。2台目のPCは未確保で、確認は未実施。[独立Issue #16](https://github.com/sakuma-dev/minecraft-mod-sync/issues/16)の残件として管理し、#11や後続の本実装を止める条件にはしない。CIの成功を別PCでの確認の代わりにはしない。

## 前提と取得

Git、Windows PowerShell 5.1以降、MicrosoftのSDK配布先とnuget.orgへ接続できる環境が必要。Visual Studioのインストールは必須にしない。以下はこのリポジトリのルートで、順番に実行する。

別PCの追試では、既存の作業と分けた新しいフォルダーに取得する。PR #14のマージ後も使えるよう、検証対象のコミットSHAをIssue #16で決め、固定して取得する。

```powershell
git clone https://github.com/sakuma-dev/minecraft-mod-sync.git
cd minecraft-mod-sync
$verificationCommit = 'Issue #16で指定したコミットSHAに置き換える'
git fetch origin
git switch --detach $verificationCommit
git rev-parse HEAD
```

`$verificationCommit`を実際のSHAへ置き換え、最後のコミットIDが一致することを確認して結果に記録する。マージ前のPR #14を追試する場合に限り、`git fetch origin pull/14/head`で取得し、`git switch --detach FETCH_HEAD`で切り替えた後にSHAを記録してもよい。このcheckoutは追試用であり、実装を始める際は[開発の進め方](../CONTRIBUTING.md)に従って作業ブランチを作る。

## 1. SDKを準備する

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup.ps1
```

成功時は`Verified SDK 10.0.401`、導入済みなら`SDK 10.0.401 is already installed`と表示される。スクリプトの実行許可はこのプロセスだけに適用し、PC全体の実行ポリシーは変更しない。

Microsoft公式ZIPを取得し、[公式リリースメタデータ](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)から固定したSHA-512と照合してから展開する。SDKは次へ保存する。

```text
%LOCALAPPDATA%\ModSync\dotnet\10.0.401-win-x64\
```

既存の.NETや恒久的なPATHは変更しない。通常の`dotnet`コマンドでSDKが見つからなくても、次の共通スクリプトは専用SDKを選ぶ。専用SDKがない場合に限り、PATH上のSDKで固定版が使えるか確認する。CIはこの経路で`actions/setup-dotnet`が用意したSDKを使う。

## 2. ビルドとテスト

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -Task Check
```

依存パッケージをロックファイルどおりに復元し、ReleaseビルドとCore・Platformのテストを実行する。件数は追加に伴って変わるため実行結果を記録する。成功時はビルドのエラー0、テストの失敗0が表示される。結果は`artifacts/TestResults/`に保存する。

Coreは画面に依存しないハッシュ計算とPrism初回取り込みの生成・照合・状態管理、Platformは実ファイルの観測・公開ハッシュでの準備確認・記録保存を持つ。試験は模擬Adapterと一時ディレクトリだけを使う。Checkは実Prism・ネットワーク取得・実認証を使わない。同期・競合判定・更新・復旧は未実装。

## 3. WPFの起動を自動確認する

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -Task Smoke
```

最小アプリを起動して描画後に自動終了する。成功時は`WPF startup passed`と結果の保存先が表示される。起動・描画とCore/Platform経由の読み取りを確認し、毎回新しい`artifacts/startup/`配下へ`startup.json`・`startup.png`・`stderr.log`を保存する。終了しない場合は30秒で今回の確認プロセスを終了し、失敗として扱う。

この確認はMinecraftやPrismを起動せず、ゲームのインスタンスを変更しない。

## 4. 画面を開いて確認する

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 -Task Run
```

「Minecraft MOD Sync」の開発用プレビューが開く。「参加」は準備中で無効。「閉じる」で終了する。日本語表示・文字の欠け・ウィンドウ操作も確認する。Debugで実行したい場合は`-Configuration Debug`を付ける。

いずれかの手順でエラーが出たら、その時点で止めて、実行したコマンドとエラー全文を対象PRへ共有する。別PCの追試結果はIssue #16へ残す。失敗した確認を済ませたものとして次へ進めない。

## このPCの確認履歴と#11の完了条件

2026-09-11時点で確認できたのはsakuma-dev側のWindows 11 Home x64（10.0.26200）。PowerShell 7と5.1でビルド・テスト、WPF起動を確認し、描画画像も確認した。

2026-10-03 22:24 JSTには、コミット`3d56ba293589d437c6ee5cdb70ddbd7287fc39dd`でCheckを再実行し、Releaseビルド（警告0・エラー0）とCore 5件・Platform 3件が成功した。その後のSmokeも成功した。結果は`artifacts/TestResults/sasak_SAKE_2026-10-03_22_24_44_net10.0.trx`と同名の`net10.0[1].trx`、`artifacts/startup/3d54946614f54b05ab430f3ee993efdc/`に保存した。この再確認ではRunの手動操作を実施していない。

これらは過去の検証履歴であり、2026-10-03のユーザー承認による完了条件変更とは区別する。#11の完了には、このPCの確認結果、取り込み対象版のCI成功、差分と未解決指摘の確認、PR #14のdevへのマージが必要。文書変更をpushした後は、その版のCI結果を改めて確認する。

## 2台目のPCでの確認結果（Issue #16）

別PCでは上記のSetup・Check・Smoke・Runをすべて実行し、次の形式でIssue #16へ結果を残す。PC未確保・追試未実施の間はIssue #16をopenで維持し、取りやめ扱いや確認済みにはしない。全手順の成功と記録が揃ってからIssue #16を閉じる。CIのWindowsランナーは別の自動実行環境であり、もう1人のWindows 11 PCの確認を代用しない。

```text
確認したコミット：
実行日時・確認者：
Windowsの版・アーキテクチャ・PowerShell版：
SDKセットアップ：成功／失敗（表示されたSDK版）
Check：ビルド結果・テスト結果
Smoke：成功／失敗
Run：画面表示・日本語・閉じる操作
ログ・描画画像の保存先：
未確認・エラー：
```

## 依存・CIの更新

- SDKは`global.json`と`eng/dotnet-sdk.json`で固定する。更新時はMicrosoft公式メタデータのURL・SHA-512も一緒に更新し、導入と検証をやり直す。
- NuGetの取得先は`NuGet.Config`、バージョンは各プロジェクト、解決結果は`packages.lock.json`へ記録する。意図した依存更新時だけ`dotnet restore ModSync.slnx --force-evaluate`でロックを更新し、差分を確認する。通常の共通コマンドは`--locked-mode`で復元する。
- 必須の`Repository checks`で、既存の文書・JSON・Python検査に加えて、同じ`Check`と`Smoke`を実行する。テスト結果と起動画像は`verification-results`として7日間保持する。
- Pythonによるリポジトリ検査は`python -m unittest discover -s tests -p "test_*.py" -v`と`python scripts/validate_repository.py`。CIはPython 3.13を用意する。アプリの共通コマンドはPythonに依存しない。

SDK固定は[global.json公式](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)、WPF構成は[Desktop SDK公式](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop)、CIのSDK準備は[setup-dotnet公式](https://github.com/actions/setup-dotnet)を参照。

## Issue #12のPrism実機検証（ハブ・ユーザー専用）

この手順は実機担当と本人が行う。実装担当のCheck・Smokeでは実行しない。新規検証用インスタンス1件の取り込みだけが承認範囲。2件目の作成、既存への更新、検証用インスタンスの削除、ゲーム起動、サーバー参加は別の承認が必要。準備不足を解消するために暗黙にゲームを起動しない。

対象コミットSHA、SDK 10.0.401、Check・Smokeの結果を記録してから、次を実行する。通常のRunやSmokeはこの検証入口に入らない。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/prism-import-verification.ps1
```

入力定義は[issue12-minimal/input-definition.json](../tests/fixtures/prism-import/issue12-minimal/input-definition.json)。画面には絶対パスを入力する。Minecraft 1.21.1とNeoForge 21.1.228はこの定義から読む。固定packIdは`eca05ccf-89b3-48c9-a231-8dc58377f611`。mrpackは毎回新しいoperationIdで生成し、MODや外部取得ファイルを含めない。表示名は「ModSync Verify issue12」と処理IDの先頭8桁で、既存と区別する。

1. 本人がPrism実行ファイルとデータルートを指定する。本人がPrismの「フォルダーを開く」等で`instances`の場所を確認し、想定と違えば止める。製品は`accounts.json`・`prismlauncher.cfg`を読まない。既存7件のフォルダー名を確認し、空のCOBBLEVERSEも含め、画面の保護対象欄へセミコロン区切りで入力する。件数は製品コードで固定していない。
2. 操作開始時にPrismのプロセスの有無だけを確認する。既に起動中なら「操作開始時からPrismが起動中」にチェックし、`beforeImport`を採取する。これを現在状態の基準とし、起動時から基準までの範囲は未確認と記録する。
3. 今回本人がPrismを起動する場合は、チェックを外した状態で、先に`beforePrismLaunch`を採取する。本人が起動し一覧を確認した後、`afterPrismLaunch`を採取・比較する。既存7件に差分があれば取り込みへ進まない。起動前の証拠を後から作ったことにしない。
4. 「入力を生成して取り込み依頼」を押す。直前の`beforeImport`証拠を再採取し、変更がない場合だけ新しい処理IDと入力ハッシュ、取り込み前の直下一覧を保存し、`--dir`・`--import`で依頼する。認証画面が出た場合は本人に戻し、ハブも認証情報を読まない。プロセス終了は成功を意味しない。
5. 本人がPrismの取り込み画面を確認し、指定した新規1件として確定する。既存を更新する操作へ切り替えない。名前を変更した場合は`renamedInDialog`、確定・取消・エラーは対応する申告として待機終了後に記録する。申告は登録条件を変更しない。認証情報が映らない画面と時刻だけを補助証拠にする。
6. 「配置を照合しました」になったら、自動採取された`afterImport`の証拠を確認する。「この取り込みを登録する」は、保護証拠の再比較と候補の再照合を行い、台帳の保存・読み戻し後だけ登録済みになる。識別ファイルが欠けた・不一致のときは保持インデックスで代替しない。登録せずハブへ返す。
7. 「準備状況を再確認」でR0〜R3と不足理由を処理記録に保存し、`afterReadinessCheck`を採取する。登録と準備完了は別。`ready`だけがIssue条件2の合格。宣言だけ、メタデータだけ、JAR不足・未知のrules・生成物不明は未達または未確認。追加取得やゲーム起動を自動実行しない。
8. タイムアウト・待機中止・途中終了では、画面に表示された処理IDを保存する。再起動後、同じパスと処理IDで「再確認」を押す。遅れて現れた一致候補は自動登録・削除しない。本人が登録ボタンを押した場合だけ同じ照合を通す。新規再試行は、前の処理が終端で再確認結果が候補なしの場合だけ可能。残存インスタンスがあれば、2件目の作成は許可を取り直す。
9. 失敗観測にはチェック欄で`failureProbe`を選び、不正な`game`を持つmrpackを生成できる。バージョンを不正にする方式は使わない。実機での実行可否をハブが承認境界と照合する。作業領域や残存物は削除しない。

保護証拠は再帰構造（空フォルダーを含む）、非機密ファイルのサイズ・属性・SHA-256を採取する。既知の認証ファイル、`.env`、認証・トークン・資格情報名のファイル、秘密鍵は除外し、除外一覧を記録する。独自の認証ファイルがインスタンス内にある場合は実機担当が先に把握し、この検証を開始せずハブへ返す。製品は未知の秘密ファイル名を自動識別できない。

隣り合う段階と基準から最後までの両方を比較し、`instgroups.json`のハッシュ変化は共有の変更として別に保存する。既存7件内の差分は取り込みと無関係に見えても例外にしない。予期しない変更があれば、以後の取り込み依頼・登録・承認済みの起動を止め、前後の証拠と差分を保全してハブへ報告する。自動復元・削除は行わない。基準採取の失敗やゲーム稼働で安定しない範囲は未確認として残す。

ローカル保存先は`%LOCALAPPDATA%/MinecraftModSync/prism-import/<dataRootKey>/`。データルートキーは正規化したパスのSHA-256先頭16桁。`operations/<operationId>/operation.json`に入力・依頼結果・状態履歴・本人の申告・再確認・準備状況、`input/`にmrpack、`registrations/<packId>.json`に確定台帳を保存する。ルートの`measurement.json`は処理IDを作る前の基準を保持し、処理作成後は`operations/<operationId>/measurement.json`にも段階別の保護証拠と差分を保存する。条件の実測判定・本人の操作の証拠はハブが追記する。Prismデータ領域内へアプリの記録を置かない。台帳保存失敗後もPrismインスタンスは残す。

これらは実パスやフォルダー名を含むローカル証拠なので、そのままGitHubへ出さない。ハブの実測記録へ対象SHA、OS・Prism・SDKのバージョン、処理ID、本人の操作、6条件の結果、ログ・画像パスを集約する。公開用の写しは実パス・ユーザー名を置き換え、認証情報を含まないことを確認する。実測記録の保護証拠は今回の採取区間だけを保証する。

| 未確認事項 | 実機で採取する証拠・入口 |
| --- | --- |
| U1 起動中のPrismへの依頼 | `request`のdelivery・exit code、取り込み画面と時刻。開かなければ止めてハブへ返す |
| U2 識別ファイルと保持インデックス | 候補のハッシュ・packId・operationIdとゲームルート。欠損・不一致は登録しない |
| U3 MODなしmrpack | `files: []`の入力と新規1件の照合結果。外部取得を追加して補わない |
| U4 コンポーネント | `mmc-pack.json`から採取したMinecraft・NeoForge・dependencyOnlyの事実 |
| U5 メタデータ | 索引sha256とバージョンJSONの一致、不足一覧 |
| U6 本体・ライブラリー・生成物 | 公開sha1・サイズとの一致とR0〜R3。未達ならJ1のユーザー判断へ |
| U7 作業領域と失敗入力 | `before.stagingKeys`・観測状態・取消やエラーの本人の申告。残存物は保全 |
| U8 表示名の変更 | instance.cfgの表示名は補助。変更した場合の本人の申告 |
| U9 既存7件・共有の変化 | 起動前または起動中の基準、段階別snapshot・comparison・sharedChanges・未確認区間 |
| U10 Prismの表示 | 認証情報の映らない画面と時刻。表示だけで登録・準備完了としない |

未知のPrismメタデータ・rules・インストーラー出力形式は`unconfirmed`で止め、準備完了にしない。Windowsのパス検査と読み取りの間の置換（TOCTOU）を完全に排除したものではなく、照合・確定直前の再観測で影響を抑える。実機・別PC・ゲーム起動・参加の結果はこの手順の作成やCI成功からは確認済みにしない。
