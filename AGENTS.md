# Project Rules

- NEVER edit `CHANGELOG.md` / `CHANGELOG-ja.md` / `CHANGELOG-zh-CN.md`. The changelogs are updated only once by the owner at release time, not per-change.
- DO NOT write comments unless it is not clear from the code. Only to explain WHY of the code, never WHAT.
- NEVER find scene references with `GameObject.Find("...")`; assign them through a public/serialized field in the Inspector instead, so renaming a GameObject cannot silently break the wiring.
