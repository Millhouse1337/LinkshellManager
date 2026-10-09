// Geometry for the Data Admin schema diagram (/data-admin): how tall a card is, where the
// automatic layout puts each table, the path of every relationship line, and the pan / zoom maths.
//
// Why this is its own file: it is the bug-prone part of the page, so it is kept apart from the
// script that touches the DOM (data-admin-schema.js) and can be unit tested with plain Node
// (tests/js/data-admin-schema-layout.test.js; the site has no JS build step or test framework).
// Geometry is always COMPUTED from the numbers in METRICS, never measured from the DOM. That is
// what lets a test check a layout without a browser, and it keeps every line glued to its row at
// any zoom. The stylesheet's job is to make these numbers true.
//
// Rules for code in here: no DOM and no globals (the export at the very bottom is the only
// exception), no Date, no Math.random, and strings are compared with < and > only (ordinal, the
// order the server sorts by), never localeCompare. The same model then gives the same picture in
// every browser and under Node, whatever order its arrays arrive in. Nothing passed in is changed.
(function () {
    'use strict';

    const METRICS = Object.freeze({
        // Card geometry. The stylesheet reads the same numbers through custom properties.
        cardW: 280, headH: 44, rowH: 30,
        // Free space between card columns / between stacked cards.
        gapX: 110, gapY: 40,
        // stub: the straight run where a line meets a card (the crow's foot is drawn on it).
        // sideGap: how far apart two cards must be before a line may use their facing sides.
        stub: 22, sideGap: 40,
        // Fit leaves fitPad px around the diagram and never zooms in past fitMaxZoom.
        fitPad: 60, minZoom: 0.1, maxZoom: 2, fitMaxZoom: 1.25,
        // Below farZoom a card shows one big name, drawn at about farLabelPx on screen.
        farZoom: 0.6, farLabelPx: 12,
        // Layout tuning: see "Automatic layout" and "Focus layout" below.
        spineMinChildren: 8, groupMaxH: 1200, aspect: 1.6, focusColMaxH: 1400, maxCols: 40
    });

    // ---- Small helpers -------------------------------------------------------------------------

    // Ordinal (UTF-16 code unit) order: what < and > do on strings, and what the server sorts by.
    const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
    const idOf = (item) => item.id;
    const hasId = (item) => !!item && typeof item.id === 'string';
    const listOf = (value) => (Array.isArray(value) ? value : []);
    const textOf = (value) => (value === null || value === undefined ? '' : String(value));

    // A sorted COPY: the caller's array is never reordered.
    const sortedById = (items) => items.slice().sort((a, b) => ordinal(a.id, b.id));

    // Own properties only, so an id such as "constructor" can never find Object.prototype's.
    const own = (map, key) =>
        (map && Object.prototype.hasOwnProperty.call(map, key) ? map[key] : undefined);

    // A list of ids may arrive as an array, as a Set, or not at all.
    const toSet = (ids) => (ids instanceof Set ? ids : new Set(ids || []));

    const clamp = (value, low, high) => Math.min(Math.max(value, low), high);
    const zeroBounds = () => ({ x0: 0, y0: 0, x1: 0, y1: 0 });

    // ---- The graph, indexed --------------------------------------------------------------------
    // One pass over the JSON the server wrote (tables, relationships, junctions) so that every
    // later question ("which lines touch this table?") is a lookup. Sorting here is what makes
    // everything downstream independent of the order the arrays arrived in.
    function buildIndex(graph) {
        const source = graph || {};

        const tables = [];
        const byId = {};
        sortedById(listOf(source.tables).filter(hasId)).forEach((table) => {
            if (own(byId, table.id) === undefined) {
                byId[table.id] = table;
                tables.push(table);
            }
        });
        const known = (id) => own(byId, id) !== undefined;

        // A relationship whose table is missing cannot be drawn, so it is dropped here, once.
        const rels = [];
        const relsById = {};
        sortedById(listOf(source.relationships).filter(hasId)).forEach((rel) => {
            if (known(rel.from) && known(rel.to) && own(relsById, rel.id) === undefined) {
                relsById[rel.id] = rel;
                rels.push(rel);
            }
        });

        // The same goes for a many-to-many entry that names a missing table.
        const junctions = listOf(source.junctions)
            .filter((j) => !!j && known(j.via) && known(j.left) && known(j.right))
            .sort((a, b) =>
                ordinal(a.via, b.via) || ordinal(a.left, b.left) || ordinal(a.right, b.right));

        const touching = new Map();      // table id -> its relationships, in id order
        const principals = new Map();    // table id -> ids of the tables it points at
        const dependents = new Map();    // table id -> ids of the tables that point at it
        tables.forEach((table) => {
            touching.set(table.id, []);
            principals.set(table.id, new Set());
            dependents.set(table.id, new Set());
        });
        rels.forEach((rel) => {
            touching.get(rel.from).push(rel);
            if (rel.to === rel.from) { return; }    // a self-reference: listed once, no neighbour
            touching.get(rel.to).push(rel);
            principals.get(rel.from).add(rel.to);
            dependents.get(rel.to).add(rel.from);
        });
        const sortedIds = (ids) => (ids ? Array.from(ids).sort(ordinal) : []);

        return {
            tables,
            byId,
            rels,
            relsById,
            junctions,
            // Every relationship with this table at either end (a self-reference once).
            relsOf: (id) => (touching.get(id) || []).slice(),
            // The tables one foreign key away, never the table itself.
            neighbours: (id) => ({
                principals: sortedIds(principals.get(id)),
                dependents: sortedIds(dependents.get(id))
            }),
            // How a line is drawn: 'self' loops back to its own card, 'hub' goes into a table that
            // nearly everything points at (drawn faint), 'owner' follows the layout's parent link,
            // and 'ref' is every other foreign key.
            edgeKind: (rel) => {
                if (rel.from === rel.to) { return 'self'; }
                const to = own(byId, rel.to);
                if (to && to.hub) { return 'hub'; }
                const from = own(byId, rel.from);
                return from && from.parent === rel.to ? 'owner' : 'ref';
            }
        };
    }

    // ---- Cards: rows, heights and text sizes ---------------------------------------------------

    const isKeyColumn = (column) => !!(column.pk || column.fk || column.unique);

    // The rows a card draws at a density: 'all' is every column, 'keys' only the PK / FK / unique
    // columns, 'names' none (the header alone). Anything else counts as 'all'. Always a new array.
    function visibleRows(table, density) {
        const columns = listOf(table && table.columns);
        if (density === 'names') { return []; }
        if (density === 'keys') { return columns.filter(isKeyColumn); }
        return columns.slice();
    }

    // A card is exactly this tall: the CSS draws its outline as a box-shadow, so no border adds
    // to it.
    function cardHeight(table, density) {
        return METRICS.headH + METRICS.rowH * visibleRows(table, density).length;
    }

    // Where a line meets a card, measured down from the card's top: the middle of the column's
    // row, or the middle of the header when that row is not drawn at this density (or the column
    // is unknown), so the line still lands on the card.
    function rowAnchorY(table, columnName, density) {
        const rows = visibleRows(table, density);
        for (let i = 0; i < rows.length; i++) {
            if (rows[i].name === columnName) {
                return METRICS.headH + i * METRICS.rowH + METRICS.rowH / 2;
            }
        }
        return METRICS.headH / 2;
    }

    // The two ends of a foreign key in crow's-foot terms. Child end: zero or many rows point at
    // one parent row (zero or one when the key is unique). Parent end: exactly one when the key
    // is required, zero or one when it may be null.
    function cardinality(rel) {
        return {
            child: rel.unique ? 'zeroOrOne' : 'zeroOrMany',
            parent: rel.required ? 'one' : 'zeroOrOne',
            label: rel.unique ? '1 : 1' : 'N : 1'
        };
    }

    // Names are drawn in the monospace font, so their width can be worked out: one glyph is 0.6
    // of the font size. Compared in tenths (size * 6 against width * 10) so the numbers stay
    // whole and a name that fits exactly is not thrown out by floating-point rounding.
    const textFits = (chars, size, width) => chars * size * 6 <= width * 10;

    const HEAD_PAD = 28;         // the header's 14px padding, left + right
    const COUNT_ROOM = 58;       // the "12 cols" label and the gap in front of it
    const HEAD_SIZES = [14, 13, 12, 11, 10.5];

    // Font size for a card header. Real table names run to about 40 characters, so a long name
    // first drops the column count and then steps the font down, instead of being cut off.
    function headerFont(name) {
        const chars = textOf(name).length;
        const inner = METRICS.cardW - HEAD_PAD;
        if (textFits(chars, HEAD_SIZES[0], inner - COUNT_ROOM)) {
            return { size: HEAD_SIZES[0], showCount: true };
        }
        for (let i = 0; i < HEAD_SIZES.length; i++) {
            if (textFits(chars, HEAD_SIZES[i], inner)) {
                return { size: HEAD_SIZES[i], showCount: false };
            }
        }
        return { size: 10, showCount: false };
    }

    const isUpper = (code) => code >= 65 && code <= 90;
    const isLowerOrDigit = (code) => (code >= 97 && code <= 122) || (code >= 48 && code <= 57);

    // The places a long name may wrap: in front of a capital that follows a lower-case letter or
    // a digit ("AspNetUserRoles" -> Asp, Net, User, Roles). Joining the pieces gives the name back.
    function splitName(name) {
        const text = textOf(name);
        const parts = [];
        let start = 0;
        for (let i = 1; i < text.length; i++) {
            if (isUpper(text.charCodeAt(i)) && isLowerOrDigit(text.charCodeAt(i - 1))) {
                parts.push(text.slice(start, i));
                start = i;
            }
        }
        if (text.length > 0) { parts.push(text.slice(start)); }
        return parts;
    }

    // Does the name fit a card at this font size? It is wrapped the way a browser would wrap it
    // (as many pieces on a line as fit) inside 90% of the card's width and 86% of its height,
    // with a line height of 1.15. A piece longer than a whole line does not fit at all.
    const farLabelFits = (parts, size, height) => {
        const width = METRICS.cardW * 9 / 10;
        let lines = 0;
        let used = 0;                // characters already on the current line
        for (let i = 0; i < parts.length; i++) {
            const chars = parts[i].length;
            if (!textFits(chars, size, width)) { return false; }
            if (lines > 0 && textFits(used + chars, size, width)) {
                used += chars;
            } else {
                lines += 1;
                used = chars;
            }
        }
        // lines * 1.15 * size <= height * 0.86, in whole numbers again.
        return lines * size * 115 <= height * 86;
    };

    // Zoomed far out the rows are unreadable, so a card shows just its name. This is the cap on
    // that name's font size: the largest even size from 96 down that fits the card, and never
    // less than 8 (the stylesheet lets a name that still does not fit break anywhere).
    function farLabelSize(name, height) {
        const parts = splitName(name);
        for (let size = 96; size > 8; size -= 2) {
            if (farLabelFits(parts, size, height)) { return size; }
        }
        return 8;
    }

    // The box around some cards. `ids` picks which ones (an array or a Set); left out, it is
    // every positioned table. A table with no position, or one the index does not know, is
    // skipped. Nothing to measure gives all zeros.
    function boundsOf(positions, index, density, ids) {
        const wanted = ids === null || ids === undefined ? Object.keys(positions || {}) : ids;
        let x0 = Infinity;
        let y0 = Infinity;
        let x1 = -Infinity;
        let y1 = -Infinity;
        wanted.forEach((id) => {
            const at = own(positions, id);
            const table = own(index.byId, id);
            if (!at || !table) { return; }
            x0 = Math.min(x0, at.x);
            y0 = Math.min(y0, at.y);
            x1 = Math.max(x1, at.x + METRICS.cardW);
            y1 = Math.max(y1, at.y + cardHeight(table, density));
        });
        return x1 === -Infinity ? zeroBounds() : { x0, y0, x1, y1 };
    }

    // ---- Automatic layout ("cluster pack") -----------------------------------------------------
    // Tables are grouped by cascade delete (the server's cascade forest): a table sits to the right of
    // the table that owns it (table.parent, worked out by the server). Each group is packed into
    // a rectangle (a "block") a whole number of card columns wide, and the blocks are then packed
    // into the final picture. Every x is therefore a multiple of the column pitch.
    //
    // A block is { span (columns), h (px), key (the id of its head card), cards: [{ id, col, y }] }
    // with col and y measured from the block's top-left corner.

    const readOptions = (opts) => ({
        density: opts ? opts.density : undefined,
        hidden: toSet(opts ? opts.hidden : null)
    });

    // Card heights for one layout run, worked out once per table.
    const heightLookup = (index, density) => {
        const cache = new Map();
        return (id) => {
            if (!cache.has(id)) { cache.set(id, cardHeight(own(index.byId, id), density)); }
            return cache.get(id);
        };
    };

    // Who sits beside whom: the roots, and each visible table's children, all in id order.
    const forestOf = (index, hidden) => {
        const ids = index.tables.map(idOf).filter((id) => !hidden.has(id));
        const shown = new Set(ids);

        // A hidden (or unknown) parent is no parent at all: its children become roots.
        const linkOf = (id) => {
            const parent = own(index.byId, id).parent;
            return parent !== id && shown.has(parent) ? parent : null;
        };

        // Cycle guard. The server sends a forest, so this never fires on real data. But a loop
        // (A under B under A) would leave tables that no root leads to, which would never be
        // drawn, and would send group() round for ever. So walk up from the table, and if any
        // table comes round twice, this table is a root.
        const parentOf = (id) => {
            const parent = linkOf(id);
            const seen = new Set([id]);
            for (let up = parent; up !== null; up = linkOf(up)) {
                if (seen.has(up)) { return null; }
                seen.add(up);
            }
            return parent;
        };

        const kids = new Map(ids.map((id) => [id, []]));
        const roots = [];
        ids.forEach((id) => {
            const parent = parentOf(id);
            if (parent === null) { roots.push(id); } else { kids.get(parent).push(id); }
        });
        return { roots, kids };
    };

    // Widest first, then tallest, then by id. Big blocks are placed while there is still room
    // to choose from, and the id settles every tie so the picture is the same on every run.
    const order = (blocks) => blocks.slice().sort((a, b) =>
        b.span - a.span || b.h - a.h || ordinal(a.key, b.key));

    // The column counts worth trying: at least the widest block, at most one column for every
    // block column (capped at maxCols). The outer max() keeps the range from being empty when a
    // single block is wider than maxCols.
    const columnRange = (blocks) => {
        let widest = 0;
        let total = 0;
        blocks.forEach((block) => {
            widest = Math.max(widest, block.span);
            total += block.span;
        });
        return { min: widest, max: Math.max(widest, Math.min(METRICS.maxCols, total)) };
    };

    // Skyline packing into k columns: each block, in the order given, goes where it sits highest.
    // The test is a strict "<", so on a tie the LEFTMOST start wins. Blocks then fill left to
    // right in their sorted order (the first one always lands top-left) and nothing is left to
    // chance.
    const skyline = (blocks, k) => {
        const colH = new Array(k).fill(0);      // per column: the y where its next card starts
        const placements = blocks.map((block) => {
            let col = 0;
            let y = Infinity;
            for (let start = 0; start + block.span <= k; start++) {
                let top = 0;
                for (let c = start; c < start + block.span; c++) { top = Math.max(top, colH[c]); }
                if (top < y) {
                    col = start;
                    y = top;
                }
            }
            for (let c = col; c < col + block.span; c++) { colH[c] = y + block.h + METRICS.gapY; }
            return { block, col, y };
        });
        const h = blocks.length === 0 ? 0 : Math.max.apply(null, colH) - METRICS.gapY;
        return { placements, h };
    };

    // Positions for every visible table: { positions: { id: { x, y } }, bounds }.
    // opts = { density, hidden } where hidden lists the ids of the tables that are not drawn.
    function autoLayout(index, opts) {
        const options = readOptions(opts);
        const forest = forestOf(index, options.hidden);
        const heightOf = heightLookup(index, options.density);
        const pitch = METRICS.cardW + METRICS.gapX;

        const leaf = (id) => ({ span: 1, h: heightOf(id), key: id, cards: [{ id, col: 0, y: 0 }] });

        // A table and everything under it: its own card in the first column, and its children's
        // groups packed to the right in as few columns as keep the block under groupMaxH (or
        // under the card's own height, when the card is taller than that).
        const built = new Map();        // a group is worked out once per layout
        const group = (id) => {
            if (built.has(id)) { return built.get(id); }
            const children = forest.kids.get(id);
            let block = leaf(id);
            if (children.length > 0) {
                const blocks = order(children.map(group));
                const range = columnRange(blocks);
                const limit = Math.max(heightOf(id), METRICS.groupMaxH);
                let k = range.min;
                let packed = skyline(blocks, k);
                while (packed.h > limit && k < range.max) {
                    k += 1;
                    packed = skyline(blocks, k);
                }
                const cards = block.cards;
                packed.placements.forEach((p) => {
                    p.block.cards.forEach((card) => {
                        cards.push({ id: card.id, col: p.col + 1 + card.col, y: p.y + card.y });
                    });
                });
                block = { span: 1 + k, h: Math.max(heightOf(id), packed.h), key: id, cards };
            }
            built.set(id, block);
            return block;
        };

        // Spine rule. A root that owns many tables (Linkshell owns about forty) is NOT packed as
        // one group: that group would be a single slab most of the diagram wide, which the final
        // pass could only place whole. Instead the root is a lone card, placed first so that it
        // lands top-left, and each child's group goes into the final pack as a block of its own.
        const spines = [];
        const rest = [];
        forest.roots.forEach((root) => {
            const children = forest.kids.get(root);
            if (children.length >= METRICS.spineMinChildren) {
                spines.push(leaf(root));
                children.forEach((child) => { rest.push(group(child)); });
            } else {
                rest.push(group(root));
            }
        });
        const blocks = spines.concat(order(rest));
        if (blocks.length === 0) { return { positions: {}, bounds: zeroBounds() }; }

        // The final pack: try every column count and keep the one whose shape is closest to
        // METRICS.aspect (width / height), the smallest count on a tie. "Closest" is by ratio,
        // so twice too wide is as bad as twice too tall. max(r, 1 / r) puts the candidates in
        // the same order as |ln r| and needs only division, which every engine rounds the same
        // way; Math.log may differ in its last digit, and a layout must not depend on the browser.
        const range = columnRange(blocks);
        let best = null;
        for (let k = range.min; k <= range.max; k++) {
            const packed = skyline(blocks, k);
            const r = (k * pitch - METRICS.gapX) / packed.h / METRICS.aspect;
            const miss = Math.max(r, 1 / r);
            if (best === null || miss < best.miss) { best = { miss, packed }; }
        }

        const positions = {};
        best.packed.placements.forEach((p) => {
            p.block.cards.forEach((card) => {
                positions[card.id] = { x: (p.col + card.col) * pitch, y: p.y + card.y };
            });
        });
        return { positions, bounds: boundsOf(positions, index, options.density) };
    }

    // ---- Focus layout --------------------------------------------------------------------------
    // One table and its direct neighbours only: the tables it points at in columns on its left,
    // the tables that point at it on its right. A table that is both (A points at B and B points
    // at A) is drawn once, on the left. `ids` lists what is drawn, the focused table first.
    function focusLayout(index, id, opts) {
        const options = readOptions(opts);
        if (own(index.byId, id) === undefined || options.hidden.has(id)) {
            return { positions: {}, bounds: zeroBounds(), ids: [] };
        }
        const heightOf = heightLookup(index, options.density);
        const pitch = METRICS.cardW + METRICS.gapX;

        const near = index.neighbours(id);
        const principals = near.principals.filter((other) => !options.hidden.has(other));
        const onLeft = new Set(principals);
        const dependents = near.dependents.filter((other) =>
            !options.hidden.has(other) && !onLeft.has(other));

        const positions = {};
        positions[id] = { x: 0, y: 0 };

        // Fill a column from the top and start the next one, further out, when one more card
        // would pass the limit. A column always takes at least one card, however tall it is.
        const limit = Math.max(heightOf(id), METRICS.focusColMaxH);
        const wrap = (list, side) => {
            let column = 1;
            let y = 0;
            list.forEach((other) => {
                if (y > 0 && y + heightOf(other) > limit) {
                    column += 1;
                    y = 0;
                }
                positions[other] = { x: side * column * pitch, y };
                y += heightOf(other) + METRICS.gapY;
            });
        };
        wrap(principals, -1);
        wrap(dependents, 1);

        return {
            positions,
            bounds: boundsOf(positions, index, options.density),
            ids: [id].concat(principals, dependents)
        };
    }

    // ---- Relationship lines --------------------------------------------------------------------

    // Numbers in a path: 2 decimals at most and no trailing zeros ("12", "12.5", "-0.25").
    const num = (value) => String(Math.round(value * 100) / 100);
    const xy = (x, y) => num(x) + ' ' + num(y);

    // The line for one foreign key. a is the child's anchor and b the parent's, each
    // { x: the card's LEFT edge, y: the centre of the row }. The line leaves each card along a
    // short straight stub (the crow's foot is drawn on it) and then curves or steps across.
    // Returns the path, its middle (where the "N : 1" pill goes) and, for each end, where it
    // meets the card and which way it leaves.
    function edgeGeometry(a, b, style) {
        const width = METRICS.cardW;

        // Which sides to use (the mock's rule): the facing sides when the cards are clearly
        // apart, otherwise both ends leave on the right and the line loops round.
        let da;
        let db;
        let ax;
        let bx;
        if (b.x > a.x + width + METRICS.sideGap) {
            da = 1; db = -1; ax = a.x + width; bx = b.x;
        } else if (b.x + width + METRICS.sideGap < a.x) {
            da = -1; db = 1; ax = a.x; bx = b.x + width;
        } else {
            da = 1; db = 1; ax = a.x + width; bx = b.x + width;
        }

        // Both ends on the same side at almost the same height: a self-reference whose two rows
        // are one row (or one header). Pull the ends apart so that there is a loop to see.
        let ay = a.y;
        let by = b.y;
        if (da === db && Math.abs(ay - by) < 16) {
            ay -= 8;
            by += 8;
        }

        const sx = ax + da * METRICS.stub;
        const ex = bx + db * METRICS.stub;
        let d;
        let midX;
        if (style === 'orthogonal') {
            midX = da === db ? Math.max(sx, ex) + 20 : (sx + ex) / 2;
            d = 'M' + xy(ax, ay) + ' H' + num(midX) + ' V' + num(by) + ' H' + num(bx);
        } else {
            const c = Math.max(60, Math.abs(ex - sx) / 2);
            midX = (sx + ex) / 2 + 0.375 * c * (da + db);       // the curve at t = 0.5
            d = 'M' + xy(ax, ay) + ' H' + num(sx) +
                ' C' + xy(sx + da * c, ay) + ' ' + xy(ex + db * c, by) + ' ' + xy(ex, by) +
                ' H' + num(bx);
        }

        return {
            d,
            mid: { x: midX, y: (ay + by) / 2 },
            from: { x: ax, y: ay, dir: da },
            to: { x: bx, y: by, dir: db }
        };
    }

    // A full circle as two half-circle arcs, starting from its left-most point.
    const circle = (cx, cy, r) =>
        'M' + xy(cx - r, cy) +
        ' a' + xy(r, r) + ' 0 1 0 ' + xy(2 * r, 0) +
        ' a' + xy(r, r) + ' 0 1 0 ' + xy(-2 * r, 0);

    // The crow's-foot end drawn where a line meets a card edge at (x, y); dir is +1 when the line
    // leaves to the right and -1 to the left. Everything stays on the stub, within METRICS.stub px
    // of the card. One path is enough: the circle is the only shape with an inside (the CSS fills
    // it with the canvas colour so the line does not show through) and bare strokes have no area.
    function markerPath(kind, x, y, dir) {
        const bar = (offset) => 'M' + xy(x + dir * offset, y - 7) + ' V' + num(y + 7);
        const toe = (dy) => 'M' + xy(x + dir * 12, y) + ' L' + xy(x, y + dy);
        const ring = circle(x + dir * 16, y, 3.5);
        if (kind === 'one') { return bar(8) + ' ' + bar(13); }
        if (kind === 'zeroOrOne') { return bar(8) + ' ' + ring; }
        if (kind === 'zeroOrMany') { return toe(-7) + ' ' + toe(0) + ' ' + toe(7) + ' ' + ring; }
        return '';
    }

    // ---- Pan and zoom --------------------------------------------------------------------------
    // A view is { x, y, zoom }: the canvas point p is drawn on screen at p * zoom + (x, y).

    // The view that shows all of `bounds` in the middle of the viewport, fitPad px clear of its
    // edges. With nothing to fit, or nowhere to fit it (the canvas has no size until the page has
    // been laid out), it is the plain 1:1 view.
    function fitView(bounds, viewport) {
        const w = bounds.x1 - bounds.x0;
        const h = bounds.y1 - bounds.y0;
        const vw = viewport.width;
        const vh = viewport.height;
        if (!(w > 0 && h > 0 && vw > 0 && vh > 0)) { return { x: 0, y: 0, zoom: 1 }; }
        const pad = 2 * METRICS.fitPad;
        const zoom = clamp(
            Math.min((vw - pad) / w, (vh - pad) / h), METRICS.minZoom, METRICS.fitMaxZoom);
        return {
            x: (vw - w * zoom) / 2 - bounds.x0 * zoom,
            y: (vh - h * zoom) / 2 - bounds.y0 * zoom,
            zoom
        };
    }

    // Change the zoom while the canvas point under `point` (a screen position) stays exactly
    // where it is. That is what makes the wheel zoom toward the cursor.
    function zoomAt(view, point, nextZoom) {
        const zoom = clamp(nextZoom, METRICS.minZoom, METRICS.maxZoom);
        return {
            x: point.x - (point.x - view.x) * zoom / view.zoom,
            y: point.y - (point.y - view.y) * zoom / view.zoom,
            zoom
        };
    }

    const api = {
        METRICS,
        buildIndex,
        visibleRows,
        cardHeight,
        rowAnchorY,
        cardinality,
        headerFont,
        splitName,
        farLabelSize,
        autoLayout,
        focusLayout,
        edgeGeometry,
        markerPath,
        fitView,
        zoomAt,
        boundsOf
    };

    // Node (the tests) takes module.exports; in a browser this is a plain script and there is no
    // module, so it becomes one global that data-admin-schema.js reads.
    if (typeof module !== 'undefined' && module.exports) {
        module.exports = api;
    } else {
        window.LsmSchemaLayout = api;
    }
})();
