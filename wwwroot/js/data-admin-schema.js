// The Data Admin schema diagram (/data-admin/schema): draws every table as a card and every foreign
// key as a line, and lets the viewer pan, zoom and move the cards about.
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
    const cardsEl = root.querySelector('[data-erd-cards]');
    const zoomLabel = root.querySelector('[data-erd-zoom-label]');
    if (!canvas || !layer || !edgesSvg || !cardsEl) { return; }

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
    const KEY_SLOT_WIDE = '46px';   // room for two badges (the stylesheet's default holds one)
    const ZERO_WIDTH_SPACE = String.fromCharCode(0x200b);   // an invisible place where a name may wrap
    // A stored position beyond this (layer px) is treated as damage: the automatic layout is about
    // 5000px across, and a value such as 1e300 would blank the canvas and break every SVG path.
    const POSITION_LIMIT = 1000000;

    // Own properties only, so a table called "constructor" can never find Object.prototype's.
    const own = (map, key) =>
        (map && Object.prototype.hasOwnProperty.call(map, key) ? map[key] : undefined);
    const isObject = (value) => !!value && typeof value === 'object' && !Array.isArray(value);
    const clamp = (value, low, high) => Math.min(Math.max(value, low), high);

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

    const cardEls = new Map();          // table id -> its card element
    const edgeEls = new Map();          // relationship id -> { hit, line, childMark, parentMark }

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

    function buildCard(table) {
        const rows = L.visibleRows(table, options.density);
        const total = (table.columns || []).length;
        const name = table.name || table.id;

        const card = el('div', 'erd-card');
        card.setAttribute('data-table', table.id);
        if (table.framework) { card.classList.add('is-framework'); }
        if (table.hub) { card.classList.add('is-hub'); }
        if (rows.some((column) => badgesOf(column).length > 1)) {
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

        rows.forEach((column) => { card.appendChild(buildRow(table, column)); });

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
        index.tables.forEach((table) => {
            if (!own(positions, table.id)) { return; }
            const card = buildCard(table);
            cardEls.set(table.id, card);
            placeCard(table.id);
            fragment.appendChild(card);
        });
        cardsEl.textContent = '';
        cardsEl.appendChild(fragment);
    }

    // ---- Relationship lines --------------------------------------------------------------------

    function svgPath(className) {
        const path = document.createElementNS(SVG_NS, 'path');
        path.setAttribute('class', className);
        return path;
    }

    const DELETE_CLASS = { cascade: 'is-cascade', setNull: 'is-set-null' };

    // Where a line meets a card: the card's left edge (the layout module picks the side) at the
    // middle of the column's row.
    const anchorOf = (tableId, columns) => ({
        x: positions[tableId].x,
        y: positions[tableId].y +
            L.rowAnchorY(index.byId[tableId], (columns || [])[0], options.density)
    });

    function setEdgeGeometry(rel, parts) {
        const geometry = L.edgeGeometry(
            anchorOf(rel.from, rel.fromColumns), anchorOf(rel.to, rel.toColumns), options.edges);
        const ends = L.cardinality(rel);
        parts.hit.setAttribute('d', geometry.d);
        parts.line.setAttribute('d', geometry.d);
        parts.childMark.setAttribute('d',
            L.markerPath(ends.child, geometry.from.x, geometry.from.y, geometry.from.dir));
        parts.parentMark.setAttribute('d',
            L.markerPath(ends.parent, geometry.to.x, geometry.to.y, geometry.to.dir));
    }

    // Rebuilds every line between two drawn cards. Paint order inside a group: the wide
    // invisible hit path, the line, then the two crow's feet on top of the line's ends.
    function renderEdges() {
        const fragment = document.createDocumentFragment();
        edgeEls.clear();
        index.rels.forEach((rel) => {
            if (!own(positions, rel.from) || !own(positions, rel.to)) { return; }
            const group = document.createElementNS(SVG_NS, 'g');
            group.setAttribute('class', 'erd-edge is-' + index.edgeKind(rel) +
                ' ' + (own(DELETE_CLASS, rel.onDelete) || 'is-restrict'));
            group.setAttribute('data-rel', rel.id);
            const parts = {
                hit: svgPath('erd-edge__hit'),
                line: svgPath('erd-edge__line'),
                childMark: svgPath('erd-edge__mark'),
                parentMark: svgPath('erd-edge__mark')
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

    function resetLayout() {
        saved[options.density] = {};
        computePositions();
        cardEls.forEach((card, id) => { placeCard(id); });
        updateEdges(Array.from(cardEls.keys()));
        fit();
        saveStored();
    }

    // ---- Pointer: pan, or drag one card --------------------------------------------------------
    // One listener set on the canvas, with pointer capture, so a drag keeps working when the
    // pointer leaves the card or the window. A press on a card header drags that card; a press
    // anywhere else (a card body included) pans.

    function onPointerDown(e) {
        if (gesture || !e.isPrimary || e.button !== 0) { return; }
        const head = e.target instanceof Element ? e.target.closest('.erd-card__head') : null;
        const id = head ? head.parentNode.getAttribute('data-table') : null;
        const card = id !== null && own(positions, id) ? id : null;
        const origin = card !== null ? positions[card] : view;
        gesture = {
            pointerId: e.pointerId,
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

    // Ends the gesture however it ended (released, cancelled by the browser, capture lost): a
    // moved card stays where it was dropped and is remembered.
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
    fit();

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', endGesture);
    canvas.addEventListener('pointercancel', endGesture);
    canvas.addEventListener('lostpointercapture', endGesture);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('scroll', onCanvasScroll);

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
