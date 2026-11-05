# Dhole — Mandatory Instructions for Codex and Contributors

These rules apply to DholeWeb, DholeApiGateway, all Dhole microservices and every future component.

1. **SemVer:** Every change included in a release must bump global and component versions (`MAJOR.MINOR.PATCH`). Never decrease or reset versions. Synchronize `version.json`, native metadata (`package.json`, lockfile, `Directory.Build.targets` or equivalent), and `CHANGELOG.md`.
2. **Git policy:** Never develop directly on `develop` or `master`. Create intermediate branches per repository, accumulate one coordinated batch, integrate first into `develop` and validate staging, then promote the same release to `master`. Do not modify `main` without explicit permission.
3. **Production safety:** Do not deploy production without explicit authorization. Audit deployment triggers before any `master` merge.
4. **Realtime:** Current and future Dhole services must participate in the SignalR/Redis Streams architecture with durable Outbox/Inbox, idempotency, reconnect reconciliation and REST compatibility.
5. **Verification:** Run build, tests and CI version gates. Check the central release manifest; record previous/new versions, commits, PRs, staging evidence and rollback plan.
6. **Required checks:** Configure `Dhole semantic version gate` as a required status check in GitHub branch protection for `develop` and `master`. A workflow alone does not enforce branch protection.

A documentation-only repository is exempt from executable assembly metadata, not from release tracking.
