# Work packages

Each file here is one package of work: a goal, its dependencies, the interface it must expose, acceptance criteria, and the files it touches. The status of every package is in the table in [`../ROADMAP.md`](../ROADMAP.md). Point an agent at a package id ("execute M1-04") or at a milestone ("do everything in milestone 1") and this is what it follows.

## Rules for anyone working a package

1. **Dependencies first.** Start a package only when every package in its *Depends on* line is **Done** in the roadmap table. Packages with no unfinished shared dependency can be worked in parallel by different people or agents.
2. **One package, one branch, one pull request.** Branch from `main` as `plan/<id>`, for example `plan/M1-04`. The pull request gate must be green: format, build, tests and the server image, all on Linux. The Windows jobs cost several times as much per minute and no longer run per commit; run the full gate from the Actions tab before a release, and fix what it finds before the tag. Merge it yourself when the gate is green.
3. **Scope is the file.** Do what the package says and nothing beyond its *Touches* list plus tests. If the plan is wrong, say so in the pull request and do the smallest correct thing rather than widening scope silently.
4. **Interfaces are contracts.** Table names, columns, routes, headers and record shapes named in a plan are what other packages build against. Follow them exactly; raise mismatches instead of renaming.
5. **Update the status.** Set the package to **In progress** when you start, **In review** when the pull request opens, **Done** after merge.
6. **Commits carry the human author only.** No `Co-Authored-By`, no "Generated with", no tool attribution in commit messages, pull request text, code or comments.
7. **Format and test before pushing.** `dotnet format`, `dotnet test`, and `deploy/smoke-test.sh` against a local image for server packages.

## File layout

```
M<milestone>-<nn>-<slug>.md
```

Sections in every file: Goal, Context, Scope (In / Out), Interface, Steps, Acceptance criteria, Verification, Touches. Release packages (`M*-release-*.md`) also carry the version bump and the documentation checklist.
