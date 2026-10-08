// The Data Admin schema diagram (/data-admin/schema): draws every table as a card and every foreign
// key as a line, lets the viewer pan, zoom and move the cards about, lights up what the pointer is
// over, and keeps the side panel (search, selection, lists, legend) in step with the picture.
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
    const BUTTON_ZOOM = 1.2;
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

    // Where the "Browse rows" link goes, and which tables may have one: only those the super admin
    // has put on Data Admin's table list. Everything else is structure only.
    const browseUrl = root.getAttribute('data-browse-url') || '';
    const tablesUrl = root.getAttribute('data-tables-url') || '';
    const shownIds = new Set((root.getAttribute('data-shown') || '').split(',').filter(Boolean));

    // ---- What the browser remembers ------------------------------------------------------------
    // One localStorage entry: { v: 1, options, positions: { all: { id: [x, y] }, keys, names } }.
    // Moved cards are remembered per density because a card's height, and so the whole layout,
    // differs between them. Pan and zoom are never stored: a fresh visit starts with the overview.

    function cleanOptions(raw) {
        const options = {};
        Object.keys(OPTION_DEFAULTS).forEach((key) => {
            const value = isObject(raw) ? own(raw, key) : undefined;
            const choices = own(OPTION_CHOICES, key);
            const valid = choices ? choices.indexOf(value) >= 0 : typeof value === 'boolean';
            options[key] = valid ? value : OPTION_DEFAULTS[key];
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
    // where they were left.
    function computePositions() {
        const auto = L.autoLayout(index, { density: options.density, hidden: hiddenIds() }).positions;
        const moved = saved[options.density];
        const next = {};
        Object.keys(auto).forEach((id) => {
            const at = own(moved, id);
            next[id] = at ? { x: at[0], y: at[1] } : auto[id];
        });
        positions = next;
    }

    const isDrawn = (id) => own(positions, id) !== undefined;
    const isDrawnRel = (rel) => isDrawn(rel.from) && isDrawn(rel.to);

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

    // Rebuilds every card for the current density and set of visible tables.
    function renderCards() {
        const fragment = document.createDocumentFragment();
        cardEls.clear();
        rowEls.clear();
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
        const had = focusedPanelPart();
        pinned = next;
        renderSelected();
        renderRels();
        applyHighlight();
        restoreFocus(had);
    }

    // Where the keyboard focus is when it sits in a part of the panel that a pin rebuilds: a
    // relationship card (remembered by id), or the Selected section (its Clear button or a
    // "select table" button). null anywhere else.
    function focusedPanelPart() {
        const active = document.activeElement;
        if (!(active instanceof Element)) { return null; }
        if (side.rels.contains(active)) {
            const card = active.closest('.erd-rel');
            return { part: 'rels', rel: card ? card.getAttribute('data-rel') : null };
        }
        return side.selected.contains(active) ? { part: 'selected' } : null;
    }

    // Re-rendering removes the focused element (or hides its section), and the browser then
    // drops the focus to the body: the next Tab would start again from the top of the page. So
    // the focus is put back on the nearest thing that still makes sense: the card of the same
    // relationship (a pinned relationship is always in the full list), else the first card or
    // the list's heading; the Clear button of the Selected section; or, when that section has
    // just gone, the search box above it. preventScroll keeps the panel where it was; only the
    // relationship list scrolls, and only as far as it must to show the card.
    const relsSummary = side.rels.parentNode.querySelector('summary');
    const clearButton = side.selected.querySelector('[data-erd-clear]');
    function restoreFocus(was) {
        if (!was) { return; }
        const active = document.activeElement;
        if (active instanceof Element && active !== document.body && active.isConnected &&
            !active.closest('[hidden]')) {
            return;
        }
        let target = null;
        if (was.part === 'rels') {
            target = (was.rel !== null && relEls.get(was.rel)) ||
                side.rels.querySelector('.erd-rel') || relsSummary;
        } else {
            target = side.selected.hidden ? side.search : clearButton;
        }
        if (!target) { return; }
        target.focus({ preventScroll: true });
        if (was.part === 'rels') { target.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }
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
        if (table.slug && shownIds.has(id)) {
            const link = el('a', 'btn sm primary', 'Browse rows');
            link.setAttribute('data-erd-browse', '');
            link.href = browseUrl.replace('__slug__', encodeURIComponent(table.slug));
            actions.appendChild(link);
        } else if (table.slug) {
            const note = el('p', 'erd-selected__note', 'Not in Data Admin\'s table list. ');
            const link = el('a', 'erd-selected__link', 'Choose tables');
            link.href = tablesUrl;
            note.appendChild(link);
            actions.appendChild(note);
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

    function onSearchKey(e) {
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

    function onPanelClick(e) {
        panelClickAt = { x: e.clientX, y: e.clientY };
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

    function resetLayout() {
        saved[options.density] = {};
        computePositions();
        cardEls.forEach((card, id) => { placeCard(id); });
        updateEdges(Array.from(cardEls.keys()));
        renderTopEdges();
        fit();
        saveStored();
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
            if (g.card !== null) { cardEls.get(g.card).classList.add('is-dragging'); }
        }
        if (g.card !== null) {
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
    function clickAt(target) {
        const focus = focusAt(target);
        if (focus.rels) {
            pinRel(focus.rels[0]);
        } else if (focus.table !== null) {
            setPinned(pinned.table === focus.table ? NOTHING : focus);
        } else {
            setPinned(NOTHING);
        }
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
            cardEls.get(g.card).classList.remove('is-dragging');
            saved[options.density][g.card] = [positions[g.card].x, positions[g.card].y];
            saveStored();
        }
        if (!g.moved && e.type === 'pointerup') {
            clickAt(g.target);
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

    computePositions();
    renderCards();
    renderEdges();
    renderLegend();
    renderPanel();
    fit();

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', endGesture);
    canvas.addEventListener('pointercancel', endGesture);
    canvas.addEventListener('lostpointercapture', endGesture);
    canvas.addEventListener('pointerover', onCanvasPointerOver);
    canvas.addEventListener('pointerleave', onPointerLeave);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('scroll', onCanvasScroll);

    panel.addEventListener('pointerover', onPanelPointerOver);
    panel.addEventListener('pointermove', onPanelPointerMove);
    panel.addEventListener('pointerleave', onPointerLeave);
    // Scrolling the panel under a still pointer is a real intent, so hover follows it again.
    panel.addEventListener('wheel', forgetPanelClick, { passive: true });
    panel.addEventListener('click', onPanelClick);
    panel.addEventListener('keydown', onPanelKey);
    side.search.addEventListener('input', onSearchInput);
    side.search.addEventListener('keydown', onSearchKey);

    onClick('[data-erd-zoom-in]', () => { zoomBy(BUTTON_ZOOM); });
    onClick('[data-erd-zoom-out]', () => { zoomBy(1 / BUTTON_ZOOM); });
    onClick('[data-erd-fit]', fit);
    onClick('[data-erd-reset]', resetLayout);

    // Until the viewer takes over, keep the whole diagram in view as the canvas changes size
    // (the first layout pass, a window resize).
    if (typeof window.ResizeObserver === 'function') {
        resizeWatch = new window.ResizeObserver(() => { if (!touched) { fit(); } });
        resizeWatch.observe(canvas);
    }
})();
