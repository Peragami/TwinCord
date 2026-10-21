# ビルドとリリース

## 必要なもの

- Windows 10 2004 (ビルド19041) 以降 / Windows 11
- .NET 10 SDK
- Visual Studio は不要です。`dotnet` CLI でビルドできます。

## ビルドとテスト

```powershell
dotnet build -c Release
dotnet test TwinCord.Tests\TwinCord.Tests.csproj -c Release
```

テストは WPF アプリ本体を参照するため、Windows でのみ実行できます。

## 配布物の作成

| スクリプト | 用途 |
| --- | --- |
| `scripts\publish-portable.bat` | リリース用。フォルダ構成 + `TwinCord.zip`(ランタイム同梱・約72MB) |
| `scripts\publish.bat` | 開発用の軽量版。単一exe・フレームワーク依存(.NET 10 が必要) |
| `scripts\publish-selfcontained.bat` | 単一exeが必要なときの例外用(自己完結) |

`publish-portable.bat` は実行中の `TwinCord.exe` を終了し、`publish-zip` にクリーンな配布物を作って zip 化したあと、`publish-portable` を更新します。`data` と `mods` は同期対象外です。

## リリース手順

1. `TwinCord.csproj` の `<Version>` を更新する
2. `docs/changelog.md` の「バージョン履歴」に同じ番号の項目を追加する
3. ビルドとテストを実行する
4. `scripts\publish-portable.bat` で `TwinCord.zip` を作成する
5. タグを作成して push する
6. GitHub Release を作成し、`TwinCord.zip` をその名前のまま添付する

`TwinCord.zip` という名前は必須です。アプリの自動更新がこの名前の添付を探します。

## 配布のルール

- リリースには `publish-portable.bat` が作る `TwinCord.zip` を添付する。
- `publish.bat` の成果物は開発中の起動用で、リリースには添付しない。
- 単一exe配布は廃止した。1.10.1 以前の単一exe版からは自動更新できないため、zip版へ手動で移行する。
- スクリプトは失敗時に終了コード1を返し、古い成果物を残したまま成功扱いにしない。
- アプリのフォルダに私物のファイルを置かない(`data` と `mods` を除く)。

## 自動更新の仕組み

- 起動時に `https://api.github.com/repos/Peragami/TwinCord/releases/latest` を確認する。
- 添付の `TwinCord.zip` をダウンロードし、GitHub が返す SHA256 を検証する。SHA256 が取得できない場合は自動更新を中止する。
- zip の内容(`TwinCord.deps.json` など)を検証し、アプリ本体を退避してからミラー同期する。
- 同期先は GitHub の当該リポジトリのリリースURLに限定する。
- 失敗時は元のファイルへ戻して再起動し、理由を `update-error.log` に記録する。次回起動時に通知する。
- `data` と `mods` は同期対象外なので、設定・ログイン情報・mod は残る。

## CI

GitHub Actions で次を実行します。

- `TwinCord.csproj` のバージョンが `docs/changelog.md` の履歴にあるか確認
- `dotnet test` ですべてのテストを実行
