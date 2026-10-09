// The Data Admin schema diagram (/data-admin): draws every table as a card and every foreign
// key as a line, lets the viewer pan, zoom and move the cards about, lights up what the pointer is
// over, keeps the side panel (search, selection, lists, legend) in step with the picture, cuts the
// picture down to one table and its neighbours (focus mode), answers the keyboard, and opens the
// selected table's records: the View records link, the v key, and a #table=<slug> deep link that
// selects a table on arrival (the records pages link back with it).
//
// Why two files: every number on this page (where a card goes, how tall it is, the path of a line,
// the pan and zoom maths) comes from data-admin-schema-layout.js (window.LsmSchemaLayout), which has
// no DOM in it and is unit tested under Node. This file is the other half: it turns those numbers
// into elements and turns pointer and wheel events back into numbers. It never measures a card or a
// line. The only things it reads from the page are the canvas rectangle and where the pointer is.
// The stylesheet (lsm-theme.css, "Data Admin: schema diagram") makes the numbers true.
//
// External file because the CSP is nonce-only (no inline scripts or handlers). The model arrives as
// a JSON block (#erd-data) written by DataAdminSchemaGraph: structure only, never rows.
(function () {
    'use strict';

    const root = document.getElementById('erd');
    const dataEl = document.getElementById('erd-data');
    const L = window.LsmSchemaLayout;
    if (!root || !dataEl || !L) { return; }

    const canvas = root.querySelector('[data-erd-canvas]');
    const layer = root.querySelector('[data-erd-layer]');
    const edgesSvg = root.querySelector('[data-erd-edges]');
    const topSvg = root.querySelector('[data-erd-edges-top]');
    const cardsEl = root.querySelector('[data-erd-cards]');
    const zoomLabel = root.querySelector('[data-erd-zoom-label]');
    const panel = root.querySelector('[data-erd-panel]');
    if (!canvas || !layer || !edgesSvg || !topSvg || !cardsEl || !panel) { return; }

    // The panel's containers. The script fills all of them, so a missing one is a broken page, not
    // a page without that section.
    const side = {
        search: panel.querySelector('[data-erd-search]'),
        selected: panel.querySelector('[data-erd-selected]'),
        selectedBody: panel.querySelector('[data-erd-selected-body]'),
        tables: panel.querySelector('[data-erd-tables]'),
        rels: panel.querySelector('[data-erd-rels]'),
        relsTitle: panel.querySelector('[data-erd-rels-title]'),
        junctions: panel.querySelector('[data-erd-junctions]'),
        legend: panel.querySelector('[data-erd-legend]'),
        tableCount: panel.querySelector('[data-erd-count="tables"]'),
        relCount: panel.querySelector('[data-erd-count="relationships"]'),
        junctionCount: panel.querySelector('[data-erd-count="junctions"]')
    };
    if (Object.keys(side).some((key) => !side[key])) { return; }

    // The View section's controls (segmented buttons for density and line style, switches for
    // the rest) and the bar's Panel button. Static markup: the script only reads and marks them.
    const viewSection = panel.querySelector('[data-erd-view]');
    const panelToggle = root.querySelector('[data-erd-panel-toggle]');
    if (!viewSection || !panelToggle) { return; }
    const controls = {
        density: viewSection.querySelector('[data-erd-opt="density"]'),
        edges: viewSection.querySelector('[data-erd-opt="edges"]'),
        types: viewSection.querySelector('[data-erd-opt="types"]'),
        hubs: viewSection.querySelector('[data-erd-opt="hubs"]'),
        framework: viewSection.querySelector('[data-erd-opt="framework"]')
    };
    if (Object.keys(controls).some((key) => !controls[key])) { return; }

    // Focus mode's places in the bar: the button, and the chip that names the table and leaves.
    const focusButton = root.querySelector('[data-erd-focus]');
    const focusChip = root.querySelector('[data-erd-focus-chip]');
    const focusName = root.querySelector('[data-erd-focus-name]');
    const focusExit = root.querySelector('[data-erd-focus-exit]');
    if (!focusButton || !focusChip || !focusName || !focusExit) { return; }

    // The bar's View records link: the script writes its href (the pinned table's records page)
    // and its disabled state.
    const recordsLink = root.querySelector('[data-erd-records]');
    if (!recordsLink) { return; }

    const M = L.METRICS;
    const SVG_NS = 'http://www.w3.org/2000/svg';
    const STORAGE_KEY = 'lsm.dataAdmin.schema.v1';
    const DENSITIES = ['all', 'keys', 'names'];
    const OPTION_DEFAULTS = {
        density: 'all', types: true, edges: 'curved', hubs: true, framework: true, panel: true
    };
    // Options that are a choice from a list; every other option is a plain true / false.
    const OPTION_CHOICES = { density: DENSITIES, edges: ['curved', 'orthogonal'] };
    const CLICK_SLOP = 4;           // px a press may move and still be a click, not a drag
    const DOUBLE_CLICK_MS = 500;    // two clicks on one card this close together in time...
    const DOUBLE_CLICK_PX = 5;      // ...and in place are one double-click
    const BUTTON_ZOOM = 1.2;
    const ARROW_PAN = 80;           // screen px one arrow key moves the view
    const GRID_STEP = 20;           // dot grid spacing at 100%
    const GRID_MIN_PX = 14;         // the dots never get closer together than this on screen
    const CENTRE_MARGIN = 40;       // a card this close to the canvas edge is not "in view" yet
    const KEY_SLOT_WIDE = '46px';   // room for two badges (the stylesheet's default holds one)
    const PILL_W = 52;              // the "N : 1" pill on a highlighted line, in screen px
    const PILL_H = 22;
    const LEGEND_W = 64;            // one legend sample: a short line ending at a card edge
    const LEGEND_H = 22;
    const ZERO_WIDTH_SPACE = String.fromCharCode(0x200b);   // an invisible place where a name may wrap
    const MIDDLE_DOT = ' ' + String.fromCharCode(0xb7) + ' ';
    const ARROW = ' ' + String.fromCharCode(0x2192) + ' ';
    // A stored position beyond this (layer px) is treated as damage: the automatic layout is about
    // 5000px across, and a value such as 1e300 would blank the canvas and break every SVG path.
    const POSITION_LIMIT = 1000000;
    const NOTHING = { table: null, rels: null };   // a hover or a pin with nothing in it

    // Own properties only, so a table called "constructor" can never find Object.prototype's.
    const own = (map, key) =>
        (map && Object.prototype.hasOwnProperty.call(map, key) ? map[key] : undefined);
    const isObject = (value) => !!value && typeof value === 'object' && !Array.isArray(value);
    const clamp = (value, low, high) => Math.min(Math.max(value, low), high);
    const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

    // ---- The model -----------------------------------------------------------------------------

    function showEmpty() {
        const empty = root.querySelector('[data-erd-empty]');
        if (empty) { empty.hidden = false; }
    }

    let graph = null;
    try { graph = JSON.parse(dataEl.textContent || ''); } catch (e) { graph = null; }
    const index = L.buildIndex(graph);
    if (index.tables.length === 0) {
        showEmpty();
        return;
    }

    // Which relationship a foreign-key column belongs to: table id -> column name -> relationship.
    const fkOf = new Map();
    index.rels.forEach((rel) => {
        if (!fkOf.has(rel.from)) { fkOf.set(rel.from, new Map()); }
        const columns = fkOf.get(rel.from);
        (rel.fromColumns || []).forEach((name) => {
            if (!columns.has(name)) { columns.set(name, rel); }
        });
    });

    const nameOf = (id) => index.byId[id].name || id;
    // Lists in the panel go by the name people see (AspNetUsers), not the CLR id (AppUser).
    const byName = (a, b) =>
        ordinal(nameOf(a).toLowerCase(), nameOf(b).toLowerCase()) || ordinal(a, b);
    const tableIdsByName = index.tables.map((table) => table.id).sort(byName);

    // Where View records goes. Every table with a slug has a records page (the policy decides
    // which tables get one); everything else is structure only. The slug is also the deep link
    // (#table=<slug>), looked up lower-cased so a hand-typed one still finds its table.
    const recordsUrl = root.getAttribute('data-browse-url') || '';
    const idBySlug = new Map();
    index.tables.forEach((table) => {
        if (table.slug) { idBySlug.set(String(table.slug).toLowerCase(), table.id); }
    });
    const slugOf = (id) => index.byId[id].slug || null;
    const recordsUrlFor = (id) => recordsUrl.replace('__slug__', encodeURIComponent(slugOf(id)));

    // ---- What the browser remembers ------------------------------------------------------------
    // One localStorage entry: { v: 1, options, positions: { all: { id: [x, y] }, keys, names } }.
    // Moved cards are remembered per density because a card's height, and so the whole layout,
    // differs between them. Pan and zoom are never stored: a fresh visit starts with the overview.

    // A known option holding one of its listed choices, or a plain true / false: the same rule
    // for a stored value and for a value read off a control. The key is checked first, so a
    // stray data-erd-opt in the markup can never be written to storage.
    const validOption = (key, value) => {
        if (own(OPTION_DEFAULTS, key) === undefined) { return false; }
        const choices = own(OPTION_CHOICES, key);
        return choices ? choices.indexOf(value) >= 0 : typeof value === 'boolean';
    };

    function cleanOptions(raw) {
        const options = {};
        Object.keys(OPTION_DEFAULTS).forEach((key) => {
            const value = isObject(raw) ? own(raw, key) : undefined;
            options[key] = validOption(key, value) ? value : OPTION_DEFAULTS[key];
        });
        return options;
    }

    // Keeps only [x, y] pairs of whole numbers within POSITION_LIMIT for tables that still exist:
    // the model changes between releases, and anything may have been typed into the stored text.
    const sanePosition = (n) => Number.isFinite(n) && Math.abs(n) <= POSITION_LIMIT;
    function cleanPositions(raw) {
        const positions = {};
        DENSITIES.forEach((density) => {
            const source = isObject(raw) ? own(raw, density) : undefined;
            const kept = {};
            if (isObject(source)) {
                Object.keys(source).forEach((id) => {
                    const at = source[id];
                    if (own(index.byId, id) !== undefined && Array.isArray(at) && at.length === 2 &&
                        sanePosition(at[0]) && sanePosition(at[1])) {
                        kept[id] = [Math.round(at[0]), Math.round(at[1])];
                    }
                });
            }
            positions[density] = kept;
        });
        return positions;
    }

    // Never throws: storage can be blocked (private mode), full, or hold text we did not write.
    function loadStored() {
        let raw = null;
        try { raw = JSON.parse(window.localStorage.getItem(STORAGE_KEY)); } catch (e) { raw = null; }
        const usable = isObject(raw) && raw.v === 1;
        return {
            options: cleanOptions(usable ? raw.options : null),
            saved: cleanPositions(usable ? raw.positions : null)
        };
    }

    function saveStored() {
        try {
            window.localStorage.setItem(
                STORAGE_KEY, JSON.stringify({ v: 1, options: options, positions: saved }));
        } catch (e) {
            // Not being able to remember a layout is no reason to bother the viewer.
        }
    }

    // ---- State ---------------------------------------------------------------------------------

    const stored = loadStored();
    const options = stored.options;
    const saved = stored.saved;         // density -> { id: [x, y] }: only the cards the viewer moved
    let positions = {};                 // id -> { x, y }: where every drawn card is right now
    let view = { x: 0, y: 0, zoom: 1 }; // layer point p is on screen at p * zoom + (x, y)
    let touched = false;                // has the viewer panned, zoomed or dragged yet?
    let gesture = null;                 // the pan or card drag in progress
    let resizeWatch = null;
    // What is lit up: the pointer's choice (hover) wins over the click's (pinned) while it lasts.
    // Each is { table: id | null, rels: [relationship id] | null }.
    let hover = NOTHING;
    let pinned = NOTHING;
    let search = '';                    // the search box, trimmed and lower-cased
    let listedTableIds = [];            // the Tables list as shown (search applied), in list order
    // Focus mode: the table drawn alone with its neighbours, or null; and the overview it
    // replaced, kept so that leaving puts everything back exactly (never persisted).
    let focusTable = null;
    let beforeFocus = null;             // { positions, view, density, framework }

    const cardEls = new Map();          // table id -> its card element
    const rowEls = new Map();           // table id -> Map(column name -> its row element)
    const edgeEls = new Map();          // relationship id -> { hit, line, childMark, parentMark }
    const listEls = new Map();          // table id -> its Tables list item
    const relEls = new Map();           // relationship id -> its card in the Relationships list
    const junctionEls = new Map();      // via table id -> its card in the Many-to-many list

    // ---- Layout --------------------------------------------------------------------------------

    // The tables that are not drawn at all.
    function hiddenIds() {
        const hidden = new Set();
        if (!options.framework) {
            index.tables.forEach((table) => { if (table.framework) { hidden.add(table.id); } });
        }
        return hidden;
    }

    // The automatic layout, with the cards the viewer has moved (at this density) put back
    // where they were left. In focus mode: the focused table and its drawn neighbours, laid out
    // around it, and nothing else (what is not positioned is not drawn).
    function computePositions() {
        const hidden = hiddenIds();
        if (focusTable !== null) {
            positions = L.focusLayout(
                index, focusTable, { density: options.density, hidden: hidden }).positions;
            return;
        }
        const auto = L.autoLayout(index, { density: options.density, hidden: hidden }).positions;
        const moved = saved[options.density];
        const next = {};
        Object.keys(auto).forEach((id) => {
            const at = own(moved, id);
            next[id] = at ? { x: at[0], y: at[1] } : auto[id];
        });
        positions = next;
    }

    const isDrawn = (id) => own(positions, id) !== undefined;
    // In focus mode only the lines that touch the focused table are drawn: a line between two
    // of its neighbours says nothing about it.
    const isDrawnRel = (rel) => isDrawn(rel.from) && isDrawn(rel.to) &&
        (focusTable === null || rel.from === focusTable || rel.to === focusTable);

    // ---- Cards ---------------------------------------------------------------------------------

    function el(tag, className, text) {
        const node = document.createElement(tag);
        node.className = className;
        if (text !== undefined) { node.textContent = text; }
        return node;
    }

    const badgesOf = (column) => {
        const badges = [];
        if (column.pk) { badges.push('pk'); }
        if (column.fk) { badges.push('fk'); }
        if (column.unique) { badges.push('uq'); }
        return badges;
    };

    const DELETE_TEXT = { cascade: 'cascade', setNull: 'set null' };
    const deleteText = (rel) => own(DELETE_TEXT, rel.onDelete) || 'blocked';

    // The tooltip tells the whole story of a column: the row itself only has room for the short
    // type, and a long name is cut with an ellipsis.
    function rowTitle(column, rel) {
        const lines = [column.name + ' : ' + (column.storeType || column.type || '') +
            (column.nullable ? ', null' : ', not null')];
        if (rel) {
            lines.push('FK to ' + index.byId[rel.to].name + '.' + (rel.toColumns || [])[0] +
                ' (on delete: ' + deleteText(rel) + ')');
        }
        if (column.protected) { lines.push('value never shown in Data Admin'); }
        return lines.join('\n');
    }

    function buildRow(table, column) {
        const row = el('div', 'erd-row');
        row.setAttribute('data-col', column.name);
        const columns = fkOf.get(table.id);
        const rel = columns ? columns.get(column.name) : undefined;
        if (rel) { row.setAttribute('data-rel', rel.id); }
        if (column.pk) { row.classList.add('is-pk'); }
        if (column.protected) { row.classList.add('is-protected'); }
        row.title = rowTitle(column, rel);

        const keys = el('span', 'erd-row__keys');
        badgesOf(column).forEach((kind) => {
            keys.appendChild(el('span', 'erd-key erd-key--' + kind, kind.toUpperCase()));
        });
        row.appendChild(keys);
        row.appendChild(el('span', 'erd-row__name', column.name));
        row.appendChild(el('span', 'erd-row__type', (column.type || '') + (column.nullable ? '?' : '')));
        return row;
    }

    function buildCard(table, rows) {
        const columns = L.visibleRows(table, options.density);
        const total = (table.columns || []).length;
        const name = table.name || table.id;

        const card = el('div', 'erd-card');
        card.setAttribute('data-table', table.id);
        if (table.framework) { card.classList.add('is-framework'); }
        if (table.hub) { card.classList.add('is-hub'); }
        if (columns.some((column) => badgesOf(column).length > 1)) {
            card.style.setProperty('--erd-key-w', KEY_SLOT_WIDE);
        }
        // The far-zoom name may grow until it fills the card, and no further.
        card.style.setProperty(
            '--erd-far-max', L.farLabelSize(name, L.cardHeight(table, options.density)) + 'px');

        // tabindex: the header is the card's handle for the mouse now and for the keyboard later.
        const head = el('div', 'erd-card__head');
        head.tabIndex = 0;
        head.title = name + '\n' + (table.label || table.id) + '\n' +
            total + (total === 1 ? ' column' : ' columns');
        const font = L.headerFont(name);
        const nameEl = el('span', 'erd-card__name', name);
        nameEl.style.fontSize = font.size + 'px';
        head.appendChild(nameEl);
        if (font.showCount) {
            head.appendChild(el('span', 'erd-card__count', total + (total === 1 ? ' col' : ' cols')));
        }
        card.appendChild(head);

        columns.forEach((column) => {
            const row = buildRow(table, column);
            rows.set(column.name, row);
            card.appendChild(row);
        });

        // Zero-width spaces let the browser wrap the name exactly where farLabelSize assumed.
        card.appendChild(el('div', 'erd-card__far', L.splitName(name).join(ZERO_WIDTH_SPACE)));
        return card;
    }

    function placeCard(id) {
        const card = cardEls.get(id);
        const at = positions[id];
        card.style.left = at.x + 'px';
        card.style.top = at.y + 'px';
    }

    // Rebuilds every card for the current density and set of visible tables. Whatever was lit is
    // forgotten with the old elements; the caller lights the new ones (applyHighlight).
    function renderCards() {
        const fragment = document.createDocumentFragment();
        cardEls.clear();
        rowEls.clear();
        resetLit();
        index.tables.forEach((table) => {
            if (!isDrawn(table.id)) { return; }
            const rows = new Map();
            const card = buildCard(table, rows);
            cardEls.set(table.id, card);
            rowEls.set(table.id, rows);
            placeCard(table.id);
            fragment.appendChild(card);
        });
        cardsEl.textContent = '';
        cardsEl.appendChild(fragment);
    }

    // ---- Relationship lines --------------------------------------------------------------------

    function svgEl(tag, className) {
        const node = document.createElementNS(SVG_NS, tag);
        node.setAttribute('class', className);
        return node;
    }

    const DELETE_CLASS = { cascade: 'is-cascade', setNull: 'is-set-null' };
    const deleteClass = (rel) => own(DELETE_CLASS, rel.onDelete) || 'is-restrict';

    // Where a line meets a card: the card's left edge (the layout module picks the side) at the
    // middle of the column's row.
    const anchorOf = (tableId, columns) => ({
        x: positions[tableId].x,
        y: positions[tableId].y +
            L.rowAnchorY(index.byId[tableId], (columns || [])[0], options.density)
    });

    // The whole drawing of one line: its path, and the crow's-foot ends at each card.
    function lineShape(rel) {
        const geometry = L.edgeGeometry(
            anchorOf(rel.from, rel.fromColumns), anchorOf(rel.to, rel.toColumns), options.edges);
        const ends = L.cardinality(rel);
        return {
            d: geometry.d,
            mid: geometry.mid,
            childMark: L.markerPath(ends.child, geometry.from.x, geometry.from.y, geometry.from.dir),
            parentMark: L.markerPath(ends.parent, geometry.to.x, geometry.to.y, geometry.to.dir)
        };
    }

    function setEdgeGeometry(rel, parts) {
        const shape = lineShape(rel);
        parts.hit.setAttribute('d', shape.d);
        parts.line.setAttribute('d', shape.d);
        parts.childMark.setAttribute('d', shape.childMark);
        parts.parentMark.setAttribute('d', shape.parentMark);
    }

    // Rebuilds every line between two drawn cards. Paint order inside a group: the wide
    // invisible hit path, the line, then the two crow's feet on top of the line's ends.
    function renderEdges() {
        const fragment = document.createDocumentFragment();
        edgeEls.clear();
        index.rels.forEach((rel) => {
            if (!isDrawnRel(rel)) { return; }
            const group = svgEl('g', 'erd-edge is-' + index.edgeKind(rel) + ' ' + deleteClass(rel));
            group.setAttribute('data-rel', rel.id);
            const parts = {
                hit: svgEl('path', 'erd-edge__hit'),
                line: svgEl('path', 'erd-edge__line'),
                childMark: svgEl('path', 'erd-edge__mark'),
                parentMark: svgEl('path', 'erd-edge__mark')
            };
            setEdgeGeometry(rel, parts);
            group.appendChild(parts.hit);
            group.appendChild(parts.line);
            group.appendChild(parts.childMark);
            group.appendChild(parts.parentMark);
            edgeEls.set(rel.id, parts);
            fragment.appendChild(group);
        });
        edgesSvg.textContent = '';
        edgesSvg.appendChild(fragment);
    }

    // Recomputes only the lines that touch these tables (a dragged card moves a handful of
    // lines, not all of them).
    function updateEdges(tableIds) {
        const done = new Set();
        tableIds.forEach((id) => {
            index.relsOf(id).forEach((rel) => {
                const parts = edgeEls.get(rel.id);
                if (!parts || done.has(rel.id)) { return; }
                done.add(rel.id);
                setEdgeGeometry(rel, parts);
            });
        });
    }

    // ---- Highlighting --------------------------------------------------------------------------
    // The lit relationships are drawn AGAIN in the top SVG, in the accent colour, over the cards.
    // The originals stay where they are, so the line under the pointer never moves away from it.
    // With one relationship lit, a pill names its cardinality at the middle of the line.

    // What is lit right now, so that a change toggles only the elements that differ.
    const lit = {
        rels: [],                       // the active relationships (objects), in id order
        relIds: new Set(),
        related: new Set(),             // the tables at their ends, plus the table itself
        rows: new Set(),                // the row elements at both ends of every active line
        active: null,                   // the hovered or pinned table
        selected: null,                 // the pinned table
        pinnedRels: new Set(),
        dim: new Set()                  // tables greyed out by the search
    };

    // Forgets what is lit. The elements it named have just been thrown away with the cards, so
    // the next applyHighlight starts from nothing and marks the new elements; the copies in the
    // top SVG go the same way and are drawn again for whatever is still lit.
    function resetLit() {
        lit.rels = [];
        lit.relIds.clear();
        lit.related.clear();
        lit.rows.clear();
        lit.active = null;
        lit.selected = null;
        lit.pinnedRels.clear();
        lit.dim.clear();
        topSvg.textContent = '';
    }

    // Calls change(id, true) for what joins the set and change(id, false) for what leaves it,
    // then makes `current` the new set. Returns whether anything changed.
    function diffSet(current, next, change) {
        let changed = false;
        current.forEach((id) => { if (!next.has(id)) { change(id, false); changed = true; } });
        next.forEach((id) => { if (!current.has(id)) { change(id, true); changed = true; } });
        current.clear();
        next.forEach((id) => { current.add(id); });
        return changed;
    }

    const toggleIn = (map, id, className, on) => {
        const node = map.get(id);
        if (node) { node.classList.toggle(className, on); }
    };

    // The relationships a hover or a pin lights up: the ones it names, or every drawn one that
    // touches its table.
    function activeRelsOf(focus) {
        if (focus.rels) {
            return focus.rels
                .map((id) => own(index.relsById, id))
                .filter((rel) => rel && isDrawnRel(rel));
        }
        if (focus.table !== null) {
            return index.relsOf(focus.table).filter(isDrawnRel);
        }
        return [];
    }

    function renderTopEdges() {
        topSvg.textContent = '';
        lit.rels.forEach((rel) => {
            const shape = lineShape(rel);
            const group = svgEl('g', 'erd-edge is-active ' + deleteClass(rel));
            group.setAttribute('data-rel', rel.id);
            const line = svgEl('path', 'erd-edge__line');
            line.setAttribute('d', shape.d);
            const childMark = svgEl('path', 'erd-edge__mark');
            childMark.setAttribute('d', shape.childMark);
            const parentMark = svgEl('path', 'erd-edge__mark');
            parentMark.setAttribute('d', shape.parentMark);
            group.appendChild(line);
            group.appendChild(childMark);
            group.appendChild(parentMark);
            topSvg.appendChild(group);
        });
        if (lit.rels.length !== 1) { return; }

        // The pill is drawn around (0, 0) and moved to the line's middle by the stylesheet, which
        // also undoes the zoom so that it stays PILL_W x PILL_H on screen.
        const shape = lineShape(lit.rels[0]);
        const pill = svgEl('g', 'erd-edge-pill');
        pill.style.setProperty('--erd-pill-x', shape.mid.x + 'px');
        pill.style.setProperty('--erd-pill-y', shape.mid.y + 'px');
        const rect = svgEl('rect', 'erd-edge-pill__box');
        rect.setAttribute('x', String(-PILL_W / 2));
        rect.setAttribute('y', String(-PILL_H / 2));
        rect.setAttribute('width', String(PILL_W));
        rect.setAttribute('height', String(PILL_H));
        rect.setAttribute('rx', '4');
        const text = svgEl('text', 'erd-edge-pill__text');
        text.setAttribute('x', '0');
        text.setAttribute('y', '4');
        text.textContent = L.cardinality(lit.rels[0]).label;
        pill.appendChild(rect);
        pill.appendChild(text);
        topSvg.appendChild(pill);
    }

    // Brings the page in line with `hover` and `pinned`: card, row and panel classes, the layer's
    // has-focus, and the copies in the top SVG.
    function applyHighlight() {
        const focus = hover.table !== null || hover.rels ? hover : pinned;
        const rels = activeRelsOf(focus);
        const relIds = new Set(rels.map((rel) => rel.id));
        const related = new Set();
        rels.forEach((rel) => { related.add(rel.from); related.add(rel.to); });
        if (focus.table !== null && isDrawn(focus.table)) { related.add(focus.table); }
        const rows = new Set();
        rels.forEach((rel) => {
            const child = rowEls.get(rel.from).get((rel.fromColumns || [])[0]);
            const parent = rowEls.get(rel.to).get((rel.toColumns || [])[0]);
            if (child) { rows.add(child); }
            if (parent) { rows.add(parent); }
        });
        const active = focus.table !== null && isDrawn(focus.table) ? focus.table : null;
        const selected = pinned.table !== null && isDrawn(pinned.table) ? pinned.table : null;

        layer.classList.toggle('has-focus', related.size > 0);
        diffSet(lit.related, related, (id, on) => { toggleIn(cardEls, id, 'is-rel', on); });
        diffSet(lit.rows, rows, (row, on) => { row.classList.toggle('is-active', on); });
        if (lit.active !== active) {
            toggleIn(cardEls, lit.active, 'is-active', false);
            toggleIn(listEls, lit.active, 'is-active', false);
            toggleIn(cardEls, active, 'is-active', true);
            toggleIn(listEls, active, 'is-active', true);
            lit.active = active;
        }
        if (lit.selected !== selected) {
            toggleIn(cardEls, lit.selected, 'is-selected', false);
            toggleIn(listEls, lit.selected, 'is-selected', false);
            toggleIn(cardEls, selected, 'is-selected', true);
            toggleIn(listEls, selected, 'is-selected', true);
            lit.selected = selected;
        }
        diffSet(lit.pinnedRels, new Set(pinned.rels || []),
            (id, on) => { toggleIn(relEls, id, 'is-selected', on); });
        if (diffSet(lit.relIds, relIds, (id, on) => { toggleIn(relEls, id, 'is-active', on); })) {
            lit.rels = rels;
            renderTopEdges();
        }
        index.junctions.forEach((junction) => {
            toggleIn(junctionEls, junction.via, 'is-active',
                relIds.has(junction.leftRelationship) && relIds.has(junction.rightRelationship));
        });
    }

    const sameFocus = (a, b) =>
        a.table === b.table && (a.rels || []).join('\n') === (b.rels || []).join('\n');

    function setHover(next) {
        if (sameFocus(hover, next)) { return; }
        hover = next;
        applyHighlight();
    }

    // A pin changes the Selected section and, for a table, which relationships are listed.
    function setPinned(next) {
        if (sameFocus(pinned, next)) { return; }
        const had = focusedPart();
        pinned = next;
        renderSelected();
        renderRels();
        applyHighlight();
        applyFocusChrome();
        applyRecordsLink();
        syncHash();
        restoreFocus(had);
        if (pinned.table !== null) { revealListed(pinned.table); }
    }

    // Scrolls one box just far enough to show an item, and nothing else. Not scrollIntoView:
    // that moves every scrollable ancestor as well, so on a short window pinning a table from
    // the diagram would scroll the whole panel and take the search box out of view.
    function revealIn(box, item) {
        const at = item.getBoundingClientRect();
        const top = at.top - box.getBoundingClientRect().top + box.scrollTop;
        const bottom = top + at.height;
        if (top < box.scrollTop) {
            box.scrollTop = top;
        } else if (bottom > box.scrollTop + box.clientHeight) {
            box.scrollTop = bottom - box.clientHeight;
        }
    }

    // A table pinned from the diagram or the search box may be anywhere in the Tables list, so
    // its item is brought into view there: only as far as the list must scroll, never the panel.
    function revealListed(id) {
        const item = listEls.get(id);
        if (item) { revealIn(side.tables, item); }
    }

    // The panel's lists, for putting the keyboard focus back after a rebuild: each knows its
    // items by id, the attribute that carries the id, and its heading (the last resort).
    const LISTS = {
        tables: { els: listEls, box: side.tables, attr: 'data-table' },
        rels: { els: relEls, box: side.rels, attr: 'data-rel' },
        junctions: { els: junctionEls, box: side.junctions, attr: 'data-via' }
    };
    Object.keys(LISTS).forEach((part) => {
        LISTS[part].summary = LISTS[part].box.parentNode.querySelector('summary');
    });
    const clearButton = side.selected.querySelector('[data-erd-clear]');

    // Where the keyboard focus is when it sits in a part of the page that a rebuild replaces:
    // one of the panel's lists (with the focused item's table, relationship or via id), the
    // Selected section (its Clear button or a "select table" button), or the diagram (the
    // canvas itself, or a card header with its table id). null anywhere else.
    function focusedPart() {
        const active = document.activeElement;
        if (!(active instanceof Element)) { return null; }
        const part = Object.keys(LISTS).find((name) => LISTS[name].box.contains(active));
        if (part) {
            const item = active.closest('[' + LISTS[part].attr + ']');
            return { part: part, id: item ? item.getAttribute(LISTS[part].attr) : null };
        }
        if (side.selected.contains(active)) { return { part: 'selected', id: null }; }
        if (canvas.contains(active)) {
            const card = active.closest('.erd-card');
            return { part: 'canvas', id: card ? card.getAttribute('data-table') : null };
        }
        return null;
    }

    // Re-rendering removes the focused element (or hides its section), and the browser then
    // drops the focus to the body: the next Tab would start again from the top of the page. So
    // the focus is put back on the nearest thing that still makes sense: the item of the same
    // table or relationship in the rebuilt list, else that list's first item or its heading;
    // the Clear button of the Selected section; or, when that section has just gone, the search
    // box above it; the same table's new card header, or the canvas when that table is no
    // longer drawn (focus mode in or out). A keyboard user must be able to see the focus, so a
    // list item is then brought into view: first its list scrolls, then the panel, each only as
    // far as it must (preventScroll: the page and the canvas never move).
    function restoreFocus(was) {
        if (!was) { return; }
        const active = document.activeElement;
        if (active instanceof Element && active !== document.body && active.isConnected &&
            !active.closest('[hidden]')) {
            return;
        }
        const list = own(LISTS, was.part);
        let target = null;
        if (list) {
            target = (was.id !== null && list.els.get(was.id)) ||
                list.box.querySelector('[' + list.attr + ']') || list.summary;
        } else if (was.part === 'canvas') {
            const card = was.id !== null ? cardEls.get(was.id) : undefined;
            target = card ? card.querySelector('.erd-card__head') : canvas;
        } else {
            target = side.selected.hidden ? side.search : clearButton;
        }
        if (!target) { return; }
        target.focus({ preventScroll: true });
        if (list && target !== list.summary) {
            revealIn(list.box, target);
            revealIn(panel, target);
        }
    }

    const pinTable = (id) => { setPinned({ table: id, rels: null }); };
    const pinRel = (id) => { setPinned({ table: null, rels: [id] }); };

    // What a point in the diagram means: a foreign-key row (its relationship), a card (its
    // table), a line (its relationship), or nothing. The same reading serves hover and click.
    function focusAt(target) {
        if (!(target instanceof Element) || !canvas.contains(target)) { return NOTHING; }
        const row = target.closest('.erd-row[data-rel]');
        if (row) { return { table: null, rels: [row.getAttribute('data-rel')] }; }
        const card = target.closest('.erd-card');
        if (card) { return { table: card.getAttribute('data-table'), rels: null }; }
        const edge = target.closest('.erd-edge');
        if (edge) { return { table: null, rels: [edge.getAttribute('data-rel')] }; }
        return NOTHING;
    }

    // The same for the panel: a table list item or a "select table" button, a relationship card,
    // or a many-to-many card (both of its relationships).
    function panelFocusAt(target) {
        if (!(target instanceof Element)) { return NOTHING; }
        const item = target.closest('[data-via], [data-rel], [data-table]');
        if (!item || !panel.contains(item)) { return NOTHING; }
        if (item.hasAttribute('data-via')) {
            const junction = index.junctions.find((j) => j.via === item.getAttribute('data-via'));
            return junction
                ? { table: null, rels: [junction.leftRelationship, junction.rightRelationship] }
                : NOTHING;
        }
        if (item.hasAttribute('data-rel')) { return { table: null, rels: [item.getAttribute('data-rel')] }; }
        return { table: item.getAttribute('data-table'), rels: null };
    }

    // The pointer is not "over" anything while it is busy moving the view or a card, and a
    // finger has no hover at all.
    const hoverAllowed = (e) => !gesture && e.pointerType !== 'touch';

    function onCanvasPointerOver(e) {
        if (hoverAllowed(e)) { setHover(focusAt(e.target)); }
    }

    // Where the last click in the panel landed, until the pointer moves away from there. A
    // click that rebuilds a list (pinning a relationship while a table is pinned swaps the
    // short grouped list for the full one) puts some other card under the unmoved pointer, and
    // the browser reports that as a pointerover. It is not a hover: the hover stays on what was
    // clicked, which is now pinned, until the pointer really moves. (Within a pixel: a click
    // reports whole pixels where pointer events carry fractions.)
    let panelClickAt = null;
    const atPanelClick = (e) =>
        panelClickAt !== null &&
        Math.abs(e.clientX - panelClickAt.x) < 1 && Math.abs(e.clientY - panelClickAt.y) < 1;

    function onPanelPointerOver(e) {
        if (!hoverAllowed(e) || atPanelClick(e)) { return; }
        panelClickAt = null;
        setHover(panelFocusAt(e.target));
    }

    // The first real movement after such a click: hover follows the pointer again.
    function onPanelPointerMove(e) {
        if (panelClickAt === null || atPanelClick(e)) { return; }
        panelClickAt = null;
        if (hoverAllowed(e)) { setHover(panelFocusAt(e.target)); }
    }

    function forgetPanelClick() {
        panelClickAt = null;
    }

    function onPointerLeave(e) {
        panelClickAt = null;
        if (hoverAllowed(e)) { setHover(NOTHING); }
    }

    // ---- The side panel ------------------------------------------------------------------------

    // The text the search box looks through, per table, worked out once.
    const searchText = new Map();
    index.tables.forEach((table) => {
        searchText.set(table.id, {
            own: [table.name, table.label, table.id].join('\n').toLowerCase(),
            columns: (table.columns || []).map((column) => String(column.name).toLowerCase())
        });
    });

    // null when the table does not match the search; otherwise { column }: the first matching
    // column name when only a column matched (the list shows it), else null.
    function searchMatch(id) {
        if (!search) { return { column: null }; }
        const text = searchText.get(id);
        if (text.own.indexOf(search) >= 0) { return { column: null }; }
        const i = text.columns.findIndex((name) => name.indexOf(search) >= 0);
        return i >= 0 ? { column: index.byId[id].columns[i].name } : null;
    }

    const setCount = (node, listed, total) => {
        node.textContent = search ? listed + ' of ' + total : String(listed);
    };

    function renderTables() {
        const fragment = document.createDocumentFragment();
        listEls.clear();
        listedTableIds = [];
        let total = 0;
        tableIdsByName.forEach((id) => {
            if (!isDrawn(id)) { return; }
            total++;
            const match = searchMatch(id);
            if (!match) { return; }
            const table = index.byId[id];
            const item = el('button', 'erd-list__item');
            item.type = 'button';
            item.setAttribute('data-table', id);
            item.classList.toggle('is-active', lit.active === id);
            item.classList.toggle('is-selected', lit.selected === id);
            item.appendChild(el('span', 'erd-list__name', nameOf(id)));
            const meta = [String((table.columns || []).length)];
            if (table.hub) { meta.push('hub'); }
            if (table.framework) { meta.push('login'); }
            item.appendChild(el('span', 'erd-list__meta', meta.join(MIDDLE_DOT)));
            // A table found through a column says which one, on a line of its own.
            if (match.column) { item.appendChild(el('span', 'erd-list__col', match.column)); }
            listEls.set(id, item);
            listedTableIds.push(id);
            fragment.appendChild(item);
        });
        if (listedTableIds.length === 0) {
            fragment.appendChild(el('div', 'erd-list__empty', 'No table matches'));
        }
        side.tables.textContent = '';
        side.tables.appendChild(fragment);
        setCount(side.tableCount, listedTableIds.length, total);
    }

    // The first line of a relationship card: parent, the "N : 1" pill, child.
    function relHeadline(parentName, pillText, childName, pillClass) {
        const line = el('div', 'erd-rel__line');
        line.appendChild(el('span', 'erd-rel__name', parentName));
        line.appendChild(el('span', 'erd-pill' + (pillClass ? ' ' + pillClass : ''), pillText));
        line.appendChild(el('span', 'erd-rel__name', childName));
        return line;
    }

    const relText = (rel) =>
        nameOf(rel.from) + '.' + (rel.fromColumns || [])[0] + ARROW +
        nameOf(rel.to) + '.' + (rel.toColumns || [])[0];
    const relRule = (rel) => 'on delete: ' + deleteText(rel) + (rel.required ? '' : MIDDLE_DOT + 'optional');

    function buildRelCard(rel) {
        const card = el('div', 'erd-rel ' + deleteClass(rel));
        card.setAttribute('data-rel', rel.id);
        card.tabIndex = 0;
        card.setAttribute('role', 'button');
        card.classList.toggle('is-active', lit.relIds.has(rel.id));
        card.classList.toggle('is-selected', lit.pinnedRels.has(rel.id));
        card.appendChild(relHeadline(nameOf(rel.to), L.cardinality(rel).label, nameOf(rel.from)));
        card.appendChild(el('div', 'erd-rel__detail', relText(rel)));
        card.appendChild(el('div', 'erd-rel__detail', relRule(rel)));
        return card;
    }

    // Every drawn relationship, or, with a table pinned, just its own in two groups: the keys it
    // holds (References) and the keys that point at it (Referenced by).
    function renderRels() {
        const fragment = document.createDocumentFragment();
        relEls.clear();
        const table = pinned.table !== null && isDrawn(pinned.table) ? pinned.table : null;
        let listed = 0;
        const addGroup = (title, rels) => {
            fragment.appendChild(el('div', 'erd-rels__group', title + ' (' + rels.length + ')'));
            rels.forEach((rel) => {
                const card = buildRelCard(rel);
                relEls.set(rel.id, card);
                fragment.appendChild(card);
                listed++;
            });
        };
        if (table !== null) {
            const mine = index.relsOf(table).filter(isDrawnRel);
            addGroup('References', mine.filter((rel) => rel.from === table));
            addGroup('Referenced by', mine.filter((rel) => rel.to === table && rel.from !== table));
            side.relsTitle.textContent = 'Relationships of ' + nameOf(table);
            // A long name is cut with an ellipsis in the heading; the tooltip has all of it.
            side.relsTitle.title = side.relsTitle.textContent;
        } else {
            side.relsTitle.removeAttribute('title');
            const matching = search
                ? new Set(listedTableIds)
                : null;
            index.rels
                .filter((rel) => isDrawnRel(rel) &&
                    (!matching || matching.has(rel.from) || matching.has(rel.to)))
                .sort((a, b) => byName(a.to, b.to) || byName(a.from, b.from) || ordinal(a.id, b.id))
                .forEach((rel) => {
                    const card = buildRelCard(rel);
                    relEls.set(rel.id, card);
                    fragment.appendChild(card);
                    listed++;
                });
            side.relsTitle.textContent = 'Relationships';
        }
        side.rels.classList.toggle('is-pinned', table !== null);
        side.rels.textContent = '';
        side.rels.appendChild(fragment);
        side.relCount.textContent = String(listed);
    }

    function renderJunctions() {
        const fragment = document.createDocumentFragment();
        junctionEls.clear();
        index.junctions.forEach((junction) => {
            if (!isDrawn(junction.via) || !isDrawn(junction.left) || !isDrawn(junction.right)) { return; }
            const card = el('div', 'erd-rel erd-rel--junction');
            card.setAttribute('data-via', junction.via);
            card.tabIndex = 0;
            card.setAttribute('role', 'button');
            card.appendChild(relHeadline(
                nameOf(junction.left), 'M : N', nameOf(junction.right), 'erd-pill--accent'));
            card.appendChild(el('div', 'erd-rel__detail', 'via ' + nameOf(junction.via)));
            junctionEls.set(junction.via, card);
            fragment.appendChild(card);
        });
        side.junctions.textContent = '';
        side.junctions.appendChild(fragment);
        side.junctionCount.textContent = String(junctionEls.size);
    }

    // One legend sample: a short line that ends at an imaginary card edge on the right, drawn
    // with the diagram's own classes so that its look can never drift from the real lines.
    function legendSample(groupClass, markKind) {
        const svg = svgEl('svg', 'erd-legend__svg');
        svg.setAttribute('viewBox', '0 0 ' + LEGEND_W + ' ' + LEGEND_H);
        svg.setAttribute('width', String(LEGEND_W));
        svg.setAttribute('height', String(LEGEND_H));
        svg.setAttribute('aria-hidden', 'true');
        const group = svgEl('g', 'erd-edge ' + groupClass);
        const edgeX = LEGEND_W - 4;
        const y = LEGEND_H / 2;
        const line = svgEl('path', 'erd-edge__line');
        line.setAttribute('d', 'M4 ' + y + ' H' + edgeX);
        group.appendChild(line);
        if (markKind) {
            const mark = svgEl('path', 'erd-edge__mark');
            mark.setAttribute('d', L.markerPath(markKind, edgeX, y, -1));
            group.appendChild(mark);
        }
        svg.appendChild(group);
        return svg;
    }

    function renderLegend() {
        const fragment = document.createDocumentFragment();
        const row = (sample, text) => {
            const node = el('div', 'erd-legend__row');
            node.appendChild(sample);
            node.appendChild(el('span', 'erd-legend__text', text));
            fragment.appendChild(node);
        };
        row(legendSample('is-cascade', 'one'), 'exactly one (required parent)');
        row(legendSample('is-cascade', 'zeroOrOne'), 'zero or one (optional parent, or a 1 : 1 child)');
        row(legendSample('is-cascade', 'zeroOrMany'), 'zero or many (child rows)');
        row(legendSample('is-cascade', null), 'on delete: cascade');
        row(legendSample('is-set-null', null), 'on delete: set null');
        row(legendSample('is-restrict', null), 'on delete: blocked');
        const hubs = index.tables.filter((table) => table.hub).map((table) => table.name);
        row(legendSample('is-hub is-cascade', null),
            'line into a hub table' + (hubs.length ? ' (' + hubs.join(', ') + ')' : ''));
        const key = (className, badge, text) => {
            row(el('span', 'erd-legend__key ' + className, badge), text);
        };
        key('erd-key erd-key--pk', 'PK', 'primary key');
        key('erd-key erd-key--fk', 'FK', 'foreign key');
        key('erd-key erd-key--uq', 'UQ', 'unique');
        key('erd-legend__null', '?', 'nullable');
        side.legend.textContent = '';
        side.legend.appendChild(fragment);
    }

    // A unique index worth a line of its own: more than one column, or a filter.
    const uniqueText = (unique) =>
        'unique (' + (unique.columns || []).join(', ') + ')' +
        (unique.filter ? ' where ' + unique.filter : '');

    function selectedTable(id) {
        const table = index.byId[id];
        const body = document.createDocumentFragment();
        const total = (table.columns || []).length;
        const uniques = table.uniques || [];
        body.appendChild(el('div', 'erd-selected__name', nameOf(id)));
        body.appendChild(el('div', 'erd-selected__meta',
            (table.label || id) + MIDDLE_DOT + total + (total === 1 ? ' column' : ' columns')));

        const tags = el('div', 'erd-selected__tags');
        if (table.hub) { tags.appendChild(el('span', 'tag', 'hub')); }
        if (table.framework) { tags.appendChild(el('span', 'tag', 'login table')); }
        if (uniques.length) {
            tags.appendChild(el('span', 'tag',
                uniques.length + (uniques.length === 1 ? ' unique rule' : ' unique rules')));
        }
        if (tags.childNodes.length) { body.appendChild(tags); }
        uniques
            .filter((unique) => (unique.columns || []).length > 1 || unique.filter)
            .forEach((unique) => { body.appendChild(el('div', 'erd-selected__uq', uniqueText(unique))); });

        const actions = el('div', 'erd-selected__actions');
        // A table with a slug has a records page; a login table is structure only; a table with
        // neither (policy-hidden) gets nothing.
        if (table.slug) {
            const link = el('a', 'btn sm primary', 'View records');
            link.href = recordsUrlFor(id);
            link.title = 'Open the records of ' + nameOf(id);
            actions.appendChild(link);
        } else if (table.framework) {
            actions.appendChild(el('p', 'erd-selected__note',
                'Structure only: rows of login tables are never shown here.'));
        }
        if (actions.childNodes.length) { body.appendChild(actions); }
        return body;
    }

    function selectedRel(rel) {
        const body = document.createDocumentFragment();
        body.appendChild(el('div', 'erd-selected__name erd-selected__name--rel', relText(rel)));
        body.appendChild(el('div', 'erd-selected__meta',
            L.cardinality(rel).label + MIDDLE_DOT + (rel.required ? 'required' : 'optional') +
            MIDDLE_DOT + 'on delete: ' + deleteText(rel)));
        if (rel.efDelete) {
            body.appendChild(el('div', 'erd-selected__uq', 'EF DeleteBehavior: ' + rel.efDelete));
        }
        const actions = el('div', 'erd-selected__actions');
        [rel.from, rel.to].forEach((id) => {
            const button = el('button', 'erd-linkbtn', nameOf(id));
            button.type = 'button';
            button.setAttribute('data-table', id);
            button.title = 'Select this table';
            actions.appendChild(button);
        });
        body.appendChild(actions);
        return body;
    }

    function renderSelected() {
        side.selectedBody.textContent = '';
        const rel = pinned.rels ? own(index.relsById, pinned.rels[0]) : undefined;
        if (pinned.table !== null && isDrawn(pinned.table)) {
            side.selectedBody.appendChild(selectedTable(pinned.table));
        } else if (rel && isDrawnRel(rel)) {
            side.selectedBody.appendChild(selectedRel(rel));
        } else {
            side.selected.hidden = true;
            return;
        }
        side.selected.hidden = false;
    }

    // Cards that do not match the search are greyed out, the same way as unrelated ones.
    function applyDim() {
        const dim = new Set();
        if (search) {
            cardEls.forEach((card, id) => { if (!searchMatch(id)) { dim.add(id); } });
        }
        diffSet(lit.dim, dim, (id, on) => { toggleIn(cardEls, id, 'is-dim', on); });
    }

    function onSearchInput() {
        search = side.search.value.trim().toLowerCase();
        renderTables();
        renderRels();
        applyDim();
        // New lists start at the top: a shorter list would otherwise keep the old scroll offset
        // and hide its first matches above the fold.
        side.tables.scrollTop = 0;
        side.rels.scrollTop = 0;
    }

    function clearSearch() {
        side.search.value = '';
        onSearchInput();
    }

    // Enter pins the first table listed. Escape empties the box and leaves it, so that the next
    // Escape, on the page, backs out of the next thing (focus mode, the pin).
    function onSearchKey(e) {
        if (e.key === 'Escape') {
            e.preventDefault();
            if (side.search.value) { clearSearch(); }
            side.search.blur();
            return;
        }
        if (e.key !== 'Enter' || listedTableIds.length === 0) { return; }
        e.preventDefault();
        pinTable(listedTableIds[0]);
        centreOn([listedTableIds[0]]);
    }

    // A click (or Enter / Space) on something in the panel.
    function activatePanelItem(target) {
        if (!(target instanceof Element)) { return; }
        const item = target.closest('[data-erd-clear], .erd-rel, [data-table]');
        if (!item || !panel.contains(item)) { return; }
        if (item.hasAttribute('data-erd-clear')) {
            setPinned(NOTHING);
        } else if (item.hasAttribute('data-via')) {
            const junction = index.junctions.find((j) => j.via === item.getAttribute('data-via'));
            if (!junction) { return; }
            pinTable(junction.via);
            centreOn([junction.left, junction.right, junction.via]);
        } else if (item.hasAttribute('data-rel')) {
            const rel = own(index.relsById, item.getAttribute('data-rel'));
            if (!rel) { return; }
            pinRel(rel.id);
            centreOn([rel.from, rel.to]);
        } else {
            const id = item.getAttribute('data-table');
            pinTable(id);
            centreOn([id]);
        }
    }

    // What was clicked is what the pointer is over, whatever a scroll had put there before, so
    // the hover is brought in line before the click acts (and then frozen there, see above).
    // Only when the click names something: a keyboard activation arrives as a click too
    // (detail 0, no position), and Enter on a View control must leave the mouse hover alone.
    function onPanelClick(e) {
        if (e.detail !== 0) { panelClickAt = { x: e.clientX, y: e.clientY }; }
        const at = panelFocusAt(e.target);
        if (hoverAllowed(e) && (at.table !== null || at.rels)) { setHover(at); }
        activatePanelItem(e.target);
    }

    // The relationship cards are divs, so the keyboard needs this; buttons and links already act.
    function onPanelKey(e) {
        if ((e.key !== 'Enter' && e.key !== ' ') || !(e.target instanceof Element)) { return; }
        const card = e.target.closest('.erd-rel');
        if (!card) { return; }
        e.preventDefault();
        activatePanelItem(card);
    }

    function renderPanel() {
        renderTables();
        renderRels();
        renderJunctions();
        renderSelected();
    }

    // ---- The view (pan and zoom) ---------------------------------------------------------------

    // Writes the view to the page. --erd-zoom lets the stylesheet keep lines, outlines and the
    // far-zoom names a constant size on screen.
    function applyView() {
        layer.style.transform =
            'translate(' + view.x + 'px, ' + view.y + 'px) scale(' + view.zoom + ')';
        layer.style.setProperty('--erd-zoom', String(view.zoom));
        layer.classList.toggle('is-far', view.zoom < M.farZoom);

        // The dot grid thins out (every 2nd, 4th... dot) instead of turning into a grey haze.
        let step = GRID_STEP;
        while (step * view.zoom < GRID_MIN_PX) { step *= 2; }
        const size = step * view.zoom;
        canvas.style.backgroundSize = size + 'px ' + size + 'px';
        canvas.style.backgroundPosition = view.x + 'px ' + view.y + 'px';

        if (zoomLabel) { zoomLabel.textContent = Math.round(view.zoom * 100) + '%'; }
    }

    // Pointer and wheel events can arrive several times per frame; the page is written once.
    const pending = { frame: 0, view: false, card: null };

    function flushFrame() {
        pending.frame = 0;
        if (pending.view) {
            pending.view = false;
            applyView();
        }
        if (pending.card !== null) {
            const id = pending.card;
            pending.card = null;
            // The card may have gone meanwhile (the picture drawn again under the drag).
            if (!cardEls.has(id)) { return; }
            placeCard(id);
            updateEdges([id]);
            // A lit line that touches the moved card has moved too, so its copy is drawn again.
            if (lit.related.has(id)) { renderTopEdges(); }
        }
    }

    function requestFrame() {
        if (!pending.frame) { pending.frame = window.requestAnimationFrame(flushFrame); }
    }

    // For the end of a gesture: what is on screen must match the state before it is saved.
    function flushNow() {
        if (pending.frame) {
            window.cancelAnimationFrame(pending.frame);
            flushFrame();
        }
    }

    const canvasSize = () => ({ width: canvas.clientWidth, height: canvas.clientHeight });

    // The first pan, zoom or drag ends the automatic fitting: from then on the view is the
    // viewer's, and a window resize must not move it.
    function markTouched() {
        touched = true;
        if (resizeWatch) {
            resizeWatch.disconnect();
            resizeWatch = null;
        }
    }

    function fit() {
        const size = canvasSize();
        if (!(size.width > 0 && size.height > 0)) { return; }
        view = L.fitView(L.boundsOf(positions, index, options.density), size);
        applyView();
    }

    function zoomBy(factor) {
        const size = canvasSize();
        markTouched();
        view = L.zoomAt(view, { x: size.width / 2, y: size.height / 2 }, view.zoom * factor);
        applyView();
    }

    // Brings these cards into view, whole. Nothing moves when they are already in view at a
    // readable zoom; otherwise the zoom is kept when it is readable and they fit at it, and is
    // otherwise the largest zoom (up to 100%) at which they do.
    function centreOn(ids) {
        const size = canvasSize();
        const box = L.boundsOf(positions, index, options.density, ids);
        const w = box.x1 - box.x0;
        const h = box.y1 - box.y0;
        if (!(w > 0 && h > 0 && size.width > 0 && size.height > 0)) { return; }
        markTouched();
        const left = box.x0 * view.zoom + view.x;
        const top = box.y0 * view.zoom + view.y;
        const inView = left >= CENTRE_MARGIN && top >= CENTRE_MARGIN &&
            left + w * view.zoom <= size.width - CENTRE_MARGIN &&
            top + h * view.zoom <= size.height - CENTRE_MARGIN;
        if (inView && view.zoom >= M.farZoom) { return; }
        const zFit = clamp(
            Math.min((size.width - 2 * M.fitPad) / w, (size.height - 2 * M.fitPad) / h), M.minZoom, 1);
        const zoom = view.zoom >= M.farZoom && view.zoom <= zFit ? view.zoom : zFit;
        view = {
            x: (size.width - w * zoom) / 2 - box.x0 * zoom,
            y: (size.height - h * zoom) / 2 - box.y0 * zoom,
            zoom: zoom
        };
        applyView();
    }

    // Every line again, from where the cards are now and in the current line style.
    function reshapeEdges() {
        updateEdges(Array.from(cardEls.keys()));
        renderTopEdges();
    }

    // Lays the drawn cards out again and fits them. In the overview that also forgets the moved
    // cards (at this density); in focus mode it only re-runs the focus layout, which was never
    // remembered in the first place, and the overview's moved cards stay remembered.
    function resetLayout() {
        if (focusTable === null) { saved[options.density] = {}; }
        computePositions();
        cardEls.forEach((card, id) => { placeCard(id); });
        reshapeEdges();
        fit();
        if (focusTable === null) { saveStored(); }
    }

    // Whether everything a hover or a pin names is drawn.
    const focusDrawn = (focus) =>
        (focus.table === null || isDrawn(focus.table)) &&
        (focus.rels || []).every((id) => {
            const rel = own(index.relsById, id);
            return !!rel && isDrawnRel(rel);
        });

    // Draws the page again from the options: the cards for the density and the visible tables,
    // every line, the panel's lists, and the highlight on the new elements. A hover or a pin on
    // something that is no longer drawn is dropped first; a keyboard focus in the panel's lists
    // or in the diagram is put back afterwards (the rebuild would otherwise drop it to the body).
    // `restored`, when given, is a set of positions to draw instead of computing them (leaving
    // focus mode).
    function redraw(restored) {
        const had = focusedPart();
        // The focused table has just been hidden (the Identity tables switched off): focus mode
        // is over, and the overview comes back laid out afresh for the new options.
        if (focusTable !== null && hiddenIds().has(focusTable)) {
            focusTable = null;
            beforeFocus = null;
        }
        if (restored) { positions = restored; } else { computePositions(); }
        if (!focusDrawn(hover)) { hover = NOTHING; }
        const dropped = pinned !== NOTHING && !focusDrawn(pinned);
        if (dropped) { pinned = NOTHING; }
        renderCards();
        renderEdges();
        renderPanel();
        applyHighlight();
        applyDim();
        applyFocusChrome();
        applyRecordsLink();
        // A pin this redraw has dropped (its table hidden by a view option) takes its hash with
        // it; a redraw that keeps the pin leaves the address alone.
        if (dropped) { syncHash(); }
        restoreFocus(had);
    }

    // ---- Focus mode ----------------------------------------------------------------------------
    // One table and its direct neighbours, laid out around it by focusLayout (the tables it
    // references on the left, the tables that reference it on the right), with only the lines
    // that touch it. It is a temporary arrangement: the overview's positions and view are put
    // aside on the way in and come back exactly on the way out, and nothing done inside is
    // remembered. The table stays pinned throughout.

    // Writes focus mode to the bar: is-focus on the root (the stylesheet frames the canvas and
    // draws hub lines normally), the chip with the table's name, and the Focus button, pressed
    // while in and live whenever it can do something (enter for a pinned table, or leave).
    function applyFocusChrome() {
        const on = focusTable !== null;
        root.classList.toggle('is-focus', on);
        focusChip.hidden = !on;
        focusName.textContent = on ? nameOf(focusTable) : '';
        // The chip cuts a long name with an ellipsis; the tooltip has all of it.
        focusName.title = focusName.textContent;
        focusButton.setAttribute('aria-pressed', on ? 'true' : 'false');
        const disabled = !on && (pinned.table === null || !isDrawn(pinned.table));
        // A disabled button cannot hold the keyboard focus: the browser would drop it to the
        // body, so the diagram takes it first.
        if (disabled && document.activeElement === focusButton) {
            canvas.focus({ preventScroll: true });
        }
        focusButton.disabled = disabled;
    }

    // Into focus mode on this table, or across to it from another table's focus mode (the
    // overview put aside the first time is kept: leaving always returns to it).
    function focusOn(id) {
        if (id === focusTable || !isDrawn(id)) { return; }
        if (focusTable === null) {
            beforeFocus = {
                positions: Object.assign({}, positions),
                view: Object.assign({}, view),
                density: options.density,
                framework: options.framework
            };
        }
        focusTable = id;
        redraw();
        fit();
    }

    // Back to the overview as it was. Unless the cards themselves changed meanwhile (a density
    // or Identity change made in focus mode): then the put-aside positions no longer fit the
    // cards, and the overview is laid out and fitted afresh instead.
    function leaveFocus() {
        if (focusTable === null) { return; }
        // The chip goes, and with it any keyboard focus on its button (Escape pressed there):
        // the diagram takes the focus instead, so the arrow keys pan straight away.
        if (document.activeElement === focusExit) { canvas.focus({ preventScroll: true }); }
        const was = beforeFocus;
        focusTable = null;
        beforeFocus = null;
        if (was.density === options.density && was.framework === options.framework) {
            redraw(was.positions);
            view = was.view;
            applyView();
        } else {
            redraw();
            fit();
        }
    }

    // The f key: in for the pinned table; across to it when another table was pinned inside
    // focus mode; out again when the focused table is the pinned one, or nothing is.
    function onFocusKey() {
        if (focusTable !== null && (pinned.table === null || pinned.table === focusTable)) {
            leaveFocus();
        } else if (pinned.table !== null) {
            focusOn(pinned.table);
        }
    }

    // The Focus button is a plain switch: in for the pinned table, out again.
    function onFocusButton() {
        if (focusTable !== null) {
            leaveFocus();
        } else if (pinned.table !== null) {
            focusOn(pinned.table);
        }
    }

    // The chip's button, by mouse or keyboard: the diagram takes the focus either way (a
    // browser that does not focus a clicked button would otherwise leave it on the body).
    function onFocusExit() {
        leaveFocus();
        canvas.focus({ preventScroll: true });
    }

    // ---- View records --------------------------------------------------------------------------
    // The pinned table's records page (/data-admin/<slug>): the bar's link, the v key, and the
    // same link at the top of the panel's Selected section. The pinned table is also written to
    // the address as #table=<slug>, so a records page's Back link (or a refresh, or a bookmark)
    // comes back to the diagram with that table selected.

    const HASH_PREFIX = 'table=';

    // The pinned table when it has a records page, else null.
    function recordsTable() {
        const id = pinned.table;
        return id !== null && isDrawn(id) && slugOf(id) ? id : null;
    }

    // Why the link is disabled: nothing (or a relationship) is pinned, the pinned table is a
    // login table, or it has no page for another reason (policy-hidden).
    function recordsDisabledTitle() {
        if (pinned.table === null || !isDrawn(pinned.table)) {
            return 'Open the selected table\'s records (V). Select a table first.';
        }
        return index.byId[pinned.table].framework
            ? 'Login tables have no records page: structure only.'
            : 'This table has no records page.';
    }

    // Writes the bar's link: a real href while the pinned table has a records page (the browser
    // then gives middle-click, open in a new tab and copy link), else no href and aria-disabled.
    // An anchor without an href cannot hold the keyboard focus: taking the href off the focused
    // link would drop the focus to the body, so the diagram takes it first (as the Focus button).
    function applyRecordsLink() {
        const id = recordsTable();
        if (id !== null) {
            recordsLink.setAttribute('href', recordsUrlFor(id));
            recordsLink.removeAttribute('aria-disabled');
            recordsLink.title = 'Open the records of ' + nameOf(id) + ' (V)';
            return;
        }
        if (document.activeElement === recordsLink) { canvas.focus({ preventScroll: true }); }
        recordsLink.removeAttribute('href');
        recordsLink.setAttribute('aria-disabled', 'true');
        recordsLink.title = recordsDisabledTitle();
    }

    // The slug the address names (#table=<slug>), lower-cased, or null when the hash is not ours.
    // Decoded first, in case a link percent-encoded it; a hash that will not decode is read as it is.
    function readHashSlug() {
        let hash = window.location.hash.replace(/^#/, '');
        try { hash = decodeURIComponent(hash); } catch (e) { /* not percent-encoded: keep the text */ }
        return hash.indexOf(HASH_PREFIX) === 0 ? hash.slice(HASH_PREFIX.length).toLowerCase() : null;
    }

    // The fragment the page arrived with when it is not ours (an anchor of someone else's, or
    // nothing): a pin writes over it, and the unpin puts it back instead of clearing the address.
    const foreignHash = readHashSlug() === null ? window.location.hash : '';

    // Keeps the address in step with the pin: #table=<slug> while a table with a records page is
    // pinned, and once it is not, the fragment the page arrived with (none, usually). replaceState,
    // never pushState: a pin is not a page of its own, and the browser's Back must still leave the
    // diagram. A hash that is not ours is never cleared. Called from setPinned, and from redraw
    // only when the redraw has dropped the pin: the first redraw at start-up runs before the hash
    // has been read, and must not touch it.
    function syncHash() {
        try {
            const id = recordsTable();
            if (id !== null) {
                window.history.replaceState(null, '', '#' + HASH_PREFIX + slugOf(id));
            } else if (readHashSlug() !== null) {
                window.history.replaceState(null, '', window.location.pathname + window.location.search + foreignHash);
            }
        } catch (e) {
            // A browser that refuses (a sandboxed frame, say) keeps the address as it is.
        }
    }

    // The v key: the page the link opens, while the link is enabled.
    function openRecords() {
        const id = recordsTable();
        if (id !== null) { window.location.assign(recordsUrlFor(id)); }
    }

    // ---- Keyboard ------------------------------------------------------------------------------
    // Keys are left alone while the viewer types (a field or an editable element has them), with
    // Ctrl, Alt or Meta held (Ctrl+0 and Ctrl+- belong to the browser), when something else on
    // the page has already answered the key, and in the middle of a pan or a card drag.

    // Inputs that take no text: a key pressed on one of them (a View switch, which keeps the
    // focus after a mouse click) is the page's.
    const NO_TEXT_INPUTS = ['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color'];

    function typingIn(target) {
        if (!(target instanceof Element)) { return false; }
        if (target.isContentEditable) { return true; }
        const tag = target.tagName;
        if (tag === 'INPUT') { return NO_TEXT_INPUTS.indexOf(target.type) < 0; }
        return tag === 'TEXTAREA' || tag === 'SELECT';
    }

    // An arrow key looks that way: the view moves the other way, as a scroll would.
    const ARROWS = { ArrowLeft: [1, 0], ArrowRight: [-1, 0], ArrowUp: [0, 1], ArrowDown: [0, -1] };

    function panBy(dx, dy) {
        markTouched();
        view = { x: view.x + dx, y: view.y + dy, zoom: view.zoom };
        applyView();
    }

    function onKeyDown(e) {
        if (e.defaultPrevented || e.ctrlKey || e.altKey || e.metaKey || typingIn(e.target)) { return; }
        // A drag measures from the zoom and the cards it started with (as onWheel): a key that
        // zoomed, or drew the cards again, under a live drag would move or lose the dragged card.
        if (gesture) { return; }
        const key = e.key;
        const inCanvas = e.target instanceof Element && canvas.contains(e.target);
        const arrow = own(ARROWS, key);
        if (arrow) {
            // Only while the keyboard is in the diagram (the canvas or a card header): anywhere
            // else the arrows are the page's.
            if (!inCanvas) { return; }
            e.preventDefault();
            panBy(arrow[0] * ARROW_PAN, arrow[1] * ARROW_PAN);
        } else if (key === 'Enter' || key === ' ') {
            // A focused card header pins its table, and unpins it again, as a click does. Space
            // must not scroll; every other Enter and Space (buttons, switches, summaries, the
            // relationship cards) keeps its own meaning.
            const head = inCanvas ? e.target.closest('.erd-card__head') : null;
            if (!head) { return; }
            e.preventDefault();
            const id = head.parentNode.getAttribute('data-table');
            setPinned(pinned.table === id ? NOTHING : { table: id, rels: null });
        } else if (key === '+' || key === '=') {
            zoomBy(BUTTON_ZOOM);
        } else if (key === '-') {
            zoomBy(1 / BUTTON_ZOOM);
        } else if (key === '0') {
            fit();
        } else if (key === '/') {
            e.preventDefault();         // the browser's own quick find
            if (!options.panel) { setOption('panel', true); }
            side.search.focus({ preventScroll: true });
        } else if (key === 'f' || key === 'F') {
            onFocusKey();
        } else if (key === 'v' || key === 'V') {
            openRecords();
        } else if (key === 'Escape') {
            // One level at a time: out of focus mode, then the search text, then the pin.
            if (focusTable !== null) {
                leaveFocus();
            } else if (side.search.value) {
                clearSearch();
            } else {
                setPinned(NOTHING);
            }
        }
    }

    // ---- View options --------------------------------------------------------------------------
    // Every option takes effect at once and is remembered. Column types, hub lines and the panel
    // are a class on #erd that the stylesheet reads; the line style is new paths for the same
    // lines; density and the Identity tables change which cards exist and how tall they are, so
    // they draw everything again and fit the result.

    // Writes the options to the page: the controls show them and #erd carries their classes.
    function applyOptions() {
        ['density', 'edges'].forEach((key) => {
            controls[key].querySelectorAll('[data-value]').forEach((button) => {
                const on = button.getAttribute('data-value') === options[key];
                button.classList.toggle('is-active', on);
                button.setAttribute('aria-pressed', on ? 'true' : 'false');
            });
        });
        ['types', 'hubs', 'framework'].forEach((key) => { controls[key].checked = options[key]; });
        root.classList.toggle('no-types', !options.types);
        root.classList.toggle('hide-hubs', !options.hubs);
        root.classList.toggle('is-panel-closed', !options.panel);
        panelToggle.setAttribute('aria-expanded', options.panel ? 'true' : 'false');
    }

    function setOption(key, value) {
        if (!validOption(key, value) || options[key] === value) { return; }
        options[key] = value;
        applyOptions();
        if (key === 'density' || key === 'framework') {
            redraw();
            fit();
        } else if (key === 'edges') {
            reshapeEdges();
        } else if (key === 'panel' && !touched) {
            // The canvas has just changed width; the overview is still the viewer's view.
            fit();
        }
        saveStored();
    }

    // A segmented button: its group's option takes the button's value.
    function onViewClick(e) {
        const button = e.target instanceof Element ? e.target.closest('[data-value]') : null;
        const group = button ? button.closest('[data-erd-opt]') : null;
        if (group) { setOption(group.getAttribute('data-erd-opt'), button.getAttribute('data-value')); }
    }

    // A switch: its option is its checked state.
    function onViewChange(e) {
        const input = e.target;
        if (input instanceof HTMLInputElement && input.hasAttribute('data-erd-opt')) {
            setOption(input.getAttribute('data-erd-opt'), input.checked);
        }
    }

    // ---- Pointer: pan, or drag one card --------------------------------------------------------
    // One listener set on the canvas, with pointer capture, so a drag keeps working when the
    // pointer leaves the card or the window. A press on a card header drags that card; a press
    // anywhere else (a card body included) pans. A press that never moves is a click.

    function onPointerDown(e) {
        if (gesture || !e.isPrimary || e.button !== 0) { return; }
        const head = e.target instanceof Element ? e.target.closest('.erd-card__head') : null;
        const id = head ? head.parentNode.getAttribute('data-table') : null;
        const card = id !== null && isDrawn(id) ? id : null;
        const origin = card !== null ? positions[card] : view;
        gesture = {
            pointerId: e.pointerId,
            target: e.target,           // what was pressed, for when it turns out to be a click
            card: card,                 // table id, or null for a pan
            inFocus: focusTable !== null,   // a drag begun in focus mode is never remembered
            startX: e.clientX,
            startY: e.clientY,
            originX: origin.x,
            originY: origin.y,
            moved: false
        };
        try { canvas.setPointerCapture(e.pointerId); } catch (err) { /* the pointer is already gone */ }
    }

    function onPointerMove(e) {
        const g = gesture;
        if (!g || e.pointerId !== g.pointerId) { return; }
        const dx = e.clientX - g.startX;
        const dy = e.clientY - g.startY;
        if (!g.moved) {
            // Below the slop it is still a click: nothing moves, so a shaky press never nudges a card.
            if (Math.abs(dx) < CLICK_SLOP && Math.abs(dy) < CLICK_SLOP) { return; }
            g.moved = true;
            markTouched();
            canvas.classList.add('is-grabbing');
            if (g.card !== null) { toggleIn(cardEls, g.card, 'is-dragging', true); }
        }
        if (g.card !== null) {
            // A card that is no longer drawn (the picture was drawn again under the drag) is
            // not moved: a position for it would be a phantom.
            if (!cardEls.has(g.card)) { return; }
            // The pointer moves in screen pixels, the card in layer pixels.
            positions[g.card] = {
                x: Math.round(g.originX + dx / view.zoom),
                y: Math.round(g.originY + dy / view.zoom)
            };
            pending.card = g.card;
        } else {
            view = { x: g.originX + dx, y: g.originY + dy, zoom: view.zoom };
            pending.view = true;
        }
        requestFrame();
    }

    // A click in the diagram: a foreign-key row or a line pins its relationship, a card pins its
    // table (the pinned one again unpins it), the empty background unpins.
    // Except the second click of a double-click on a card: that one leaves the table pinned,
    // because the dblclick that follows focuses on it and needs it pinned (the browser sends two
    // whole clicks before the dblclick; pin, unpin, pin would flash the panel three times).
    let lastCardClick = null;           // { table, x, y, time } of the last click on a card

    // Whether a press is the second half of a double-click on this card: soon after the last
    // click and on the same spot, as the browser's own rule has it. A second click elsewhere on
    // the card, a row 100px down say, is a click of its own.
    const doubleClickOn = (table, g, time) =>
        lastCardClick !== null && lastCardClick.table === table &&
        time - lastCardClick.time < DOUBLE_CLICK_MS &&
        Math.abs(g.startX - lastCardClick.x) <= DOUBLE_CLICK_PX &&
        Math.abs(g.startY - lastCardClick.y) <= DOUBLE_CLICK_PX;

    function clickAt(g, time) {
        const focus = focusAt(g.target);
        if (focus.rels) {
            pinRel(focus.rels[0]);
        } else if (focus.table !== null) {
            const again = pinned.table === focus.table && !doubleClickOn(focus.table, g, time);
            lastCardClick = { table: focus.table, x: g.startX, y: g.startY, time: time };
            setPinned(again ? NOTHING : focus);
            return;
        } else {
            setPinned(NOTHING);
        }
        lastCardClick = null;
    }

    // A double-click on a card: focus mode on its table (the two clicks have pinned it already;
    // in focus mode on another table this moves the focus across). The canvas holds the pointer
    // capture during a press, so the browser names the canvas as the target; the card is read
    // from under the pointer instead.
    function onDoubleClick(e) {
        const under = document.elementFromPoint(e.clientX, e.clientY);
        const card = under instanceof Element ? under.closest('.erd-card') : null;
        const id = card ? card.getAttribute('data-table') : null;
        if (id === null || !isDrawn(id)) { return; }
        e.preventDefault();
        pinTable(id);
        focusOn(id);
    }

    // Ends the gesture however it ended (released, cancelled by the browser, capture lost): a
    // moved card stays where it was dropped and is remembered; an unmoved press is a click.
    function endGesture(e) {
        const g = gesture;
        if (!g || e.pointerId !== g.pointerId) { return; }
        gesture = null;
        flushNow();
        canvas.classList.remove('is-grabbing');
        if (canvas.hasPointerCapture(g.pointerId)) { canvas.releasePointerCapture(g.pointerId); }
        if (g.card !== null && g.moved) {
            toggleIn(cardEls, g.card, 'is-dragging', false);
            // Focus mode is a passing arrangement: a card moved there is not remembered, not
            // even when the drag outlives it. Nor is a card that is no longer drawn.
            if (!g.inFocus && focusTable === null && cardEls.has(g.card)) {
                saved[options.density][g.card] = [positions[g.card].x, positions[g.card].y];
                saveStored();
            }
        }
        if (!g.moved && e.type === 'pointerup') {
            clickAt(g, e.timeStamp);
        } else if (g.moved && e.pointerType !== 'touch') {
            // Hover was frozen during the gesture; the view or the card has moved under the pointer.
            setHover(focusAt(document.elementFromPoint(e.clientX, e.clientY)));
        }
    }

    // ---- Wheel: zoom toward the cursor ---------------------------------------------------------

    function onWheel(e) {
        // Always ours: the page must never scroll or browser-zoom under the diagram.
        e.preventDefault();
        if (gesture) { return; }        // a drag measures from the zoom it started at
        // Mice report pixels or lines; one line is about 16px. The clamp keeps one event (a
        // free-spinning wheel can report thousands of pixels) to a gentle step.
        const delta = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
        const factor = clamp(Math.exp(-delta * 0.0015), 0.8, 1.25);
        const box = canvas.getBoundingClientRect();
        markTouched();
        view = L.zoomAt(
            view, { x: e.clientX - box.left, y: e.clientY - box.top }, view.zoom * factor);
        pending.view = true;
        requestFrame();
    }

    // The canvas must stay unscrolled: every position on this page assumes it. overflow: clip
    // already stops it where supported; without it, focusing a card that is off screen would
    // scroll the box, so put it straight back.
    function onCanvasScroll() {
        if (canvas.scrollLeft !== 0) { canvas.scrollLeft = 0; }
        if (canvas.scrollTop !== 0) { canvas.scrollTop = 0; }
    }

    function onClick(selector, handler) {
        const button = root.querySelector(selector);
        if (button) { button.addEventListener('click', handler); }
    }

    // ---- Start ---------------------------------------------------------------------------------

    root.style.setProperty('--erd-card-w', M.cardW + 'px');
    root.style.setProperty('--erd-head-h', M.headH + 'px');
    root.style.setProperty('--erd-row-h', M.rowH + 'px');

    // The options first: a closed panel gives the canvas its full width before the first fit.
    applyOptions();
    renderLegend();
    redraw();
    fit();
    // A deep link (#table=<slug>, written by the records pages' Back links and by every pin)
    // selects that table and brings its card into view. An unknown or hidden table is ignored
    // and the hash left as it is.
    const linkedSlug = readHashSlug();
    const linked = linkedSlug !== null ? idBySlug.get(linkedSlug) : undefined;
    let pendingCentre = null;
    if (linked !== undefined && isDrawn(linked)) {
        pinTable(linked);
        centreOn([linked]);
        // A canvas with no size yet (the page laid out while hidden) cannot centre: the resize
        // watch below does it once the canvas has a size and has been fitted.
        if (!touched) { pendingCentre = linked; }
    }

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', endGesture);
    canvas.addEventListener('pointercancel', endGesture);
    canvas.addEventListener('lostpointercapture', endGesture);
    canvas.addEventListener('pointerover', onCanvasPointerOver);
    canvas.addEventListener('pointerleave', onPointerLeave);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('scroll', onCanvasScroll);
    canvas.addEventListener('dblclick', onDoubleClick);
    document.addEventListener('keydown', onKeyDown);
    focusButton.addEventListener('click', onFocusButton);
    focusExit.addEventListener('click', onFocusExit);

    panel.addEventListener('pointerover', onPanelPointerOver);
    panel.addEventListener('pointermove', onPanelPointerMove);
    panel.addEventListener('pointerleave', onPointerLeave);
    // Scrolling the panel under a still pointer is a real intent, so hover follows it again.
    panel.addEventListener('wheel', forgetPanelClick, { passive: true });
    panel.addEventListener('click', onPanelClick);
    panel.addEventListener('keydown', onPanelKey);
    side.search.addEventListener('input', onSearchInput);
    side.search.addEventListener('keydown', onSearchKey);
    viewSection.addEventListener('click', onViewClick);
    viewSection.addEventListener('change', onViewChange);
    panelToggle.addEventListener('click', () => { setOption('panel', !options.panel); });

    onClick('[data-erd-zoom-in]', () => { zoomBy(BUTTON_ZOOM); });
    onClick('[data-erd-zoom-out]', () => { zoomBy(1 / BUTTON_ZOOM); });
    onClick('[data-erd-fit]', fit);
    onClick('[data-erd-reset]', resetLayout);

    // Until the viewer takes over, keep the whole diagram in view as the canvas changes size
    // (the first layout pass, a window resize). Not after a deep link: centring on its card
    // counts as the viewer's view (touched), and a later resize must not re-fit over it.
    if (!touched && typeof window.ResizeObserver === 'function') {
        resizeWatch = new window.ResizeObserver(() => {
            if (touched) { return; }
            fit();
            // The deep link's card, centred now that there is room (centreOn marks the view
            // touched and disconnects this watch; a canvas still without a size leaves it for
            // the next change).
            if (pendingCentre !== null) { centreOn([pendingCentre]); }
        });
        resizeWatch.observe(canvas);
    }
})();
