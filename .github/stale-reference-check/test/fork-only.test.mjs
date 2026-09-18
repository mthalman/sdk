// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import test from 'node:test';
import { createForkClient, finishFork } from '../fork-only.mjs';
import { finalize } from '../finalize.mjs';

const repository = 'mthalman/sdk';
const ref = 'refs/heads/main';
const marker = `<!-- stale-reference:v1:${'a'.repeat(64)} -->`;
const payload = { owner: 'mthalman', repo: 'sdk', title: 'Revalidate test', body: marker, labels: ['cookie'] };

function issue(number, overrides = {}) {
    return {
        number, state: 'closed', body: marker, labels: [],
        user: { type: 'User', login: 'mthalman' }, ...overrides,
    };
}

function mock({ history = [], pages, create } = {}) {
    const calls = { reads: [], histories: [], writes: [] };
    const state = [...history];
    const github = {
        rest: {
            issues: {
                async get(parameters) {
                    calls.reads.push(parameters);
                    return { data: { state: 'closed', state_reason: 'completed', closed_at: '2026-09-01T00:00:00Z' } };
                },
                listForRepo() {},
                async create(parameters) {
                    calls.writes.push(parameters);
                    if (create) {
                        return create(parameters, calls);
                    }
                    const created = issue(1000 + calls.writes.length, {
                        body: parameters.body, state: 'open', user: { type: 'Bot', login: 'github-actions[bot]' },
                    });
                    state.push(created);
                    return { data: created };
                },
            },
            pulls: {
                async get(parameters) {
                    calls.reads.push(parameters);
                    return { data: { state: 'closed', merged_at: '2026-09-01T00:00:00Z' } };
                },
            },
        },
        paginate: async (method, parameters) => {
            assert.equal(method, github.rest.issues.listForRepo);
            return state.filter(item => item.state === parameters.state);
        },
    };
    github.paginate.iterator = async function* (method, parameters) {
        assert.equal(method, github.rest.issues.listForRepo);
        calls.histories.push(parameters);
        if (pages) {
            yield* pages();
        } else {
            yield { data: [...state], status: 200, headers: {} };
        }
    };
    return { github, calls, state };
}

function client(api, overrides = {}) {
    return createForkClient({ github: api.github, repository, ref, dryRun: false, ...overrides });
}

test('the deployment refuses every other target, branch, and implicit preview mode before I/O', async () => {
    const github = new Proxy({}, { get() { throw new Error('No I/O permitted.'); } });
    for (const overrides of [
        { repository: 'dotnet/sdk' }, { repository: 'mthalman/runtime' }, { repository: 'Mthalman/sdk' },
        { ref: 'refs/heads/feature' }, { ref: 'refs/pull/1/merge' },
        { dryRun: undefined }, { dryRun: 'false' },
    ]) {
        assert.throws(() => createForkClient({ github, repository, ref, dryRun: false, ...overrides }),
            /requires mthalman\/sdk/);
        await assert.rejects(finishFork({ github, repository, ref, dryRun: false, ...overrides }),
            /requires mthalman\/sdk/);
    }
});

test('the issue-creation boundary refuses upstream and other targets before any API call', async () => {
    for (const target of [{ owner: 'dotnet' }, { repo: 'runtime' }, { owner: 'Mthalman' }, { owner: undefined }]) {
        const api = mock();
        const guarded = client(api);
        await assert.rejects(guarded.github.rest.issues.create({ ...payload, ...target }), /issue-writing target/);
        assert.equal(api.calls.histories.length, 0);
        assert.equal(api.calls.writes.length, 0);
        assert.throws(() => guarded.assertHealthy(), /issue-writing target/);
    }
});

test('preview cannot create even if a caller bypasses the shared dry-run check', async () => {
    const api = mock();
    const guarded = client(api, { dryRun: true });
    await assert.rejects(guarded.github.rest.issues.create(payload), /preview refuses all issue writes/);
    assert.equal(api.calls.histories.length, 0);
    assert.equal(api.calls.writes.length, 0);
});

test('the facade retains cross-repository read access but exposes no other write APIs', async () => {
    const api = mock();
    const { github } = client(api);
    await github.rest.issues.get({ owner: 'dotnet', repo: 'sdk', issue_number: 1 });
    await github.rest.pulls.get({ owner: 'dotnet', repo: 'runtime', pull_number: 2 });
    assert.equal(api.calls.reads.length, 2);
    assert.deepEqual(Object.keys(github).sort(), ['paginate', 'rest']);
    assert.deepEqual(Object.keys(github.rest).sort(), ['issues', 'pulls']);
    assert.deepEqual(Object.keys(github.rest.issues).sort(), ['create', 'get', 'listForRepo']);
    assert.deepEqual(Object.keys(github.rest.pulls), ['get']);
    assert.equal(api.calls.writes.length, 0);
});

test('new issues are clearly fork test output and preserve stable markers and safe routing', async () => {
    const api = mock();
    const guarded = client(api);
    await guarded.github.rest.issues.create({
        ...payload, title: 'x'.repeat(256),
        url: 'https://api.github.com/repos/dotnet/sdk/issues',
        request: { retries: 99, baseUrl: 'https://example.invalid' },
    });
    const written = api.calls.writes[0];
    assert.equal(written.title.length, 256);
    assert.ok(written.title.startsWith('[Fork workflow test] '));
    assert.match(written.body, /Fork workflow test output/);
    assert.match(written.body, /not an upstream work request/);
    assert.match(written.body, /Do not dispatch Issue Monster/);
    assert.ok(written.body.endsWith(marker));
    assert.deepEqual(written.labels, payload.labels);
    assert.deepEqual(written.request, { retries: 0 });
    assert.equal(written.url, undefined);
    assert.equal(written.owner, 'mthalman');
    assert.equal(written.repo, 'sdk');
    guarded.assertHealthy();
});

test('unmarked issue creation is refused without consuming or querying the budget', async () => {
    const api = mock();
    await assert.rejects(client(api).github.rest.issues.create({ ...payload, body: 'unmarked' }), /only creates marked/);
    assert.equal(api.calls.histories.length, 0);
    assert.equal(api.calls.writes.length, 0);
});

test('three issues total across fresh clients and closed history exhaust the lifetime budget', async () => {
    const api = mock();
    for (let attempt = 0; attempt < 3; attempt++) {
        await client(api).github.rest.issues.create(payload);
        api.state.at(-1).state = 'closed';
    }
    const final = client(api);
    await assert.rejects(final.github.rest.issues.create(payload), /lifetime limit of three/);
    assert.throws(() => final.assertHealthy(), /lifetime limit/);
    assert.equal(api.calls.writes.length, 3);
    assert.equal(api.calls.histories.length, 4);
    assert.ok(api.calls.histories.every(parameters => parameters.state === 'all' &&
        parameters.per_page === 100 && parameters.labels === undefined));
});

test('all pages are consumed including closed, unlabelled, and markerless Actions-bot issues', async () => {
    const visited = [];
    const api = mock({ pages: async function* () {
        visited.push(1);
        yield { data: [issue(1)], status: 200, headers: { link: '<https://api.github.com/page2>; rel="next"' } };
        visited.push(2);
        yield {
            data: [
                issue(2, { state: 'open' }),
                issue(3, { body: null, user: { type: 'Bot', login: 'github-actions[bot]' } }),
            ],
            status: 200, headers: {},
        };
    } });
    await assert.rejects(client(api).github.rest.issues.create(payload), /lifetime limit/);
    assert.deepEqual(visited, [1, 2]);
    assert.equal(api.calls.writes.length, 0);
});

test('human issues and pull requests do not consume the tracking budget', async () => {
    const api = mock({ history: [
        issue(1, { body: 'human issue' }),
        issue(2, { pull_request: { url: 'https://api.github.com/repos/mthalman/sdk/pulls/2' } }),
        issue(3, { body: null }),
    ] });
    await client(api).github.rest.issues.create(payload);
    assert.equal(api.calls.writes.length, 1);
});

test('partial pagination, errors, and malformed records all fail closed', async () => {
    for (const pages of [
        async function* () {},
        async function* () { throw new Error('history unavailable'); },
        async function* () {
            yield { data: [issue(1)], status: 200, headers: { link: '<next>; rel="next"' } };
            throw new Error('page two unavailable');
        },
        async function* () { yield { data: [issue(1)], status: 200, headers: { link: '<next>; rel="next"' } }; },
        async function* () { yield { data: [], status: 200, headers: { link: '<next>; rel="next"' } }; },
        async function* () { yield { data: [], status: 206, headers: {} }; },
        async function* () { yield { data: 'incomplete', status: 200, headers: {} }; },
        async function* () { yield { data: [], status: 200 }; },
        async function* () { yield { data: [issue(1, { body: undefined })], status: 200, headers: {} }; },
        async function* () { yield { data: [issue(1, { user: null })], status: 200, headers: {} }; },
        async function* () { yield { data: [issue(1), issue(1)], status: 200, headers: {} }; },
        async function* () {
            yield { data: [], status: 200, headers: {} };
            yield { data: [], status: 200, headers: {} };
        },
    ]) {
        const api = mock({ pages });
        const guarded = client(api);
        await assert.rejects(guarded.github.rest.issues.create(payload));
        await assert.rejects(guarded.github.rest.issues.create(payload));
        assert.throws(() => guarded.assertHealthy());
        assert.equal(api.calls.writes.length, 0);
        assert.equal(api.calls.histories.length, 1);
    }
});

test('concurrent requests serialize and cannot exceed the remaining budget', async () => {
    const api = mock({ history: [issue(1), issue(2)] });
    const guarded = client(api);
    const results = await Promise.allSettled(Array.from({ length: 5 }, () =>
        guarded.github.rest.issues.create(payload)));
    assert.equal(results.filter(result => result.status === 'fulfilled').length, 1);
    assert.equal(api.calls.writes.length, 1);
});

test('successful writes remain counted when later list responses lag', async () => {
    const api = mock({ pages: async function* () { yield { data: [], status: 200, headers: {} }; } });
    const guarded = client(api);
    for (let index = 0; index < 3; index++) {
        await guarded.github.rest.issues.create(payload);
    }
    await assert.rejects(guarded.github.rest.issues.create(payload), /lifetime limit/);
    assert.equal(api.calls.writes.length, 3);
});

test('ambiguous writes are never retried and prohibit subsequent writes', async () => {
    for (const create of [
        async () => { throw new Error('connection lost after POST'); },
        async () => ({ data: {} }),
        async () => ({ data: { number: 1 } }),
    ]) {
        const api = mock({ history: [issue(1)], create });
        const guarded = client(api);
        await assert.rejects(guarded.github.rest.issues.create(payload));
        await assert.rejects(guarded.github.rest.issues.create(payload));
        assert.throws(() => guarded.assertHealthy());
        assert.equal(api.calls.writes.length, 1);
    }
});

function action(index) {
    const url = `https://github.com/dotnet/sdk/issues/${index}`;
    return {
        candidateId: `candidate-${index}`, kind: 'ignore', path: `test/Feature${index}.cs`,
        anchor: `Tests.Feature${index}.Works`, testNames: [`Tests.Feature${index}.Works`],
        startLine: 1, endLine: 1, sourceExcerpt: `[Ignore("${url}")]`, urls: [url], additionalConditions: [],
    };
}

test('unchanged finalizer is bounded by closed history and never writes upstream', async () => {
    const api = mock({ history: [issue(1), issue(2)] });
    const guarded = client(api);
    const warnings = [];
    const result = await finalize({
        github: guarded.github, repository, headSha: 'b'.repeat(40), dryRun: false,
        actions: [action(4), action(5), action(6)], logger: { warn: message => warnings.push(message) },
    });
    assert.equal(result.created.length, 1);
    assert.equal(api.calls.writes.length, 1);
    assert.ok(api.calls.writes.every(parameters => parameters.owner === 'mthalman' && parameters.repo === 'sdk'));
    assert.ok(api.calls.reads.every(parameters => parameters.owner === 'dotnet'));
    assert.ok(warnings.some(message => message.includes('create-failed')));
    assert.throws(() => guarded.assertHealthy(), /lifetime limit/);
});

test('preview still proposes with an exhausted lifetime budget but performs zero writes', async () => {
    const api = mock({ history: [issue(1), issue(2), issue(3)] });
    const guarded = client(api, { dryRun: true });
    const result = await finalize({
        github: guarded.github, repository, headSha: 'b'.repeat(40), dryRun: true,
        actions: [action(4)], logger: { warn() {} },
    });
    assert.equal(result.proposed.length, 1);
    assert.equal(result.created.length, 0);
    assert.equal(api.calls.writes.length, 0);
    assert.equal(api.calls.histories.length, 0);
    guarded.assertHealthy();
});
