# ANI Cursor Tool

Windows の `.ani` カーソル一式から、VRChat / Modular Avatar 対応の立体アニメーションカーソルを生成する VPM パッケージです。

## VCC へ追加

[Add ANI Cursor Tool to VCC](vcc://vpm/addRepo?url=https%3A%2F%2Fchanyavrc.github.io%2FAniCursor%2Findex.json)

Listing URL:

```text
https://chanyavrc.github.io/AniCursor/index.json
```

VCC の `Settings > Packages > Add Repository` に上記URLを貼り付けても追加できます。

## 使い方

1. VCCから `ANI Cursor Tool` をアバタープロジェクトへ追加します。
2. Unityで `Tools > ANI Cursor > ANI to Modular Avatar` を開きます。
3. `.ani` を含むフォルダーをドロップして生成します。

初回のみ、ローカルの Blender と LogoTracer ZIP が見つからない場合に選択が必要です。

## リポジトリ構成

- `Packages/com.chanya.ani-cursor`: 配布されるVPMパッケージ
- `.github/workflows/release.yml`: Release ZIPとUnityPackageを生成
- `.github/workflows/build-listing.yml`: GitHub PagesへVPM listingを公開
- `Website`: Add to VCCページのテンプレート

このリポジトリ自体は Unity 2022.3.22f1 の開発プロジェクトです。利用者がパッケージを手作業で `Packages` へ移動する必要はありません。

## リリース

1. `Packages/com.chanya.ani-cursor/package.json` と `CHANGELOG.md` のversionを更新します。
2. GitHub Actionsの `Build Release` を実行します。
3. Release完成後、`Build Repo Listing` がlistingを自動更新します。

公開済みの古いReleaseは、既存プロジェクトの復元に必要なので削除しません。

## License

MIT。外部ツールの扱いは [Third Party Notices](Packages/com.chanya.ani-cursor/Third%20Party%20Notices.md) を参照してください。
