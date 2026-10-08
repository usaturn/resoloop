# ライセンス・由来・外部依存

## この配布物

このスキル・runbook・5 scripts は `usaturn/create-reso-world` の
`ce597301f797e011b9313792cb5a7a5ed4e0d104` を移植元とする。
著作者 `usaturn` の依頼・許可により `usaturn/resoloop` fork へ移植し、
ResoLoop の **AGPL-3.0-or-later** で配布する。ResoLoop 配布物の `LICENSE` を参照。

移植元:

- `.agents/skills/meshy-resoloop/SKILL.md`
- `docs/meshy-resoloop.md`
- `scripts/meshy.py`, `scripts/meshy_workflow.py`, `scripts/meshy_conversion.py`,
  `scripts/meshy_blender.py`, `scripts/meshy_cli_guard.mjs`
- 回帰テストの由来: `tests/scripts/test_meshy.py`, `tests/scripts/test_meshy_conversion.py`,
  `tests/fixtures/meshy/create_fixture.py`

移植元 root には LICENSE がない。この許可は上記の所有者による fork への移植についてであり、
移植元リポジトリ全体を第三者が無条件で再配布できるという説明ではない。
移植後の変更は ResoLoop の Git 履歴で確認する。

## 同梱しない任意依存

**Meshy CLI 本体、Node.js、uv、Python、Blender、NumPy は同梱しない。**
利用者が各配布元の条件を確認して導入する。init / skills sync は依存の導入・認証・通信を行わない。

Meshy CLI の npm metadata と公式 LICENSE を 2026-10-08 に確認した:

- package: `meshy-cli@0.4.0`
- license: **MIT**（ResoLoop の AGPL とは別の外部依存）
- engines: **Node.js >=22.12.0**
- 確認元: [npm metadata](https://www.npmjs.com/package/meshy-cli)、
  [公式 repository / LICENSE](https://github.com/meshy-dev/meshy-cli/blob/main/LICENSE)

Meshy CLI の自動更新や未検証版への切り替えは行わない。wrapper の journal guard は検証済み版の書き込みを前提とする。
第三者ソースを新たにコピーして内包したものではない。

生成結果の利用条件、画像の外部送信と権利、料金は依頼ごとに別途確認する。
スクリプトのライセンスやキーの存在は、有料操作・外部送信・ワールドへの適用の許可にはならない。
