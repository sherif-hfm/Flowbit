/* Shared static markup for standalone and inline hosts. */
window.FlowbitEditorTemplate = String.raw`<header class="app-toolbar" aria-label="Workflow editor commands">
  <a class="editor-back hosted-only" href="workflows" aria-label="Back to Workflows" title="Back to Workflows"><span aria-hidden="true">←</span><span class="back-label">Workflows</span></a>
  <div class="toolbar-identity">
    <div class="app-brand">
      <div class="app-mark" aria-hidden="true"><svg viewBox="0 0 48 48"><rect width="48" height="48" rx="12" fill="#2563eb"/><path d="M14 35V13H35M14 24H29" fill="none" stroke="#fff" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round"/><g fill="#fff"><rect x="10" y="9" width="8" height="8" rx="2.5"/><rect x="10" y="31" width="8" height="8" rx="2.5"/></g><g fill="#bfdbfe"><rect x="31" y="9" width="8" height="8" rx="2.5"/><rect x="25" y="20" width="8" height="8" rx="2.5"/></g></svg></div>
      <h1 aria-label="Flowbit — Workflow Editor">Flowbit<small>Workflow Editor</small></h1>
    </div>
    <div class="toolbar-group toolbar-menu-group" role="group" aria-label="Editor menus">
    <details id="fileMenu" class="toolbar-menu">
      <summary id="fileMenuSummary" title="File actions" aria-label="File actions">
        <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
          <path d="M4 5h6l2 2h8v12H4z"/>
        </svg>
        <span class="button-label">File</span>
        <svg class="toolbar-menu-caret" viewBox="0 0 12 12" aria-hidden="true"><path d="m2 4 4 4 4-4"/></svg>
      </summary>
      <div class="toolbar-menu-popover" role="group" aria-label="File actions">
        <button id="newBtn" type="button" title="Create a new workflow">
          <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h9l4 4v14H6z"/><path d="M14 3v5h5M9 14h6M12 11v6"/></svg>
          <span class="button-label">New workflow</span>
        </button>
        <button id="loadBtn" type="button" title="Load workflow JSON">
          <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 19h16V8h-8l-2-3H4z"/><path d="M12 10v6M9 13l3 3 3-3"/></svg>
          <span class="button-label">Load JSON</span>
        </button>
        <button id="saveBtn" type="button" title="Save workflow JSON">
          <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 3h12l2 2v16H5z"/><path d="M8 3v6h8V3M8 21v-7h8v7"/></svg>
          <span class="button-label">Save JSON</span>
        </button>
      </div>
    </details>
    <details id="editMenu" class="toolbar-menu">
      <summary id="editMenuSummary" title="Edit history" aria-label="Edit history">
        <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
          <path d="M9 7 4 12l5 5M5 12h8a6 6 0 0 1 6 6"/>
        </svg>
        <span class="button-label">Edit</span>
        <svg class="toolbar-menu-caret" viewBox="0 0 12 12" aria-hidden="true"><path d="m2 4 4 4 4-4"/></svg>
      </summary>
      <div class="toolbar-menu-popover" role="group" aria-label="Edit history actions">
        <button id="undoBtn" type="button" title="Undo (Ctrl+Z)" aria-label="Undo" disabled>
          <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M9 7 4 12l5 5M5 12h8a6 6 0 0 1 6 6"/></svg>
          <span class="button-label">Undo</span>
        </button>
        <button id="redoBtn" type="button" title="Redo (Ctrl+Y or Ctrl+Shift+Z)" aria-label="Redo" disabled>
          <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="m15 7 5 5-5 5M19 12h-8a6 6 0 0 0-6 6"/></svg>
          <span class="button-label">Redo</span>
        </button>
      </div>
    </details>
    <details id="viewMenu" class="toolbar-menu toolbar-menu-end">
      <summary id="viewMenuSummary" title="View options" aria-label="View options">
        <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
          <path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z"/><circle cx="12" cy="12" r="2.5"/>
        </svg>
        <span class="button-label">View</span>
        <svg class="toolbar-menu-caret" viewBox="0 0 12 12" aria-hidden="true"><path d="m2 4 4 4 4-4"/></svg>
      </summary>
      <div class="toolbar-menu-popover view-menu-popover" role="group" aria-label="View options">
        <label class="toolbar-menu-field" for="labelModeSelect">
          <span>Flow label visibility</span>
          <select id="labelModeSelect" class="toolbar-select"
                  title="Flow label visibility" aria-label="Flow label visibility">
            <option value="smart">Labels: Smart</option>
            <option value="all">Labels: Show</option>
            <option value="off">Labels: Hide</option>
          </select>
        </label>
        <label class="toolbar-grid-toggle" for="snapToGridInput"
               title="Align nodes to the nearest grid point while dragging">
          <input id="snapToGridInput" type="checkbox" />
          <span>Snap nodes to grid</span>
        </label>
        <label class="toolbar-grid-size" for="gridSizeInput"
               title="Grid spacing in workflow units">
          <span>Grid size</span>
          <input id="gridSizeInput" type="number" min="4" max="200" step="1"
                 value="24" inputmode="numeric" aria-label="Grid size" />
          <span aria-hidden="true">px</span>
        </label>
        <label class="toolbar-opacity" for="traceOpacityInput"
               title="Opacity of nodes and flows outside the active path">
          <span>Other items</span>
          <input id="traceOpacityInput" type="range" min="10" max="100" step="5"
                 value="100" aria-label="Untraced item opacity" />
          <output id="traceOpacityValue" for="traceOpacityInput">100%</output>
        </label>
        <div class="toolbar-menu-divider" aria-hidden="true"></div>
        <button id="themeToggleBtn" class="theme-toggle" type="button"
                title="Switch to light theme" aria-label="Switch to light theme">
          <svg class="button-icon theme-icon-moon" viewBox="0 0 24 24" aria-hidden="true">
            <path d="M20 15.2A8.5 8.5 0 0 1 8.8 4 8.5 8.5 0 1 0 20 15.2Z"/>
          </svg>
          <svg class="button-icon theme-icon-sun" viewBox="0 0 24 24" aria-hidden="true">
            <circle cx="12" cy="12" r="3.5"/>
            <path d="M12 2v2.2M12 19.8V22M4.9 4.9l1.6 1.6M17.5 17.5l1.6 1.6M2 12h2.2M19.8 12H22M4.9 19.1l1.6-1.6M17.5 6.5l1.6-1.6"/>
          </svg>
          <span id="themeToggleLabel" class="button-label">Light theme</span>
        </button>
      </div>
    </details>
  </div>
    <input id="wfName" class="wf-name" placeholder="Workflow name" aria-label="Workflow name" />
  </div>
  <div class="editor-host-actions hosted-only">
    <span class="editor-save-context"><span id="editor-version"></span><span id="editor-status" role="status"></span></span>
    <button id="save-workflow-version" class="primary editor-save" type="button" disabled>
      <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 3h12l4 4v14H3V3h2Z"/><path d="M7 3v6h10V3M7 21v-8h10v8"/></svg>
      <span>Save new version</span>
    </button>
  </div>
  <input id="fileInput" type="file" accept="application/json,.json" hidden />
</header>
<div class="editor-feedback hosted-only" aria-live="polite" hidden>
  <div id="editor-error" role="alert" hidden></div>
  <div id="editor-notice" role="status" hidden></div>
</div>

<div class="editor-workspace">
  <div class="canvas-wrap">
    <svg id="svg" role="application" aria-label="Workflow diagram canvas" data-active-tool="pan">
      <defs>
        <pattern id="gridPattern" width="24" height="24" patternUnits="userSpaceOnUse">
          <circle cx="1" cy="1" r="1" fill="var(--grid-dot)" />
        </pattern>
        <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5"
                markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M0,0 L10,5 L0,10 z" fill="var(--edge-stroke)" />
        </marker>
        <marker id="arrowSel" viewBox="0 0 10 10" refX="9" refY="5"
                markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M0,0 L10,5 L0,10 z" fill="var(--edge-selected)" />
        </marker>
        <marker id="arrowPreview" viewBox="0 0 10 10" refX="9" refY="5"
                markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M0,0 L10,5 L0,10 z" fill="var(--connect-stroke)" />
        </marker>
        <marker id="arrowError" viewBox="0 0 10 10" refX="9" refY="5"
                markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M0,0 L10,5 L0,10 z" fill="var(--edge-error)" />
        </marker>
        <symbol id="icon-user" viewBox="0 0 24 24"><circle cx="12" cy="8" r="3.5" fill="none" stroke="currentColor" stroke-width="2"/><path d="M5 20c.8-4 3-6 7-6s6.2 2 7 6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></symbol>
        <symbol id="icon-task" viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="16" rx="3" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="m8 12 2.2 2.2L16 8.8" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></symbol>
        <symbol id="icon-service" viewBox="0 0 24 24"><circle cx="12" cy="12" r="3" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></symbol>
        <symbol id="icon-script" viewBox="0 0 24 24"><path d="m9 7-5 5 5 5M15 7l5 5-5 5M13 5l-2 14" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></symbol>
        <symbol id="icon-start" viewBox="0 0 24 24"><path d="m9 7 8 5-8 5z" fill="currentColor"/></symbol>
        <symbol id="icon-end" viewBox="0 0 24 24"><rect x="7" y="7" width="10" height="10" rx="1.5" fill="currentColor"/></symbol>
        <symbol id="icon-gateway" viewBox="0 0 24 24"><path d="m7 7 10 10M17 7 7 17" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/></symbol>
        <symbol id="icon-parallel-gateway" viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round"/></symbol>
        <symbol id="icon-inclusive-gateway" viewBox="0 0 24 24"><circle cx="12" cy="12" r="7" fill="none" stroke="currentColor" stroke-width="2.5"/></symbol>
        <symbol id="icon-complex-gateway" viewBox="0 0 24 24"><path d="M12 4v16M5.1 8l13.8 8M5.1 16 18.9 8" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/></symbol>
        <symbol id="icon-scoped-interrupt" viewBox="0 0 24 24"><path d="M12 3v6M12 15v6M3 12h6M15 12h6M8.5 8.5l7 7" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round"/></symbol>
        <symbol id="icon-terminate" viewBox="0 0 24 24"><circle cx="12" cy="12" r="7.5" fill="currentColor"/></symbol>
        <symbol id="icon-message" viewBox="0 0 24 24"><rect x="3" y="6" width="18" height="12" rx="2" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="m4 8 8 6 8-6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/></symbol>
        <symbol id="icon-timer" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M12 7v5l3.5 2M9 2h6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></symbol>
        <symbol id="icon-conditional" viewBox="0 0 24 24"><path d="M6.5 3.5h8l3 3v14h-11zM14.5 3.5v4h3" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/><path d="M9 11h6M9 14h6M9 17h4.5" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/></symbol>
        <symbol id="icon-error-throw" viewBox="0 0 24 24"><path d="M5 20 8.5 4 12 9 19 4 15.5 20 12 15z" fill="currentColor" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/></symbol>
        <symbol id="icon-error-catch" viewBox="0 0 24 24"><path d="M5 20 8.5 4 12 9 19 4 15.5 20 12 15z" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/></symbol>
        <symbol id="icon-assignee" viewBox="0 0 24 24"><circle cx="9" cy="8" r="3" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M3.5 19c.6-3.3 2.4-5 5.5-5 1.4 0 2.6.3 3.5 1M15 16l2 2 4-5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></symbol>
      </defs>
      <rect id="gridSurface" x="-100000" y="-100000" width="200000" height="200000" fill="url(#gridPattern)"></rect>
      <g id="lanes"></g>
      <g id="edges"></g>
      <g id="connectionPreview"></g>
      <g id="nodes"></g>
      <g id="selectionOverlay" aria-hidden="true"></g>
    </svg>
    <section id="authoringPalette" class="authoring-palette"
             aria-label="Workflow authoring tools">
      <button id="authoringPaletteHandle"
              class="authoring-palette-handle tool-menu-summary"
              type="button" data-active-tool="pan"
              aria-controls="authoringPaletteContent" aria-expanded="false"
              title="Show authoring tools; current editor tool: Pan"
              aria-label="Show authoring tools; current editor tool: Pan">
        <svg class="button-icon tool-summary-select" viewBox="0 0 24 24" aria-hidden="true">
          <path d="m5 3 6.8 16 2.1-6.1L20 10.8 5 3Z"/>
        </svg>
        <svg class="button-icon tool-summary-rectangle" viewBox="0 0 24 24" aria-hidden="true">
          <rect x="4" y="4" width="16" height="16" rx="1" stroke-dasharray="3 2"/>
          <path d="m8 8 4.5 10 1.5-4 4-1.5L8 8Z"/>
        </svg>
        <svg class="button-icon tool-summary-pan" viewBox="0 0 24 24" aria-hidden="true">
          <path d="M8.5 11V5.5a1.5 1.5 0 0 1 3 0V10M11.5 10V4.5a1.5 1.5 0 0 1 3 0V10M14.5 10V6a1.5 1.5 0 0 1 3 0v5M8.5 9.5a1.5 1.5 0 0 0-3 0v4.2c0 4.6 2.6 7.3 6.8 7.3h.7c4.4 0 6.5-3.1 6.5-7.2V10a1.5 1.5 0 0 0-3 0v1"/>
        </svg>
      </button>
      <div id="authoringPaletteContent" class="authoring-palette-content"
           role="group" aria-label="Workflow authoring actions">
        <div class="authoring-palette-header">
          <div class="authoring-palette-copy">
            <span class="authoring-palette-title">Tools</span>
          </div>
          <button id="authoringPalettePin" class="dock-pin" type="button"
                  title="Pin authoring tools open" aria-label="Pin authoring tools open"
                  aria-pressed="false">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="m9 4 6 0-.8 5 3.3 3.3v1.2h-11v-1.2L9.8 9 9 4Z"></path>
              <path d="M12 13.5V21"></path>
            </svg>
          </button>
        </div>
        <div class="authoring-palette-section" role="group" aria-label="Add workflow items">
          <span class="authoring-palette-section-label" aria-hidden="true">Add</span>
          <button id="addStepBtn" type="button"
                  title="Add a workflow node" aria-label="Add Node">
            <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>
            <span class="button-label">Add Node</span>
          </button>
          <button id="addPhaseBtn" type="button"
                  title="Add a swimlane" aria-label="Add Lane">
            <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 10h18"/></svg>
            <span class="button-label">Add Lane</span>
          </button>
          <button id="connectBtn" type="button"
                  title="Connect two nodes with a sequence flow"
                  aria-label="Connect Flow" aria-pressed="false">
            <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="2.5"/><circle cx="19" cy="12" r="2.5"/><path d="M7.5 12h9M14 9l3 3-3 3"/></svg>
            <span class="button-label">Connect Flow</span>
          </button>
        </div>
        <div class="authoring-palette-divider" aria-hidden="true"></div>
        <div class="authoring-palette-section" role="group" aria-label="Choose editor tool">
          <span class="authoring-palette-section-label" aria-hidden="true">Tool</span>
          <button id="selectToolBtn" type="button"
                  title="Select and move workflow items (V)" aria-label="Select and move workflow items"
                  aria-pressed="false">
            <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
              <path d="m5 3 6.8 16 2.1-6.1L20 10.8 5 3Z"/>
            </svg><span class="button-label">Select and move</span>
          </button>
          <button id="rectangleSelectToolBtn" type="button"
                  title="Rectangle select multiple nodes (R)" aria-label="Rectangle select multiple nodes"
                  aria-pressed="false">
            <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
              <rect x="4" y="4" width="16" height="16" rx="1" stroke-dasharray="3 2"/>
              <path d="m8 8 4.5 10 1.5-4 4-1.5L8 8Z"/>
            </svg><span class="button-label">Rectangle select</span>
          </button>
          <button id="panToolBtn" class="active" type="button"
                  title="Pan the workflow canvas without moving items (H)" aria-label="Pan workflow canvas"
                  aria-pressed="true">
            <svg class="button-icon" viewBox="0 0 24 24" aria-hidden="true">
              <path d="M8.5 11V5.5a1.5 1.5 0 0 1 3 0V10M11.5 10V4.5a1.5 1.5 0 0 1 3 0V10M14.5 10V6a1.5 1.5 0 0 1 3 0v5M8.5 9.5a1.5 1.5 0 0 0-3 0v4.2c0 4.6 2.6 7.3 6.8 7.3h.7c4.4 0 6.5-3.1 6.5-7.2V10a1.5 1.5 0 0 0-3 0v1"/>
            </svg><span class="button-label">Pan canvas</span>
          </button>
        </div>
      </div>
    </section>
    <div class="hint" id="hint" role="status" aria-live="polite" hidden></div>
    <div class="diagram-tools" id="diagramTools" aria-label="Diagram navigation">
      <button id="diagramToolsHandle" class="diagram-tools-handle" type="button"
              title="Show diagram tools" aria-label="Show diagram tools">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <circle cx="10.5" cy="10.5" r="5.5"></circle>
          <path d="m15 15 4.5 4.5M4 5h2M18 5h2M5 19h2"></path>
        </svg>
      </button>
      <div class="diagram-tools-content">
        <div class="diagram-search">
          <div class="diagram-search-row">
            <label class="sr-only" for="diagramSearchInput">Search workflow diagram</label>
            <input id="diagramSearchInput" type="search" autocomplete="off"
                   placeholder="Search nodes, flows, lanes…  (/)"
                   aria-controls="diagramSearchResults" aria-expanded="false" />
            <button id="diagramSearchClear" class="diagram-search-clear" type="button"
                    title="Clear diagram search" aria-label="Clear diagram search">×</button>
            <button id="diagramToolsPin" class="dock-pin" type="button"
                    title="Pin diagram tools open" aria-label="Pin diagram tools open"
                    aria-pressed="false">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="m9 4 6 0-.8 5 3.3 3.3v1.2h-11v-1.2L9.8 9 9 4Z"></path>
                <path d="M12 13.5V21"></path>
              </svg>
            </button>
          </div>
          <div id="diagramSearchResults" class="diagram-search-results"
               role="listbox" aria-label="Diagram search results" hidden></div>
        </div>
        <div class="diagram-nav">
          <div class="trace-modes" role="group" aria-label="Trace selected workflow path">
            <button type="button" data-trace-mode="local" aria-pressed="true">Local</button>
            <button type="button" data-trace-mode="upstream" aria-pressed="false">Upstream</button>
            <button type="button" data-trace-mode="downstream" aria-pressed="false">Downstream</button>
            <button type="button" data-trace-mode="route" aria-pressed="false">Route</button>
          </div>
          <button id="fitSelectionBtn" type="button" title="Fit the selected item">Fit selection</button>
          <button id="fitLaneBtn" type="button" title="Fit the selected node's lane">Fit lane</button>
        </div>
      </div>
    </div>
    <div class="canvas-controls" aria-label="Canvas zoom controls">
      <button id="zoomOutBtn" type="button" title="Zoom out" aria-label="Zoom out">−</button>
      <button id="zoomResetBtn" type="button" class="zoom-readout" title="Reset zoom to 100%">100%</button>
      <button id="zoomInBtn" type="button" title="Zoom in" aria-label="Zoom in">+</button>
      <button id="fitViewBtn" type="button" class="fit-view" title="Fit the workflow to the canvas">Fit</button>
    </div>
  </div>

  <div class="inspector-dock" id="inspectorDock">
    <div class="resizer" id="sidebar-resizer" role="separator"
         aria-orientation="vertical" aria-label="Resize inspector" tabindex="0"></div>
    <aside id="inspectorPanel" aria-label="Properties inspector">
      <div class="inspector-header">
        <span class="inspector-header-label">Inspector</span>
        <button id="inspectorPin" class="dock-pin" type="button"
                title="Pin inspector open" aria-label="Pin inspector open"
                aria-pressed="false">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="m9 4 6 0-.8 5 3.3 3.3v1.2h-11v-1.2L9.8 9 9 4Z"></path>
            <path d="M12 13.5V21"></path>
          </svg>
        </button>
      </div>
      <div class="inspector-content" id="inspector"></div>
    </aside>
    <span class="inspector-peek-label" aria-hidden="true">Inspector</span>
  </div>
</div>

<!-- JS Editor Modal Overlay -->
<div id="js-editor-modal" class="modal-overlay" style="display: none;">
  <div class="modal-container">
    <div class="modal-header">
      <h3 id="js-editor-title">Edit JavaScript Code</h3>
      <button type="button" class="close-btn" data-editor-action="close-script">&times;</button>
    </div>
    <div class="modal-body js-editor-layout">
      <!-- Code Editor Column -->
      <div class="js-code-column">
        <textarea id="js-editor-textarea" class="modal-textarea" spellcheck="false" placeholder="// Write your JavaScript code here..."></textarea>
      </div>
      <!-- Variables Panel Column -->
      <div class="js-variables-panel">
        <div class="js-variables-scroll">
          <!-- User Variables Section -->
          <div>
            <h4 style="margin: 0 0 4px 0; font-size: 11px; text-transform: uppercase; color: var(--muted); letter-spacing: 0.05em;">Available Variables</h4>
            <p class="empty-note" id="js-editor-variables-hint" style="margin: 0 0 8px 0; font-size: 12px; line-height: 1.4;"></p>
            <div id="js-editor-variables-list" style="display: flex; flex-direction: column; gap: 8px;"></div>
          </div>
          <!-- System Variables Section -->
          <div>
            <h4 style="margin: 0 0 8px 0; font-size: 11px; text-transform: uppercase; color: var(--muted); letter-spacing: 0.05em; border-top: 1px solid var(--border); padding-top: 12px;">System Context</h4>
            <div id="js-editor-sys-list" style="display: flex; flex-direction: column; gap: 8px;"></div>
          </div>
        </div>
      </div>
    </div>
    <div class="modal-footer">
      <button type="button" data-editor-action="close-script">Cancel</button>
      <button type="button" class="primary" data-editor-action="save-script">Save & Close</button>
    </div>
  </div>
</div>

<!-- Save Validation Modal Overlay -->
<div id="validation-modal" class="modal-overlay" style="display: none;" role="dialog" aria-modal="true" aria-labelledby="validation-title">
  <div class="modal-container validation-modal-container">
    <div class="modal-header">
      <h3 id="validation-title">Workflow cannot be saved</h3>
      <button id="validation-close" type="button" class="close-btn" data-editor-action="close-validation">&times;</button>
    </div>
    <div class="modal-body">
      <p class="empty-note" style="margin-top: 0;">Fix the following definition errors and save again.</p>
      <ol id="validation-errors" class="validation-list"></ol>
    </div>
    <div class="modal-footer">
      <button type="button" class="primary" data-editor-action="close-validation">Close</button>
    </div>
  </div>
</div>`;
