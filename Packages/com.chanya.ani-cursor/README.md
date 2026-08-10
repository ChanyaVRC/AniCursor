# ANI Cursor Tool

Windows の `.ani` カーソル一式から、VRChat / Modular Avatar 対応の立体アニメーションカーソルを生成する VPM パッケージです。

## インストール

VCC または ALCOM に次のVPM listingを登録し、`ANI Cursor Tool` をプロジェクトへ追加してください。

```text
https://chanyavrc.github.io/AniCursor/index.json
```

[VCCへ追加](vcc://vpm/addRepo?url=https%3A%2F%2Fchanyavrc.github.io%2FAniCursor%2Findex.json)

ローカル開発では、このリポジトリの `Packages` フォルダーをVCCのUser Packagesへ登録できます。

## 使い方

1. Unity の `Tools > ANI Cursor > ANI to Modular Avatar` を開きます。
2. `.ani` を含むフォルダーをドロップします。
3. `MA Prefabを生成` を押します。

通常の入力は ANI フォルダーだけです。初回に自動検出できなかった外部ツールのみ指定します。

詳しい準備、生成物、アバターへの導入方法は [Documentation~/README.md](Documentation~/README.md) を参照してください。
