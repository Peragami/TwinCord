# TwinCord

[![CI](https://github.com/Peragami/TwinCord/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Peragami/TwinCord/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Peragami/TwinCord?label=release)](https://github.com/Peragami/TwinCord/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%202004%2B-0078D4?logo=windows&logoColor=white)](#必要環境)

2アカウント同時常駐の軽量Discordラッパー (Windows / C# + WebView2)

公式デスクトップ版を2つ起動すると重いため、OS共有のWebView2ランタイム上に
Discord公式Web版を2枚埋め込み、1つのアプリで2アカウントを同時利用します。

## 主な特徴

- 2アカウントを同時常駐し、左レールで切替(通知ソケットは維持)
- 非表示側の未読バッジ、メンション等のバルーン通知、通知履歴
- トレイ常駐。二重起動時は先発ウィンドウを表示
- ページ内 `Ctrl+1/2`、グローバル `Ctrl+Alt+1/2/M`
- 自動更新、アカウント別ミュート、拡大率、Windows 自動開始
- 設定・ログイン情報はアプリのフォルダ内 `data\` に保存(ポータブル)

## 入手

1. [Releases](https://github.com/Peragami/TwinCord/releases) から `TwinCord.zip` をダウンロード
2. **書き込みできる場所**(デスクトップやドキュメントなど)へ「すべて展開」
3. 出てきた `TwinCord.exe` を実行

ランタイム同梱のため、**.NET のインストールは不要**です。初回起動時に Windows の
セキュリティ警告(SmartScreen)が出る場合は、「詳細情報」→「実行」で許可してください。

`Program Files` など書き込みできない場所では起動できません。また、自動更新が
`TwinCord.exe` という名前を使うため、exe の名前は変えないでください。

## 必要環境

- Windows 10 2004 (ビルド19041) 以降 / Windows 11
- WebView2 ランタイム(Windows 11 は標準搭載)

ビルドには .NET 10 SDK が必要です。[ビルドとリリース](docs/build.md)を参照してください。

## ドキュメント

- [機能と使い方](docs/features.md) … 通知、ホットキー、省メモリ、自己診断
- [データの保存場所と削除](docs/data.md) … ポータブル形式、バックアップ、アンインストール
- [ビルドとリリース](docs/build.md) … ビルド、配布物作成、自動更新の仕組み
- [設計メモ](docs/design.md) … WebView2 環境の共有、起動・監視の設計
- [アカウント安全性](docs/security.md) … 外部通信、Discord 規約、注意事項
- [バージョン履歴](docs/changelog.md) … バージョニング方針と変更履歴

## 注意

Discord 公式 Web 版を WebView2 で表示する非公式ツールです。トークンの抽出・
非公開 API の直接利用・自動操作は行いませんが、100% の安全保証はできません。
利用は自己責任でお願いします。詳細は [アカウント安全性](docs/security.md) を参照してください。
