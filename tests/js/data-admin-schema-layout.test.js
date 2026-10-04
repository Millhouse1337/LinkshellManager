// Tests for wwwroot/js/data-admin-schema-layout.js: the geometry behind the Data Admin schema
// diagram (card sizes, automatic layout, relationship line paths, pan / zoom). They run on Node's
// built-in test runner, so there is nothing to install:
//
//     node --test tests/js/data-admin-schema-layout.test.js
//
// Why these exist: the layout module is the part of the diagram that is easy to get subtly wrong
// (a card one row out, a line that misses its row, two tables on top of each other) and none of
// that shows until someone opens the page. The module is kept free of the DOM so that it can be
// pinned here instead.
//
// Every expected path string and position in the worked examples is worked out BY HAND from the
// formulas, with the working in a comment beside it. None is pasted from the module's output: a
// pasted value only proves that the code does what it does.
//
// The "random" blocks at the end of the line and layout sections go wider: a few hundred anchors
// and graphs from a seeded generator (the same ones on every run), checked against the line
// formula written out a second time and against what a layout promises.
'use strict';

const { describe, it } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const MODULE_PATH = path.join(
    __dirname, '..', '..', 'wwwroot', 'js', 'data-admin-schema-layout.js');
const L = require(MODULE_PATH);
const M = L.METRICS;
const PITCH = M.cardW + M.gapX;     // 280 + 110 = 390: the x step from one card column to the next

// ---- Builders for small synthetic graphs ------------------------------------------------------

// A table with `rows` columns in all: an Id primary key, then one foreign-key column per entry
// of `fks`, then plain columns. Flags that are false are left out, as the server leaves them out.
function table(id, options) {
    const o = options || {};
    const columns = [{ name: 'Id', type: 'int', storeType: 'integer', pk: true }];
    (o.fks || []).forEach((name) => {
        columns.push({ name, type: 'int', storeType: 'integer', fk: true });
    });
    for (let i = columns.length; i < (o.rows || 1); i++) {
        columns.push({ name: 'Col' + i, type: 'text', storeType: 'text', nullable: true });
    }
    const made = { id, name: id + 's', label: id, columns, uniques: [] };
    if (o.parent) { made.parent = o.parent; }
    if (o.hub) { made.hub = true; }
    return made;
}

// The foreign key from.column -> to.Id.
function rel(from, column, to, extra) {
    return Object.assign({
        id: from + '.' + column, from, fromColumns: [column], to, toColumns: ['Id'],
        required: true, onDelete: 'cascade', efDelete: 'Cascade'
    }, extra);
}

// Collects a graph: add(id, rows, parentId) makes a table that parentId owns through a
// "<parent>Id" foreign key (so it is also the table's layout parent).
function graphBuilder() {
    const tables = [];
    const relationships = [];
    const add = (id, rows, parent) => {
        tables.push(table(id, { rows, parent, fks: parent ? [parent + 'Id'] : [] }));
        if (parent) { relationships.push(rel(id, parent + 'Id', parent)); }
    };
    return { add, graph: () => ({ tables, relationships, junctions: [] }) };
}

const two = (n) => (n < 10 ? '0' + n : String(n));
const range = (from, to) => Array.from({ length: to - from + 1 }, (unused, i) => from + i);

// Freezes a graph all the way down. The module is strict-mode code, so an attempt to change its
// input then throws instead of passing silently.
function deepFreeze(value) {
    if (value && typeof value === 'object' && !Object.isFrozen(value)) {
        Object.freeze(value);
        Object.keys(value).forEach((key) => deepFreeze(value[key]));
    }
    return value;
}

// The same graph with its arrays in another order. The order is pseudo-random but fixed by the
// seed (no Math.random here either), so a failure can be reproduced.
function shuffled(graph, seed) {
    let state = seed;
    const next = () => {
        state = (state * 48271) % 2147483647;
        return state;
    };
    const mix = (list) => {
        const out = list.slice();
        for (let i = out.length - 1; i > 0; i--) {
            const j = next() % (i + 1);
            const held = out[i];
            out[i] = out[j];
            out[j] = held;
        }
        return out;
    };
    return {
        tables: mix(graph.tables),
        relationships: mix(graph.relationships),
        junctions: mix(graph.junctions)
    };
}

// Pseudo-random numbers in [0, 1) that are the same on every run and every machine (mulberry32).
function seededRandom(seed) {
    let a = seed;
    return () => {
        a = (a + 0x6D2B79F5) | 0;
        let t = Math.imul(a ^ (a >>> 15), 1 | a);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}
const intBetween = (rng, low, high) => low + Math.floor(rng() * (high - low + 1));

// A graph of `count` tables with 0 to 60 columns each and ids in mixed case (so ordinal order
// matters). About a third hang under the first table, which makes it a spine in the bigger
// graphs. Some tables name themselves, or a table that does not exist, as their parent. With
// `loops` a parent may be ANY table, so parent links form cycles; without it a parent always
// comes earlier in the list, so the links form a forest, as the server's do.
function randomGraph(rng, count, loops) {
    const prefixes = ['T', 'a', 'Zeta', 'm_'];
    const ids = range(0, count - 1).map((i) => prefixes[intBetween(rng, 0, 3)] + i);
    const tables = ids.map((id, i) => {
        const rows = rng() < 0.2 ? intBetween(rng, 0, 60) : intBetween(rng, 0, 12);
        const columns = range(1, rows).map((n) => {
            const column = { name: 'c' + n, type: 'int', storeType: 'integer' };
            if (rng() < 0.2) { column.pk = true; }
            if (rng() < 0.25) { column.fk = true; }
            if (rng() < 0.15) { column.unique = true; }
            return column;
        });
        const made = { id, name: id + 's', label: id, columns, uniques: [] };
        const r = rng();
        if (r < 0.2) { return made; }
        if (r < 0.25) {
            made.parent = id;
        } else if (r < 0.3) {
            made.parent = 'Unknown' + intBetween(rng, 0, 3);
        } else if (loops) {
            made.parent = rng() < 0.35 ? ids[0] : ids[intBetween(rng, 0, count - 1)];
        } else if (i > 0) {
            made.parent = rng() < 0.35 ? ids[0] : ids[intBetween(rng, 0, i - 1)];
        }
        return made;
    });
    return { tables, relationships: [], junctions: [] };
}

// Does following parent links from some table come back round (through at least two tables)?
function hasParentLoop(graph) {
    const parentOf = new Map(graph.tables.map((t) => [t.id, t.parent]));
    return graph.tables.some((t) => {
        const seen = new Set([t.id]);
        let at = t.id;
        let up = parentOf.get(at);
        while (up !== at && parentOf.has(up)) {
            if (seen.has(up)) { return true; }
            seen.add(up);
            at = up;
            up = parentOf.get(at);
        }
        return false;
    });
}

// The shapes the layout has to cope with, in one graph: a hub root with 12 children (enough to
// make it a spine), one of those children with children of its own, a 5-long chain, a table on
// its own, and a group nested two deep.
function sampleGraph() {
    const b = graphBuilder();
    b.add('Hub', 9);
    range(1, 12).forEach((i) => { b.add('HubChild' + two(i), 2 + (i * 5) % 11, 'Hub'); });
    b.add('HubChild03Sub1', 4, 'HubChild03');
    b.add('HubChild03Sub2', 7, 'HubChild03');
    b.add('ChainA', 3);
    b.add('ChainB', 5, 'ChainA');
    b.add('ChainC', 2, 'ChainB');
    b.add('ChainD', 8, 'ChainC');
    b.add('ChainE', 4, 'ChainD');
    b.add('Lonely', 6);
    b.add('Owner', 5);
    b.add('OwnerItem1', 12, 'Owner');
    b.add('OwnerItem2', 3, 'Owner');
    b.add('OwnerItem3', 20, 'Owner');
    b.add('OwnerItem1Part1', 6, 'OwnerItem1');
    b.add('OwnerItem1Part2', 2, 'OwnerItem1');
    const graph = b.graph();
    graph.tables.find((t) => t.id === 'Hub').hub = true;
    return graph;
}

// About the shape of the real model: one root that owns most of the database (35 tables), a
// second root with 15, groups nested two and three deep, a chain and a few tables on their own.
// 1 + 35 + 10 + 2 + 1 + 15 + 4 + 3 = 71 tables, with 2 to 53 columns each.
function databaseSizedGraph() {
    const b = graphBuilder();
    b.add('Linkshell', 53);
    range(1, 35).forEach((i) => { b.add('Ls' + two(i), 2 + (i * 7) % 29, 'Linkshell'); });
    range(1, 10).forEach((i) => { b.add('Ls' + two(i) + 'Item', 2 + (i * 5) % 17, 'Ls' + two(i)); });
    b.add('Ls01ItemPart', 4, 'Ls01Item');
    b.add('Ls02ItemPart', 9, 'Ls02Item');
    b.add('AppUser', 40);
    range(1, 15).forEach((i) => { b.add('User' + two(i), 2 + i, 'AppUser'); });
    b.add('ChainA', 6);
    b.add('ChainB', 3, 'ChainA');
    b.add('ChainC', 11, 'ChainB');
    b.add('ChainD', 2, 'ChainC');
    range(1, 3).forEach((i) => { b.add('Solo' + i, 4 + i); });
    return b.graph();
}

// ---- What a layout promises -------------------------------------------------------------------
// Two cards overlap when their rectangles intersect. The layouts promise more than "no overlap":
// every card sits on the column grid (x is a whole number of pitches, so cards in different
// columns are at least gapX apart), and cards stacked in one column keep at least gapY between
// them.
function assertTidy(positions, index, density) {
    const cards = Object.keys(positions).map((id) => ({
        id, x: positions[id].x, y: positions[id].y, h: L.cardHeight(index.byId[id], density)
    }));
    cards.forEach((card) => {
        assert.ok(Number.isInteger(card.x / PITCH), card.id + ' is off the grid at x = ' + card.x);
    });
    for (let i = 0; i < cards.length; i++) {
        for (let j = i + 1; j < cards.length; j++) {
            const a = cards[i];
            const b = cards[j];
            const apart = a.x + M.cardW <= b.x || b.x + M.cardW <= a.x ||
                a.y + a.h <= b.y || b.y + b.h <= a.y;
            assert.ok(apart, a.id + ' and ' + b.id + ' overlap');
            if (a.x === b.x) {
                const upper = a.y <= b.y ? a : b;
                const lower = upper === a ? b : a;
                assert.ok(lower.y - (upper.y + upper.h) >= M.gapY,
                    upper.id + ' and ' + lower.id + ' are closer than gapY');
            }
        }
    }
}

const near = (actual, expected, what) => {
    assert.ok(Math.abs(actual - expected) < 1e-9, what + ': ' + actual + ' is not ' + expected);
};

// Everything autoLayout promises for the tables that are not hidden: each is drawn exactly once,
// tidily, with the picture's top-left corner at (0, 0) and bounds that match the cards.
function assertLayout(out, graph, index, density, hidden) {
    const visible = graph.tables.map((t) => t.id).filter((id) => !hidden.includes(id)).sort();
    assert.deepEqual(Object.keys(out.positions).sort(), visible);
    if (visible.length === 0) {
        assert.deepEqual(out.bounds, { x0: 0, y0: 0, x1: 0, y1: 0 });
        return;
    }
    assertTidy(out.positions, index, density);
    assert.deepEqual(out.bounds, L.boundsOf(out.positions, index, density));
    assert.equal(out.bounds.x0, 0);
    assert.equal(out.bounds.y0, 0);
}

// Two layouts are the same picture. Checked before deepEqual, which is very slow at describing
// the difference between two large objects when they do differ.
function assertSameLayout(actual, expected, what) {
    assert.equal(JSON.stringify(sortedKeys(actual)), JSON.stringify(sortedKeys(expected)), what);
}
function sortedKeys(positions) {
    const out = {};
    Object.keys(positions).sort().forEach((id) => { out[id] = positions[id]; });
    return out;
}

// ---- The line formula, written out a second time ----------------------------------------------
// The edgeGeometry rules, straight from the design. The random test compares the module with
// this; the worked examples (numbers by hand) guard against both sharing a mistake.
function expectedEdge(a, b, style) {
    const text = (value) => String(Math.round(value * 100) / 100);
    let da = 1;
    let db = 1;
    let ax = a.x + M.cardW;
    let bx = b.x + M.cardW;
    if (b.x > a.x + M.cardW + M.sideGap) {
        db = -1;
        bx = b.x;
    } else if (b.x + M.cardW + M.sideGap < a.x) {
        da = -1;
        ax = a.x;
    }
    let ay = a.y;
    let by = b.y;
    if (da === db && Math.abs(ay - by) < 16) {
        ay -= 8;
        by += 8;
    }
    const sx = ax + da * M.stub;
    const ex = bx + db * M.stub;
    let d;
    let midX;
    if (style === 'orthogonal') {
        midX = da === db ? Math.max(sx, ex) + 20 : (sx + ex) / 2;
        d = ['M' + text(ax), text(ay), 'H' + text(midX), 'V' + text(by), 'H' + text(bx)].join(' ');
    } else {
        const c = Math.max(60, Math.abs(ex - sx) / 2);
        midX = (sx + ex) / 2 + 0.375 * c * (da + db);
        d = ['M' + text(ax), text(ay), 'H' + text(sx), 'C' + text(sx + da * c), text(ay),
            text(ex + db * c), text(by), text(ex), text(by), 'H' + text(bx)].join(' ');
    }
    return {
        d, mid: { x: midX, y: (ay + by) / 2 },
        from: { x: ax, y: ay, dir: da }, to: { x: bx, y: by, dir: db }
    };
}

// ---- Reading a path string back ---------------------------------------------------------------

// 'M280 100 H302' -> [{ cmd: 'M', args: [280, 100] }, { cmd: 'H', args: [302] }]
function parsePath(d) {
    const steps = [];
    const pattern = /([MLHVCa])([^MLHVCa]*)/g;
    let found = pattern.exec(d);
    while (found !== null) {
        steps.push({ cmd: found[1], args: found[2].split(' ').filter(Boolean).map(Number) });
        found = pattern.exec(d);
    }
    return steps;
}

// Everywhere a marker path reaches: { x0, x1, y0, y1 }. Each "a" is half of a circle that
// starts and ends on its horizontal diameter, so it reaches ry above and below the pen.
function extentOf(d) {
    const xs = [];
    const ys = [];
    let x = 0;
    let y = 0;
    parsePath(d).forEach((step) => {
        const a = step.args;
        if (step.cmd === 'M' || step.cmd === 'L') { x = a[0]; y = a[1]; }
        if (step.cmd === 'H') { x = a[0]; }
        if (step.cmd === 'V') { y = a[0]; }
        if (step.cmd === 'a') {
            ys.push(y - a[1], y + a[1]);
            x += a[5];
            y += a[6];
        }
        xs.push(x);
        ys.push(y);
    });
    return {
        x0: Math.min.apply(null, xs), x1: Math.max.apply(null, xs),
        y0: Math.min.apply(null, ys), y1: Math.max.apply(null, ys)
    };
}

// The point of a curved path at t = 0.5, read back from the path string itself:
// "M ax ay H sx C x1 y1 x2 y2 x3 y3 H bx" is a cubic from (sx, ay), and a cubic's midpoint is
// (P0 + 3 P1 + 3 P2 + P3) / 8.
function curveMiddle(d) {
    const steps = parsePath(d);
    assert.deepEqual(steps.map((s) => s.cmd), ['M', 'H', 'C', 'H']);
    const c = steps[2].args;
    const x0 = steps[1].args[0];
    const y0 = steps[0].args[1];
    return { x: (x0 + 3 * c[0] + 3 * c[2] + c[4]) / 8, y: (y0 + 3 * c[1] + 3 * c[3] + c[5]) / 8 };
}

const layoutOf = (graph, opts) => L.autoLayout(L.buildIndex(graph), opts);
const ZERO_BOUNDS = { x0: 0, y0: 0, x1: 0, y1: 0 };
const DENSITIES = ['all', 'keys', 'names'];

// ===============================================================================================

describe('the module itself', () => {
    it('exports every function the page script uses', () => {
        ['buildIndex', 'visibleRows', 'cardHeight', 'rowAnchorY', 'cardinality', 'headerFont',
            'splitName', 'farLabelSize', 'autoLayout', 'focusLayout', 'edgeGeometry', 'markerPath',
            'fitView', 'zoomAt', 'boundsOf'].forEach((name) => {
            assert.equal(typeof L[name], 'function', name);
        });
    });

    it('has the metrics the stylesheet and the spec agree on', () => {
        assert.deepEqual(Object.assign({}, M), {
            cardW: 280, headH: 44, rowH: 30, gapX: 110, gapY: 40, stub: 22, sideGap: 40,
            fitPad: 60, minZoom: 0.1, maxZoom: 2, fitMaxZoom: 1.25, farZoom: 0.6, farLabelPx: 12,
            spineMinChildren: 8, groupMaxH: 1200, aspect: 1.6, focusColMaxH: 1400, maxCols: 40
        });
    });

    it('does not let a caller change the metrics', () => {
        assert.ok(Object.isFrozen(M));
        assert.throws(() => { M.cardW = 1; }, TypeError);
        assert.equal(L.cardHeight(table('T', { rows: 2 }), 'all'), 104);
    });

    // In the browser the file is a plain <script defer>: there is no `module`, only `window`.
    it('loads as a classic script and sets window.LsmSchemaLayout', () => {
        const source = fs.readFileSync(MODULE_PATH, 'utf8');
        const sandbox = { window: {} };
        vm.runInNewContext(source, sandbox, { filename: 'data-admin-schema-layout.js' });
        const api = sandbox.window.LsmSchemaLayout;
        assert.equal(typeof api.autoLayout, 'function');
        assert.equal(api.METRICS.cardW, 280);
        assert.equal(api.markerPath('one', 280, 100, 1), 'M288 93 V107 M293 93 V107');
    });
});

describe('buildIndex', () => {
    const graph = () => {
        const b = table('B', { fks: ['AId', 'CId', 'BId'], parent: 'A' });
        const z = table('Z', { fks: ['CId', 'GoneId'], parent: 'C' });
        const s = table('S', { fks: ['SId'], hub: true });
        return {
            tables: [z, table('C'), s, b, table('A', { hub: true })],
            relationships: [
                rel('Z', 'GoneId', 'Gone'), rel('Z', 'CId', 'C'), rel('S', 'SId', 'S'),
                rel('B', 'CId', 'C'), rel('B', 'BId', 'B'), rel('B', 'AId', 'A')
            ],
            junctions: []
        };
    };

    it('sorts tables and relationships by id and drops a line to an unknown table', () => {
        const index = L.buildIndex(graph());
        assert.deepEqual(index.tables.map((t) => t.id), ['A', 'B', 'C', 'S', 'Z']);
        assert.deepEqual(index.rels.map((r) => r.id), ['B.AId', 'B.BId', 'B.CId', 'S.SId', 'Z.CId']);
        assert.equal(index.byId.B.id, 'B');
        assert.equal(index.relsById['B.CId'].to, 'C');
        assert.equal(index.relsById['Z.GoneId'], undefined);
    });

    // A locale-aware sort would mix the cases (a, A, b, B). Code units give: A B _ a b.
    it('sorts ordinally, not by locale', () => {
        const index = L.buildIndex({
            tables: ['b', 'B', 'a', 'A', '_x'].map((id) => table(id)), relationships: []
        });
        assert.deepEqual(index.tables.map((t) => t.id), ['A', 'B', '_x', 'a', 'b']);
    });

    it('lists the relationships at either end of a table, a self-reference once', () => {
        const index = L.buildIndex(graph());
        assert.deepEqual(index.relsOf('B').map((r) => r.id), ['B.AId', 'B.BId', 'B.CId']);
        assert.deepEqual(index.relsOf('C').map((r) => r.id), ['B.CId', 'Z.CId']);
        assert.deepEqual(index.relsOf('S').map((r) => r.id), ['S.SId']);
        assert.deepEqual(index.relsOf('Nope'), []);
    });

    it('gives distinct, sorted neighbours that never include the table itself', () => {
        const index = L.buildIndex(graph());
        assert.deepEqual(index.neighbours('B'), { principals: ['A', 'C'], dependents: [] });
        assert.deepEqual(index.neighbours('C'), { principals: [], dependents: ['B', 'Z'] });
        assert.deepEqual(index.neighbours('S'), { principals: [], dependents: [] });
        assert.deepEqual(index.neighbours('Nope'), { principals: [], dependents: [] });
    });

    it('classifies a line as self, then hub, then owner, then ref', () => {
        const index = L.buildIndex(graph());
        const kind = (id) => index.edgeKind(index.relsById[id]);
        assert.equal(kind('S.SId'), 'self');     // S is a hub, but a self-reference comes first
        assert.equal(kind('B.BId'), 'self');
        assert.equal(kind('B.AId'), 'hub');      // A is B's parent, but a hub comes before owner
        assert.equal(kind('Z.CId'), 'owner');
        assert.equal(kind('B.CId'), 'ref');
    });

    it('copes with an empty or missing graph', () => {
        [undefined, null, {}, { tables: [] }].forEach((g) => {
            const index = L.buildIndex(g);
            assert.deepEqual(index.tables, []);
            assert.deepEqual(index.rels, []);
            assert.deepEqual(L.autoLayout(index, {}), { positions: {}, bounds: ZERO_BOUNDS });
        });
    });
});

describe('rows, heights and anchors', () => {
    // Id (pk), AId (fk), Col2, Col3, Code (unique): 5 columns, 3 of them keys.
    const t = table('T', { rows: 4, fks: ['AId'] });
    t.columns.push({ name: 'Code', type: 'text', storeType: 'text', unique: true });

    it('picks the rows for each density', () => {
        const names = (density) => L.visibleRows(t, density).map((c) => c.name);
        assert.deepEqual(names('all'), ['Id', 'AId', 'Col2', 'Col3', 'Code']);
        assert.deepEqual(names('keys'), ['Id', 'AId', 'Code']);
        assert.deepEqual(names('names'), []);
        assert.deepEqual(names('no-such-density'), names('all'));
        assert.deepEqual(names(undefined), names('all'));
    });

    it('makes a card headH + rowH per row tall', () => {
        assert.equal(L.cardHeight(t, 'all'), 194);       // 44 + 5 * 30
        assert.equal(L.cardHeight(t, 'keys'), 134);      // 44 + 3 * 30
        assert.equal(L.cardHeight(t, 'names'), 44);
    });

    it('anchors a line on the middle of its row, or of the header when the row is not drawn', () => {
        assert.equal(L.rowAnchorY(t, 'Id', 'all'), 59);       // 44 + 0 * 30 + 15
        assert.equal(L.rowAnchorY(t, 'Col3', 'all'), 149);    // 44 + 3 * 30 + 15
        assert.equal(L.rowAnchorY(t, 'Code', 'all'), 179);    // 44 + 4 * 30 + 15
        assert.equal(L.rowAnchorY(t, 'Code', 'keys'), 119);   // third key row: 44 + 2 * 30 + 15
        assert.equal(L.rowAnchorY(t, 'Col3', 'keys'), 22);    // not a key: header middle
        assert.equal(L.rowAnchorY(t, 'Id', 'names'), 22);
        assert.equal(L.rowAnchorY(t, 'Missing', 'all'), 22);
    });

    it('reads the two ends of a foreign key', () => {
        assert.deepEqual(L.cardinality({ required: true }),
            { child: 'zeroOrMany', parent: 'one', label: 'N : 1' });
        assert.deepEqual(L.cardinality({}),
            { child: 'zeroOrMany', parent: 'zeroOrOne', label: 'N : 1' });
        assert.deepEqual(L.cardinality({ required: true, unique: true }),
            { child: 'zeroOrOne', parent: 'one', label: '1 : 1' });
    });

    it('measures the box around cards', () => {
        const index = L.buildIndex({ tables: [table('A', { rows: 2 }), table('B', { rows: 5 })] });
        const at = { A: { x: -100, y: 20 }, B: { x: 390, y: -50 }, Ghost: { x: 9999, y: 9999 } };
        // A: 280 x 104 -> right 180, bottom 124. B: 280 x 194 -> right 670, bottom 144.
        assert.deepEqual(L.boundsOf(at, index, 'all'), { x0: -100, y0: -50, x1: 670, y1: 144 });
        assert.deepEqual(L.boundsOf(at, index, 'all', ['A']), { x0: -100, y0: 20, x1: 180, y1: 124 });
        assert.deepEqual(L.boundsOf(at, index, 'names', new Set(['B'])),
            { x0: 390, y0: -50, x1: 670, y1: -6 });
        assert.deepEqual(L.boundsOf(at, index, 'all', []), ZERO_BOUNDS);
        assert.deepEqual(L.boundsOf({}, index, 'all'), ZERO_BOUNDS);
    });
});

describe('headerFont', () => {
    const font = (chars) => L.headerFont('x'.repeat(chars));

    // inner = 280 - 28 = 252. With the count: chars * 8.4 + 58 <= 252, so chars <= 23.
    // Without it: chars * size * 0.6 <= 252 -> 14: 30, 13: 32, 12: 35, 11: 38, 10.5: 40 chars.
    it('steps down exactly where the name stops fitting', () => {
        assert.deepEqual(font(1), { size: 14, showCount: true });
        assert.deepEqual(font(23), { size: 14, showCount: true });     // 251.2
        assert.deepEqual(font(24), { size: 14, showCount: false });    // 259.6 with the count
        assert.deepEqual(font(30), { size: 14, showCount: false });    // 252 exactly
        assert.deepEqual(font(31), { size: 13, showCount: false });    // 241.8
        assert.deepEqual(font(32), { size: 13, showCount: false });    // 249.6
        assert.deepEqual(font(33), { size: 12, showCount: false });    // 237.6
        assert.deepEqual(font(35), { size: 12, showCount: false });    // 252 exactly
        assert.deepEqual(font(36), { size: 11, showCount: false });    // 237.6
        assert.deepEqual(font(38), { size: 11, showCount: false });    // 250.8
        assert.deepEqual(font(39), { size: 10.5, showCount: false });  // 245.7
        assert.deepEqual(font(40), { size: 10.5, showCount: false });  // 252 exactly
        assert.deepEqual(font(41), { size: 10, showCount: false });
        assert.deepEqual(font(90), { size: 10, showCount: false });
    });
});

describe('splitName', () => {
    it('breaks before a capital that follows a lower-case letter or a digit', () => {
        assert.deepEqual(L.splitName('AspNetUserRoles'), ['Asp', 'Net', 'User', 'Roles']);
        assert.deepEqual(L.splitName('Ls01Item'), ['Ls01', 'Item']);
        assert.deepEqual(L.splitName('appUser'), ['app', 'User']);
        assert.deepEqual(L.splitName('Tod'), ['Tod']);
        assert.deepEqual(L.splitName('DKPPool'), ['DKPPool']);        // capital after a capital
        assert.deepEqual(L.splitName('lower_case'), ['lower_case']);
        assert.deepEqual(L.splitName(''), []);
    });

    it('gives the name back when the pieces are joined', () => {
        ['AspNetUserRoles', 'EventPartySlotSignup', 'X', 'a1B2c3D', 'DKPPool', ''].forEach((name) => {
            assert.equal(L.splitName(name).join(''), name);
        });
    });
});

describe('farLabelSize', () => {
    // Box: 252 wide, 0.86 * height high. Glyph 0.6 * size wide, line 1.15 * size high.
    it('is limited by the height for a short name', () => {
        // 'Tod' on a 44px card: 1.15 * size <= 37.84 -> size <= 32.9 -> 32 (width allows 140).
        assert.equal(L.farLabelSize('Tod', 44), 32);
        // On a 194px card the height allows 145, so the cap of 96 applies.
        assert.equal(L.farLabelSize('Tod', 194), 96);
    });

    it('is limited by the width for a long name on one line', () => {
        // 15 chars on one line: 15 * 0.6 * size <= 252 -> 28. Two lines would need size <= 16.
        assert.equal(L.farLabelSize('AspNetUserRoles', 44), 28);
    });

    it('wraps at name boundaries when there is height for more lines', () => {
        // 344px card: 295.84 high. At 70 a line holds 252 / 42 = 6 chars: AspNet / User / Roles,
        // 3 lines = 241.5 high: fits. At 72 a line holds 5 chars: Asp / Net / User / Roles,
        // 4 lines = 331.2: too tall. From 86 up 'Roles' (5 chars) is wider than a line.
        assert.equal(L.farLabelSize('AspNetUserRoles', 344), 70);
    });

    it('never goes below 8, even when nothing fits', () => {
        assert.equal(L.farLabelSize('x'.repeat(60), 44), 8);     // 60 * 0.6 * 8 = 288 > 252
        assert.equal(L.farLabelSize('Tod', 1), 8);
    });

    it('returns even sizes and never shrinks as the card grows', () => {
        ['Tod', 'AspNetUserRoles', 'EventPartySlotSignup', 'AppUserEventHistoryWindowSnapshot']
            .forEach((name) => {
                let last = 0;
                for (let rows = 0; rows <= 60; rows++) {
                    const size = L.farLabelSize(name, M.headH + rows * M.rowH);
                    assert.ok(size >= 8 && size <= 96 && size % 2 === 0, name + ' ' + size);
                    assert.ok(size >= last, name + ' shrank at ' + rows + ' rows');
                    last = size;
                }
            });
    });
});

describe('edgeGeometry', () => {
    // W = 280, stub = 22, sideGap = 40. a is the child's anchor, b the parent's.

    it('uses the facing sides when the parent is clearly to the right', () => {
        // 390 > 0 + 280 + 40: da = 1, db = -1, ax = 280, bx = 390, sx = 302, ex = 368.
        // c = max(60, 66 / 2) = 60: control points 302 + 60 = 362 and 368 - 60 = 308.
        const a = { x: 0, y: 100 };
        const b = { x: 390, y: 59 };
        const g = L.edgeGeometry(a, b, 'curved');
        assert.equal(g.d, 'M280 100 H302 C362 100 308 59 368 59 H390');
        assert.deepEqual(g.mid, { x: 335, y: 79.5 });          // (302 + 368) / 2, (100 + 59) / 2
        assert.deepEqual(g.from, { x: 280, y: 100, dir: 1 });
        assert.deepEqual(g.to, { x: 390, y: 59, dir: -1 });
        assert.equal(L.edgeGeometry(a, b).d, g.d);             // curved is the default

        const o = L.edgeGeometry(a, b, 'orthogonal');          // mx = (302 + 368) / 2 = 335
        assert.equal(o.d, 'M280 100 H335 V59 H390');
        assert.deepEqual(o.mid, { x: 335, y: 79.5 });
        assert.deepEqual(o.from, g.from);
        assert.deepEqual(o.to, g.to);
    });

    it('uses the facing sides when the parent is clearly to the left', () => {
        // 0 + 280 + 40 < 780: da = -1, db = 1, ax = 780, bx = 280, sx = 758, ex = 302.
        // c = max(60, 456 / 2) = 228: control points 758 - 228 = 530 and 302 + 228 = 530.
        const a = { x: 780, y: 200 };
        const b = { x: 0, y: 66 };
        const g = L.edgeGeometry(a, b, 'curved');
        assert.equal(g.d, 'M780 200 H758 C530 200 530 66 302 66 H280');
        assert.deepEqual(g.mid, { x: 530, y: 133 });
        assert.deepEqual(g.from, { x: 780, y: 200, dir: -1 });
        assert.deepEqual(g.to, { x: 280, y: 66, dir: 1 });

        const o = L.edgeGeometry(a, b, 'orthogonal');          // mx = (758 + 302) / 2 = 530
        assert.equal(o.d, 'M780 200 H530 V66 H280');
        assert.deepEqual(o.mid, { x: 530, y: 133 });
    });

    it('loops round the right-hand side when the cards share a column', () => {
        // da = db = 1, ax = bx = 280, sx = ex = 302, c = 60: both control points at 362.
        const a = { x: 0, y: 300 };
        const b = { x: 0, y: 66 };
        const g = L.edgeGeometry(a, b, 'curved');
        assert.equal(g.d, 'M280 300 H302 C362 300 362 66 302 66 H280');
        assert.deepEqual(g.mid, { x: 347, y: 183 });           // 302 + 0.375 * 60 * 2
        assert.deepEqual(g.from, { x: 280, y: 300, dir: 1 });
        assert.deepEqual(g.to, { x: 280, y: 66, dir: 1 });

        const o = L.edgeGeometry(a, b, 'orthogonal');          // mx = max(302, 302) + 20
        assert.equal(o.d, 'M280 300 H322 V66 H280');
        assert.deepEqual(o.mid, { x: 322, y: 183 });
    });

    it('needs MORE than sideGap between the cards to use the facing sides', () => {
        // Exactly 40 apart (320 is not > 320): both on the right, ax = 280, bx = 600. The two
        // ends are then on one side at the same height, so they are pulled 8px apart.
        // sx = 302, ex = 622, c = max(60, 160) = 160: control points 462 and 782.
        const g = L.edgeGeometry({ x: 0, y: 100 }, { x: 320, y: 100 }, 'curved');
        assert.equal(g.d, 'M280 92 H302 C462 92 782 108 622 108 H600');
        assert.deepEqual(g.mid, { x: 582, y: 100 });           // 462 + 0.375 * 160 * 2
        // 41 apart: facing. sx = 302, ex = 299, c = 60. No pull: the ends are on different sides.
        assert.equal(L.edgeGeometry({ x: 0, y: 100 }, { x: 321, y: 100 }, 'curved').d,
            'M280 100 H302 C362 100 239 100 299 100 H321');
        // Mirror image: the parent exactly 40 to the left is also a loop on the right.
        assert.equal(L.edgeGeometry({ x: 320, y: 100 }, { x: 0, y: 300 }, 'orthogonal').d,
            'M600 100 H642 V300 H280');                        // max(622, 302) + 20
    });

    it('opens a self-reference on one row into a visible loop', () => {
        // Same card, same row (y = 59): ay = 51, by = 67, sx = ex = 302, c = 60.
        const a = { x: 0, y: 59 };
        const g = L.edgeGeometry(a, a, 'curved');
        assert.equal(g.d, 'M280 51 H302 C362 51 362 67 302 67 H280');
        assert.deepEqual(g.mid, { x: 347, y: 59 });
        assert.deepEqual(g.from, { x: 280, y: 51, dir: 1 });
        assert.deepEqual(g.to, { x: 280, y: 67, dir: 1 });
        assert.deepEqual(a, { x: 0, y: 59 });                  // the anchor itself is not moved
        assert.equal(L.edgeGeometry(a, a, 'orthogonal').d, 'M280 51 H322 V67 H280');
        // A self-reference between two different rows (30px apart) is left alone.
        assert.equal(L.edgeGeometry({ x: 0, y: 89 }, { x: 0, y: 59 }, 'curved').d,
            'M280 89 H302 C362 89 362 59 302 59 H280');
        // 15px apart is still "one row" (74 - 8 = 66, 59 + 8 = 67); 16px is not.
        assert.equal(L.edgeGeometry({ x: 0, y: 74 }, { x: 0, y: 59 }, 'orthogonal').d,
            'M280 66 H322 V67 H280');
        assert.equal(L.edgeGeometry({ x: 0, y: 75 }, { x: 0, y: 59 }, 'orthogonal').d,
            'M280 75 H322 V59 H280');
    });

    it('writes numbers with at most 2 decimals and no trailing zeros', () => {
        // ax = 280.5, bx = 500, sx = 302.5, ex = 478, c = max(60, 175.5 / 2) = 87.75:
        // control points 302.5 + 87.75 = 390.25 and 478 - 87.75 = 390.25.
        assert.equal(L.edgeGeometry({ x: 0.5, y: 10.25 }, { x: 500, y: -0.25 }, 'curved').d,
            'M280.5 10.25 H302.5 C390.25 10.25 390.25 -0.25 478 -0.25 H500');
        // 1/3 -> 0.33, 2/3 -> 0.67.
        assert.equal(L.edgeGeometry({ x: 0, y: 1 / 3 }, { x: 390, y: 2 / 3 }, 'curved').d,
            'M280 0.33 H302 C362 0.33 308 0.67 368 0.67 H390');
        // A value that rounds to zero from below is "0", never "-0".
        assert.equal(L.edgeGeometry({ x: 0, y: -0.001 }, { x: 390, y: 50 }, 'orthogonal').d,
            'M280 0 H335 V50 H390');
    });

    it('reports a middle that lies on the curve', () => {
        const cases = [
            [{ x: 0, y: 100 }, { x: 390, y: 59 }], [{ x: 780, y: 200 }, { x: 0, y: 66 }],
            [{ x: 0, y: 300 }, { x: 0, y: 66 }], [{ x: 0, y: 59 }, { x: 0, y: 59 }],
            [{ x: 0, y: 100 }, { x: 320, y: 100 }], [{ x: 1170, y: 733 }, { x: 390, y: 59 }],
            [{ x: 390, y: 59 }, { x: 250, y: 911 }]
        ];
        cases.forEach((pair) => {
            const g = L.edgeGeometry(pair[0], pair[1], 'curved');
            const middle = curveMiddle(g.d);
            near(g.mid.x, middle.x, 'mid.x of ' + g.d);
            near(g.mid.y, middle.y, 'mid.y of ' + g.d);
        });
    });

    it('starts and ends every path on the reported card edges', () => {
        ['curved', 'orthogonal'].forEach((style) => {
            const g = L.edgeGeometry({ x: 780, y: 200 }, { x: 0, y: 66 }, style);
            const steps = parsePath(g.d);
            assert.deepEqual(steps[0], { cmd: 'M', args: [g.from.x, g.from.y] });
            assert.deepEqual(steps[steps.length - 1], { cmd: 'H', args: [g.to.x] });
        });
    });
});

describe('edgeGeometry: random anchors', () => {
    // 900 child / parent pairs: a third with the parent clearly to the right, a third clearly to
    // the left, a third in overlapping columns (the loop), and about one in three almost level
    // (the 8px pull). Everything is on a 0.5px grid, so each path number is exact to 2 decimals.
    it('matches the formula for every side case and both styles', () => {
        const rng = seededRandom(123);
        const half = (value) => Math.round(value * 2) / 2;
        const sides = { '1,-1': 0, '-1,1': 0, '1,1': 0 };
        for (let i = 0; i < 900; i++) {
            const a = { x: half((rng() - 0.5) * 6000), y: half((rng() - 0.5) * 4000) };
            const shift = i % 3 === 0 ? 321 + rng() * 3000
                : i % 3 === 1 ? -321 - rng() * 3000
                    : (rng() - 0.5) * 640;
            const b = {
                x: half(a.x + shift),
                y: rng() < 0.3 ? a.y + half((rng() - 0.5) * 30) : half((rng() - 0.5) * 4000)
            };
            const before = JSON.stringify([a, b]);
            ['curved', 'orthogonal'].forEach((style) => {
                const what = style + ' ' + before;
                const got = L.edgeGeometry(a, b, style);
                const want = expectedEdge(a, b, style);
                assert.equal(got.d, want.d, what);
                assert.deepEqual(got.from, want.from, what);
                assert.deepEqual(got.to, want.to, what);
                near(got.mid.x, want.mid.x, 'mid.x ' + what);
                near(got.mid.y, want.mid.y, 'mid.y ' + what);
                // 2 decimals at most, no trailing zeros, never "-0".
                got.d.split(/[ MHVC]+/).filter(Boolean).forEach((number) => {
                    assert.match(number, /^-?\d+(\.\d?[1-9])?$/, what);
                    assert.notEqual(number, '-0', what);
                });
                if (style === 'curved') {
                    const middle = curveMiddle(got.d);
                    near(got.mid.x, middle.x, 'mid on the curve, x ' + what);
                    near(got.mid.y, middle.y, 'mid on the curve, y ' + what);
                    sides[got.from.dir + ',' + got.to.dir] += 1;
                }
            });
            assert.equal(JSON.stringify([a, b]), before);      // the anchors are not changed
        }
        // Every side case really was exercised.
        Object.keys(sides).forEach((key) => { assert.ok(sides[key] >= 250, key + ': ' + sides[key]); });
    });
});

describe('markerPath', () => {
    // The circle for both "zero" ends: centre x + dir * 16, r 3.5, starting at its left-most point.

    it('draws two bars for exactly one', () => {
        assert.equal(L.markerPath('one', 280, 100, 1), 'M288 93 V107 M293 93 V107');
        assert.equal(L.markerPath('one', 390, 59, -1), 'M382 52 V66 M377 52 V66');
    });

    it('draws a bar and a circle for zero or one', () => {
        // Circle centre 296: starts at 292.5.
        assert.equal(L.markerPath('zeroOrOne', 280, 100, 1),
            'M288 93 V107 M292.5 100 a3.5 3.5 0 1 0 7 0 a3.5 3.5 0 1 0 -7 0');
        // dir -1 at x = 0: bar at -8, circle centre -16 starts at -19.5.
        assert.equal(L.markerPath('zeroOrOne', 0, 50, -1),
            'M-8 43 V57 M-19.5 50 a3.5 3.5 0 1 0 7 0 a3.5 3.5 0 1 0 -7 0');
    });

    it('draws a crow\'s foot and a circle for zero or many', () => {
        assert.equal(L.markerPath('zeroOrMany', 280, 100, 1),
            'M292 100 L280 93 M292 100 L280 100 M292 100 L280 107 ' +
            'M292.5 100 a3.5 3.5 0 1 0 7 0 a3.5 3.5 0 1 0 -7 0');
        assert.equal(L.markerPath('zeroOrMany', 0, 50, -1),
            'M-12 50 L0 43 M-12 50 L0 50 M-12 50 L0 57 ' +
            'M-19.5 50 a3.5 3.5 0 1 0 7 0 a3.5 3.5 0 1 0 -7 0');
    });

    it('draws nothing for an unknown kind', () => {
        assert.equal(L.markerPath('many', 280, 100, 1), '');
        assert.equal(L.markerPath(undefined, 280, 100, 1), '');
    });

    it('stays on the stub and mirrors with dir', () => {
        ['one', 'zeroOrOne', 'zeroOrMany'].forEach((kind) => {
            const right = extentOf(L.markerPath(kind, 500, 200, 1));
            const left = extentOf(L.markerPath(kind, 500, 200, -1));
            assert.ok(right.x0 >= 500 && right.x1 <= 500 + M.stub, kind + ' leaves the stub (right)');
            assert.ok(left.x1 <= 500 && left.x0 >= 500 - M.stub, kind + ' leaves the stub (left)');
            assert.ok(right.y0 >= 193 && right.y1 <= 207, kind + ' is taller than 7px each way');
            // Mirror image about x = 500.
            near(left.x0, 1000 - right.x1, kind + ' mirror x0');
            near(left.x1, 1000 - right.x0, kind + ' mirror x1');
            assert.equal(left.y0, right.y0);
            assert.equal(left.y1, right.y1);
        });
    });
});

describe('fitView', () => {
    const viewport = { width: 1200, height: 800 };

    it('fits and centres the bounds', () => {
        // min(1080 / 1000, 680 / 500) = 1.08. x = (1200 - 1080) / 2 = 60, y = (800 - 540) / 2 = 130.
        const v = L.fitView({ x0: 0, y0: 0, x1: 1000, y1: 500 }, viewport);
        near(v.zoom, 1.08, 'zoom');
        near(v.x, 60, 'x');
        near(v.y, 130, 'y');
        // The same box moved to (100, 200): x = 60 - 100 * 1.08, y = 130 - 200 * 1.08.
        const moved = L.fitView({ x0: 100, y0: 200, x1: 1100, y1: 700 }, viewport);
        near(moved.zoom, 1.08, 'zoom');
        near(moved.x, -48, 'x');
        near(moved.y, -86, 'y');
        // Its centre (600, 450) lands on the viewport's centre (600, 400).
        near(600 * moved.zoom + moved.x, 600, 'centre x');
        near(450 * moved.zoom + moved.y, 400, 'centre y');
    });

    it('never zooms in past fitMaxZoom', () => {
        // 100 x 100 would fit at 6.8: clamped to 1.25. x = (1200 - 125) / 2, y = (800 - 125) / 2.
        assert.deepEqual(L.fitView({ x0: 0, y0: 0, x1: 100, y1: 100 }, viewport),
            { x: 537.5, y: 337.5, zoom: 1.25 });
    });

    it('never zooms out past minZoom', () => {
        // 100000 square would need 0.0068: clamped to 0.1. x = (1200 - 10000) / 2, y likewise.
        const v = L.fitView({ x0: 0, y0: 0, x1: 100000, y1: 100000 }, viewport);
        assert.equal(v.zoom, 0.1);
        near(v.x, -4400, 'x');
        near(v.y, -4600, 'y');
        // A viewport smaller than the padding gives a negative ratio: still minZoom.
        const tiny = L.fitView({ x0: 0, y0: 0, x1: 500, y1: 500 }, { width: 100, height: 100 });
        assert.equal(tiny.zoom, 0.1);
    });

    it('gives the plain view when there is nothing to fit or nowhere to fit it', () => {
        const plain = { x: 0, y: 0, zoom: 1 };
        const box = { x0: 0, y0: 0, x1: 10, y1: 10 };
        assert.deepEqual(L.fitView(ZERO_BOUNDS, viewport), plain);
        assert.deepEqual(L.fitView({ x0: 5, y0: 5, x1: 5, y1: 90 }, viewport), plain);
        assert.deepEqual(L.fitView(box, { width: 0, height: 800 }), plain);
        assert.deepEqual(L.fitView(box, { width: 1200, height: 0 }), plain);
    });
});

describe('zoomAt', () => {
    it('keeps the canvas point under the cursor where it is', () => {
        // Canvas point under (400, 300) at zoom 1 is (300, 250). At zoom 2: 400 - 300 * 2 = -200
        // and 300 - 250 * 2 = -200.
        assert.deepEqual(L.zoomAt({ x: 100, y: 50, zoom: 1 }, { x: 400, y: 300 }, 2),
            { x: -200, y: -200, zoom: 2 });
        const view = { x: -37, y: 212, zoom: 0.8 };
        const point = { x: 613, y: 77 };
        [0.1, 0.33, 0.8, 1, 1.7, 2].forEach((zoom) => {
            const next = L.zoomAt(view, point, zoom);
            near((point.x - next.x) / next.zoom, (point.x - view.x) / view.zoom, 'x at ' + zoom);
            near((point.y - next.y) / next.zoom, (point.y - view.y) / view.zoom, 'y at ' + zoom);
        });
        assert.deepEqual(view, { x: -37, y: 212, zoom: 0.8 });
    });

    it('clamps the zoom to minZoom..maxZoom', () => {
        const view = { x: 0, y: 0, zoom: 1 };
        assert.equal(L.zoomAt(view, { x: 10, y: 10 }, 5).zoom, 2);
        assert.equal(L.zoomAt(view, { x: 10, y: 10 }, 0.01).zoom, 0.1);
        // Clamped to 2: x = 10 - 10 * 2 = -10.
        assert.deepEqual(L.zoomAt(view, { x: 10, y: 10 }, 99), { x: -10, y: -10, zoom: 2 });
    });
});

describe('autoLayout: worked examples', () => {
    it('puts children in a column to the right of their parent, tallest first', () => {
        // P 104 tall; A 74, B 134. Children sort tallest first: B then A. One column:
        // B at y 0, A at y 134 + 40 = 174; height 174 + 74 = 248 <= 1200, so k = 1.
        const b = graphBuilder();
        b.add('P', 2);
        b.add('A', 2, 'P');
        b.add('B', 3, 'P');
        const g = b.graph();
        g.tables.find((t) => t.id === 'A').columns.pop();       // A: Id only -> 74 tall
        const out = layoutOf(g, { density: 'all' });
        assert.deepEqual(out.positions,
            { P: { x: 0, y: 0 }, B: { x: 390, y: 0 }, A: { x: 390, y: 174 } });
        assert.deepEqual(out.bounds, { x0: 0, y0: 0, x1: 670, y1: 248 });
    });

    it('widens a group to the smallest column count that keeps it under groupMaxH', () => {
        // Four children 644 tall (20 rows). 1 column: 2696. 2 columns: 1328. 3 columns: the
        // fourth card goes under the first, 1328 again. 4 columns: 644 <= 1200.
        const b = graphBuilder();
        b.add('P', 2);
        range(1, 4).forEach((i) => { b.add('C' + i, 20, 'P'); });
        assert.deepEqual(layoutOf(b.graph(), { density: 'all' }).positions, {
            P: { x: 0, y: 0 }, C1: { x: 390, y: 0 }, C2: { x: 780, y: 0 },
            C3: { x: 1170, y: 0 }, C4: { x: 1560, y: 0 }
        });
    });

    it('lets a group be as tall as its own head card', () => {
        // The parent has 53 rows (1634 tall), so the limit is 1634 and 2 columns (1328) will do.
        const b = graphBuilder();
        b.add('P', 53);
        range(1, 4).forEach((i) => { b.add('C' + i, 20, 'P'); });
        const out = layoutOf(b.graph(), { density: 'all' });
        assert.deepEqual(out.positions, {
            P: { x: 0, y: 0 }, C1: { x: 390, y: 0 }, C2: { x: 780, y: 0 },
            C3: { x: 390, y: 684 }, C4: { x: 780, y: 684 }
        });
        assert.deepEqual(out.bounds, { x0: 0, y0: 0, x1: 1060, y1: 1634 });
    });

    it('picks the column count whose shape is closest to the target aspect', () => {
        // Six lone tables 74 tall. k = 1: 280 x 644 (0.43). k = 2: 670 x 302 (2.22).
        // k = 3: 1060 x 188 (5.64). Against 1.6, k = 2 is closest (off by a factor 1.39).
        // Equal blocks fill left to right in id order.
        const g = { tables: range(1, 6).map((i) => table('T' + i)), relationships: [] };
        assert.deepEqual(layoutOf(g, { density: 'all' }).positions, {
            T1: { x: 0, y: 0 }, T2: { x: 390, y: 0 }, T3: { x: 0, y: 114 },
            T4: { x: 390, y: 114 }, T5: { x: 0, y: 228 }, T6: { x: 390, y: 228 }
        });
    });

    it('places wider blocks first, then taller, then by id', () => {
        // Blocks: group G (G + G1, span 2, 104 tall), Tall (span 1, 164), A and B (span 1, 74).
        // Order: G, Tall, A, B. k = 2 (670 wide): G at y 0; Tall (0, 144); A (390, 144);
        // B (390, 258): 332 tall -> 2.02. k = 3 (1060 wide): 218 tall -> 4.86. k = 2 wins.
        const b = graphBuilder();
        b.add('B', 1);
        b.add('A', 1);
        b.add('Tall', 4);
        b.add('G', 2);
        b.add('G1', 2, 'G');
        assert.deepEqual(layoutOf(b.graph(), { density: 'all' }).positions, {
            G: { x: 0, y: 0 }, G1: { x: 390, y: 0 }, Tall: { x: 0, y: 144 },
            A: { x: 390, y: 144 }, B: { x: 390, y: 258 }
        });
    });

    it('keeps a root with fewer than spineMinChildren children as one group', () => {
        // 7 children 104 tall in one column: 7 * 104 + 6 * 40 = 968 <= 1200.
        const b = graphBuilder();
        b.add('R', 1);
        range(1, 7).forEach((i) => { b.add('C' + i, 2, 'R'); });
        const at = layoutOf(b.graph(), { density: 'all' }).positions;
        assert.deepEqual(at.R, { x: 0, y: 0 });
        range(1, 7).forEach((i) => { assert.deepEqual(at['C' + i], { x: 390, y: (i - 1) * 144 }); });
    });

    it('turns a root with spineMinChildren children into a lone first card', () => {
        // Density 'names': 9 separate blocks, each 44 tall. k = 1: 280 x 716 (0.39).
        // k = 2: 5 rows, 670 x 380 (1.76). k = 3: 3 rows, 1060 x 212 (5). k = 2 wins.
        // R goes first (top-left), then the children in id order, left to right.
        const b = graphBuilder();
        b.add('R', 1);
        range(1, 8).forEach((i) => { b.add('C' + i, 2, 'R'); });
        assert.deepEqual(layoutOf(b.graph(), { density: 'names' }).positions, {
            R: { x: 0, y: 0 }, C1: { x: 390, y: 0 }, C2: { x: 0, y: 84 }, C3: { x: 390, y: 84 },
            C4: { x: 0, y: 168 }, C5: { x: 390, y: 168 }, C6: { x: 0, y: 252 },
            C7: { x: 390, y: 252 }, C8: { x: 0, y: 336 }
        });
    });
});

describe('autoLayout: a synthetic graph', () => {
    it('draws every table once, on the grid, with no overlap, at every density', () => {
        const graph = sampleGraph();
        const index = L.buildIndex(graph);
        DENSITIES.forEach((density) => {
            const out = L.autoLayout(index, { density });
            assert.deepEqual(Object.keys(out.positions).sort(), graph.tables.map((t) => t.id).sort());
            assertTidy(out.positions, index, density);
            assert.deepEqual(out.bounds, L.boundsOf(out.positions, index, density));
            assert.equal(out.bounds.x0, 0);
            assert.equal(out.bounds.y0, 0);
        });
    });

    it('puts the spine at (0, 0)', () => {
        const index = L.buildIndex(sampleGraph());
        DENSITIES.forEach((density) => {
            assert.deepEqual(L.autoLayout(index, { density }).positions.Hub, { x: 0, y: 0 });
        });
    });

    it('lays a chain out left to right on one y', () => {
        const index = L.buildIndex(sampleGraph());
        DENSITIES.forEach((density) => {
            const at = L.autoLayout(index, { density }).positions;
            ['ChainB', 'ChainC', 'ChainD', 'ChainE'].forEach((id, i) => {
                assert.equal(at[id].y, at.ChainA.y, id + ' y');
                assert.equal(at[id].x, at.ChainA.x + (i + 1) * PITCH, id + ' x');
            });
        });
    });

    it('puts a nested group to the right of its head card', () => {
        const at = L.autoLayout(L.buildIndex(sampleGraph()), { density: 'all' }).positions;
        ['OwnerItem1', 'OwnerItem2', 'OwnerItem3'].forEach((id) => {
            assert.ok(at[id].x > at.Owner.x && at[id].y >= at.Owner.y, id);
        });
        ['OwnerItem1Part1', 'OwnerItem1Part2'].forEach((id) => {
            assert.ok(at[id].x > at.OwnerItem1.x && at[id].y >= at.OwnerItem1.y, id);
        });
        ['HubChild03Sub1', 'HubChild03Sub2'].forEach((id) => {
            assert.equal(at[id].x, at.HubChild03.x + PITCH, id);
        });
    });

    it('promotes the children of a hidden parent', () => {
        const graph = sampleGraph();
        const index = L.buildIndex(graph);
        // Hiding Owner must give the picture of a database that has no Owner table at all.
        const without = {
            tables: graph.tables.filter((t) => t.id !== 'Owner'),
            relationships: graph.relationships, junctions: []
        };
        DENSITIES.forEach((density) => {
            const hidden = L.autoLayout(index, { density, hidden: ['Owner'] });
            assert.equal(hidden.positions.Owner, undefined);
            assert.deepEqual(hidden.positions, layoutOf(without, { density }).positions);
            assertTidy(hidden.positions, index, density);
        });
        // The same through a Set, and with the spine itself hidden.
        const viaSet = L.autoLayout(index, { density: 'all', hidden: new Set(['Owner']) });
        assert.deepEqual(viaSet, L.autoLayout(index, { density: 'all', hidden: ['Owner'] }));
        const noHub = L.autoLayout(index, { density: 'all', hidden: ['Hub', 'Lonely'] });
        assert.equal(Object.keys(noHub.positions).length, graph.tables.length - 2);
        assertTidy(noHub.positions, index, 'all');
        // null and a missing opts mean "nothing hidden, every row".
        assert.deepEqual(L.autoLayout(index, { density: 'all', hidden: null }),
            L.autoLayout(index, { density: 'all' }));
        assert.deepEqual(L.autoLayout(index), L.autoLayout(index, { density: 'all' }));
    });

    it('gives nothing when every table is hidden', () => {
        const graph = sampleGraph();
        const out = layoutOf(graph, { density: 'all', hidden: graph.tables.map((t) => t.id) });
        assert.deepEqual(out, { positions: {}, bounds: ZERO_BOUNDS });
    });

    it('gives the same picture whatever order the arrays arrive in', () => {
        const graph = sampleGraph();
        const index = L.buildIndex(graph);
        [7, 1234, 99991].forEach((seed) => {
            const mixed = shuffled(graph, seed);
            assert.notDeepEqual(mixed.tables.map((t) => t.id), graph.tables.map((t) => t.id));
            const other = L.buildIndex(mixed);
            assert.deepEqual(other.tables.map((t) => t.id), index.tables.map((t) => t.id));
            assert.deepEqual(other.rels.map((r) => r.id), index.rels.map((r) => r.id));
            DENSITIES.forEach((density) => {
                assert.deepEqual(L.autoLayout(other, { density }), L.autoLayout(index, { density }));
                assert.deepEqual(L.autoLayout(other, { density, hidden: ['Hub', 'ChainC'] }),
                    L.autoLayout(index, { density, hidden: ['ChainC', 'Hub'] }));
                assert.deepEqual(L.focusLayout(other, 'Hub', { density }),
                    L.focusLayout(index, 'Hub', { density }));
            });
        });
        // And the same call twice gives the same answer.
        assert.deepEqual(L.autoLayout(index, { density: 'all' }), L.autoLayout(index, { density: 'all' }));
    });

    it('never changes what it is given', () => {
        const graph = deepFreeze(sampleGraph());
        const before = JSON.stringify(graph);
        const hidden = Object.freeze(['Lonely']);
        const index = L.buildIndex(graph);
        DENSITIES.forEach((density) => {
            L.autoLayout(index, { density, hidden });
            L.focusLayout(index, 'Hub', { density, hidden });
        });
        index.relsOf('Hub').pop();                         // a caller's change to a returned list...
        assert.equal(index.relsOf('Hub').length, 12);      // ...does not reach the index
        assert.equal(JSON.stringify(graph), before);
    });

    it('terminates on a parent cycle and still draws every table', () => {
        // A under B under A; C under A; D under itself; E under a table that does not exist;
        // X under Y under Z under X.
        const tables = [
            table('A', { parent: 'B' }), table('B', { parent: 'A' }), table('C', { parent: 'A' }),
            table('D', { parent: 'D' }), table('E', { parent: 'Nowhere' }),
            table('X', { parent: 'Y' }), table('Y', { parent: 'Z' }), table('Z', { parent: 'X' })
        ];
        const index = L.buildIndex({ tables, relationships: [] });
        ['all', 'names'].forEach((density) => {
            const out = L.autoLayout(index, { density });
            assert.deepEqual(Object.keys(out.positions).sort(),
                ['A', 'B', 'C', 'D', 'E', 'X', 'Y', 'Z']);
            assertTidy(out.positions, index, density);
        });
    });
});

describe('autoLayout: a database-sized graph', () => {
    it('lays out about 70 tables without overlap at the three densities', () => {
        const graph = databaseSizedGraph();
        const index = L.buildIndex(graph);
        const counts = graph.tables.map((t) => t.columns.length);
        assert.equal(graph.tables.length, 71);
        assert.equal(Math.min.apply(null, counts), 2);
        assert.equal(Math.max.apply(null, counts), 53);
        assert.equal(index.neighbours('Linkshell').dependents.length, 35);
        assert.equal(index.neighbours('AppUser').dependents.length, 15);

        DENSITIES.forEach((density) => {
            const out = L.autoLayout(index, { density });
            assert.equal(Object.keys(out.positions).length, 71);
            assertTidy(out.positions, index, density);
            assert.deepEqual(out.bounds, L.boundsOf(out.positions, index, density));
            // Both big roots are spines: lone cards on the top row, the first one top-left.
            assert.deepEqual(out.positions.AppUser, { x: 0, y: 0 });
            assert.deepEqual(out.positions.Linkshell, { x: PITCH, y: 0 });
            // A nested group stays together: head, then its item, then the item's part.
            assert.equal(out.positions.Ls01Item.x, out.positions.Ls01.x + PITCH);
            assert.equal(out.positions.Ls01ItemPart.x, out.positions.Ls01.x + 2 * PITCH);
            assert.equal(out.positions.Ls01ItemPart.y, out.positions.Ls01.y);
            // The picture is a sensible shape, not one long row or one tall column.
            const shape = (out.bounds.x1 - out.bounds.x0) / (out.bounds.y1 - out.bounds.y0);
            assert.ok(shape > M.aspect / 2.5 && shape < M.aspect * 2.5, density + ' shape ' + shape);
            assert.deepEqual(L.autoLayout(L.buildIndex(shuffled(graph, 4242)), { density }), out);
        });
    });
});

describe('autoLayout: random graphs', () => {
    const SIZES = [0, 1, 2, 3, 5, 8, 12, 20, 35, 60];

    // Some of the tables (none, a fifth or a half), sometimes with an id that does not exist.
    const randomHidden = (rng, graph) => {
        const share = [0, 0.2, 0.5][intBetween(rng, 0, 2)];
        const hidden = graph.tables.map((t) => t.id).filter(() => rng() < share);
        if (rng() < 0.3) { hidden.push('GhostTable'); }
        return hidden;
    };

    it('draws every visible table once, on the grid, with no overlap', () => {
        const rng = seededRandom(20261004);
        for (let i = 0; i < 240; i++) {
            const graph = randomGraph(rng, SIZES[intBetween(rng, 0, SIZES.length - 1)], false);
            const density = DENSITIES[i % 3];
            const hidden = randomHidden(rng, graph);
            const index = L.buildIndex(graph);
            const out = L.autoLayout(index, { density, hidden });
            assertLayout(out, graph, index, density, hidden);

            // The order of the arrays, and an array against a Set, make no difference.
            const mixed = L.buildIndex(shuffled(graph, 1 + i));
            assertSameLayout(L.autoLayout(mixed, { density, hidden: new Set(hidden) }).positions,
                out.positions, 'shuffled, case ' + i);
            // A hidden table is laid out round as if it were not in the database at all.
            const without = {
                tables: graph.tables.filter((t) => !hidden.includes(t.id)),
                relationships: [], junctions: []
            };
            assertSameLayout(layoutOf(without, { density }).positions, out.positions,
                'hidden against removed, case ' + i);
        }
    });

    it('terminates on parent cycles and still draws every table', () => {
        const rng = seededRandom(999);
        let loops = 0;
        for (let i = 0; i < 240; i++) {
            const graph = randomGraph(rng, [2, 3, 4, 6, 10, 16, 30][intBetween(rng, 0, 6)], true);
            const density = DENSITIES[i % 3];
            const hidden = randomHidden(rng, graph);
            const index = L.buildIndex(graph);
            const out = L.autoLayout(index, { density, hidden });
            if (hasParentLoop(graph)) { loops += 1; }
            assertLayout(out, graph, index, density, hidden);
            assertSameLayout(
                L.autoLayout(L.buildIndex(shuffled(graph, 1 + i)), { density, hidden }).positions,
                out.positions, 'shuffled, case ' + i);
        }
        assert.ok(loops >= 40, 'only ' + loops + ' of the graphs had a parent loop');
    });

    // The extremes: one loop through every table, one chain 300 deep (a block 300 columns wide,
    // far past maxCols), and one root with 299 children.
    it('copes with a 300-table ring, chain and star', () => {
        const make = (parentOf) => ({
            tables: range(0, 299).map((i) => {
                const made = table('N' + i);
                const parent = parentOf(i);
                if (parent !== null) { made.parent = 'N' + parent; }
                return made;
            }),
            relationships: [], junctions: []
        });
        const shapes = {
            ring: make((i) => (i + 1) % 300),
            chain: make((i) => (i > 0 ? i - 1 : null)),
            star: make((i) => (i > 0 ? 0 : null))
        };
        Object.keys(shapes).forEach((name) => {
            const index = L.buildIndex(shapes[name]);
            DENSITIES.forEach((density) => {
                assertLayout(L.autoLayout(index, { density }), shapes[name], index, density, []);
            });
        });
        // The chain is one row; the star's root is a spine at the top-left.
        const chain = L.autoLayout(L.buildIndex(shapes.chain), { density: 'all' }).positions;
        assert.deepEqual(chain.N299, { x: 299 * PITCH, y: 0 });
        assert.deepEqual(L.autoLayout(L.buildIndex(shapes.star), { density: 'all' }).positions.N0,
            { x: 0, y: 0 });
    });
});

describe('focusLayout', () => {
    // F points at M, P1 and P2 (and at itself); D1, D2 and M point at F.
    const focusGraph = () => ({
        tables: [
            table('F', { fks: ['P1Id', 'P2Id', 'MId', 'FId'] }),       // 5 columns: 194 tall
            table('P1', { rows: 2 }), table('P2'),                     // 104 and 74 tall
            table('M', { fks: ['FId'] }),                              // 104 tall
            table('D1', { fks: ['FId'] }), table('D2', { fks: ['FId', 'OtherFId'] }),
            table('Far', { fks: ['P1Id'] })
        ],
        relationships: [
            rel('F', 'P1Id', 'P1'), rel('F', 'P2Id', 'P2'), rel('F', 'MId', 'M'), rel('F', 'FId', 'F'),
            rel('M', 'FId', 'F'), rel('D1', 'FId', 'F'), rel('D2', 'FId', 'F'),
            rel('D2', 'OtherFId', 'F'), rel('Far', 'P1Id', 'P1')
        ],
        junctions: []
    });

    it('puts principals on the left and dependents on the right', () => {
        // Left column (x = -390), in id order: M at 0, P1 at 104 + 40 = 144, P2 at 144 + 144 = 288.
        // Right column (x = 390): D1 at 0 (104 tall), D2 at 144 (134 tall).
        const index = L.buildIndex(focusGraph());
        const out = L.focusLayout(index, 'F', { density: 'all' });
        assert.deepEqual(out.positions, {
            F: { x: 0, y: 0 },
            M: { x: -390, y: 0 }, P1: { x: -390, y: 144 }, P2: { x: -390, y: 288 },
            D1: { x: 390, y: 0 }, D2: { x: 390, y: 144 }
        });
        // P2 ends at 288 + 74 = 362; the right edge is 390 + 280.
        assert.deepEqual(out.bounds, { x0: -390, y0: 0, x1: 670, y1: 362 });
        assertTidy(out.positions, index, 'all');
    });

    it('lists each table once: the focus first, a two-way neighbour on the left only', () => {
        const out = L.focusLayout(L.buildIndex(focusGraph()), 'F', { density: 'all' });
        assert.deepEqual(out.ids, ['F', 'M', 'P1', 'P2', 'D1', 'D2']);
        assert.equal(new Set(out.ids).size, out.ids.length);
        assert.deepEqual(Object.keys(out.positions).sort(), out.ids.slice().sort());
    });

    it('leaves out hidden neighbours', () => {
        const index = L.buildIndex(focusGraph());
        const out = L.focusLayout(index, 'F', { density: 'all', hidden: new Set(['M', 'D1']) });
        assert.deepEqual(out.ids, ['F', 'P1', 'P2', 'D2']);
        assert.deepEqual(out.positions, {
            F: { x: 0, y: 0 }, P1: { x: -390, y: 0 }, P2: { x: -390, y: 144 }, D2: { x: 390, y: 0 }
        });
    });

    it('follows the density', () => {
        // 'names': every card 44 tall, so the step down a column is 84.
        const out = L.focusLayout(L.buildIndex(focusGraph()), 'F', { density: 'names' });
        assert.deepEqual(out.positions.P2, { x: -390, y: 168 });
        assert.deepEqual(out.positions.D2, { x: 390, y: 84 });
    });

    it('gives nothing for an unknown or hidden table', () => {
        const index = L.buildIndex(focusGraph());
        const nothing = { positions: {}, bounds: ZERO_BOUNDS, ids: [] };
        assert.deepEqual(L.focusLayout(index, 'Nope', { density: 'all' }), nothing);
        assert.deepEqual(L.focusLayout(index, undefined, { density: 'all' }), nothing);
        assert.deepEqual(L.focusLayout(index, 'constructor', { density: 'all' }), nothing);
        assert.deepEqual(L.focusLayout(index, 'F', { density: 'all', hidden: ['F'] }), nothing);
    });

    it('draws a table with no neighbours on its own', () => {
        const index = L.buildIndex({ tables: [table('Solo', { rows: 3 })], relationships: [] });
        assert.deepEqual(L.focusLayout(index, 'Solo', { density: 'all' }), {
            positions: { Solo: { x: 0, y: 0 } },
            bounds: { x0: 0, y0: 0, x1: 280, y1: 134 },
            ids: ['Solo']
        });
    });

    // A focus table with `focusRows` columns that points at `count` tables (U1..) and is pointed
    // at by `count` tables (W1..), each of them with `rows` columns.
    const wrapGraph = (focusRows, rows, count) => {
        const fks = range(1, count).map((i) => 'U' + i + 'Id');
        const tables = [table('F', { rows: focusRows, fks })];
        const relationships = [];
        range(1, count).forEach((i) => {
            tables.push(table('W' + i, { rows, fks: ['FId'] }));
            relationships.push(rel('W' + i, 'FId', 'F'));
            tables.push(table('U' + i, { rows }));
            relationships.push(rel('F', 'U' + i + 'Id', 'U' + i));
        });
        return { tables, relationships, junctions: [] };
    };

    it('wraps into further columns, outwards on both sides', () => {
        // Cards 644 tall (20 rows), limit 1400: the second card starts at 684 and ends at 1328;
        // a third would end at 1368 + 644 = 2012, so it starts the next column out.
        const index = L.buildIndex(wrapGraph(6, 20, 5));
        const out = L.focusLayout(index, 'F', { density: 'all' });
        assert.deepEqual(out.ids, ['F', 'U1', 'U2', 'U3', 'U4', 'U5', 'W1', 'W2', 'W3', 'W4', 'W5']);
        assert.deepEqual(out.positions, {
            F: { x: 0, y: 0 },
            W1: { x: 390, y: 0 }, W2: { x: 390, y: 684 }, W3: { x: 780, y: 0 },
            W4: { x: 780, y: 684 }, W5: { x: 1170, y: 0 },
            U1: { x: -390, y: 0 }, U2: { x: -390, y: 684 }, U3: { x: -780, y: 0 },
            U4: { x: -780, y: 684 }, U5: { x: -1170, y: 0 }
        });
        assert.deepEqual(out.bounds, { x0: -1170, y0: 0, x1: 1450, y1: 1328 });
        assertTidy(out.positions, index, 'all');
    });

    it('lets a column grow to the focus card\'s own height', () => {
        // Cards 494 tall (15 rows): a third would end at 1068 + 494 = 1562 > 1400, so 2 per column.
        const short = L.focusLayout(L.buildIndex(wrapGraph(6, 15, 3)), 'F', { density: 'all' });
        assert.deepEqual(short.positions.W3, { x: 780, y: 0 });
        // With a 53-row focus card (1634 tall) the limit is 1634 and the third fits.
        const tall = L.focusLayout(L.buildIndex(wrapGraph(53, 15, 3)), 'F', { density: 'all' });
        assert.deepEqual(tall.positions.W3, { x: 390, y: 1068 });
        assert.deepEqual(tall.positions.U3, { x: -390, y: 1068 });
    });

    it('gives a card taller than the limit a column of its own', () => {
        // 53 rows = 1634 > 1400: one card per column.
        const out = L.focusLayout(L.buildIndex(wrapGraph(6, 53, 3)), 'F', { density: 'all' });
        range(1, 3).forEach((i) => {
            assert.deepEqual(out.positions['W' + i], { x: i * 390, y: 0 });
            assert.deepEqual(out.positions['U' + i], { x: -i * 390, y: 0 });
        });
    });
});
