import { createHash } from "node:crypto";
import { appendFile, copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { collect } from "./collect.mjs";
import {
    getCachedResults,
    mergeCache,
    readContext,
    selectBatch,
    validateInterpretations,
} from "./interpretations.mjs";
import { finalize } from "./finalize.mjs";

const inputPath = (repoRoot) => path.resolve(repoRoot, process.env.STALE_REFERENCE_INPUT ?? ".stale-reference-check/input");
const statePath = (repoRoot) => path.join(repoRoot, ".stale-reference-check/state/cache.json");
const resultsPath = (repoRoot) => path.join(repoRoot, ".stale-reference-check/results/interpretations.json");

async function readJson(file)
{
    return JSON.parse(await readFile(file, "utf8"));
}

async function writeJson(file, value)
{
    await mkdir(path.dirname(file), { recursive: true });
    await writeFile(file, `${JSON.stringify(value, null, 2)}\n`);
}

async function readOptional(file)
{
    try
    {
        return await readFile(file, "utf8");
    }
    catch (error)
    {
        if (error.code === "ENOENT")
        {
            return null;
        }
        throw error;
    }
}

export async function rulesHash(repoRoot)
{
    const hash = createHash("sha256");
    for (const file of [
        ".github/stale-reference-check/collect.mjs",
        ".github/stale-reference-check/interpretations.mjs",
        ".github/stale-reference-check/workflow.mjs",
        ".github/stale-reference-check/source-tools.mjs",
        ".github/workflows/stale-reference-interpret.md",
    ])
    {
        hash.update(file).update("\0").update(await readFile(path.join(repoRoot, file))).update("\0");
    }
    return hash.digest("hex");
}

export async function prepare({ repoRoot, refreshCache = false, logger = console })
{
    let cache = null;
    const cachedText = refreshCache ? null : await readOptional(statePath(repoRoot));
    if (cachedText !== null)
    {
        try
        {
            cache = JSON.parse(cachedText);
        }
        catch (error)
        {
            logger.warn(`Discarding invalid interpretation cache JSON: ${error.message}`);
        }
    }

    const manifest = await collect(repoRoot, { rulesHash: await rulesHash(repoRoot) });
    const selected = selectBatch(manifest, cache);
    for (const diagnostic of selected.diagnostics ?? [])
    {
        logger.warn(diagnostic);
    }
    const directory = inputPath(repoRoot);
    await writeJson(path.join(directory, "manifest.json"), manifest);
    await writeJson(path.join(directory, "batch.json"), selected.batch);
    await writeJson(path.join(directory, "cache.json"), selected.cache);
    await writeJson(path.join(directory, "cursor.json"), { nextCursor: selected.nextCursor });
    await writeJson(path.join(directory, "context-evidence.json"), { schemaVersion: 1, windows: [] });
    const sourceFiles = {};
    for (const candidate of selected.batch.candidates)
    {
        if (!Object.hasOwn(sourceFiles, candidate.path))
        {
            sourceFiles[candidate.path] = await readFile(path.join(repoRoot, candidate.path), "utf8");
        }
    }
    await writeJson(path.join(directory, "source-context.json"), sourceFiles);
    await copyFile(path.join(repoRoot, ".github/stale-reference-check/source-tools.mjs"),
        path.join(directory, "source-tools.mjs"));
    const misses = selected.batch.candidates.length;
    if (process.env.GITHUB_OUTPUT)
    {
        await appendFile(process.env.GITHUB_OUTPUT, `has_misses=${misses > 0}\ncandidate_count=${manifest.candidates.length}\n`);
    }
    logger.info(`Collected ${manifest.candidates.length} candidates; ${misses} require interpretation in this batch.`);
    return { manifest, ...selected };
}

async function loadInput(repoRoot)
{
    const directory = inputPath(repoRoot);
    const [manifest, batch, cache, cursor] = await Promise.all([
        readJson(path.join(directory, "manifest.json")),
        readJson(path.join(directory, "batch.json")),
        readJson(path.join(directory, "cache.json")),
        readJson(path.join(directory, "cursor.json")),
    ]);
    if (manifest.rulesHash !== await rulesHash(repoRoot))
    {
        throw new Error("The source interpretation rules do not match the collection artifact.");
    }
    if (process.env.GITHUB_SHA && manifest.headSha !== process.env.GITHUB_SHA)
    {
        throw new Error("The collection artifact does not match this workflow's source commit.");
    }
    const ids = new Set(manifest.candidates.map(candidate => candidate.id));
    if (batch.schemaVersion !== 1 || !Array.isArray(batch.candidates) ||
        batch.candidates.some(candidate => !ids.has(candidate.id)))
    {
        throw new Error("The interpretation batch does not belong to the current source manifest.");
    }
    return { manifest, batch, cache, cursor };
}

export async function printBatch(repoRoot)
{
    const { batch } = await loadInput(repoRoot);
    process.stdout.write(`${JSON.stringify(batch, null, 2)}\n`);
}

export async function expandContext(repoRoot, { candidateId, startLine, endLine })
{
    const { manifest, batch } = await loadInput(repoRoot);
    if (!batch.candidates.some(candidate => candidate.id === candidateId))
    {
        throw new Error("Context may only be expanded for a candidate in this batch.");
    }
    const evidenceFile = path.join(inputPath(repoRoot), "context-evidence.json");
    const evidence = await readContextEvidence(evidenceFile);
    if (evidence.expansions.filter(window => window.candidateId === candidateId).length >= 2)
    {
        throw new Error("The two-window context expansion budget for this candidate is exhausted.");
    }
    const context = await readContext(repoRoot, manifest, { candidateId, startLine, endLine });
    if (Buffer.byteLength(context.context, "utf8") > 32 * 1024)
    {
        throw new Error("The context window exceeds 32 KiB; request fewer lines.");
    }
    evidence.expansions.push({ candidateId, startLine, endLine });
    await writeJson(evidenceFile, { schemaVersion: 1, windows: evidence.expansions });
    process.stdout.write(`${typeof context === "string" ? context : JSON.stringify(context, null, 2)}\n`);
}

async function readContextEvidence(file)
{
    const evidence = await readJson(file);
    if (evidence?.schemaVersion !== 1 || !Array.isArray(evidence.windows) ||
        Object.keys(evidence).some(key => key !== "schemaVersion" && key !== "windows"))
    {
        throw new Error("Invalid trusted context-expansion evidence.");
    }
    return { schemaVersion: 1, expansions: evidence.windows };
}

export async function record(repoRoot, outputFile)
{
    if (!outputFile)
    {
        throw new Error("GH_AW_AGENT_OUTPUT is required.");
    }
    const { manifest, batch } = await loadInput(repoRoot);
    const output = await readJson(outputFile);
    if (!Array.isArray(output.items))
    {
        throw new Error("Safe output does not contain an items array.");
    }
    const items = output.items.filter(item => item.type === "record_interpretations");
    if (items.length !== 1 || typeof items[0].payload !== "string" ||
        Buffer.byteLength(items[0].payload, "utf8") > 512 * 1024)
    {
        throw new Error("Expected exactly one record_interpretations payload.");
    }
    const payload = JSON.parse(items[0].payload);
    const contextEvidence = await readContextEvidence(path.join(inputPath(repoRoot), "context-evidence.json"));
    await validateInterpretations(payload, manifest, {
        repoRoot,
        expectedCandidateIds: batch.candidates.map(candidate => candidate.id),
        contextEvidence,
    });
    await writeJson(resultsPath(repoRoot), payload);
    await writeJson(path.join(path.dirname(resultsPath(repoRoot)), "context-evidence.json"),
        { schemaVersion: 1, windows: contextEvidence.expansions });
}

function interpretationPayload(results)
{
    return {
        schemaVersion: 1,
        results: results.map(result => ({
            candidateId: result.candidateId,
            status: result.status,
            reason: result.reason,
            actions: result.actions.map(action => ({
                kind: action.kind,
                anchor: action.anchor,
                ...(action.testNames ? { testNames: action.testNames } : {}),
                startLine: action.startLine,
                endLine: action.endLine,
                urls: action.urls,
                additionalConditions: action.additionalConditions,
            })),
        })),
    };
}

export async function finish({ github, repository, repoRoot, dryRun, logger = console })
{
    const { manifest, batch, cache, cursor } = await loadInput(repoRoot);
    const payload = batch.candidates.length === 0
        ? { schemaVersion: 1, results: [] }
        : await readJson(resultsPath(repoRoot));
    const fresh = await validateInterpretations(payload, manifest, {
        repoRoot,
        expectedCandidateIds: batch.candidates.map(candidate => candidate.id),
        contextEvidence: batch.candidates.length === 0 ? { schemaVersion: 1, expansions: [] }
            : await readContextEvidence(path.join(path.dirname(resultsPath(repoRoot)), "context-evidence.json")),
    });
    let updated = mergeCache(cache, manifest, fresh, { nextCursor: cursor.nextCursor });
    const restored = getCachedResults(updated, manifest);
    const cachedResults = await validateInterpretations(interpretationPayload(restored), manifest, {
        repoRoot,
        expectedCandidateIds: restored.map(result => result.candidateId),
        contextEvidence: { schemaVersion: 1, expansions: restored.flatMap(result => result.contextExpansions ?? []) },
    });
    updated = mergeCache(updated, manifest, cachedResults, { nextCursor: cursor.nextCursor });
    const actions = cachedResults.flatMap(result => result.actions);
    // Cache semantic work before remote I/O so a GitHub outage does not repeat inference.
    if (!dryRun)
    {
        await writeJson(statePath(repoRoot), updated);
    }

    const reportFile = path.join(repoRoot, ".stale-reference-check/report.json");
    try
    {
        const report = await finalize({ github, repository, headSha: manifest.headSha, actions, dryRun, logger });
        report.interpretations = {
            actionable: cachedResults.filter(result => result.status === "actionable").length,
            irrelevant: cachedResults.filter(result => result.status === "irrelevant").length,
            deferred: fresh.filter(result => result.status === "insufficient_context").map(result => ({
                candidateId: result.candidateId,
                reason: result.reason,
            })),
            remaining: manifest.candidates.length - cachedResults.length,
        };
        await writeJson(reportFile, report);
        if (process.env.GITHUB_STEP_SUMMARY)
        {
            await appendFile(process.env.GITHUB_STEP_SUMMARY,
                `## Potentially stale references\n\nMode: ${dryRun ? "preview (no writes)" : "live"}\n\n` +
                `Created: ${report.created.length}; proposed: ${report.proposed.length}; skipped: ${report.skipped.length}.\n\n` +
                `Remaining interpretations: ${report.interpretations.remaining}. See the decision-report artifact for details.\n`);
        }
        return report;
    }
    catch (error)
    {
        await writeJson(reportFile, { failed: true, error: error.message, dryRun });
        throw error;
    }
}
