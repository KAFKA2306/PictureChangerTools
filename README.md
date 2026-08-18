# PictureChanger Tools for Unity Editor

Unity Editorで `PictureChanger` と `Picture` オブジェクトの画像を確認・生成・割り当てするEditor toolです。実装は `Assets/Editor/PictureChangerTools.cs` にあります。

## メニュー

現在のコードが登録するメニューは次の3つです。

- `Tools/PictureChanger/Scan usages and sizes`
  - PrefabとSceneを検索し、画像利用状況を `Assets/PictureChanger/picture_changer_report.txt` に出力します。
- `Tools/PictureChanger/Random assign VRChat images (resize, scenes)`
  - 入力画像をリサイズして `Assets/PictureChanger/Compressed1023` 以下へ生成し、向きに合わせてScene内の対象へ割り当てます。
  - 実行前に確認ダイアログを表示します。
- `Tools/PictureChanger/Clean unreferenced compressed images`
  - PrefabとSceneから参照されていない圧縮画像を削除します。
  - 実行前に確認ダイアログを表示します。

## 既定値

コード上の既定値は次のとおりです。

- 出力root: `Assets/PictureChanger`
- 入力folder: `Assets/sameR&D/Picture`
- 圧縮画像folder: `Compressed1023`
- 最大長辺: 1023 px

設定が存在する場合はコード内の `PictureChangerConfig` がこれらを上書きします。

## 検証

このrepositoryには現在、Unity project設定、test suite、GitHub Actions workflowは含まれていません。Unity Editorでのcompileと各メニューの動作確認は別途必要です。
