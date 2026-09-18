// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { finish } from './workflow.mjs';

const repositoryName = 'mthalman/sdk';
const mainRef = 'refs/heads/main';
const maximumTotalIssues = 3;
const trackingMarker = /<!-- stale-reference(?::|-evidence:)/;

export function createForkClient({ github, repository, ref, dryRun }) {
    if (repository !== repositoryName || ref !== mainRef || typeof dryRun !== 'boolean') {
        throw new Error('Fork deployment requires mthalman/sdk, refs/heads/main, and an explicit dryRun boolean.');
    }

    const observed = new Set();
    let failure;
    let pending = Promise.resolve();

    async function create(parameters) {
        if (failure) {
            throw failure;
        }
        try {
            if (parameters?.owner !== 'mthalman' || parameters?.repo !== 'sdk') {
                throw new Error('Fork deployment refuses an issue-writing target other than mthalman/sdk.');
            }
            if (dryRun) {
                throw new Error('Fork deployment preview refuses all issue writes.');
            }
            if (typeof parameters.body !== 'string' || !trackingMarker.test(parameters.body)) {
                throw new Error('Fork deployment only creates marked stale-reference tracking issues.');
            }

            const seen = new Set();
            let pages = 0;
            let hasNext = true;
            // No label filter: closed tasks and edited labels must still consume the lifetime budget.
            for await (const page of github.paginate.iterator(github.rest.issues.listForRepo, {
                owner: 'mthalman', repo: 'sdk', state: 'all', per_page: 100,
                sort: 'created', direction: 'asc', request: { retries: 0 },
            })) {
                if (!hasNext || page?.status !== 200 || !Array.isArray(page.data) ||
                    !page.headers || (page.headers.link !== undefined && typeof page.headers.link !== 'string')) {
                    throw new Error('Fork issue history is incomplete or malformed.');
                }
                pages++;
                hasNext = /rel="next"/.test(page.headers.link ?? '');
                if (hasNext && page.data.length === 0) {
                    throw new Error('Fork issue history has an empty nonterminal page.');
                }
                for (const issue of page.data) {
                    if (!Number.isSafeInteger(issue?.number) || issue.number <= 0 || seen.has(issue.number) ||
                        !['open', 'closed'].includes(issue.state) ||
                        !(issue.body === null || typeof issue.body === 'string') ||
                        !['User', 'Bot'].includes(issue.user?.type) || typeof issue.user.login !== 'string') {
                        throw new Error('Fork issue history contains malformed or repeated records.');
                    }
                    seen.add(issue.number);
                    // Actions-bot authorship survives removal of every body marker and label.
                    if (!issue.pull_request &&
                        (issue.user.login === 'github-actions[bot]' || trackingMarker.test(issue.body ?? ''))) {
                        observed.add(issue.number);
                    }
                }
            }
            if (pages === 0 || hasNext) {
                throw new Error('Fork issue history pagination did not complete.');
            }
            if (observed.size >= maximumTotalIssues) {
                throw new Error('Fork deployment lifetime limit of three created tracking issues is exhausted.');
            }

            // Do not forward caller-supplied routing, authentication, or retry overrides.
            const result = await github.rest.issues.create({
                owner: 'mthalman', repo: 'sdk',
                title: `[Fork workflow test] ${parameters.title}`.slice(0, 256),
                body: '## Fork workflow test output\n\n' +
                    'This issue was created only to test stale-reference discovery in `mthalman/sdk`. ' +
                    'It is not an upstream work request. Do not dispatch Issue Monster for this issue.\n\n' +
                    parameters.body,
                labels: parameters.labels,
                request: { retries: 0 },
            });
            if (!Number.isSafeInteger(result?.data?.number) || result.data.number <= 0 ||
                seen.has(result.data.number) || observed.has(result.data.number)) {
                throw new Error('Fork issue creation has an ambiguous result; no further writes are permitted.');
            }
            observed.add(result.data.number);
            return result;
        } catch (error) {
            failure = error;
            throw error;
        }
    }

    return {
        github: {
            rest: {
                issues: {
                    get: parameters => github.rest.issues.get(parameters),
                    get listForRepo() { return github.rest.issues.listForRepo; },
                    create: parameters => {
                        const operation = pending.then(() => create(parameters));
                        // Serialize concurrent requests; create() retains and rethrows the first failure.
                        pending = operation.then(() => undefined, () => undefined);
                        return operation;
                    },
                },
                pulls: { get: parameters => github.rest.pulls.get(parameters) },
            },
            paginate: (...parameters) => github.paginate(...parameters),
        },
        assertHealthy() {
            if (failure) {
                throw failure;
            }
        },
    };
}

export async function finishFork({ github, repository, ref, dryRun, ...options }) {
    const client = createForkClient({ github, repository, ref, dryRun });
    const report = await finish({ ...options, github: client.github, repository, dryRun });
    // The unchanged finalizer reconciles POST failures; a safety refusal must still fail the job.
    client.assertHealthy();
    return report;
}
