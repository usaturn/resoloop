# Meshy から ResoLoop / Resonite へ

Meshy は有機的・曖昧な形の小物や画像参考からの生成に向く。寸法、機械構造、指定した低ポリ形状を厳密に再現する場合は Blender を使う。
この手順は公式 `meshy-cli` を [ローカル wrapper](../scripts/meshy.py) から呼び、生成物を Blender で編集可能にして ResoLoop bundle を作る。生成とワールドへの適用は別の承認が必要。

## 配置と対象プロジェクト

ResoLoop の `init` は `.agents/skills/meshy-resoloop/` へこの手順と隣接 scripts を配置する。
既存プロジェクトは `resoloop skills sync --check` で確認し、差分を確認してから `resoloop skills sync --update`。
同期するのは配布ファイルだけ。operation / journal / 元 GLB / 生成物 / 編集済み blend / state / 利用者の追加ファイルは同期・削除しない。
手編集した配布ファイルがあれば、欠落ファイルの復旧より先に全体が停止する。バックアップして差分を整理し、競合を解決してから update する。

操作順序は制作前計画（`resonite-model-preproduction` の Build Handoff）→ [meshy-resoloop](../SKILL.md) → Blender 仕上げ（`resonite-blender`）→ 適用（`resonite-build`）。
`not-authorized-at-preproduction-stage` なら停止する。Build Handoff の承認は有料操作・画像外部送信・ワールド適用の許可ではない。
相対リンクはこのスキル内で解決し、対象プロジェクトの `AGENTS.md` の所有範囲・安全規約も守る。

wrapper は **Linux のみ**。WSL2 は Linux 側で動かす。macOS / Windows native の生成・復旧・ダウンロード・変換は未対応。
Windows の init / skills sync と、完成 bundle の validate / diff / apply は wrapper とは別で利用できる。
以下の Linux 例の絶対パスは実際の配置先・対象に置き換える。`--project` が書き込み先を決め、`.resoloop.json` が必要。
project / operation 内の symlink と project 外の入出力は拒否される。

```bash
WORLD="/absolute/path/to/project"
SKILL="$WORLD/.agents/skills/meshy-resoloop"
meshy_op() { uv run --no-project "$SKILL/scripts/meshy.py" --project "$WORLD" "$@"; }
meshy_op --help
meshy_op plan --help
meshy_op convert --help
```

## 任意依存と無認証確認

Meshy を使わない利用者に追加依存・キーは不要。init / skills sync はファイル配置のみで、導入・認証・API 呼び出しを行わない。
[ライセンスと由来](licenses.md)も確認する。依存の導入や更新は利用者が選んで行い、自動では実行しない。

- wrapper: Python >=3.12 と uv。通常の wrapper は標準ライブラリだけを使う。
- offline plan: Meshy CLI / Node / API キー不要。
- API 操作: 検証済み `meshy-cli@0.4.0`、Node.js >=22.12.0（推奨 Node 24）、環境変数のキー。未検証版へ自動更新せず、生の `meshy update` は使わない。
- offline convert: Blender と、その Python が読める NumPy、ResoLoop exporter。Meshy CLI / キー不要。`resoloop blender find/run/export` を使う。
- ResoLoop: NuGet の最新公開版（prerelease を含む）。通常セットアップで版を固定しない。

利用者による導入後、キーを外した version / help だけを確認する。login / status / list / balance / create はセットアップ確認に使わない。

```bash
uv --version
uv run --no-project python --version
node --version
# Meshy を使う利用者が手動で選んだ場合のみ:
npm install --global meshy-cli@0.4.0
env -u MESHY_API_KEY meshy --no-update-check --version
env -u MESHY_API_KEY meshy --no-update-check --help
# ResoLoop の更新も利用者が選んだ場合のみ:
dotnet tool update --global ResoLoop --prerelease --allow-downgrade
resoloop --version
resoloop blender find --json
```

実測 version を作業記録へ残し、ResoLoop 更新後は help と `resoloop skills sync --check` を確認する。スキルの更新は差分を見てから行う。
課金前にはキー・Resonite 接続を使わず、local fixture の生成・変換・export・bundle validate を実際に検証する。
必要な検証が skipped なら、終了コード0でも事前確認は未完了で submit へ進まない。
Blender / NumPy / exporter が不足すれば停止し、利用者が依存導入を判断する。一時的な検証環境の成功を標準環境の成功と読み替えない。

## キー: 利用者が zsh で非表示入力する

`MESHY_API_KEY` は**環境変数だけ**で渡す。キー値をチャットで要求しない。キー / env / credentials ファイル、コマンド引数、ログ、スクリーンショットへ残さない。
env ファイルでキーを永続化しない。`meshy auth login` など credentials 保存を伴う操作もしない。
以下は利用者が対話 zsh で手動実行する。入力値は非表示で、コマンド履歴には値を含めない。shell tracing / 端末記録は先に止める。

```zsh
unsetopt XTRACE
unset MESHY_API_KEY
read -rs 'MESHY_API_KEY?Meshy API key (非表示): ' && export MESHY_API_KEY
printf '\n'
[[ -n ${MESHY_API_KEY:-} ]] && printf 'Meshy key: set\n' || printf 'Meshy key: missing\n'
# このシェルから対象プロジェクトのエージェントを新しく起動する
```

export はこのシェルの**新しい子プロセスだけ**に継承される。すでに起動しているエージェント、端末管理 server / pane、エディター本体・拡張ホストには後から届かない。
新しい pane / terminal でも既存 server / エディターの環境から作られる場合は継承されないので、その中で手動入力してエージェントを起動する。
既存プロセスへのキー注入や設定変更を自動で行わない。確認は有無だけとし、`env` / `printenv` / `echo "$MESHY_API_KEY"` を使わない。
終了時に `unset MESHY_API_KEY`。すでに継承した子からは消えないので、不要な子も終了する。環境を作り直した後は再入力する。

wrapper は project 内の隔離した config / journal を使い、既存の利用者 credentials へ fallback しない。
`.resoloop/meshy-cli/env-only-no-credentials.json` は**作成されてはいけない**パスで、存在すれば停止する。
wrapper の redact / journal guard は安全策であり、prompt やファイル名に秘密情報を書いてよいという意味ではない。

## 課金の承認

キーの設定・「Meshy を使いたい」だけでは生成を許可したことにならない。submit の前に以下を明記して承認を得る。

- 対象プロジェクト、生成対象・用途、operation 名、prompt または送信する local image。
- 送信件数と予算上限（credits または通貨単位）、モデル、texture / PBR / 解像度。`latest` の実際のモデル・料金は変わり得るため送信前に公式料金を確認する。
- 形状確認後の refine を含むか、何を満たしたら次へ進めるか。text preview と refine は**別の課金操作で計2件**。やり直しは追加件数として再承認する。
- 画像の外部送信の許可。生成結果の利用条件・権利も確認する。

wrapper は予算・承認内容を自動で検証しない。`--confirm-paid` は実行ゲートであって承認記録の代わりではない。
承認済み件数・予算を超える自動反復は禁止。残高の read-only 照会も、利用者の明示指示がある場合だけ行う。

```bash
meshy_op balance
```

## Text: preview と refine を分ける

以下は操作例であり、**submit は該当 operation の承認後だけ**実行する。
plan は offline で `operation.json` に request を保存する。同名 operation は上書きしない。
wrapper の prompt 上限は600文字。boolean は `true` / `false`、texture resolution は `2k` / `4k` / `8k`。

```bash
meshy_op plan --operation vessel-preview --kind text \
  --prompt 'a small irregular ceramic vessel, no lid' --model latest --texture false
# content/generated/meshy/vessel-preview/operation.json の request を確認し承認
meshy_op submit --operation vessel-preview --confirm-paid
meshy_op status --operation vessel-preview
meshy_op wait --operation vessel-preview --timeout 600
meshy_op download --operation vessel-preview
```

preview は無 texture の形状確認用。成功した preview の `task.task_id` を取り、形状をレビューしてから別 operation を作る。
refine の submit は対象が `SUCCEEDED` の text preview かを確認する。`PREVIEW_TASK_ID` は実 ID に置き換える。

```bash
meshy_op plan --operation vessel-refine --kind refine --preview-task-id PREVIEW_TASK_ID \
  --prompt 'unglazed pale ceramic' --model latest --texture true --pbr true --texture-resolution 2k
# refine の request・追加1件・残予算を確認し承認
meshy_op submit --operation vessel-refine --confirm-paid
meshy_op wait --operation vessel-refine --timeout 600
meshy_op download --operation vessel-refine
```

## Image: project 内の local file

`--image` は project 基準の相対パスまたは project 内の絶対パス。URL、project 外、symlink は使わない。
画像を project 内に置いてから plan し、送信許可を得る。plan は image の path と SHA-256 を保存し、submit 前に変更を検出する。
image は既定で texture なし。texture が必要なら明示的に `--texture true` を指定する。refine のような2段階を自動で追加しない。

```bash
meshy_op plan --operation image-vessel --kind image --image content/references/vessel.png \
  --model latest --texture true --pbr true --texture-resolution 2k
# request と画像の送信・1件の課金を承認
meshy_op submit --operation image-vessel --confirm-paid
meshy_op wait --operation image-vessel --timeout 600
meshy_op download --operation image-vessel
```

## 状態・journal と再開

| 保存先（project 基準） | 内容 |
| --- | --- |
| `content/generated/meshy/<operation>/operation.json` | operation UUID、request、stage、task ID / status、downloads、conversion |
| `content/generated/meshy/<operation>/source/` | CLI が報告した取得ファイル（GLB 名は固定しない） |
| `.resoloop/meshy-cli/operations/` | 公式 CLI の operation journal / locks |
| `content/generated/meshy/<operation>/converted/` | 編集用 blend / images、変換 report、bundle |
| Windows で明示する `.resoloop/state/<ownership>.json` | ResoLoop の ownership と適用結果。Meshy の journal とは別 |

project は利用者が管理する永続ディレクトリに保存する。環境更新後も同じ絶対パスの project、operation、journal、source を残し、キーだけ再入力する。
manifest 内の image / download / conversion は絶対パスを持つため、別 checkout への移動をそのまま再開できるとは扱わない。
`.resoloop/` は ignore 対象であって消してよいものではない。生成物の `content/generated/meshy/` は一律 ignore ではないので、画像・生成物の権利と容量を確認せず `git add .` しない。

- task ID が既知: `status` / `wait` で再開。wait の timeout は再生成理由にならない。
- `submitting` / `unknown`、create の応答不明、通信切断: **submit を再送しない**。同じ operation UUID、journal と Meshy 側の記録を照合する。
- wrapper の list は recent task ID / status を返すだけなので、候補が複数ならそれだけで対応を推測しない。特定できなければ停止して利用者へ報告する。

```bash
meshy_op list --resource text-to-3d
meshy_op list --resource image-to-3d
# 同じ operation の送信と確定できた task ID だけを紐付ける
meshy_op attach --operation vessel-refine --task-id CONFIRMED_TASK_ID
meshy_op status --operation vessel-refine
meshy_op wait --operation vessel-refine --timeout 600
meshy_op download --operation vessel-refine
```

attach は GET で返された ID、task の type / resource が operation と一致することを保存前に確認する。
text は `text-to-3d-preview`、refine は `text-to-3d-refine`、image は `image-to-3d` が必要。
refine に plan の `preview_task_id` 自体を紐付けることも拒否する。これは人間による送信内容の照合を代替しない。

partial / failed download は成功扱いにしない。同じ成功 task の `download` は再実行できる（source 内へ再取得する）。
未指定の download 待ち上限は従来どおり120秒。大きな資産や低速回線で `cli_timeout` になった場合は、同じ operation の取得だけを `--timeout`（非負の有限秒数）で延長する。新しい task を生成せず、submit は再送しない。

```bash
meshy_op download --operation vessel-refine --timeout 600
```

manifest の file の asset key を保持し、key 付き GLB では `model.glb` を一意に選ぶ。
`model.pre_remeshed_glb` は backup であり、主 GLB の代わりには使わない。主 key の欠落・重複は download 完了や変換成功として扱わない。
すべての GLB が key 無しの古い schema1 manifest に限り、従来どおり単一 GLB を使用する（複数なら拒否）。ファイル名や一覧の順序では選ばない。
`downloads.state == completed` が必要で、選んだ主 GLB の実 path、symlink の有無、byte 数、SHA-256 を変換開始時と処理後に確認する。
FAILED / CANCELED task を新 operation でやり直す場合も追加課金の承認が必要。
convert 中断後に `converted/` だけが残った場合は消してやり直さず、report / manifest を照合して手動復旧を判断する。

## Convert と素材の制限

`convert` は offline。Meshy API、Resonite 接続、apply は行わない。`--parent` は**以前に確認済みの selector**で、convert は live 検証しない。
height は model 全体の実頂点 bounds の高さ（m）、yaw は Blender Z 軸まわり（度）。ground は底面、center は bounds 中心を原点に置き、XY は中央へ揃える。
寸法・向き・原点は目的に合わせてレビューする。複数 object を結合・自動 decimate しない。元 GLB は保持し、既存 `converted/` は上書きしない。

```bash
meshy_op convert --operation vessel-refine --height 0.30 --yaw 0 --origin ground \
  --parent 'path:["Root","GeneratedProps"]' --name Vessel
```

**この既定例は通常 culling の保持不可で拒否される。** ResoLoop exporter は `Culling` を出力せず、glTF の `doubleSided`（省略時 false、素材省略時も片面）を保持できない。
片面・両面のどちらでも損失を黙認しない。利用者がこの制限を了承したときだけ、同じ convert に `--allow-culling-change` を追加する。
これは culling の損失だけを許可し、他の拒否条件を解除しない。runtime の culling の既定値や見た目を推測しない。

```bash
# culling の損失を別途了承した場合のみ
meshy_op convert --operation vessel-refine --height 0.30 --yaw 0 --origin ground \
  --parent 'path:["Root","GeneratedProps"]' --name Vessel --allow-culling-change
resoloop validate "$WORLD/content/generated/meshy/vessel-refine/converted/bundle/model.apply.json" --json
```

変換 report の `material_culling` / `warnings` に素材ごとの元の `doubleSided` と保持不可が記録される。
bundle の `report.json` の warnings が空でも culling が保持できた意味ではない。変換 report も必ず読む。

- 対象: static mesh / Empty の階層、active UV、opaque Principled。direct image の Base Color / Emission は sRGB、scalar map / tangent normal は Non-Color。
- glTF packed MR の B=metallic / G=roughness を分離した画像へ変換し、`resoloop blender export --preserve-hierarchy --pack-pbr` で再出力する。
- 非対応: alpha BLEND / MASK、transmission、rig / skin、shape key、animation、modifiers / constraints、非対応 shader や node 接続、外部 GLB resource URI、occlusion texture 等。名前付きで停止し、勝手な bake / texture 無視 / 静止画化で成功扱いにしない。
- report で mesh 数、三角形数、bounds、素材・画像寸法 / 色空間を確認する。拒否時は元データを残して、別途承認した Blender 修正を行う。編集済み blend を再 export する場合も**新しい出力ディレクトリ**を使う。

成功出力は `converted/model.blend`、`converted/images/`、`converted/conversion-report.json`、`converted/bundle/`。
bundle は `model.apply.json`、`report.json`、mesh / texture asset を含む。apply document の `assets.*.source` は bundle 基準の相対パス。
変換・export の成功は Windows 上の texture 表示や実機への適用成功を保証しない。

## Windows handoff: bundle 一式と state 一つ

Linux 側から適用しない経路では、**bundle ディレクトリ全体**を Windows の新しい作業フォルダへコピーする。
`model.apply.json` 単独では texture / mesh が足りない。asset の相対配置を変えない。
編集用に converted の blend / images / conversion-report と operation.json、source の hash / task ID も渡す。Linux の絶対パスを Windows の取得先として使わず、bundle の相対 asset を使う。
キー・credentials は渡さない。Meshy journal は元 project に保持し、Windows で生成を再送しない。

Windows 10 / 11 に .NET 10 SDK、Resonite / ResoniteLink と **PowerShell 7.3 以上**を用意する。[新しい PowerShell の導入](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows)を推奨し、`pwsh.exe` を起動する。標準の Windows PowerShell 5.1（`powershell.exe`）はこの手順の対象外。
native command の Legacy 引数渡しでは `path:["Root","GeneratedProps"]` の埋め込み二重引用符が失われる。7.3 以上で `$PSNativeCommandArgumentPassing = 'Standard'` を明示し、selector をそのまま渡す。根拠は [about_Parsing](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_parsing) と [about_Preference_Variables](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_preference_variables#psnativecommandargumentpassing)。
以下は**利用者が手動実行する手順**で、Windows 上では未実行。最初の version guard と設定から順に、すべてのブロックを**同じ `pwsh.exe` セッション**で実行する。guard が停止したら後続を実行せず、新しい shell に切り替える。latest へ更新し、PATH 上の別版でなく global tool を確認する。version を固定する install 例にはしない。

```powershell
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [version]'7.3') { throw 'Use PowerShell 7.3 or newer (pwsh.exe), not Windows PowerShell 5.1' }
$PSNativeCommandArgumentPassing = 'Standard'
$PSVersionTable.PSVersion
$PSNativeCommandArgumentPassing
dotnet --list-sdks
dotnet tool update --global ResoLoop --prerelease --allow-downgrade
if ($LASTEXITCODE -ne 0) { throw 'ResoLoop update failed' }
$ResoLoop = Join-Path $HOME '.dotnet\tools\resoloop.exe'
dotnet tool list --global
$WindowsResoLoopVersion = & $ResoLoop --version
if ($LASTEXITCODE -ne 0) { throw 'ResoLoop cannot run' }
$WindowsResoLoopVersion
& $ResoLoop --help
```

Linux 側と Windows 側の実測 version、確認日を handoff の作業記録へ残す。latest は動くので、版が違う場合は help と offline validate を再確認する。
適用先 URL は ResoniteLink の実値、parent は事前に確認済みの正確な selector に置き換える。
apply には `--parent` がないため、**bundle の `slot.parent` が指定先と一致すること**を確認する。違えば適用せず、新しい bundle を作る。

```powershell
$Handoff = 'C:\ResoniteWork\meshy-vessel'
Set-Location $Handoff
$VersionRecord = Join-Path $Handoff ('resoloop-windows-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
@((Get-Date -Format o), $WindowsResoLoopVersion) | Out-File -FilePath $VersionRecord -Encoding utf8 -NoClobber
$Bundle = Join-Path $Handoff 'bundle'
$Apply = Join-Path $Bundle 'model.apply.json'
$Url = 'ws://localhost:PORT'  # 実ポートへ置き換える
$Parent = 'path:["Root","GeneratedProps"]'  # 実際の確認済み selector
# この ownership に対する唯一の state。再適用・再開・他の担当も同じファイルを使う
$State = Join-Path $Handoff '.resoloop\state\meshy-vessel.json'
New-Item -ItemType Directory -Force (Split-Path $State) | Out-Null
$Document = Get-Content -Raw $Apply | ConvertFrom-Json
if ($Document.slot.parent -cne $Parent) { throw 'Parent mismatch: do not apply' }
$Document.ownership.key  # 他の所有範囲と衝突しないことを利用者が確認
& $ResoLoop validate $Apply --json
if ($LASTEXITCODE -ne 0) { throw 'Invalid bundle' }
& $ResoLoop inspect $Parent --url $Url --state $State --json
if ($LASTEXITCODE -ne 0) { throw 'Parent verification failed' }
& $ResoLoop diff $Apply --url $Url --state $State --json
if ($LASTEXITCODE -ne 0) { throw 'Diff failed' }
```

diff の対象・件数・所有範囲と parent を利用者が確認し、承認を得てから次へ進む。所有外の変更が出たら停止する。
Linux / Windows で別々の state を作らず、一つの正式な保存先を決めて引き継ぐ。既存 state がある場合はそれを使用し、空の state で既存モデルを再作成しない。
担当を切り替える場合はその state を引き継ぎ、同時に apply しない。state、ownership key、parent は適用後に勝手に変えない。`--adopt` / `--prune --yes` はこの手順に含めない。

```powershell
# 最初の diff の変更を承認した後だけ
& $ResoLoop apply $Apply --url $Url --state $State --json
if ($LASTEXITCODE -ne 0) { throw 'Apply failed: retain state and inspect before retry' }
& $ResoLoop diff $Apply --url $Url --state $State --json
if ($LASTEXITCODE -ne 0) { throw 'Post-apply diff failed' }
# 無変更であることを確認し、再適用の許可後だけ
& $ResoLoop apply $Apply --url $Url --state $State --json
if ($LASTEXITCODE -ne 0) { throw 'Repeat apply failed' }
& $ResoLoop diff $Apply --url $Url --state $State --json
if ($LASTEXITCODE -ne 0) { throw 'Repeat diff failed' }
& $ResoLoop test $Apply --url $Url --state $State --json
if ($LASTEXITCODE -ne 0) { throw 'Test failed' }
```

`test` は宣言された assertion を検査するもので、texture の見た目を証明しない。bundle に assertion がなければ成功でも実機受入の代わりにならない。
probe は使わない。変更が残る場合は「収束確認済み」とせず、diff / state を保存して原因を確認する。

### 実機受入 checklist

- [ ] Linux / Windows の CLI version、Windows の PowerShell version（7.3 以上）と `Standard` 設定、URL、parent、ownership key、唯一の state 保存先を記録。
- [ ] mesh と texture の全 asset が Windows に実在。変換 report の culling 警告を素材ごとに確認し、許可範囲を記録。
- [ ] 適用範囲が承認された parent 配下だけで、既存の所有外 object を変更しない。
- [ ] Resonite で寸法・向き・原点、複数 mesh / 階層、albedo / normal / metallic / roughness / emission を対象素材に応じて目視確認。texture 欠落や culling の見た目を確認。
- [ ] 同じ bundle・同じ state の再 apply 後、diff が無変更。重複 object がなく、適用・test の結果を保存。
- [ ] ワールドを保存して Resonite を再起動・再読込した後も、モデル・階層と素材・texture が保持されていることを確認。寸法・向きと素材の表示を再確認し、同じ bundle・同じ state の diff が無変更であることと確認結果を記録。

## 検証範囲の記録

help / offline plan / wrapper 回帰、実 Blender fixture / exporter、API 操作、Windows handoff、実機表示を分けて記録する。
ローカルの fixture 成功は有料 API や Windows の表示・同じ state の無変更再適用を証明しない。
未承認の API / live 操作を smoke test の名目で行わず、未実施・skip・依存不足を成功扱いにしない。

参照: [Meshy Text to 3D](https://docs.meshy.ai/en/api/text-to-3d)、[Image to 3D](https://docs.meshy.ai/en/api/image-to-3d)、[ResoLoop](https://github.com/orange3134/resoloop)。
CLI の例は配置済み wrapper help と導入済み ResoLoop help を優先する。
