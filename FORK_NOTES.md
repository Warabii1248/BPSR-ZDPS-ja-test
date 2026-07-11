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
| `BPSR-ZDPS/Managers/Gpu/ModuleSolverCache.cs` | `ModuleOptimizer` の partial。**支配フィルタ（厳密な探索空間削減）＋総当たり結果キャッシュ（プール収集・CPU再採点・メモ化）** |
| `BPSR-ZDPS/Data/AppStrings.ext.en.json` | **フォーク追加の英語文字列**（`Module_*`）。大本の en.json を汚さないための上書きレイヤ |
| `BPSR-ZDPS/Data/AppStrings.ext.ja.json` | フォーク追加の日本語文字列（`Module_*`） |
| `README-JP.md` / `FORK_NOTES.md` | フォークのドキュメント |
| `update_package.bat` | リリース上書き用パッケージ生成スクリプト |

---

## 大本のファイルへの改変（衝突しうる）

upstream がこれらを触ると衝突します。**衝突したら基本「両方を活かす」**で解決します。

| ファイル | 改変内容 | 衝突時の方針 |
|---|---|---|
| `BPSR-ZDPS/Windows/ModuleSolver.cs` | `SolverModes.Gpu`／`ScoreMode`／`ScoringModel`／**`ComputeBackend`** enum 追加、`ModuleSet` を最大10対応（`Mod1..Mod10`/`FromValues`）、設定UIに**演算バックエンド（GPU/CPU コンボ・`ModuleWindowSettings.ComputeBackend` に保存）**・**計算結果キャッシュ（`UseBruteForceCache`/`CacheThresholdPct`・状態表示/クリアボタン）**・スコアモード・**総当たり（`BruteForceAllModules`）・計算方式（`ScoringModel` 改造版/オリジナル）**行を追加、優先度セクションの総当たり注記、**計算中バナーに進捗%（`CalcProgress`）、失敗時のエラー表示（`LastSolveError`）＋自動フォールバック廃止、Debug タブに旧フォールバック条件（combo budget／auto CPU fallback）**、**プリセット適用時に `LastUsedPreset.Config` へ再リンク（設定オブジェクト切り離しでスコアモード等が保存されないバグ修正）、計算開始時に `Settings.Save()`（異常終了対策）**、スライダーのホバー塗りつぶし修正、UI文字列の `GetLocalized` 化 | 最も衝突しやすい。**追加した行は残し**、大本の構造変更に合わせて配置し直す。文字列は `GetLocalized("Module_*")` を維持 |
| `BPSR-ZDPS/Managers/ModuleOptimizer.cs` | `Solve()` に `Gpu` 分岐＋**進捗コールバック引数**（自動フォールバックは廃止。`DebugAllowCpuFallback` 有効時のみビームサーチへ・`UsedCpuFallback` フラグ）、**NormalV2/Gpu の前段で `PrepareCandidates`（重複制限＋支配フィルタ）**、`FilterModulesWithStats` の総当たり（`BruteForceAllModules`）対応＋**合計リンク値カットオフ（`ModuleTotalCutoff`／`PassesModuleTotalCutoff`）**、`FiveModulesLoop` の合算バグ修正、ループ末尾の `-1` ガード | Gpu 分岐は薄いフック。大本の `Solve` に合わせて再配置。バグ修正は upstream へPR提案推奨 |
| `BPSR-ZDPS/Managers/ModuleOptimizer.V2.cs` | `NormalV2` の結果マッピングを最大10対応（`ModuleSet.FromValues` ループ化） | 大本のマッピング構造に追従しつつ10対応を維持 |
| `BPSR-ZDPS/Managers/ModuleOptimizer/ModuleSolver.cs` (base) | `GetStatMul` にレジェンダリ倍率（`Config.LegendaryStatMultiplier`）、`PossibleStats` 逆引き保持、**`ProgressCallback`（進捗0..1シンク）追加**、`ModuleSetIndices`/`ResloveResults` を最大10対応 | 大本のビーム系改修に合わせて再適用 |
| `BPSR-ZDPS/Managers/ModuleOptimizer/ModuleOptimizerBeam.cs` | スコア式（順位ブースト・レジェ倍率）。**`ScoringModel` で分岐: Enhanced=オーバーキャップ廃止（ブレイクポイントのみ・上限20）／Original=`CalcScore` に超過分加点を復活**（※旧 presence タイブレーク/`PackFactor` は「優先度0の効果が結果に出ない」ため廃止。ReqLevel=0 も数値目標）、`ScoreMode.CombatPower` 対応（LinkTotalFight 全体項）、**distinct 判定は常に全stat・閾値1（優先度設定時に結果が1件に潰れるバグ修正）**、**深さベースの進捗報告**、`StatDifference` の `=`→`-` バグ修正、**精度改善: 枝刈りヒープの優先度キー統一（混在による誤破棄・実行毎の結果揺れを修正）／同一セット（順列違い）の重複排除で実効ビーム幅を回復／Exactly への両側誘導進捗／探索後の局所探索（1/2-swap 山登り `RefineBeam`）** | GPU(HLSL) と同一スコア式を維持することが最重要。3者(HLSL/Beam/Gpu)で式一致 |
| `BPSR-ZDPS/Shaders/ModuleSolver.hlsl` | ZScore のオーバーキャップ余剰点 `(tv-bp)` を削除（ブレイクポイントのみ評価）。**`OriginalScoring` フラグ(cbuffer)で分岐: 1=超過分加点(upstream)／0=改造版**（※presence パッキング/`PackFactor` は廃止済み）。**列挙はオドメーター方式（スレッドごとに連続 rank 区間を割当・初回のみアンランク・先頭K-1個の部分和を維持）＋ModuleStats はバイト詰め(uint に 4 値)・per-stat 配列は4の倍数にゼロパディング** — 従来比で数倍〜10倍高速。**`CollectMode`(cbuffer)=1 で「しきい値以上の全組み合わせを AppendBuffer へ収集」（キャッシュ用・ゲート無視）**。**TDR対策の分割実行: cbuffer に `IBase`/`RankStart`/`RankEnd` を追加し、(i,group) ごとの top-K を Output にスライス間で蓄積(thread0 が前回値からマージ開始)**。注意: 事前バリアのループは fxc の X3663/X4026 制約（uniform 境界・可変分岐内にループを置かない）を満たす形を維持する。**Exactly ゲートは per-stat 判定（全 Exactly stat が raw 一致。旧 any-one-matches は複数 Exactly 指定で違反構成を通すバグ）**。`THREADS` は 256（C# 側定数と一致必須）。ランタイムコンパイルのため編集＋再ビルドで反映 | Beam の `CalcScore`／`BuildStatScoreLookup` と必ず一致させる |
| `BPSR-ZDPS/Managers/Gpu/ModuleOptimizerGpu.cs` / `GpuComputeContext.cs` | **TDR対策: `Dispatch` を COMBO_BUDGET(400万combo)単位の小分けディスパッチ＋毎回 `Flush()` に分割。`CancellationToken` を受け、キャンセルは次スライスで停止し部分結果を返す。スライスごとに進捗コールバック（処理済みcombo/総combo）。`DispatchCollect`（Append UAV＋カウンタ読み戻し）でキャッシュ用プールを収集。`MergeTop10` はステータスプロファイルで重複除去。自動CPUフォールバックは廃止（失敗は例外→UIがエラー表示）。旧フォールバック条件は `DebugMaxGpuCombos`／`DebugAllowCpuFallback`（Debug タブ専用）、スライス予算は `DebugComboBudget` で上書き可（テスト用・固定化）。**適応スライス: 実測スループットから約30ms/パケットへ自動調整（イベントクエリ同期で計測・16スライス毎に再調整・上限256M combo）。占有率: THREADS=256 x GROUPS_X=1024（262Kスレッド/dispatch、実測~2.5倍）** | HLSL/Beam と3者で式を一致させる |
| `BPSR-ZDPS/Managers/Gpu/ModuleSolverCache.cs`（新規） | **`PrepareCandidates`＝`LimitDuplicates`＋支配フィルタ（別のK個以上に全statで劣るモジュールを除外。スコア単調性を実行時検証・Exactly指定時は無効・結果は厳密に同一）。総当たりキャッシュ: フル計算時に「10位スコアからXX%以内」の全組み合わせをプール保存し、インベントリ・対象列・K が同じ間は CPU 再採点で即答（ゲートのみの変更は10件揃えば厳密、重み変更は近似フラグ）。同一設定の再実行はメモ化で即答。UI から `PoolCacheStatus`/`ClearPoolCache`** | フォーク独自ファイル。衝突しない |
| `BPSR-ZDPS/DataTypes/Modules/SolverConfig.cs` | `ScoreMode` / `OrderBoostStrength` / `LegendaryStatMultiplier` / **`BruteForceAllModules`** / **`ScoringModel`(Enhanced/Original 切替)** / **`ModuleTotalCutoff`(合計リンク値カットオフ)** フィールド追加（GPU/CPU の選択は `ModuleWindowSettings.ComputeBackend` 側に保存） | フィールド追加のみ。ほぼ自動マージ |
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
