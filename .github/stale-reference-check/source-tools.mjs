import { randomBytes } from "node:crypto";
import { readFile, rename, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";

const readers = new Map();

export async function getSourceTools(directory)
{
    const resolved = path.resolve(directory);
    if (!readers.has(resolved))
    {
        readers.set(resolved, loadSourceTools(resolved));
    }
    return readers.get(resolved);
}

async function loadSourceTools(directory)
{
    const [batchText, sourceText] = await Promise.all([
        readFile(path.join(directory, "batch.json"), "utf8"),
        readFile(path.join(directory, "source-context.json"), "utf8"),
    ]);
    const batch = JSON.parse(batchText);
    const sources = JSON.parse(sourceText);
    if (batch.schemaVersion !== 1 || !Array.isArray(batch.candidates))
    {
        throw new Error("Invalid trusted source batch.");
    }
    const candidates = new Map(batch.candidates.map(candidate => [candidate.id, candidate]));
    const expansions = new Map();
    const windows = [];
    return {
        readBatch()
        {
            return structuredClone(batch);
        },
        readContext({ candidateId, startLine, endLine })
        {
            const candidate = candidates.get(candidateId);
            if (!candidate || !Object.hasOwn(sources, candidate.path))
            {
                throw new Error("Context is only available for candidates in this batch.");
            }
            if (!Number.isSafeInteger(startLine) || !Number.isSafeInteger(endLine) ||
                startLine < 1 || endLine < startLine || endLine - startLine + 1 > 80)
            {
                throw new Error("A context window must contain between 1 and 80 source lines.");
            }
            const lines = sources[candidate.path].replaceAll("\r\n", "\n").split("\n");
            if (lines.at(-1) === "")
            {
                lines.pop();
            }
            if (endLine > lines.length)
            {
                throw new Error("The context window exceeds the source file.");
            }
            if ((expansions.get(candidateId) ?? 0) >= 2)
            {
                throw new Error("The two-window context expansion budget is exhausted.");
            }
            const context = lines.slice(startLine - 1, endLine)
                .map((line, index) => `L${startLine + index}: ${line}`).join("\n");
            if (Buffer.byteLength(context, "utf8") > 32 * 1024)
            {
                throw new Error("The context window exceeds 32 KiB; request fewer lines.");
            }
            expansions.set(candidateId, (expansions.get(candidateId) ?? 0) + 1);
            windows.push({ candidateId, startLine, endLine });
            return { path: candidate.path, startLine, endLine, context };
        },
        evidence()
        {
            return { schemaVersion: 1, windows: structuredClone(windows) };
        },
    };
}

// gh-aw launches a fresh process per MCP call. Keep budgets in one private,
// host-side reader, not in the short-lived tool process or agent-editable files.
export async function startSourceServer(directory)
{
    const tools = await getSourceTools(directory);
    const token = randomBytes(32).toString("hex");
    const server = createServer(async (request, response) =>
    {
        response.setHeader("Content-Type", "application/json");
        if (request.headers.authorization !== `Bearer ${token}`)
        {
            response.writeHead(401).end(JSON.stringify({ error: "Unauthorized source reader." }));
            return;
        }
        try
        {
            if (request.method !== "POST" ||
                !["/read-batch", "/read-context", "/evidence"].includes(request.url))
            {
                throw new Error("Unknown source-reader request.");
            }
            let body = "";
            for await (const chunk of request)
            {
                body += chunk;
                if (Buffer.byteLength(body) > 4096)
                {
                    throw new Error("Source-reader request exceeds 4 KiB.");
                }
            }
            const args = JSON.parse(body || "{}");
            const result = request.url === "/read-batch" ? tools.readBatch()
                : request.url === "/evidence" ? tools.evidence() : tools.readContext(args);
            response.end(JSON.stringify(result));
        }
        catch (error)
        {
            response.writeHead(400).end(JSON.stringify({ error: error.message }));
        }
    });
    await new Promise((resolve, reject) =>
    {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    try
    {
        await writeFile(path.join(directory, "reader.json.tmp"),
            JSON.stringify({ port: server.address().port, token }), { mode: 0o600 });
        await rename(path.join(directory, "reader.json.tmp"), path.join(directory, "reader.json"));
    }
    catch (error)
    {
        server.close();
        throw error;
    }
    return server;
}

export async function requestSourceTools(directory, operation, args = {})
{
    const { port, token } = JSON.parse(await readFile(path.join(directory, "reader.json"), "utf8"));
    const response = await fetch(`http://127.0.0.1:${port}/${operation}`, {
        method: "POST",
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        body: JSON.stringify(args),
        signal: AbortSignal.timeout(10_000),
    });
    const result = await response.json();
    if (!response.ok)
    {
        throw new Error(result.error ?? `Source reader returned HTTP ${response.status}.`);
    }
    return result;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1]))
{
    const [operation, directory, outputFile] = process.argv.slice(2);
    if (!directory)
    {
        throw new Error("A private source-input directory is required.");
    }
    if (operation === "serve")
    {
        await startSourceServer(directory);
    }
    else if (operation === "wait")
    {
        for (let attempt = 0; ; attempt++)
        {
            try
            {
                await requestSourceTools(directory, "evidence");
                break;
            }
            catch (error)
            {
                if (attempt >= 49 || (error.code !== "ENOENT" && error.cause?.code !== "ECONNREFUSED"))
                {
                    throw error;
                }
                await delay(100);
            }
        }
    }
    else if (operation === "evidence" && outputFile)
    {
        await writeFile(outputFile, `${JSON.stringify(await requestSourceTools(directory, "evidence"))}\n`);
    }
    else
    {
        throw new Error("Expected serve, wait, or evidence with an output path.");
    }
}
