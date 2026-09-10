# 開発環境のセットアップと確認

更新日：2026-09-11。Issue #11の開発基盤は.NET SDK **10.0.401**とWPFを使う。対象はWindows 11の64ビット環境。現在の導入スクリプトとCIはx64 SDKを使い、ARM64 PCでの追試はまだ行っていない。

## 前提と取得

Git、Windows PowerShell 5.1以降、MicrosoftのSDK配布先とnuget.orgへ接続できる環境が必要。Visual Studioのインストールは必須にしない。以下はこのリポジトリのルートで、順番に実行する。

PR #14を別のPCで確認する場合は、既存の作業と分けた新しいフォルダーに取得する。

```powershell
git clone https://github.com/sakuma-dev/minecraft-mod-sync.git
cd minecraft-mod-sync
git fetch origin pull/14/head
git switch --detach FETCH_HEAD
git rev-parse HEAD
```

最後のコミットIDを確認結果に記録する。このcheckoutは追試用であり、実装を始める際は[開発の進め方](../CONTRIBUTING.md)に従って作業ブランチを作る。

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

依存パッケージをロックファイルどおりに復元し、ReleaseビルドとCore・Platformのテストを実行する。現在は8件。成功時はビルドのエラー0、テストの失敗0が表示される。結果は`artifacts/TestResults/`に保存する。

Coreは画面に依存しないストリームのSHA-256計算、Platformは実ファイルを変更しない読み取りを持つ。既知のハッシュ、途中位置・シーク不可の入力、キャンセル、ファイルの不存在・ロック・ハンドル解放を確認する。これは検証の入口であり、同期・競合判定・安全な配置や復旧の実装ではない。

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

いずれかの手順でエラーが出たら、その時点で止めて、実行したコマンドとエラー全文をPRへ共有する。失敗した確認を済ませたものとして次へ進めない。

## 2台目のPCでの確認結果

Issue #11には2人のPCでのビルド・テスト確認が必要である。2026-09-11時点で確認できたのはsakuma-dev側のWindows 11 Home x64（10.0.26200）。PowerShell 7と5.1でビルド・テスト、WPF起動を確認し、描画画像も確認した。CIのWindowsランナーは別の自動実行環境であり、もう1人のWindows 11 PCの確認を代用しない。

次の形式でPR #14へ結果を残す。もう1台の結果が揃うまでIssue #11を閉じない。

```text
確認したコミット：
確認者・Windowsの版：
SDKセットアップ：成功／失敗（表示されたSDK版）
Check：ビルド結果・テスト結果
Smoke：成功／失敗
Run：画面表示・日本語・閉じる操作
未確認・エラー：
```

## 依存・CIの更新

- SDKは`global.json`と`eng/dotnet-sdk.json`で固定する。更新時はMicrosoft公式メタデータのURL・SHA-512も一緒に更新し、導入と検証をやり直す。
- NuGetの取得先は`NuGet.Config`、バージョンは各プロジェクト、解決結果は`packages.lock.json`へ記録する。意図した依存更新時だけ`dotnet restore ModSync.slnx --force-evaluate`でロックを更新し、差分を確認する。通常の共通コマンドは`--locked-mode`で復元する。
- 必須の`Repository checks`で、既存の文書・JSON・Python検査に加えて、同じ`Check`と`Smoke`を実行する。テスト結果と起動画像は`verification-results`として7日間保持する。
- Pythonによるリポジトリ検査は`python -m unittest discover -s tests -p "test_*.py" -v`と`python scripts/validate_repository.py`。CIはPython 3.13を用意する。アプリの共通コマンドはPythonに依存しない。

SDK固定は[global.json公式](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)、WPF構成は[Desktop SDK公式](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop)、CIのSDK準備は[setup-dotnet公式](https://github.com/actions/setup-dotnet)を参照。
