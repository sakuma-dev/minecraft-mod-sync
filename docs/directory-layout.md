# ディレクトリ構成案

作成日：2026-09-09。現在あるのは文書とリポジトリ検査用のCI・スクリプト・テスト。下記の`src`とアプリ用テストプロジェクトは実装開始時に追加する予定の配置である。

```text
minecraft-mod-sync/
  README.md
  CONTRIBUTING.md
  .gitignore
  .gitattributes
  .github/
    workflows/ci.yml
    rulesets/branch-protection.json
  docs/
    requirements.md
    design.md
    directory-layout.md
    implementation-plan.md
  src/
    ModSync.Core/
      Distribution/       # 配布情報、読み取り、取得経路
      Publishing/         # 選択、固定、検証用環境、確認記録、公開
      Synchronization/    # 調査、差分、状態遷移、反映
      Recovery/           # 途中失敗の復旧、前回更新の取消
      ModCatalog/         # MOD ID、依存、確認可能な競合
      ResourcePacks/      # パック分類、優先順と設定変更の計画
    ModSync.Platform/
      Prism/              # 検出、取り込み、起動、状態の観測
      FileSystem/         # 安全な配置、記録、ロック、監視
      Transfer/           # HTTPS取得、SFTP公開、手動ファイル検出
      Credentials/        # Windows上の公開用資格情報の扱い
    ModSync.Desktop/
      Participant/        # 参加者の画面と表示状態
      Administrator/      # 配布セット準備・公開画面
      Setup/              # 初回案内と専用インスタンスの登録
      Shared/             # 共通の画面要素
  tests/
    test_validate_repository.py # リポジトリ検査スクリプトのテスト
    ModSync.Core.Tests/
    ModSync.Platform.Tests/
    fixtures/             # 自作の小さな疑似データだけ
  schemas/                # 配布情報の形式を固定するときに追加
  examples/               # 接続先・認証情報を含まない例
  scripts/
    validate_repository.py # 文書・JSONの検査。ビルド・梱包用は必要時に追加
```

## 依存方向

`ModSync.Desktop`が画面と依存の組み立てを担当し、`ModSync.Core`のInterfaceと`ModSync.Platform`のAdapterを使う。`ModSync.Platform`は`ModSync.Core`で定義したInterfaceを実装する。`ModSync.Core`からWPFやPrismの具体的な処理へ依存させない。

機能名で必要な処理を探せる構成にする。初めから多数の独立したパッケージや空の`Services`・`Helpers`階層を作らず、実際の変更単位を見て分割する。

## 利用者PC上のデータ

アプリの実行時データはソースコードの外へ置く。

```text
アプリ用のローカルデータ領域/
  settings.json
  publishing/<releaseId>/
    prepared/              # 固定したmanifest・初回取込データ・自前ファイル
    verification.json      # 内容ハッシュ、検証用環境、確認結果。参加者へ配らない
  instances/<instanceKey>/
    state.json             # packId、適用版、管理台帳
    operations/            # 進行中処理と復旧用記録
    undo/                  # 成功した前回更新の取消用
    quarantine/            # 本人が退避した個人MOD
  cache/                   # 内容を照合した取得済みファイル
  logs/
```

資格情報はこの平文例に含めず、Windowsの保管機能を使う案とする。Prismのアカウント情報を複製しない。

ゲームの稼働ファイルは登録した専用インスタンスへ置く。原子的な置換が必要な一時ファイルは対象と同じボリュームに用意する。バックアップ領域が別ボリュームの場合は、コピーと照合の完了前に元ファイルを消さない。
