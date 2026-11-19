---
description: Cut a release — update all changelogs (EN/JA/zh-CN) and create + push the git tag
---

Cut release $1 titled "$2" with these changes:

$3

Usage: `/release <version> "<title>" "<changes>"`, e.g. `/release 2.1 "Some Title" "- Add foo; - Fix bar"`. Changes are one bullet per change, separated by newlines or `;`.

Steps:

1. Verify `git tag -l v$1` is empty; if the tag already exists, abort and report it.
2. Read `CHANGELOG.md`, `CHANGELOG-ja.md`, and `CHANGELOG-zh-CN.md` at the repository root.
3. Prepend a new section to each file, directly under the title line, in the established format:
   `## <today's date, YYYY-MM-DD> - v$1 $2`
   followed by the change bullets. Use the English bullets from $3 for `CHANGELOG.md`. Translate each bullet into Japanese for `CHANGELOG-ja.md` and into Simplified Chinese for `CHANGELOG-zh-CN.md`, matching the style of the existing entries (keep established localized names such as 「Double or Nothing」 / “双倍或一无所有”). Keep the release title as-is in all three headers.
4. Create an annotated tag at HEAD: `git tag -a v$1 -m "Release v$1: $2"`.
5. Push it: `git push origin v$1`. If the push fails (e.g. no credentials in this environment), say so explicitly and leave the tag local.
6. If `git status` shows uncommitted changes, mention that they are not part of the tagged commit.

Do not edit the changelogs in any other situation; they are updated only at release time via this command.
