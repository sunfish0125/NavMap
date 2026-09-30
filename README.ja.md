# NavMap

[English](README.md) | 日本語

NavMap は、Euro Truck Simulator 2 と American Truck Simulator 向けの 2D ナビゲーションマップを ETS2LA Overlay に表示する [ETS2LA](https://github.com/ETS2LA/ETS2LA) プラグインです。

マップに描くのは、ゲーム内ナビがすでに計画したルートです。NavMap 自身はルートを計算しません。ゲームがルートを再計算したり目的地を変更したりすると、マップもそれに合わせて更新されます。

## 機能

![ETS2LA Overlay 上の NavMap。2000 m までズームアウトし、周辺の道路と POI アイコンを表示している](images/NavMap.png)

- ヘディングアップ表示：自車は常に中央にあり、進行方向が上になるようにマップが回転します
- 周辺の道路を車線単位で描画
- ゲームのアクティブなルートを緑色でハイライト
- 目的地マーカー（目的地が画面外にあるときはマップの端に矢印を表示）
- 北を指す小さなコンパス
- 会社、ガソリンスタンド、サービスポイント、ガレージのアイコン（ETS2LA のデータ精度（data fidelity）が Extreme のときのみ表示）
- `-` / `+` のズームボタンで、マップの表示幅を 500 m、1000 m、1500 m、2000 m に切り替え
- 画面上の好きな位置に移動でき、サイズも自由に変えられるマップウィンドウ
- 150 m 以内のすべての信号機に AR マーカーを表示。信号機の位置に描画され、灯色・残り時間・距離を示します（SignalHUD プラグインから統合）

![前方の 3 つの信号機に表示された AR マーカー。それぞれ灯色、残り時間、距離を示し、近いマーカーほど大きく描画されている](images/SignalMarker.png)

## 設定

**Plugin Manager → NavMap → Adjustments** を開くと、次の項目を変更できます。

- **Background opacity**：マップウィンドウ背景の透明度
- **Hide when paused**：ゲームの一時停止中にマップウィンドウを閉じる
- **Traffic signals → Show AR marker**：周辺の信号機の AR マーカーを表示するかどうか
- **Traffic signals → AR marker background opacity**：AR マーカーの黒い背景の透明度（0–100%）
- **Traffic signals → AR marker font size**：AR マーカーの文字サイズ（50–300%）

このページには Diagnostics セクションもあります。テレメトリ、マップデータ、ナビゲーションデータが取得できているかが表示されるので、マップに何も表示されないときの確認に役立ちます。

NavMap は設定とズームレベルを `%APPDATA%\ETS2LA\NavMapSettings.json` に保存するため、再起動後も引き継がれます。

## インストール

使うだけなら、NavMap を自分でビルドする必要はありません。

1. [Releases ページ](https://github.com/sunfish0125/NavMap/releases) から最新の `NavMap-vX.Y.Z.zip` をダウンロードします。
2. ETS2LA が起動している場合は終了します。
3. zip から `NavMap.dll` と `NavMap.deps.json` を取り出し、ETS2LA のインストールフォルダ（`ETS2LA.exe` があるフォルダ）内の `Plugins` フォルダに置きます。`Plugins` フォルダがまだない場合は、ETS2LA を一度起動すると作成されます。
4. ETS2LA を起動し、Plugin Manager で **NavMap** を有効にします。
5. ETS2 または ATS を起動し、ゲーム内ナビで目的地を設定します。するとマップにルートが表示されます。

各リリースは特定の ETS2LA コミットに対してビルドされており、そのコミットはリリースノートに記載しています。新しい ETS2LA でプラグイン API が変わった場合、NavMap の新しいリリースが出るまで読み込めないことがあります。

NavMap を更新するには、ETS2LA を終了し、2 つのファイルを新しいリリースのものに置き換えてから、ETS2LA を再び起動します。

## ソースからのビルド

### 必要なもの

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [ETS2LA](https://github.com/ETS2LA/ETS2LA) の C# ソースのローカルコピー（このリポジトリと同じ階層のフォルダに配置）
- ETS2LA と連携できるようにセットアップ済みの ETS2 または ATS

### フォルダ構成

NavMap は ETS2LA のソースを参照してビルドするため、2 つのフォルダを並べて配置する必要があります。

```text
<workspace>\
├─ ETS2LA\          ETS2LA のソース（ETS2LA\ETS2LA.csproj を含む）
└─ NavMap\          このリポジトリ
```

### ビルドとインストール

`NavMap` フォルダでビルドスクリプトを実行します。

```powershell
.\scripts\build-and-deploy.ps1
```

このスクリプトは次の処理を行います。

1. `NavMap.csproj` をビルドする
2. `NavMap.dll` と `NavMap.deps.json` を `..\ETS2LA\ETS2LA\bin\<Configuration>\net10.0\Plugins` にコピーする

ビルドに失敗した場合、スクリプトはファイルをコピーせずに停止します。

#### オプション

| オプション | 説明 |
| --- | --- |
| `-Configuration Debug` / `-Configuration Release` | ビルド構成。既定値は `Debug` です。ファイルは同じ構成の ETS2LA ビルド出力先にコピーされます。 |
| `-SkipBuild` | ビルドを省略し、既存のビルド出力のコピーだけを行います。 |
| `-IncludeSymbols` | デバッグ用に `NavMap.pdb` もコピーします。 |

例：

```powershell
.\scripts\build-and-deploy.ps1 -Configuration Release
```

実行ポリシーによって PowerShell がスクリプトをブロックする場合は、代わりに次のように実行します。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-and-deploy.ps1
```

#### デプロイ後の手順

1. ETS2LA がすでに起動している場合は、完全に終了します。ETS2LA は起動時にしか新しいプラグインファイルを読み込まないため、Plugin Manager で NavMap を無効化・再有効化しても更新は反映されません。
2. デプロイした構成に対応する ETS2LA ビルドを起動します。
3. Plugin Manager で **NavMap** を有効にします。
4. ETS2 または ATS を起動し、ゲーム内ナビで目的地を設定します。するとマップにルートが表示されます。

新しいバージョンをビルド・デプロイするたびに、この手順を繰り返してください。

## リリース手順

リリースは GitHub Actions（[.github/workflows/release.yml](.github/workflows/release.yml)）でビルドされます。

1. `Program.cs` の `Version`（例：`0.2.0`）を更新してコミットします。
2. それに対応する `v` 付きのタグを作成し、タグをプッシュします。

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. ワークフローは `ETS2LA_REF` で指定されたコミットの ETS2LA をチェックアウトし、NavMap を Release モードでビルドして、`NavMap-v0.2.0.zip` を添付した GitHub Release を作成します。

タグが `Program.cs` の `Version` と一致しない場合、ワークフローはリリースを作成せずに停止します。

リリースせずにビルドを試したい場合は、**Actions** タブからワークフローを手動で実行します。この場合、zip の中身はワークフローのアーティファクトとしてアップロードされます。

新しい ETS2LA に対してビルドするには、ワークフロー内の `ETS2LA_REF` を新しいコミットに変更します。リリース前に、その ETS2LA バージョンで NavMap が引き続き動作することを確認してください。

## 既知の制限事項

- マップが表示されるのは、ゲームのテレメトリが取得できていて、ETS2LA がマップデータの読み込みを終えている間だけです。
- ゲーム内ナビにルートがない場合、NavMap は道路だけを表示します。
- NavMap は、自車が走行できる道路だけでなく、進入できない道路も表示します。
- ゲーム内ナビで目的地を設定していても、緑色のルート線が表示されないことがあります。
- コンパスが使っている北の方向はまだゲームと照合していないため、誤った方向を指している可能性があります。
- 150 m 以内のすべての信号機にマーカーが付きます。交差する道路や対向車線の信号機も含まれます。NavMap は、どの信号機が自車の車線に対応するかを判定しません。
- AR マーカーは距離に応じて小さくなりますが、最小でも実寸の 40% までしか縮まないため、遠くのマーカーは実際の遠近感よりも大きく見えます。
- NavMap がマーカーを付けられるのは、ゲームが ETS2LA に送ってきた信号機だけです。受け取った信号機の数と最も近い信号機までの距離は、Diagnostics セクションで確認できます。
