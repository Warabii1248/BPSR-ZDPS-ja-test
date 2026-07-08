# FORK_NOTES — 大本（upstream）追従のための管理メモ

このフォーク（`origin`: `Warabii1248/BPSR-ZDPS-ja-test`）が大本
（`upstream`: `Blue-Protocol-Source/BPSR-ZDPS`）から何を・どう変えているかをまとめた
**マージ作業用の手引き**です。upstream を取り込むときは、まずこの表で「どこが衝突しうるか」を確認します。

---

## 同期（upstream 取り込み）の手順

```bash
# 1. 大本の最新を取得
git fetch upstream

# 2. 自分の master に大本を取り込む（履歴を綺麗に保つなら rebase 推奨）
git checkout master
git rebase upstream/master        # もしくは: git merge upstream/master

# 3. 衝突したら下表「衝突しうるファイル」を参照して解決 → ビルド確認
dotnet build BPSR-ZDPS/BPSR-ZDPS.csproj -c Debug

# 4. 問題なければ自分のフォークへ
git push origin master
```

> [!IMPORTANT]
> **`upstream` には push しないこと。** 事故防止のため push URL を無効化済み
> （`git remote -v` の upstream(push) が `DISABLED_DO_NOT_PUSH_TO_UPSTREAM`）。
> 解除が必要になった場合のみ:
> `git remote set-url --push upstream https://github.com/Blue-Protocol-Source/BPSR-ZDPS.git`

> [!TIP]
> **小さくこまめに同期する**ほど衝突は軽くなります。大本更新を溜めない。

---

## フォーク独自の新規ファイル（衝突しない）

これらは大本に存在しないため、マージで衝突しません。そのまま残ります。

| ファイル | 役割 |
|---|---|
| `BPSR-ZDPS/Shaders/ModuleSolver.hlsl` | GPU（DirectCompute）探索カーネル。csproj に EmbeddedResource 登録 |
| `BPSR-ZDPS/Managers/Gpu/GpuComputeContext.cs` | D3D11 コンピュートデバイス・バッファ・Dispatch・リードバック |
| `BPSR-ZDPS/Managers/Gpu/ModuleOptimizerGpu.cs` | `ModuleOptimizer` の partial。GPU入力構築〜結果整形 |
| `BPSR-ZDPS/Data/AppStrings.ext.en.json` | **フォーク追加の英語文字列**（`Module_*`）。大本の en.json を汚さないための上書きレイヤ |
| `BPSR-ZDPS/Data/AppStrings.ext.ja.json` | フォーク追加の日本語文字列（`Module_*`） |
| `README-JP.md` / `FORK_NOTES.md` | フォークのドキュメント |
| `update_package.bat` | リリース上書き用パッケージ生成スクリプト |

---

## 大本のファイルへの改変（衝突しうる）

upstream がこれらを触ると衝突します。**衝突したら基本「両方を活かす」**で解決します。

| ファイル | 改変内容 | 衝突時の方針 |
|---|---|---|
| `BPSR-ZDPS/Windows/ModuleSolver.cs` | `SolverModes.Gpu`／`ScoreMode` enum 追加、`ModuleSet` を最大10対応（`Mod1..Mod10`/`FromValues`）、設定UIに演算バックエンド・スコアモード行を追加、スライダーのホバー塗りつぶし修正、UI文字列の `GetLocalized` 化 | 最も衝突しやすい。**追加した行は残し**、大本の構造変更に合わせて配置し直す。文字列は `GetLocalized("Module_*")` を維持 |
| `BPSR-ZDPS/Managers/ModuleOptimizer.cs` | `Solve()` に `Gpu` 分岐＋CPU軽量フォールバック（ビームサーチ・`UsedCpuFallback` フラグ）、`FiveModulesLoop` の合算バグ修正、ループ末尾の `-1` ガード | Gpu 分岐は薄いフック。大本の `Solve` に合わせて再配置。バグ修正は upstream へPR提案推奨 |
| `BPSR-ZDPS/Managers/ModuleOptimizer.V2.cs` | `NormalV2` の結果マッピングを最大10対応（`ModuleSet.FromValues` ループ化） | 大本のマッピング構造に追従しつつ10対応を維持 |
| `BPSR-ZDPS/Managers/ModuleOptimizer/ModuleSolver.cs` (base) | `GetStatMul` にレジェンダリ倍率（`Config.LegendaryStatMultiplier`）、`PossibleStats` 逆引き保持、`ModuleSetIndices`/`ResloveResults` を最大10対応 | 大本のビーム系改修に合わせて再適用 |
| `BPSR-ZDPS/Managers/ModuleOptimizer/ModuleOptimizerBeam.cs` | 原作スコア式（順位ブースト・レジェ倍率・オーバーキャップ余剰点）復活、`ScoreMode.CombatPower` 対応（LinkTotalFight 全体項）、`StatDifference` の `=`→`-` バグ修正 | GPU と同一スコア式を維持することが最重要。upstream の式変更時は GPU/HLSL と突合 |
| `BPSR-ZDPS/DataTypes/Modules/SolverConfig.cs` | `ScoreMode` / `OrderBoostStrength` / `LegendaryStatMultiplier` フィールド追加（GPU 常時優先のため `UseGpu` トグルは無し） | フィールド追加のみ。ほぼ自動マージ |
| `BPSR-ZDPS/BPSR-ZDPS.csproj` | `ModuleSolver.hlsl` の EmbeddedResource、`AppStrings.ext.*.json` の CopyToOutputDirectory | 行追加のみ。衝突したら追加行を残す |
| `BPSR-ZDPS/AppState.cs` | `LoadAppStringsTable()` を ext オーバーレイ対応に変更（`MergeAppStringsFile` ヘルパ追加） | 大本がローダーを変えたら、ext.en/ext.ja の読み込み順（base→ext→lang→ext.lang）を再現 |
| `BPSR-ZDPS/Data/AppStrings.en.json` | **現状はフォーク追加キーをすべて除去済み**（`Module_*` は ext.en.json へ移動） | 理想は「翻訳追加で en.json を編集しない」運用。大本の追加キーをそのまま受け入れるだけにする |
| `BPSR-ZDPS/Data/AppStrings.ja.json` | 全体の日本語訳（フォークの主目的）。`Module_*` は ext.ja.json へ分離済み | 大本が ja.json を持たなければ衝突なし。持つ場合は訳を統合 |

---

## ローカライズの設計（衝突を減らす肝）

文字列ロードは `AppState.LoadAppStringsTable()` で次の順にマージされます（後勝ち）:

```
AppStrings.en.json        (大本ベース・編集しない)
  → AppStrings.ext.en.json    (フォーク追加の英語)
  → AppStrings.{lang}.json    (言語別。例: ja)
  → AppStrings.ext.{lang}.json(フォーク追加の言語別。例: ext.ja)
```

**新しい独自文字列を足すときは、大本の `AppStrings.en.json` を編集せず、
`AppStrings.ext.en.json` と `AppStrings.ext.{lang}.json` に追記する。**
これで大本の en.json は常に綺麗に保たれ、マージ衝突が出ません。

---

## なぜ UI（ModuleSolver.cs）はファイル分離していないか

UIの独自ロジック（GPU/スコアモードの**計算**）は既に別ファイル
（`Managers/Gpu/*.cs`、partial クラス）へ分離済みです。一方 `ModuleSolver.cs` 側は、
(1) 追加した設定行2つ、(2) 既存文字列の `GetLocalized` 置換、が混在します。
(2) はファイル全体に散らばる**インプレース編集**で、性質上 partial へ切り出せません
（日本語化フォークの宿命）。そのため UI 行だけを別ファイル化しても衝突削減効果は小さく、
リスクに見合わないため、ここは「表で管理する」方針にしています。
