// CortexPlexus Graph Explorer

let cy = null;
let currentRepoId = null;
let selectedNodeId = null;
let focusedFqn = null; // P2b: currently focused node (null = full graph view)
let lastTotalMatchingNodes = 0;

const KIND_COLORS = {
    'class':            { color: '#4A90D9', shape: 'round-rectangle' },
    'method':           { color: '#50C878', shape: 'ellipse' },
    'interface':        { color: '#9B59B6', shape: 'diamond' },
    'property':         { color: '#E67E22', shape: 'ellipse' },
    'constructor':      { color: '#E74C3C', shape: 'ellipse' },
    'namespace':        { color: '#95A5A6', shape: 'round-rectangle' },
    'api_endpoint':     { color: '#1ABC9C', shape: 'hexagon' },
    'di_registration':  { color: '#F39C12', shape: 'ellipse' },
    'db_context':       { color: '#2C3E50', shape: 'round-rectangle' },
    'document':         { color: '#7F8C8D', shape: 'round-rectangle' },
    'section':          { color: '#BDC3C7', shape: 'round-rectangle' },
    'struct':           { color: '#3498DB', shape: 'round-rectangle' },
    'record':           { color: '#2980B9', shape: 'round-rectangle' },
    'function':         { color: '#27AE60', shape: 'ellipse' },
    'type':             { color: '#8E44AD', shape: 'diamond' },
};

const DEFAULT_STYLE = { color: '#778899', shape: 'ellipse' };

// --- Init ---

document.addEventListener('DOMContentLoaded', () => {
    initCytoscape();
    loadRepositories();
    bindEvents();
});

function initCytoscape() {
    const styleEntries = Object.entries(KIND_COLORS).map(([kind, s]) => ({
        selector: `node[kind="${kind}"]`,
        style: {
            'background-color': s.color,
            'shape': s.shape,
        }
    }));

    cy = cytoscape({
        container: document.getElementById('cy'),
        style: [
            {
                selector: 'node',
                style: {
                    'label': 'data(label)',
                    'font-size': '10px',
                    'color': '#ccc',
                    'text-valign': 'bottom',
                    'text-margin-y': 4,
                    'width': 'data(size)',
                    'height': 'data(size)',
                    'background-color': DEFAULT_STYLE.color,
                    'border-width': 0,
                    'text-max-width': '100px',
                    'text-wrap': 'ellipsis',
                }
            },
            ...styleEntries,
            {
                selector: 'node:selected',
                style: {
                    'border-width': 3,
                    'border-color': '#FFD700',
                }
            },
            {
                selector: 'node.highlighted',
                style: {
                    'border-width': 3,
                    'border-color': '#FF6347',
                }
            },
            {
                selector: 'node.dimmed',
                style: {
                    'opacity': 0.2,
                }
            },
            {
                selector: 'node.focus-root',
                style: {
                    'border-width': 4,
                    'border-color': '#FFD700',
                    'border-style': 'double',
                }
            },
            {
                selector: 'edge',
                style: {
                    'width': 1.5,
                    'line-color': '#3a3a5a',
                    'target-arrow-color': '#3a3a5a',
                    'target-arrow-shape': 'triangle',
                    'curve-style': 'bezier',
                    'arrow-scale': 0.8,
                }
            },
            {
                selector: 'edge.highlighted',
                style: {
                    'line-color': '#FFD700',
                    'target-arrow-color': '#FFD700',
                    'width': 2.5,
                }
            },
            {
                selector: 'edge.dimmed',
                style: {
                    'opacity': 0.1,
                }
            },
        ],
        layout: { name: 'preset' },
        minZoom: 0.1,
        maxZoom: 5,
    });

    cy.on('tap', 'node', onNodeClick);
    cy.on('dbltap', 'node', onNodeDblClick);
    cy.on('tap', (e) => {
        if (e.target === cy) clearSelection();
    });
}

function bindEvents() {
    document.getElementById('repo-select').addEventListener('change', onRepoChange);
    document.getElementById('search-btn').addEventListener('click', onSearch);
    document.getElementById('search-input').addEventListener('keydown', (e) => {
        if (e.key === 'Enter') onSearch();
    });
    document.getElementById('fit-btn').addEventListener('click', () => cy.fit(undefined, 40));
    document.getElementById('expand-btn').addEventListener('click', onExpandNeighbors);
    document.getElementById('focus-btn').addEventListener('click', () => {
        if (selectedNodeId) enterFocusMode(selectedNodeId);
    });
    document.getElementById('limit-select').addEventListener('change', () => {
        if (currentRepoId) loadGraph(currentRepoId);
    });
}

// --- Data Loading ---

async function loadRepositories() {
    try {
        const res = await fetch('/api/repositories');
        const repos = await res.json();
        const select = document.getElementById('repo-select');
        repos.forEach(r => {
            const opt = document.createElement('option');
            opt.value = r.id;
            opt.textContent = r.name;
            select.appendChild(opt);
        });
    } catch (err) {
        console.error('Failed to load repositories:', err);
    }
}

async function onRepoChange(e) {
    const repoId = e.target.value;
    if (!repoId) return;
    currentRepoId = repoId;
    exitFocusMode();
    await loadGraph(repoId);
}

function getSelectedLimit() {
    return parseInt(document.getElementById('limit-select').value, 10) || 500;
}

async function loadGraph(repoId) {
    cy.elements().remove();
    updateStats();
    focusedFqn = null;
    hideBreadcrumb();

    const limit = getSelectedLimit();
    try {
        const res = await fetch(`/api/graph/${repoId}?limit=${limit}`);
        const data = await res.json();
        lastTotalMatchingNodes = data.totalMatchingNodes || data.nodes.length;
        addGraphData(data);
        applyVisualWeight();
        runLayout();
        updateStats();
        buildKindFilters();
        updateStatusBanner(data.nodes.length, lastTotalMatchingNodes, limit);
    } catch (err) {
        console.error('Failed to load graph:', err);
    }
}

function addGraphData(data) {
    const existingIds = new Set(cy.nodes().map(n => n.id()));

    const newNodes = [];
    for (const node of data.nodes) {
        if (!existingIds.has(node.fqn)) {
            newNodes.push({
                group: 'nodes',
                data: {
                    id: node.fqn,
                    label: node.name,
                    kind: node.kind,
                    signature: node.signature,
                    filePath: node.filePath,
                    startLine: node.startLine,
                    size: 28,
                }
            });
        }
    }

    const existingEdges = new Set(cy.edges().map(e => e.id()));
    const newEdges = [];
    for (const edge of data.edges) {
        const edgeId = `${edge.fromFqn}|${edge.type}|${edge.toFqn}`;
        if (!existingEdges.has(edgeId)) {
            const srcExists = existingIds.has(edge.fromFqn) || newNodes.some(n => n.data.id === edge.fromFqn);
            const tgtExists = existingIds.has(edge.toFqn) || newNodes.some(n => n.data.id === edge.toFqn);
            if (srcExists && tgtExists) {
                newEdges.push({
                    group: 'edges',
                    data: {
                        id: edgeId,
                        source: edge.fromFqn,
                        target: edge.toFqn,
                        type: edge.type,
                    }
                });
            }
        }
    }

    cy.add([...newNodes, ...newEdges]);
}

function applyVisualWeight() {
    const nodes = cy.nodes();
    if (nodes.length === 0) return;

    let maxDeg = 1;
    nodes.forEach(n => {
        const deg = n.degree(false);
        if (deg > maxDeg) maxDeg = deg;
    });

    const minSize = 20;
    const maxSize = 60;
    nodes.forEach(n => {
        const deg = n.degree(false);
        const size = minSize + (deg / maxDeg) * (maxSize - minSize);
        n.data('size', Math.round(size));
    });
}

function runLayout() {
    cy.layout({
        name: 'cose',
        animate: true,
        animationDuration: 800,
        nodeRepulsion: function() { return 400000; },
        idealEdgeLength: function() { return 100; },
        edgeElasticity: function() { return 100; },
        gravity: 0.25,
        numIter: 300,
        randomize: true,
        padding: 40,
    }).run();
}

// --- Status Banner (P1) ---

function updateStatusBanner(shown, total, limit) {
    const banner = document.getElementById('status-banner');
    if (total <= 0) {
        banner.classList.add('hidden');
        return;
    }

    banner.classList.remove('hidden');
    const isTruncated = shown < total;

    if (focusedFqn) {
        banner.textContent = `Focus mode: ${shown} nodes around selected symbol.`;
        banner.classList.remove('truncated');
    } else if (isTruncated) {
        const kindsInfo = getKindFilterInfo();
        banner.textContent = `Showing ${shown} of ${total.toLocaleString()} matching nodes${kindsInfo}. Use the limit selector or check more kinds to see more.`;
        banner.classList.add('truncated');
    } else {
        banner.textContent = `Showing all ${total.toLocaleString()} matching nodes.`;
        banner.classList.remove('truncated');
    }
}

function getKindFilterInfo() {
    const checks = document.querySelectorAll('#kind-filters input[type="checkbox"]');
    if (checks.length === 0) return '';
    let checked = 0;
    checks.forEach(cb => { if (cb.checked) checked++; });
    if (checked === checks.length) return '';
    return ` (${checked} of ${checks.length} kinds checked)`;
}

// --- Node Interaction ---

function onNodeClick(e) {
    const node = e.target;
    selectedNodeId = node.id();

    cy.elements().removeClass('highlighted dimmed');
    const neighborhood = node.neighborhood().add(node);
    cy.elements().not(neighborhood).addClass('dimmed');
    neighborhood.edges().addClass('highlighted');
    node.select();

    showNodeDetail(node.data());
}

function onNodeDblClick(e) {
    const node = e.target;
    enterFocusMode(node.id());
}

function clearSelection() {
    selectedNodeId = null;
    cy.elements().removeClass('highlighted dimmed');
    document.getElementById('sidebar-empty').classList.remove('hidden');
    document.getElementById('sidebar-detail').classList.add('hidden');
}

function showNodeDetail(data) {
    document.getElementById('sidebar-empty').classList.add('hidden');
    document.getElementById('sidebar-detail').classList.remove('hidden');

    document.getElementById('detail-name').textContent = data.label;
    document.getElementById('detail-kind').textContent = data.kind;
    document.getElementById('detail-kind').style.background = (KIND_COLORS[data.kind] || DEFAULT_STYLE).color;
    document.getElementById('detail-fqn').textContent = data.id;
    document.getElementById('detail-file').textContent = data.filePath || '-';
    document.getElementById('detail-line').textContent = data.startLine || '-';
    document.getElementById('detail-sig').textContent = data.signature || '-';

    const edgeList = document.getElementById('detail-edges');
    edgeList.innerHTML = '';
    const node = cy.getElementById(data.id);
    const edges = node.connectedEdges();
    edges.forEach(edge => {
        const li = document.createElement('li');
        const isSource = edge.source().id() === data.id;
        const other = isSource ? edge.target() : edge.source();
        const direction = isSource ? '\u2192' : '\u2190';
        const typeSpan = document.createElement('span');
        typeSpan.className = 'edge-type';
        typeSpan.textContent = edge.data('type');
        li.appendChild(typeSpan);
        li.appendChild(document.createTextNode(` ${direction} ${other.data('label')}`));
        li.addEventListener('click', () => {
            other.emit('tap');
            cy.animate({ center: { eles: other }, duration: 300 });
        });
        edgeList.appendChild(li);
    });
}

async function onExpandNeighbors() {
    if (!selectedNodeId) return;
    try {
        const res = await fetch(`/api/graph/node?fqn=${encodeURIComponent(selectedNodeId)}&depth=1`);
        const data = await res.json();
        const countBefore = cy.nodes().length;
        addGraphData(data);
        const countAfter = cy.nodes().length;

        if (countAfter > countBefore) {
            const center = cy.getElementById(selectedNodeId);
            const pos = center.position();
            cy.nodes().forEach(n => {
                if (!n.position().x && !n.position().y) {
                    n.position({
                        x: pos.x + (Math.random() - 0.5) * 200,
                        y: pos.y + (Math.random() - 0.5) * 200,
                    });
                }
            });
            applyVisualWeight();
            runLayout();
        }
        updateStats();
    } catch (err) {
        console.error('Failed to expand neighbors:', err);
    }
}

// --- Focus Mode (P2b) ---

async function enterFocusMode(fqn) {
    focusedFqn = fqn;
    cy.elements().remove();
    clearSelection();

    try {
        const res = await fetch(`/api/graph/node?fqn=${encodeURIComponent(fqn)}&depth=2`);
        const data = await res.json();
        addGraphData(data);
        applyVisualWeight();
        runLayout();
        updateStats();

        const rootNode = cy.getElementById(fqn);
        if (rootNode.length) rootNode.addClass('focus-root');

        const shortName = fqn.split('.').pop() || fqn;
        showBreadcrumb(shortName, fqn);
        updateStatusBanner(data.nodes.length, data.nodes.length, 0);
    } catch (err) {
        console.error('Failed to enter focus mode:', err);
    }
}

function exitFocusMode() {
    focusedFqn = null;
    hideBreadcrumb();
}

function showBreadcrumb(name, fqn) {
    const bc = document.getElementById('breadcrumb');
    const text = document.getElementById('breadcrumb-text');
    bc.classList.remove('hidden');

    text.innerHTML = '';
    const allLink = document.createElement('a');
    allLink.textContent = 'All';
    allLink.addEventListener('click', () => {
        exitFocusMode();
        if (currentRepoId) loadGraph(currentRepoId);
    });
    text.appendChild(allLink);

    const arrow = document.createTextNode(' \u2192 ');
    text.appendChild(arrow);

    const current = document.createElement('span');
    current.textContent = name;
    current.title = fqn;
    text.appendChild(current);
}

function hideBreadcrumb() {
    document.getElementById('breadcrumb').classList.add('hidden');
}

// --- Search ---

async function onSearch() {
    const query = document.getElementById('search-input').value.trim();
    if (!query) return;

    try {
        const params = new URLSearchParams({ q: query, limit: '20' });
        if (currentRepoId) params.set('repoId', currentRepoId);

        const res = await fetch(`/api/search?${params}`);
        const results = await res.json();

        if (results.length === 0) return;

        cy.elements().removeClass('highlighted dimmed');
        const matchFqns = new Set(results.map(r => r.fqn));

        let foundAny = false;
        cy.nodes().forEach(n => {
            if (matchFqns.has(n.id())) {
                n.addClass('highlighted');
                foundAny = true;
            } else {
                n.addClass('dimmed');
            }
        });

        const highlighted = cy.nodes('.highlighted');
        if (highlighted.length > 0) {
            cy.animate({ fit: { eles: highlighted, padding: 60 }, duration: 500 });
        }
    } catch (err) {
        console.error('Search failed:', err);
    }
}

// --- Kind Filters ---

function buildKindFilters() {
    const container = document.getElementById('kind-filters');
    container.innerHTML = '';

    const kinds = new Set();
    cy.nodes().forEach(n => kinds.add(n.data('kind')));

    [...kinds].sort().forEach(kind => {
        const label = document.createElement('label');
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = true;
        cb.value = kind;
        cb.addEventListener('change', onKindFilterChange);
        label.appendChild(cb);
        label.append(kind);
        container.appendChild(label);
    });
}

function onKindFilterChange() {
    const checks = document.querySelectorAll('#kind-filters input[type="checkbox"]');
    const visible = new Set();
    checks.forEach(cb => { if (cb.checked) visible.add(cb.value); });

    cy.nodes().forEach(n => {
        if (visible.has(n.data('kind'))) {
            n.style('display', 'element');
        } else {
            n.style('display', 'none');
        }
    });

    const visibleNodes = cy.nodes().filter(n => n.style('display') !== 'none').length;
    updateStatusBanner(visibleNodes, lastTotalMatchingNodes, getSelectedLimit());
}

// --- Stats ---

function updateStats() {
    const nodeCount = cy.nodes().length;
    const edgeCount = cy.edges().length;
    document.getElementById('stats-text').textContent = `Nodes: ${nodeCount} | Edges: ${edgeCount}`;
}
