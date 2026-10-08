---
name: meshy-resoloop
description: "Use when Meshy で小物を生成、画像から 3D 化、Meshy タスクを再開、または Meshy 素材を ResoLoop / Resonite に取り込む依頼がある場合。普通の Blender モデリングには使わない。"
---

# Meshy → ResoLoop

## 実行ゲート

1. 対象プロジェクトの `AGENTS.md` と、制作前計画がある場合は `resonite-model-preproduction` の Build Handoff を読む。`not-authorized-at-preproduction-stage` なら停止する。制作前計画の承認は課金・画像外部送信・ワールド適用の承認ではない。有機的・曖昧な形や画像参考には Meshy、寸法・機械構造・低ポリの厳密な再現には Blender を選ぶ。対象、用途、寸法、向き、原点、確認済み parent を決める。
2. [手順](references/runbook.md)と[ライセンス・依存](references/licenses.md)を読む。相対パスはこの SKILL.md の実体ディレクトリ基準。[CLI](scripts/meshy.py) は Linux / WSL2 の Linux 側で `uv run --no-project <skill>/scripts/meshy.py --project PROJECT COMMAND` と起動する。`<skill>` はこのスキルの実パス、`PROJECT` は対象プロジェクトの実パス。cwd に依存しない。依存は任意で、init / skills sync は導入・認証・通信を行わない。
3. `doctor` でローカル依存を無認証・読み取り専用で診断する。失敗時は check の修復手順を利用者が選ぶ。doctor 失敗でも、任意依存なしの offline `plan` は独立して実行できる。キーは有無だけを確認する。値を求めたり表示・保存したりしない。未設定なら手順の zsh 非表示入力を利用者が行い、そのシェルからエージェントを再起動する。既存のエージェント・端末管理プロセスには後から継承されない。
4. 課金前に local fixture の実変換・bundle validate と help で操作を確認する。skip や隔離環境だけの成功を標準環境の確認にしない。対応は `meshy-cli@0.4.0` / Node >=22.12.0。版検査は送信前に停止するため、不一致を応答不明として再送しない。生成対象、operation 名、送信件数、予算上限、モデル、texture / PBR / 解像度、画像の外部送信を明示した許可を得る。text preview と refine は別 operation・別送信で計2件。再生成は追加許可。キーがあるだけでは許可にならない。
5. `plan`（offline）→保存した request の確認→許可範囲内で `submit --confirm-paid`（一度）→`status` / `wait`→`download`。image はプロジェクト内の local file だけ。`balance` も自動で呼ばず、利用者の明示指示で実行する。wrapper を迂回した create / 認証操作は禁止。
6. 完全な download manifest の GLB だけを `convert`（offline）へ渡す。既存出力を上書きしない。元 GLB、個別 mesh・階層・texture を残し、変換 report の bounds / 三角形数 / 素材を確認する。仕上げは `resonite-blender` へ、ワールドへの適用は `resonite-build` へ引き継ぐ。
7. exporter は source culling を保持しないため convert は既定拒否。利用者がこの損失だけを承認した場合に限り `--allow-culling-change`。素材ごとの `material_culling` / `warnings` を読む。透明・transmission・rig・animation・非対応 shader の検査は解除されない。
8. Windows へ bundle 一式を渡す。URL、parent、ownership、共有 state 一つを確認して `diff`→変更内容の承認→`apply`。再 diff→承認済み再 apply / `test` と実機表示で受け入れる。未検証の live 操作を自動実行せず、所有外の編集・prune / adopt は行わない。

## 復旧と報告

- `content/generated/meshy/<operation>/operation.json` と `.resoloop/meshy-cli/operations/` の journal を保持。環境更新後も同じプロジェクトから再開する。
- `submitting` / `unknown` は**再送しない**。`list` と journal / Meshy 側の記録を照合し、確認できた task ID を `attach`。特定できなければ停止・報告。既知 task は `status` / `wait`、partial download は同じ task の `download`。
- 成果報告: operation / task ID、課金件数・予算、source / blend / bundle / report、parent / state、素材の拒否・許可、検証結果。実 API、標準環境の実変換、Windows の texture 表示・無変更収束は実測なしで成功扱いにしない。実機受入の未検証範囲を明記する。
