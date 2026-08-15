# ANI Cursor Tool

Windows の `.ani` カーソル一式から、VRChat アバターで切り替え・再生できる立体カーソルを生成する Unity エディターツールです。FBX、PNG、Material、AnimationClip、Animator Controller、Modular Avatar 対応 prefab をまとめて作成します。

## インストール

VCC または ALCOM に `https://ani-cursor.buildsoft.jp/index.json` を登録し、`ANI Cursor Tool` をプロジェクトへ追加します。依存する VRChat SDK と Modular Avatar は VPM により解決されます。

配布用ZIPとVPM listingは、このリポジトリのGitHub Actionsから生成されます。

## 動作環境

- Windows
- Unity 2022.3
- VRChat SDK - Avatars 3.10.4 以上、4.0.0 未満
- Modular Avatar 1.18.1 以上、2.0.0 未満

依存する Unity/VPM パッケージは VPM のインストール時に解決されます。
Modular Avatar は必須で、MA を使わない生成経路はありません。

## 初回のみ必要な準備

外部ツール本体はこのパッケージに含まれません。次の 3 項目を用意してください。

1. [ani-extract](https://github.com/ChanyaVRC/ani-extract) を取得し、そのフォルダーで `uv sync` を実行します。
2. [Blender](https://www.blender.org/) をインストールします。
3. LogoTracer 1.21 の ZIP を入手し、展開せずに保存します。

初回起動時に自動検出されなかった項目だけ、ツール画面で次の場所を指定します。

- Python: `ani-extract/.venv/Scripts/python.exe`
- ani-extract root: `ani-extract` のリポジトリフォルダー
- Blender: `blender.exe`
- LogoTracer: 元の `.zip` ファイル

設定は保持されるため、通常は次回から入力不要です。外部ツールのライセンスについては `Third Party Notices.md` を参照してください。

## カーソルを生成する

1. Unity メニューの `Tools > ANI Cursor > ANI to Modular Avatar` を開きます。
2. 変換したい `.ani` フォルダーをドロップするか、`ANI folder` で選びます。
3. `MA Prefabを生成` を押します。

フォルダー内の `.ani` はサブフォルダーも含めて検出されます。名前、メニュー、
Parameter、出力先は自動設定され、`Assets/AniCursorGenerated/<フォルダー名>` に PNG、
FBX、Material、アニメーション一式、および `*_MA.prefab` が作成されます。
変更したい場合だけ `詳細設定` を開きます。

## アバターへ導入する

1. 生成された `*_MA.prefab` をアバター直下へ配置します。
2. `CursorDisplay` を Scene 上で希望する位置・向きに移動します。
3. そのまま Build & Publish します。

`CursorDisplay` の Modular Avatar Bone Proxy は RightHand を対象とし、配置モードは「子としてワールド位置と向きを維持」です。メニュー、パラメーター、Animator は Modular Avatar によりアップロード時に統合されます。

生成 prefab 内の `CursorDisplay` は常に Position `(0, 0, 0)`、Rotation `(0, 0, 0)`、Scale `(1, 1, 1)` です。単位と軸変換は FBX のメッシュへ焼き込まれています。位置や向きを変える場合は、アバターへ配置した prefab インスタンス側で調整します。

## プリセットを使う

プリセットは任意です。名前、メニュー順、パラメーター値を固定したい場合は、Package Manager の Samples から `Preset Example` をインポートし、`AniCursorPreset.json` の `.ani` ファイル名と表示名を編集して指定します。

プリセットを指定しない場合は、フォルダー内の `.ani` から設定が自動生成されます。

## 旧バージョンから更新する

旧形式の prefab や manifest は変換しません。元の `.ani` フォルダーを指定し、最新版で生成し直してください。

## 生成仕様

- フレーム: 32 x 32 px
- テクスチャ: Point フィルター、Mip Map なし
- 形状: 非透明ピクセルを連結した一枚の閉じた板
- 厚み: 4 mm
- アニメーション: ANI の `seq` と `rate` を反映してループ再生

## トラブルシューティング

### 外部ツールが見つからない

ツール画面の外部ツール設定を開き、見つからない項目だけ指定してください。LogoTracer は展開後のフォルダーではなく ZIP を指定します。

### 生成物がピンク色になる、または一部が透明になる

生成した Material と Atlas PNG が同じ出力一式に含まれていることを確認し、ファイル単体ではなく生成フォルダー全体を移動してください。

### アニメーションやメニューが動かない

生成された通常の prefab ではなく `*_MA.prefab` をアバター直下に配置し、Modular Avatar の依存バージョンを確認してください。
