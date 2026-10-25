# AGENTS.md

Read [`global.md`](global.md) before changing anything: it has the build/test commands, the layout, and the rules
that break the build or the tests (translations table, embedded language files, offline/server-only semantics).

Short version:

- Build: `dotnet build -c Release`. Tests: `powershell -File tests\run-tests.ps1`.
- Any new or changed user-visible text requires `--cli translation-template .` and a rebuild.
- Keep changes small and consistent with the surrounding code; leave validation, backups and error handling intact.
- When a behaviour changes, update the torture test that asserts it.
