# Project Rules

- Releases are done with the `/release` command (see `.opencode/commands/release.md`), which updates `CHANGELOG.md` / `CHANGELOG-ja.md` / `CHANGELOG-zh-CN.md` and creates + pushes the git tag. Do not edit those changelogs by hand or per-change; they are touched only at release time via `/release`.
- DO NOT write comments unless it is not clear from the code. Only to explain WHY of the code, never WHAT.
- NEVER find scene references with `GameObject.Find("...")`; assign them through a public/serialized field in the Inspector instead, so renaming a GameObject cannot silently break the wiring.
