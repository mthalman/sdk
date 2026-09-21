---
coverage: Persistent repository gotchas, compatibility traps, and documented workarounds
---

# Known Issues

These are durable development gotchas, not a list of open product bugs. Follow linked
area guidance when it is more specific.

## Product Tests Can Exercise a Stale SDK

**Affected area:** [`test/`](../../test/), `artifacts/bin/redist/`

**Description:** Most integration tests exercise the SDK in the redist layout. Building
only a test project can leave production assemblies or targets in that layout stale.

**Workaround:** Use the [`run-tests` skill](../skills/run-tests/SKILL.md), which owns the
product-layout freshness decision and invokes area-specific deployment workflows where
applicable.

## Windows Builds Can Exceed Legacy Path Limits

**Affected area:** Full builds and generated intermediates on Windows

**Description:** Deep generated paths can cause misleading missing-resource failures.

**Workaround:** Enable Windows long paths and run `git config core.longpaths true`, then
retry. See the [Developer Guide](../../documentation/project-docs/developer-guide.md#building).

## Helix Uses a Different Filesystem Layout

**Affected area:** Tests that assume repository-relative paths or undeclared runtime files

**Description:** Helix publishes tests as tools and separates the SDK, work-item payload,
and correlation payload. A test can pass locally but fail when it depends on the checkout
layout, a machine-installed dependency, or an environment variable not propagated to the
Helix runner.

**Workaround:** Use `SdkTestContext` paths, deploy extra runtime files through
`TestExecutionDirectoryFiles`, and reproduce with the local Helix layout described in
[`repro-helix-failure.md`](../../documentation/project-docs/repro-helix-failure.md).

## Test Parallelism Is Disabled by Default

**Affected area:** MSTest projects under [`test/`](../../test/)

**Description:** Shared environment variables, current directory, console state, static
caches, and scratch paths have caused broad concurrency flakiness. Repository defaults
therefore set `MSTestParallelizeScope=None`.

**Workaround:** Do not raise parallelism globally. In projects that deliberately opt in,
eliminate shared state first, then use a narrow `[ResourceLock]`; reserve
`[DoNotParallelize]` for state a resource lock cannot cover. See
[`test/AGENTS.md`](../../test/AGENTS.md#conventions--gotchas).

## Resolver Code Runs in Two Hosts and Links Shared Sources

**Affected area:** `src/Resolvers`

**Description:** Resolver projects run in .NET MSBuild and Visual Studio/.NET Framework.
Several components are compiled from linked sources rather than referenced assemblies,
and dependencies are constrained by MSBuild binding redirects.

**Workaround:** Exercise both target-framework paths, keep hostfxr interop compatible,
and coordinate dependency changes with MSBuild. See
[`src/Resolvers/AGENTS.md`](../../src/Resolvers/AGENTS.md#conventions--gotchas).

## Generated Files Are Easy to Edit Accidentally

**Affected area:** `.xlf`, `.github/workflows/*.lock.yml`, generated man pages, Verify snapshots

**Description:** Manual edits drift from their source or are overwritten. Verify also
creates `*.received.*` files on mismatch that must not be committed.

**Workaround:** Edit `.resx` and regenerate XLF; change manpage content in `dotnet/docs`;
regenerate workflow locks through their owning workflow; inspect received snapshots and
promote only intentional output to `*.verified.*`. See the
[root generated-file guardrails](../../AGENTS.md#do-not-hand-edit-generated-files)
and [`snapshot-based-testing.md`](../../documentation/project-docs/snapshot-based-testing.md).

## Copilot CLI 1.0.85 Can Reject Initial Agent Requests

**Affected area:** gh-aw workflows using the Copilot engine

**Description:** Copilot CLI 1.0.85 can return an immediate, zero-token HTTP 400
from `/responses` before agent work or MCP tool calls begin. The gh-aw upstream
reverted its default to 1.0.83 in
[github/gh-aw#62421](https://github.com/github/gh-aw/pull/62421), which resolves
[github/gh-aw#62363](https://github.com/github/gh-aw/issues/62363), but the fix
was not yet released with gh-aw v0.89.17.

**Workaround:** Set `engine.version: 1.0.83` in affected workflow source and
regenerate the `.lock.yml` with its pinned compiler. Remove the explicit pin only
after upgrading to a release that includes the upstream fix.

## Redist Requires Correct Outer-Build Ordering

**Affected area:** `src/Layout/redist`

**Description:** Some multi-targeted component projects generate SDK content in their
outer build. Referencing only an inner build can race or leave `Sdk.props`/`Sdk.targets`
out of the layout.

**Workaround:** Preserve `ReferenceOutputAssembly="false"` and
`SkipGetTargetFrameworkProperties="true"` on redist build-ordering references unless the
producing project contract changes. See the comments in
[`redist.csproj`](../../src/Layout/redist/redist.csproj).
