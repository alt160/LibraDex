import assert from 'node:assert/strict';

// Design model only: no LibraDex binary, storage, concurrency, or durability is exercised.
// Mirrors RouterReader's exact multi-byte search, then stem-only nearest-route fallback.
// Bytes are unsigned; absent routing bytes are zero, as in GetKeyByteOrZero.
const results = [];
function comparePadded(a, b) {
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
        const difference = (a[i] ?? 0) - (b[i] ?? 0);
        if (difference) return difference;
    }
    return 0;
}
function findCompressed(routes, key) {
    let probes = 0;
    for (const route of routes) {
        probes++;
        if (comparePadded(route.stem, key.slice(0, route.stem.length)) !== 0) continue;
        const last = key[route.stem.length] ?? 0;
        if (last >= route.lo && last <= route.hi) return { target: route.target, exact: true, probes };
    }
    let previous = 0;
    for (let i = 0; i < routes.length; i++) {
        probes++;
        if (comparePadded(key.slice(0, routes[i].stem.length), routes[i].stem) < 0)
            return { target: routes[i === 0 ? 0 : previous].target, exact: false, probes };
        previous = i;
    }
    return { target: routes[previous]?.target ?? null, exact: false, probes };
}
function check(id, description, test) {
    results.push({ id, description, ...test(), passed: true });
}

check('M01', 'Shared-parent promotion admits an earlier-byte divergence', () => {
    const route = key => [0x20, 0x21].includes(key[0]) ? (key[1] < 0x80 ? 'L' : 'R') : null;
    assert.equal(route([0x20, 0xf0]), 'R');
    assert.equal(route([0x21, 0x00]), 'L');
    assert.ok(comparePadded([0x21, 0x00], [0x20, 0xf0]) > 0);
    return { result: 'Counterexample: later key enters earlier shelf' };
});

const complete = [
    { stem: [0x3c], lo: 0, hi: 0x7b, target: 'L' },
    { stem: [0x3c], lo: 0x7c, hi: 255, target: 'R' }
];
check('M02', 'Multi-byte fallback expands effective ownership beyond stored stems', () => {
    const lower = findCompressed(complete, [0x3b, 255]);
    const upper = findCompressed(complete, [0x3d, 0]);
    assert.equal(lower.target, 'L');
    assert.equal(upper.target, 'R');
    assert.equal(lower.exact, false);
    assert.equal(upper.exact, false);
    return { lower, upper };
});
check('M03', 'Sparse final-byte ranges cannot safely be constructed from tuple min/max alone', () => {
    const sparse = [
        { stem: [0x3c], lo: 0x20, hi: 0x3f, target: 'L' },
        { stem: [0x3c], lo: 0x80, hi: 0x9f, target: 'R' }
    ];
    const actual = findCompressed(sparse, [0x3c, 0x10]);
    assert.equal(actual.target, 'R');
    assert.ok(comparePadded([0x3c, 0x10], [0x3c, 0x20]) < 0);
    return { result: 'Counterexample to proposed sparse encoding; not a reproduced production publisher', actual };
});
check('M04', 'Complete two-route layout has monotone targets over all two-byte keys', () => {
    let previous = 0, probes = 0, left = 0, right = 0;
    for (let a = 0; a < 256; a++) for (let b = 0; b < 256; b++) {
        const found = findCompressed(complete, [a, b]);
        const rank = found.target === 'L' ? 0 : 1;
        assert.ok(rank >= previous);
        previous = rank;
        probes += found.probes;
        if (rank === 0) left++; else right++;
    }
    return { keys: 65536, left, right, probes, result: 'This layout only; not subsequent mutation or engine proof' };
});
check('M05', 'One-byte consumer cannot interpret a two-byte router correctly', () => {
    const key = [0x3c, 0x90];
    const byteOnly = complete.find(route => key[0] >= route.lo && key[0] <= route.hi).target;
    assert.equal(byteOnly, 'L');
    assert.equal(findCompressed(complete, key).target, 'R');
    return { byteOnly, fullKey: 'R' };
});
check('M06', 'Parent widening must include sibling domain expansion', () => {
    const bytes = (slots, width) => 64 + slots * (10 + width - 1);
    assert.equal(bytes(257, 2), 2891);
    assert.ok(bytes(257, 2) <= 4096);
    assert.equal(bytes(512, 3), 6208);
    assert.ok(bytes(512, 3) > 4096);
    return { oneToTwoBytes: bytes(257, 2), twoToThreeBytes: bytes(512, 3), note: 'Representation bounds, not full-consumer eligibility' };
});
check('M07', 'Exact direct-parent refinement excludes unseen sibling stems', () => {
    let cold = 0;
    for (let a = 0; a < 256; a++) for (let b = 0; b < 256; b++) {
        const target = a === 0x20 ? (b < 128 ? 'L' : 'R') : null;
        if (a !== 0x20) { assert.equal(target, null); cold++; }
    }
    return { keys: 65536, cold, note: 'Actual cold-route creation still needs engine tests' };
});
check('M08', 'Variable-length routing equality is weaker than key equality', () => {
    assert.equal(comparePadded([0x3c], [0x3c, 0]), 0);
    assert.notDeepEqual([0x3c], [0x3c, 0]);
    return { result: 'Terminal decision must compare complete keys, including length' };
});
check('M09', 'Updating one of two owners leaves the other alias live', () => {
    const parentA = new Map([[0x20, 'T'], [0x21, 'T']]);
    const parentB = new Map([[0x22, 'T']]);
    parentA.delete(0x21);
    assert.equal(parentA.has(0x21), false);
    assert.equal(parentB.get(0x22), 'T');
    return { result: 'Counterexample to single-parent cleanup without an ownership precondition' };
});
check('M10', 'Publishing a stale complete parent image loses a sibling mutation', () => {
    const original = { a: 'A', b: 'B' };
    const first = { ...original, a: 'A2' };
    const staleSecond = { ...original, b: 'B2' };
    assert.equal(staleSecond.a, 'A');
    const revalidatedSecond = { ...first, b: 'B2' };
    assert.deepEqual(revalidatedSecond, { a: 'A2', b: 'B2' });
    return { result: 'Schedule model only; existing engine locking is not exercised' };
});
console.log(JSON.stringify({ kind: 'standalone design model, not engine acceptance', checks: results.length, results }, null, 2));
