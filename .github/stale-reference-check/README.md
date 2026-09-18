# Potentially stale reference discovery

This maintenance workflow finds ignored tests, actionable TODOs, and temporary
workarounds whose referenced GitHub blockers may have been resolved. It creates
revalidation tasks, not claims that tests pass or code can safely be removed.

The [driver](../workflows/stale-reference-check.yml) runs daily on non-fork
`main`. It creates at most **three** issues per run. It never changes source,
opens a pull request, comments on an existing issue, or removes an Ignore.

## Deterministic processing and the agent boundary

1. [`collect.mjs`](collect.mjs) uses tracked-source grep to locate relevant
   constructs and collect numbered context, including URLs on adjacent lines.
2. [`interpretations.mjs`](interpretations.mjs) restores compatible cached
   classifications and selects a bounded batch of new or changed source.
3. The [reusable interpreter](../workflows/stale-reference-interpret.md) identifies
   the actual action, owning declaration/test, and relevant blocking URLs. It
   does not query GitHub or write issues. Its structured results are checked
   against a separate trusted checkout before being retained.
4. [`github.mjs`](github.mjs) deduplicates reference lookups and obtains current
   issue/PR states. [`finalize.mjs`](finalize.mjs) checks live tracking issues and
   creates fixed-template tasks. [`workflow.mjs`](workflow.mjs) connects these
   stages, including the route that skips the agent completely.

The compiler's standard threat-detection stage remains enabled. It checks agent
output rather than performing a second semantic investigation of references.
Recording requires successful detection and source validation. The compiler's
conclusion job is disabled because this compiler version can otherwise create
diagnostic issues outside the filing limit and preview guard. Native job results,
logs, and the deterministic decision report provide diagnostics instead.

## Scope and bounds

Discovery scans repository-owned source, real tests, scripts, and build files.
Documentation and prompt text, test-input fixtures, snapshots, localization,
generated files, `eng/common`, and manifest-declared vendored files are excluded.
The checker's own synthetic test cases are also excluded from discovery.
The collector is deliberately not a general-purpose parser of every language.

Files without any GitHub issue/PR URL are eliminated before interpretation.
Initial context is 20 lines on either side of a hit. Overlapping windows share
context without merging unrelated actions. Each batch contains at most 25 windows
and 64 KiB of initial context. The interpreter may request up to two additional
windows of at most 80 lines per candidate. Cases that cannot be identified
confidently within those bounds are explicitly deferred, not guessed.

Historical references and retained compatibility behavior are not cleanup tasks.
Distinct nearby comments can depend on different URLs. A class-level Ignore
requires complete identification of the affected tests; ambiguous coverage is
deferred. Data rows do not create separate tracking issues.

The interpreter has no source checkout, general-purpose Node execution, file
editing, or GitHub tools. The bounded reader in
[`source-tools.mjs`](source-tools.mjs) runs on the host; its private input artifact
is outside the agent's filesystem mounts. Only selected batch/context results
cross that boundary. The host enforces both the two-window limit and an additional
32 KiB limit per expanded window. Because the pinned
[gh-aw runtime](https://github.com/github/gh-aw/blob/v0.88.7/actions/setup/js/mcp_server_core.cjs)
launches a fresh process per MCP script call, a private loopback reader retains the shared
budget. Tool calls only read source; a trusted post-agent step exports the served
window receipts. Recording verifies them against source, and the cache retains
the verified expansion evidence for later runners.

## Fresh runners and interpretation caching

GitHub Actions cache persists validated interpretations between runners. Each
entry is associated with its source identity, entire file blob, and collector/
validation/prompt rule hash. Changing a declaration elsewhere in the file
invalidates the interpretation even when its initial snippet is unchanged.

The cache contains **interpretations, not authoritative GitHub states**. Every
retained actionable reference is checked again on later runs, even when there is
no new context for the agent. Deleted and changed entries are pruned. Deliberately
irrelevant classifications are reusable; incomplete or invalid outputs are not.
Batch continuation prevents one unresolved context from monopolizing discovery.

Cache eviction is safe: the workflow interprets a bounded batch again and still
checks live tracking issues before filing. Corrupt cache data is reported and
discarded. Only the trusted main-branch workflow saves production cache entries;
preview runs do not.

## Eligibility and duplicate protection

Supported references are public GitHub issue and pull-request URLs, including
cross-repository blockers. Fragments and query strings do not produce duplicate
lookups. References through `/issues/` that identify a PR are checked as PRs.

An issue qualifies only when closed **as completed**. A PR qualifies only when
**merged**. All identified blockers for an action must qualify. Open/reopened
issues, not-planned or unknown closure reasons, closed-unmerged PRs, unavailable
references, and incomplete duplicate listings cannot authorize filing.

Additional source conditions, such as consuming a fixed dependency version,
remain explicit unverified prerequisites. A tracking task asks the assignee to
check them before changing code.

Durable identity is separate from interpretation-cache identity:

- Ignored test: repository path plus fully qualified test declaration, independent
  of line number, source commit, data rows, and original reference.
- TODO/workaround: repository path, owning declaration/structural anchor, and
  normalized actionable source text, not the surrounding window.

Code creates versioned body markers and visible identity fields. Open issues are
fully paginated and compared locally, without relying on hidden-marker search
indexing, mutable titles, or labels. Conservative checks also recognize existing
unmarked tasks with the same exact test/source identity. A shared upstream URL
alone is not a duplicate.

The workflow serializes its runs, checks again before creation, and reconciles
ambiguous creation errors before any further action. It does not repeatedly file
unchanged tasks already closed by maintainers when matching workflow history is
available. Closed-history lookup uses the existing `agentic-workflows` label;
removing that label from a closed issue can remove this suppression. Open
duplicate detection is not label-dependent.

GitHub does not enforce unique issue-body keys atomically. These checks protect
against this workflow's repeated and concurrent runs, but cannot prevent an
unrelated human or automation from creating the same task at the same instant.

## Filed tasks and Issue Monster

Each issue includes the stable identifier, exact source excerpt, commit-pinned
source link, original references, verified resolution information, and explicit
unknowns. Titles and bodies are generated by code, not by the model.

Issues receive `cookie` and `agentic-workflows`. Ignored-test/test-debt tasks also
receive `Test Debt`; general product-code cleanup does not automatically receive
that label. The existing [Issue Monster](../workflows/issue-monster.md) scheduled
queue handles assignment, so creation does not depend on an issue event from
`GITHUB_TOKEN` triggering another workflow.

For ignored tests, the task starts by removing the relevant Ignore and following
the [`run-tests` skill](../skills/run-tests/SKILL.md) for the smallest appropriate
selection on the required platform. The test must actually execute. A passing
test can be re-enabled; a newly exposed failure belongs in the tracking task.

## Preview, refresh, and diagnostics

Manually dispatch **Check potentially stale references** from `main`:

- `dry_run: true` is the default. It produces proposed issue payloads and decisions
  without changing issues or the production cache.
- `refresh_cache: true` ignores cached interpretations for collection. Batch
  limits still apply.
- Scheduled runs are live and retain validated interpretations.

The run's `stale-reference-report-*` artifact records created/proposed/skipped
decisions, deferred context, and remaining interpretations. A failure is explicit,
not a successful empty result. API failures do not establish that a blocker was
resolved. Failed/cancelled interpretation jobs cannot authorize the filing job.
Artifact consumers use the successful collection job's artifact ID, rather than
the current attempt number. Partial reruns can therefore reuse successful
producers without searching for an unrelated latest artifact.

For local, deterministic source collection without GitHub access or inference:

```powershell
node .github\stale-reference-check\cli.mjs collect
node .github\stale-reference-check\cli.mjs read-batch
```

Temporary input, cache, and report files live in the git-ignored
`.stale-reference-check` directory. Collection alone never files issues.

## Development

The helpers use Node built-ins and the Octokit instance from
`actions/github-script`; there is no package installation or SDK build.
Run fixture and mocked-API tests with:

```powershell
node --test .github\stale-reference-check\test\*.test.mjs
```

The [helper test workflow](../workflows/stale-reference-check-tests.yml) runs
these tests on relevant pull requests without invoking the interpreter or
granting issue-write permissions.

Edit the interpreter Markdown, never its generated lock file. The checked-in
workflow is compiled with gh-aw v0.88.7 and its matching immutable runtime:

```powershell
gh aw compile stale-reference-interpret --action-mode action --action-tag v0.88.7
```

When changing the compiler/runtime together, regenerate only this workflow and
inspect its job dependencies, artifact handoff, action pins, and permissions.
The caller's Actions-write ceiling is required by the compiler's disabled
conclusion job; every executing interpreter job explicitly uses read-only
Actions permissions, and none can write issues.
The source interpreter uses the repository's existing Copilot PAT pool only for
Copilot authentication; deterministic GitHub operations use the job token.
No new secrets are required.
