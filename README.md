# Minecraft MOD Sync

管理者が確認した構成へ友人のPrism Launcher専用インスタンスを揃え、Minecraftの起動・サーバーへの接続まで進めるWindows向けツール。

現在は開発基盤と起動確認用の最小アプリを準備しています。MODの同期・Prism連携・サーバー参加の機能はまだありません。

- [要件定義](docs/requirements.md)：対話で決めた動作と対応範囲
- [設計案](docs/design.md)：処理の分担、配布形式、更新・復旧、画面
- [ディレクトリ構成案](docs/directory-layout.md)：実装時の配置と依存方向
- [実装・検証計画](docs/implementation-plan.md)：実現性を先に確認する項目と受入条件
- [開発環境のセットアップ](docs/development.md)：固定SDKの導入、ビルド・テスト・最小アプリの起動、2台目の確認手順
- [開発の進め方](CONTRIBUTING.md)：Issueと担当分担、Draft PRでの進捗共有、日本語コミット、main・devとリリースの流れ

初期対応はWindows 11、Minecraft 1.21.1、NeoForge、Prism Launcherです。利用規模は管理者を含め最大8人程度を想定します。

検証用の基準環境は「All of Create - Aeronautics」です。特定のMODパックを再配布する公式プロジェクトではありません。実際に配布するファイルは、管理者が動作と取得・配布方法を確認して選びます。

このリポジトリはツールの文書と、今後作成するソースコードを管理します。実際のMOD、個人のゲーム環境、ワールド、認証情報は含めません。
