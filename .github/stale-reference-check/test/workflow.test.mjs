import assert from "node:assert/strict";
import { execFile, execFileSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test, { after, before } from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { expandContext, finish, prepare, record, rulesHash } from "../workflow.mjs";
import { getSourceTools, requestSourceTools, startSourceServer } from "../source-tools.mjs";
import { finishFork } from "../fork-only.mjs";

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const workflowDirectory = path.join(repository, ".github/workflows");
const environmentNames = ["GITHUB_SHA", "GITHUB_OUTPUT", "GITHUB_STEP_SUMMARY", "STALE_REFERENCE_INPUT"];
const environment = Object.fromEntries(environmentNames.map(name => [name, process.env[name]]));
before(() =>
{
    for (const name of environmentNames)
    {
        delete process.env[name];
    }
});
after(() =>
{
    for (const [name, value] of Object.entries(environment))
    {
        if (value !== undefined)
        {
            process.env[name] = value;
        }
    }
});

async function fixture(t, padding = 0)
{
    const root = await mkdtemp(path.join(os.tmpdir(), "stale-reference-workflow-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const files = {
        "src/Sample.cs": [
            "namespace Example.Tests;",
            ...Array(padding).fill(""),
            "[TestClass]",
            "public class SampleTests",
            "{",
            "    [TestMethod]",
            '    [Ignore("https://github.com/dotnet/sdk/issues/123")]',
            "    public void Works() {}",
            "}",
        ].join("\n"),
        ".github/stale-reference-check/collect.mjs": "// collector rules",
        ".github/stale-reference-check/interpretations.mjs": "// validation rules",
        ".github/stale-reference-check/workflow.mjs": "// orchestration rules",
        ".github/stale-reference-check/source-tools.mjs": "// bounded source reader rules",
        ".github/workflows/stale-reference-interpret.md": "# Interpretation rules",
    };
    for (const [file, content] of Object.entries(files))
    {
        await mkdir(path.dirname(path.join(root, file)), { recursive: true });
        await writeFile(path.join(root, file), `${content}\n`);
    }
    execFileSync("git", ["init", "--quiet", root]);
    execFileSync("git", ["-C", root, "add", "."]);
    execFileSync("git", ["-C", root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
        "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "fixture"]);
    return root;
}

const logger = { info() {}, warn() {} };

test("prepare creates a current-commit manifest and bounded batch without a cache", async (t) =>
{
    const root = await fixture(t);
    const result = await prepare({ repoRoot: root, logger });
    assert.equal(result.batch.candidates.length, 1);
    const manifest = JSON.parse(await readFile(path.join(root, ".stale-reference-check/input/manifest.json")));
    assert.match(manifest.headSha, /^[a-f0-9]{40}$/);
    assert.equal(manifest.rulesHash, await rulesHash(root));
    assert.equal(manifest.candidates[0].path, "src/Sample.cs");
});

test("malformed cache is diagnosed and recollected, never treated as interpreted", async (t) =>
{
    const root = await fixture(t);
    const cache = path.join(root, ".stale-reference-check/state/cache.json");
    await mkdir(path.dirname(cache), { recursive: true });
    await writeFile(cache, "{broken");
    const warnings = [];
    const result = await prepare({ repoRoot: root, logger: { info() {}, warn(message) { warnings.push(message); } } });
    assert.equal(result.batch.candidates.length, 1);
    assert.match(warnings.join("\n"), /invalid interpretation cache JSON/);
});

test("record validates all candidates and rejects duplicate safe-output calls", async (t) =>
{
    const root = await fixture(t);
    const { batch } = await prepare({ repoRoot: root, logger });
    const item = {
        type: "record_interpretations",
        payload: JSON.stringify({
            schemaVersion: 1,
            results: batch.candidates.map(candidate => ({
                candidateId: candidate.id,
                status: "irrelevant",
                reason: "Fixture classification.",
                actions: [],
            })),
        }),
    };
    const output = path.join(root, "output.json");
    await writeFile(output, JSON.stringify({ items: [item] }));
    await record(root, output);
    const recorded = JSON.parse(await readFile(path.join(root, ".stale-reference-check/results/interpretations.json")));
    assert.equal(recorded.results.length, 1);
    await writeFile(output, JSON.stringify({ items: [item, item] }));
    await assert.rejects(record(root, output), /exactly one/);
});

test("record refuses artifacts prepared with different rules", async (t) =>
{
    const root = await fixture(t);
    await prepare({ repoRoot: root, logger });
    await writeFile(path.join(root, ".github/workflows/stale-reference-interpret.md"), "changed rules");
    await assert.rejects(record(root, path.join(root, "unused.json")), /rules do not match/);
});

async function recordIrrelevantBatch(root, batch)
{
    const payload = {
        schemaVersion: 1,
        results: batch.candidates.map(candidate => ({
            candidateId: candidate.id,
            status: "irrelevant",
            reason: "No remaining source action in this fixture.",
            actions: [],
        })),
    };
    const output = path.join(root, "output.json");
    await writeFile(output, JSON.stringify({
        items: [{ type: "record_interpretations", payload: JSON.stringify(payload) }],
    }));
    await record(root, output);
}

test("first-run recording persists interpretations and cached-only finalization needs no agent artifact", async (t) =>
{
    const root = await fixture(t);
    const { batch } = await prepare({ repoRoot: root, logger });
    await recordIrrelevantBatch(root, batch);
    const github = new Proxy({}, { get() { throw new Error("An irrelevant batch must not call GitHub."); } });
    const options = { github, repository: "dotnet/sdk", repoRoot: root, dryRun: false, logger };
    const first = await finish(options);
    assert.equal(first.created.length, 0);
    assert.equal(first.interpretations.irrelevant, 1);
    const next = await prepare({ repoRoot: root, logger });
    assert.equal(next.batch.candidates.length, 0);
    await rm(path.join(root, ".stale-reference-check/results/interpretations.json"));
    const second = await finish(options);
    assert.equal(second.created.length, 0);
    assert.equal(second.interpretations.remaining, 0);
});

test("preview does not save production interpretations", async (t) =>
{
    const root = await fixture(t);
    const { batch } = await prepare({ repoRoot: root, logger });
    await recordIrrelevantBatch(root, batch);
    const github = new Proxy({}, { get() { throw new Error("No GitHub calls expected."); } });
    await finish({ github, repository: "dotnet/sdk", repoRoot: root, dryRun: true, logger });
    await assert.rejects(readFile(path.join(root, ".stale-reference-check/state/cache.json")), { code: "ENOENT" });
    assert.equal((await prepare({ repoRoot: root, logger })).batch.candidates.length, 1);
});

test("expanded source evidence survives recording, separate jobs, and cached-only state checks", async (t) =>
{
    const root = await fixture(t, 50);
    const { batch } = await prepare({ repoRoot: root, logger });
    const output = path.join(root, "output.json");
    await writeFile(output, JSON.stringify({
        items: [{
            type: "record_interpretations",
            payload: JSON.stringify({
                schemaVersion: 1,
                results: [{
                    candidateId: batch.candidates[0].id,
                    status: "actionable",
                    reason: "The test is ignored pending this issue.",
                    actions: [{
                        kind: "ignore",
                        anchor: "Example.Tests.SampleTests.Works",
                        testNames: ["Example.Tests.SampleTests.Works"],
                        startLine: batch.candidates[0].seedLine,
                        endLine: batch.candidates[0].seedLine + 1,
                        urls: ["https://github.com/dotnet/sdk/issues/123"],
                        additionalConditions: [],
                    }],
                }],
            }),
        }],
    }));
    await assert.rejects(record(root, output), /namespace|qualified|context/i);
    const directory = path.join(root, ".stale-reference-check/input");
    const server = await startSourceServer(directory);
    try
    {
        await requestSourceTools(directory, "read-context",
            { candidateId: batch.candidates[0].id, startLine: 1, endLine: 1 });
        await writeFile(path.join(directory, "context-evidence.json"),
            JSON.stringify(await requestSourceTools(directory, "evidence")));
    }
    finally
    {
        await new Promise(resolve => server.close(resolve));
    }
    await record(root, output);
    await rm(path.join(directory, "context-evidence.json"));
    let resolved = false;
    let lookups = 0;
    const issues = {
        async get()
        {
            lookups++;
            return { data: {
                number: 123, state: resolved ? "closed" : "open",
                state_reason: resolved ? "completed" : null,
                closed_at: resolved ? "2026-09-01T00:00:00Z" : null,
                html_url: "https://github.com/dotnet/sdk/issues/123",
            } };
        },
        async listForRepo() { return { data: [] }; },
        async create() { throw new Error("Neither a blocked run nor a preview may create issues."); },
    };
    const paginate = async (method, parameters) => (await method(parameters)).data;
    paginate.iterator = async function* (method, parameters) { yield await method(parameters); };
    const github = { rest: { issues }, paginate };
    const options = { github, repository: "mthalman/sdk", ref: "refs/heads/main", repoRoot: root, logger };
    const blocked = await finishFork({ ...options, dryRun: false });
    assert.equal(blocked.proposed.length, 0);
    resolved = true;
    const next = await prepare({ repoRoot: root, logger });
    assert.equal(next.batch.candidates.length, 0);
    await rm(path.join(root, ".stale-reference-check/results"), { recursive: true });
    const preview = await finishFork({ ...options, dryRun: true });
    assert.equal(preview.proposed.length, 1);
    assert.equal(preview.created.length, 0);
    assert.equal(lookups, 2);
});

test("fork preview saves no cache and live finalization surfaces a lifetime-cap refusal", async (t) =>
{
    const root = await fixture(t);
    const { batch } = await prepare({ repoRoot: root, logger });
    const candidate = batch.candidates[0];
    const output = path.join(root, "output.json");
    await writeFile(output, JSON.stringify({ items: [{
        type: "record_interpretations",
        payload: JSON.stringify({ schemaVersion: 1, results: [{
            candidateId: candidate.id, status: "actionable", reason: "Ignored pending the completed issue.",
            actions: [{
                kind: "ignore", anchor: "Example.Tests.SampleTests.Works",
                testNames: ["Example.Tests.SampleTests.Works"],
                startLine: candidate.seedLine, endLine: candidate.seedLine + 1,
                urls: ["https://github.com/dotnet/sdk/issues/123"], additionalConditions: [],
            }],
        }] }),
    }] }));
    await record(root, output);
    const history = [1, 2, 3].map(number => ({
        number, state: "closed", body: null, user: { type: "Bot", login: "github-actions[bot]" },
    }));
    let writes = 0;
    const github = {
        rest: { issues: {
            async get() { return { data: {
                state: "closed", state_reason: "completed", closed_at: "2026-09-01T00:00:00Z",
            } }; },
            listForRepo() {},
            async create() { writes++; throw new Error("No writes authorized."); },
        } },
        paginate: async (_method, parameters) => history.filter(issue => issue.state === parameters.state),
    };
    github.paginate.iterator = async function* () { yield { data: history, status: 200, headers: {} }; };
    const options = { github, repository: "mthalman/sdk", ref: "refs/heads/main", repoRoot: root, logger };
    const preview = await finishFork({ ...options, dryRun: true });
    assert.equal(preview.proposed.length, 1);
    await assert.rejects(readFile(path.join(root, ".stale-reference-check/state/cache.json")), { code: "ENOENT" });
    await assert.rejects(finishFork({ ...options, dryRun: false }), /lifetime limit of three/);
    assert.equal(writes, 0);
});

test("a missing required interpretation artifact cannot authorize finalization", async (t) =>
{
    const root = await fixture(t);
    await prepare({ repoRoot: root, logger });
    await assert.rejects(finish({
        github: null, repository: "dotnet/sdk", repoRoot: root, dryRun: false, logger,
    }), { code: "ENOENT" });
    await assert.rejects(readFile(path.join(root, ".stale-reference-check/state/cache.json")), { code: "ENOENT" });
});

test("context expansion is limited to the batch and two windows per candidate", async (t) =>
{
    const root = await fixture(t);
    const { batch } = await prepare({ repoRoot: root, logger });
    const request = { candidateId: batch.candidates[0].id, startLine: 1, endLine: 8 };
    const output = t.mock.method(process.stdout, "write", () => true);
    try
    {
        await assert.rejects(expandContext(root, { ...request, candidateId: "not-in-the-batch" }), /only.*this batch/);
        await expandContext(root, request);
        await expandContext(root, request);
        await assert.rejects(expandContext(root, request), /budget.*exhausted/);
    }
    finally
    {
        output.mock.restore();
    }
});

test("host-side source tools expose only the batch and enforce shared expansion limits", async (t) =>
{
    const root = await fixture(t);
    const { batch } = await prepare({ repoRoot: root, logger });
    const directory = path.join(root, ".stale-reference-check/input");
    const tools = await getSourceTools(directory);
    assert.deepEqual(tools.readBatch(), batch);
    const request = { candidateId: batch.candidates[0].id, startLine: 1, endLine: 8 };
    assert.throws(() => tools.readContext({ ...request, candidateId: "../manifest.json" }), /only available/);
    assert.throws(() => tools.readContext({ ...request, endLine: 81 }), /1 and 80/);
    assert.match(tools.readContext(request).context, /Example\.Tests/);
    const sameTools = await getSourceTools(directory);
    sameTools.readContext(request);
    assert.throws(() => tools.readContext(request), /budget is exhausted/);
});

test("fork driver serializes manual main-only runs and supports cached-only finalization", async () =>
{
    const workflow = await readFile(path.join(workflowDirectory, "stale-reference-check.yml"), "utf8");
    assert.match(workflow, /group: stale-reference-check/);
    assert.match(workflow, /cancel-in-progress: false/);
    assert.equal((workflow.match(/github\.repository == 'mthalman\/sdk' && github\.ref == 'refs\/heads\/main'/g) ?? []).length, 3);
    assert.match(workflow, /needs\.interpret\.result == 'success' \|\| needs\.interpret\.result == 'skipped'/);
    assert.match(workflow, /inputs\.dry_run/);
    assert.match(workflow, /env\.DRY_RUN != 'true'/);
    assert.doesNotMatch(workflow, /pull_request:|schedule:|cron:/);
    assert.match(workflow, /workflow_dispatch:/);
    assert.match(workflow, /await finishFork\(\{/);
    assert.match(workflow, /stale-reference-check\/fork-only\.mjs/);
    assert.match(workflow, /ref: context\.ref/);
    assert.equal((workflow.match(/issues: write/g) ?? []).length, 1);
});

test("agent records source interpretations without issue writes or GitHub tools", async () =>
{
    const workflow = await readFile(path.join(workflowDirectory, "stale-reference-interpret.md"), "utf8");
    const header = workflow.split("---")[1];
    assert.match(header, /workflow_call:/);
    assert.match(header, /if: github\.repository == 'mthalman\/sdk' && github\.ref == 'refs\/heads\/main'/);
    assert.match(header, /record-interpretations:/);
    assert.match(header, /needs\.detection\.outputs\.detection_success == 'true'/);
    assert.doesNotMatch(header, /issues: write|pull-requests: write|create-issue: true/);
    assert.doesNotMatch(header, /^\s+github:\s*$/m);
    assert.match(header, /github: false/);
    assert.match(header, /checkout: false/);
    assert.match(header, /edit: false/);
    assert.doesNotMatch(header, /bash: \[node\]/);
    assert.match(header, /artifact-ids: \$\{\{ inputs\.input_artifact_id \}\}/);
    assert.match(header, /report-failed-jobs: false/);
    assert.match(header, /conclusion:\s*\n\s*if: \$\{\{ false \}\}/);
    assert.match(header, /ref: \$\{\{ github\.sha \}\}/);
});

test("fork helper test entry point is manual and hard-gated to mthalman/sdk main", async () =>
{
    const workflow = await readFile(path.join(workflowDirectory, "stale-reference-check-tests.yml"), "utf8");
    assert.match(workflow, /workflow_dispatch:/);
    assert.doesNotMatch(workflow, /pull_request:|schedule:|cron:/);
    assert.match(workflow, /test:\s*\n\s*if: github\.repository == 'mthalman\/sdk' && github\.ref == 'refs\/heads\/main'/);
    assert.doesNotMatch(workflow, /issues: write|pull-requests: write/);
});

test("all entry points retain telemetry and immutable action pins", async () =>
{
    for (const file of ["stale-reference-check.yml", "stale-reference-interpret.md", "stale-reference-check-tests.yml"])
    {
        const workflow = await readFile(path.join(workflowDirectory, file), "utf8");
        assert.match(workflow, /DOTNET_CLI_TELEMETRY_SESSIONID: gha-\$\{\{ github\.repository_id \}\}-\$\{\{ github\.run_id \}\}-\$\{\{ github\.run_attempt \}\}/);
        for (const match of workflow.matchAll(/uses: ([^\s]+)/g))
        {
            if (match[1].startsWith("actions/"))
            {
                assert.match(match[1], /@[a-f0-9]{40}$/);
            }
        }
    }
});

test("compiled reusable jobs cannot elevate beyond the caller's permission ceiling", async () =>
{
    const generated = await readFile(path.join(workflowDirectory, "stale-reference-interpret.lock.yml"), "utf8");
    const caller = await readFile(path.join(workflowDirectory, "stale-reference-check.yml"), "utf8");
    assert.doesNotMatch(caller, /secrets: inherit/);
    assert.match(caller, /input_artifact_id: \$\{\{ steps\.upload\.outputs\.artifact-id \}\}/);
    assert.match(caller, /name: stale-reference-interpretations-\$\{\{ needs\.collect\.outputs\.input_artifact_id \}\}/);
    const ceiling = { actions: 2, contents: 1, "copilot-requests": 2, issues: 0 };
    const rank = { none: 0, read: 1, write: 2 };
    const jobs = [...generated.matchAll(/^  ([a-z_]+):\r?\n([\s\S]*?)(?=^  [a-z_]+:\r?\n|$(?![\s\S]))/gm)];
    assert.ok(jobs.some(job => job[1] === "agent"));
    for (const [, name, body] of jobs)
    {
        const permissions = body.match(/^    permissions:\r?\n((?:      [\w-]+: \w+\r?\n)+)/m)?.[1] ?? "";
        for (const [, scope, permission] of permissions.matchAll(/      ([\w-]+): (\w+)/g))
        {
            assert.ok(rank[permission] <= (ceiling[scope] ?? 0), `${name} elevates ${scope} to ${permission}`);
            if (name !== "conclusion")
            {
                assert.ok(scope !== "actions" || permission !== "write", `${name} unexpectedly writes Actions data`);
            }
        }
    }
    const conclusion = jobs.find(job => job[1] === "conclusion")?.[2];
    assert.match(conclusion, /&& \(false\)/);
    assert.match(conclusion, /issues: none/);
    for (const name of ["pre_activation", "activation"])
    {
        assert.match(jobs.find(job => job[1] === name)?.[2],
            /github\.repository == 'mthalman\/sdk' && github\.ref == 'refs\/heads\/main'/);
    }
    const agent = jobs.find(job => job[1] === "agent")?.[2];
    assert.doesNotMatch(agent, /name: Checkout repository|--allow-tool write|shell\(node\)|--allow-tool github/);
    assert.match(agent, /path: \$\{\{ runner\.temp \}\}\/stale-reference-private/);
    assert.match(agent, /artifact-ids: \$\{\{ inputs\.input_artifact_id \}\}/);
});

test("isolated concurrent MCP processes share one budget and trusted expansion evidence", async t =>
{
    const root = await fixture(t);
    const { batch } = await prepare({ repoRoot: root, logger });
    const directory = path.join(root, ".stale-reference-check/input");
    const server = await startSourceServer(directory);
    t.after(() => new Promise(resolve => server.close(resolve)));
    assert.deepEqual(await requestSourceTools(directory, "read-batch"), batch);
    const input = { candidateId: batch.candidates[0].id, startLine: 1, endLine: 8 };
    const client = "const {requestSourceTools}=await import(process.argv[1]);" +
        "console.log(JSON.stringify(await requestSourceTools(process.argv[2], 'read-context', JSON.parse(process.argv[3]))));";
    const results = await Promise.allSettled(Array.from({ length: 3 }, () =>
        promisify(execFile)(process.execPath, ["--input-type=module", "-e", client,
            new URL("../source-tools.mjs", import.meta.url).href, directory, JSON.stringify(input)])));
    assert.equal(results.filter(result => result.status === "fulfilled").length, 2);
    const failure = results.find(result => result.status === "rejected");
    assert.match(failure.reason.message, /two-window/);
    assert.deepEqual(await requestSourceTools(directory, "evidence"),
        { schemaVersion: 1, windows: [input, input] });
    await assert.rejects(requestSourceTools(directory, "unknown"), /Unknown source-reader/);
    const { port } = JSON.parse(await readFile(path.join(directory, "reader.json"), "utf8"));
    const denied = await fetch(`http://127.0.0.1:${port}/read-batch`, { method: "POST" });
    assert.equal(denied.status, 401);
    await denied.text();
});
