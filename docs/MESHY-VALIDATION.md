# Optional Meshy distribution: offline validation

Validation date: 2026-10-08–09 (JST). Runtime revision: `4fd899dda9ed5b76252d2cd76f85871f522d7d4a`.

This record covers the fork implementation, not an upstream release or public NuGet inclusion. Setup and usage are in [the bundled runbook](../skills/codex/meshy-resoloop/references/runbook.md).

## Environment and executed checks

- .NET SDK 10.0.201, uv 0.12.23, wrapper Python 3.14.4, Node.js 24.21.0.
- Minimum Python 3.12.15 was also exercised for the pre-fix full suite and CI-equivalent workflow/doctor suite.
- Meshy CLI 0.4.0 was probed locally for version; no authentication or API request was made.
- Real conversion used Blender 5.0.1 and isolated NumPy 2.5.3 for Blender's Python 3.14, through an explicitly selected local launcher. It did not rely on a create-reso-world checkout at execution time.

| Check | Result |
| --- | --- |
| `dotnet test ResoLoop.slnx -c Release` | 453 unit + 19 default non-live integration-project tests passed; no failures or skips |
| Source wrapper full Python suite | 237 passed, no skips |
| Wrapper deployed by actual packed/installed ResoLoop | 237 passed, no skips, including 54 real conversion-fixture cases |
| `dotnet pack` and isolated `dotnet tool install` | Installed the locally generated nupkg using a local-only source and fresh package cache |
| Packaged source, installed DLL and scripts | Matched the built package; no Python bytecode/cache files shipped |
| New `init` / `skills sync --check` | Succeeded without Node, Meshy, Python, uv or an API key on the execution PATH |
| Pre-migration v1/v2 locks | Updated with Meshy files; user data retained |
| Read-only sync check and doctor | File bytes and file/directory mtimes unchanged |
| Missing file plus user-edited sibling | Conflict detected before any write or lock update; reconciliation then restored the missing file |
| User operation/journal/GLB/edited blend/state/notes | Preserved across synchronization |
| Deployed help / offline plan | Ran with an explicit interpreter, no Meshy/Node/key, from a project path containing spaces |
| Real Node journal guard | Executed from relocated skill paths containing spaces; accepted task ID retained, synthetic key excluded |
| Relocated textured/hierarchy bundle | All four asset sources stayed relative, resolved inside the relocated bundle and passed installed `resoloop validate` |
| Invalid UTF-8 stdout/stderr | Stable doctor checks/repair and phase errors; process descendants stopped; failed conversion remained unpublished |
| Python syntax / Ruff F / environment-aware Pyright | Passed; Pyright checked four runtime modules and both new test modules with the actual optional test interpreter |

Test subprocess environments are explicitly minimized to avoid inheriting unrelated credentials in diagnostic failure reports. The API tests use local fake executables and synthetic sentinels, not real service credentials.

The controller independently reran the final .NET suite, all Python tests against the newly installed ENTRY, and relocated-bundle validation. Final branch review and scoped re-review found no unresolved Critical/Important issues; both minor findings were fixed in `4fd899d`.

## Reproducing the wrapper tests

The Python test dependency is optional and unrelated to normal init/sync:

```bash
uv run --group dev pytest tests/meshy/test_meshy.py tests/meshy/test_meshy_doctor.py tests/meshy/test_meshy_process_output.py -q
```

For the full real conversion suite, install Blender and NumPy into Blender's own Python environment and make the tested ResoLoop available on PATH:

```bash
uv run --group dev pytest tests/meshy -q -rs
```

To test an installed distribution instead of the source scripts, set `RESOLOOP_MESHY_ENTRY` to the initialized project's `.agents/skills/meshy-resoloop/scripts/meshy.py`. All sibling module/guard references in the tests follow that ENTRY. Set `RESOLOOP_BLENDER_EXECUTABLE` only when deliberately selecting a particular Blender installation. Required fixture skips are not acceptance.

## Limits and deployment status

- Linux wrapper support only. Windows/macOS help and platform rejection were simulated on Linux, not claimed as native wrapper support. Windows init/sync continues through the existing .NET packaging path.
- Standard Blender's NumPy import was observed during the task, but the required full conversion suite explicitly used the isolated environment. A complete standard-environment rebuild/acceptance was not performed.
- No paid generation, image upload, API authentication, real-network recovery, Resonite live operation, Windows texture display, save/reload or state convergence was tested.
- The Linux public CI job covers workflow/doctor logic without Blender. The existing Windows job builds, tests, packs, installs, initializes and checks synchronization. Their actual run results belong to the PR checks; they do not prove the unexecuted live checks above.
- Fork merge, upstream integration/PR, public NuGet inclusion and Issue #50's remaining live acceptance are separate stages and were not performed here.
