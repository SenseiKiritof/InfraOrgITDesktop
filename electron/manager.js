const api = window.desktopManager;

const els = {
  tabs: document.querySelector("#tabs"),
  tabAdd: document.querySelector("#tab-add"),
  home: document.querySelector("#home-button"),
  paletteTrigger: document.querySelector("#palette-trigger"),
  paletteShortcut: document.querySelector("[data-shortcut]"),
  tunnelToggle: document.querySelector("#tunnel-toggle"),
  tunnelBadge: document.querySelector("#tunnel-badge"),
  updateButton: document.querySelector("#update-button"),
  updateLabel: document.querySelector("#update-label"),
  settingsButton: document.querySelector("#settings-button"),
  landing: document.querySelector("#landing"),
  summary: document.querySelector("#summary"),
  downloads: document.querySelector("#downloads"),
  instances: document.querySelector("#instances"),
  empty: document.querySelector("#empty"),
  newInstance: document.querySelector("#new-instance"),
  newInstanceCard: document.querySelector("#new-instance-card"),
  slot: document.querySelector("#instance-slot"),
  stageBackdrop: document.querySelector("#stage-backdrop"),
  stageName: document.querySelector("#stage-backdrop-name"),
  drawer: document.querySelector("#drawer"),
  drawerBody: document.querySelector("#drawer-body"),
  drawerSearch: document.querySelector("#drawer-search"),
  drawerClose: document.querySelector("#drawer-close"),
  drawerRefresh: document.querySelector("#drawer-refresh"),
  palette: document.querySelector("#palette"),
  paletteInput: document.querySelector("#palette-input"),
  paletteList: document.querySelector("#palette-list"),
  paletteHint: document.querySelector("#palette-hint"),
  modal: document.querySelector("#modal"),
  modalDialog: document.querySelector("#modal-dialog"),
  modalEyebrow: document.querySelector("#modal-eyebrow"),
  modalTitle: document.querySelector("#modal-title"),
  modalBody: document.querySelector("#modal-body"),
  modalFooter: document.querySelector("#modal-footer"),
  modalClose: document.querySelector("#modal-close"),
  toast: document.querySelector("#toast"),
};

const ENV_COLORS = [
  "#5eead4",
  "#6ea8fe",
  "#f6c177",
  "#c4a7ff",
  "#6ee7a8",
  "#ff9f7a",
  "#93c5fd",
  "#f0abfc",
];

let state = null;
let updateState = null;
let drawerOpen = false;
let palette = { open: false, index: 0, query: "", commands: [] };
let modalToken = 0;
let toastTimer = null;
let lastDrawerKey = "";
let drawerDraft = null;
let drawerSearch = "";
const collapsedServers = new Set();
const collapsedInstances = new Set();
let lastBounds = "";

const shellLocale = () => window.desktopI18n?.locale || "en-GB";

const isMac = api?.platform === "darwin";
const modifierLabel = isMac ? "⌘" : "Ctrl";
els.paletteShortcut.textContent = isMac ? "⌘K" : "Ctrl K";

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function button(label, className, onClick, title) {
  const node = el("button", className, label);
  node.type = "button";
  if (title) node.title = title;
  node.addEventListener("click", (event) => {
    event.stopPropagation();
    onClick(event);
  });
  return node;
}

function envColor(environment) {
  const value = String(environment || "").toLowerCase();
  if (value.includes("prod")) return "#ff8f8f";
  if (value.includes("stag")) return "#f6c177";
  if (value.includes("test") || value.includes("lab")) return "#c4a7ff";
  if (value.includes("locale") || value.includes("local")) return "#5eead4";
  let hash = 0;
  for (const char of value) hash = (hash * 31 + char.charCodeAt(0)) | 0;
  return ENV_COLORS[Math.abs(hash) % ENV_COLORS.length];
}

function formatLastUsed(value) {
  if (!value) return "Mai aperta";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Mai aperta";
  const now = new Date();
  const minutes = Math.floor((now.getTime() - date.getTime()) / 60000);
  if (minutes < 1) return "Adesso";
  if (minutes < 60) return `${minutes} min fa`;
  const sameDay = date.toDateString() === now.toDateString();
  if (sameDay)
    return `Oggi ${date.toLocaleTimeString(shellLocale(), {
      hour: "2-digit",
      minute: "2-digit",
    })}`;
  return date.toLocaleDateString(shellLocale(), {
    day: "2-digit",
    month: "short",
  });
}

function formatCachedAt(value) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleString(shellLocale(), {
    day: "2-digit",
    month: "short",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function plural(count, singular, pluralWord) {
  return `${count} ${count === 1 ? singular : pluralWord}`;
}

function toast(message, tone = "info") {
  els.toast.textContent = message;
  els.toast.dataset.tone = tone;
  els.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    els.toast.hidden = true;
  }, 4200);
}

function instanceByUrl(url) {
  return state?.instances.find((instance) => instance.url === url) || null;
}

function activeTab() {
  return state?.tabs.find((tab) => tab.url === state.activeUrl) || null;
}

function isViewHidden() {
  return !els.palette.hidden || !els.modal.hidden || Boolean(activeTab()?.loading);
}

function sendBounds() {
  const rect = els.slot.getBoundingClientRect();
  const visible = Boolean(state?.activeUrl) && !isViewHidden();
  const payload = {
    x: Math.round(rect.left),
    y: Math.round(rect.top),
    width: Math.round(rect.width),
    height: Math.round(rect.height),
    visible,
  };
  const key = JSON.stringify(payload);
  if (key === lastBounds) return;
  lastBounds = key;
  api.setContentBounds(payload);
}

function scheduleBounds() {
  requestAnimationFrame(() => {
    sendBounds();
    updateStage();
  });
}

function updateStage() {
  const tab = activeTab();
  const hidden = Boolean(state?.activeUrl) && (isViewHidden() || tab?.loading);
  els.stageBackdrop.hidden = !hidden;
  if (hidden && tab) els.stageName.textContent = tab.name;
  document.body.dataset.drawer = drawerOpen ? "open" : "closed";
  els.drawer.setAttribute("aria-hidden", drawerOpen ? "false" : "true");
  els.tunnelToggle.setAttribute("aria-expanded", drawerOpen ? "true" : "false");
  sendBounds();
}

function render() {
  if (!state) return;
  document.documentElement.dataset.theme = state.theme.resolved;
  document.documentElement.dataset.platform = state.platform;
  document.body.dataset.view = state.activeUrl ? "instance" : "landing";
  renderSummary();
  renderDownloads();
  renderCards();
  renderTabs();
  renderDrawer();
  renderChrome();
  renderUpdate();
  scheduleBounds();
  if (palette.open) renderPaletteItems();
}

function renderChrome() {
  const active = state.tunnels.filter((tunnel) => tunnel.status === "Active");
  els.tunnelBadge.hidden = active.length === 0;
  els.tunnelBadge.textContent = String(active.length);
  els.home.classList.toggle("active", !state.activeUrl);
}

function renderUpdate() {
  const next = state?.updates || updateState;
  updateState = next;
  if (!next || !["update", "installing"].includes(next.status)) {
    els.updateButton.hidden = true;
    return;
  }
  els.updateButton.hidden = false;
  els.updateButton.disabled = next.status === "installing";
  els.updateLabel.textContent =
    next.status === "installing"
      ? "Aggiornamento…"
      : next.latestVersion
        ? `Aggiorna ${next.latestVersion}`
        : "Aggiorna";
  els.updateButton.title = next.message || "";
}

function renderSummary() {
  els.summary.replaceChildren();
  const activeTunnels = state.tunnels.filter((t) => t.status === "Active").length;
  const ready = state.instances.filter(
    (instance) => instance.auth?.status === "ready",
  ).length;
  const activeDownloads = (state.downloads || []).filter(
    (item) => item.state === "progressing",
  ).length;
  const chips = [
    ["Istanze", state.instances.length, "Istanza"],
    ["Tab aperte", state.tabs.length, "Tab aperta"],
    ["Tunnel attivi", activeTunnels, "Tunnel attivo"],
    ["Sessioni", ready, "Sessione"],
  ];
  if (activeDownloads > 0)
    chips.push(["Download attivi", activeDownloads, "Download attivo"]);
  for (const [label, value, singular] of chips) {
    const chip = el("span", "summary-chip");
    if (label === "Tunnel attivi" && activeTunnels > 0)
      chip.append(el("span", "pulse"));
    chip.append(
      el("strong", "", String(value)),
      document.createTextNode(value === 1 ? singular : label),
    );
    els.summary.append(chip);
  }
}

function downloadLabel(item) {
  if (item.state === "completed") return "Completato";
  if (item.state === "cancelled") return "Annullato";
  if (item.state === "failed") return "Non riuscito";
  const total = Number(item.totalBytes) || 0;
  const received = Number(item.receivedBytes) || 0;
  if (total <= 0) return "In corso";
  return `In corso · ${Math.min(100, Math.round((received / total) * 100))}%`;
}

function renderDownloads() {
  const items = state.downloads || [];
  els.downloads.hidden = items.length === 0;
  els.downloads.replaceChildren();
  if (items.length === 0) return;

  els.downloads.append(el("h2", "downloads-title", "Download"));
  for (const item of items) {
    const row = el("div", "download-row");
    const main = el("div", "download-main");
    main.append(el("span", "download-name", item.fileName));
    main.append(el("span", "download-meta", downloadLabel(item)));

    const bar = el("div", "download-bar");
    const total = Number(item.totalBytes) || 0;
    const received = Number(item.receivedBytes) || 0;
    const fill = el("span", "download-fill");
    if (total > 0) {
      fill.style.width = `${Math.min(100, Math.round((received / total) * 100))}%`;
    } else {
      fill.dataset.indeterminate = "true";
    }
    bar.append(fill);
    main.append(bar);
    row.append(main);
    els.downloads.append(row);
  }
}

function renderCards() {
  els.instances.replaceChildren();
  els.empty.hidden = state.instances.length > 0;

  for (const instance of state.instances) {
    const card = el("article", "instance-card");
    card.style.setProperty("--env", envColor(instance.environment));
    card.dataset.active = instance.active ? "true" : "false";

    const top = el("div", "card-top");
    const titleWrap = el("div", "card-title-row");
    titleWrap.append(el("span", "env-dot"));
    titleWrap.append(el("h2", "card-title", instance.name));
    top.append(titleWrap);

    const chips = el("div", "card-chips");
    chips.style.display = "flex";
    chips.style.gap = "5px";
    chips.style.flexWrap = "wrap";
    const env = el("span", "chip env", instance.environment || "Ambiente");
    env.style.setProperty("--env", envColor(instance.environment));
    chips.append(env);
    if (instance.openAtStartup)
      chips.append(el("span", "chip startup", "Apri all’avvio"));
    if (instance.offline) {
      const offline = el("span", "chip offline", "Offline");
      offline.title = `Dati locali del ${formatCachedAt(instance.cachedAt)}`;
      chips.append(offline);
    }
    top.append(chips);
    card.append(top);

    const url = el("p", "card-url", instance.url);
    url.title = instance.url;
    card.append(url);

    const meta = el("div", "card-meta");
    const serverValue =
      instance.serverCount === null || instance.serverCount === undefined
        ? "—"
        : String(instance.serverCount);
    meta.append(metaItem("Server", serverValue));
    meta.append(metaItem("Tunnel attivi", String(instance.tunnelCount || 0)));
    meta.append(metaItem("Connessioni", policyLabel(instance.connectionPolicy)));
    meta.append(metaItem("Ultimo uso", formatLastUsed(instance.lastUsedAt), true));
    if (instance.offline)
      meta.append(metaItem("Dati locali", formatCachedAt(instance.cachedAt), true));
    if (instance.pendingEvents > 0)
      meta.append(
        metaItem("Da sincronizzare", String(instance.pendingEvents), true),
      );
    card.append(meta);

    const actions = el("div", "card-actions");
    const open = button(
      instance.active ? "Apri" : "Apri",
      "primary",
      () => void openInstance(instance.url),
    );
    open.disabled = false;
    const manage = button("Gestisci", "secondary", () =>
      openManage(instance.url),
    );
    const verify = button("Verifica", "ghost", () => openLocalCheck(instance));
    const sync =
      instance.pendingEvents > 0
        ? button("Sincronizza", "ghost", () => void syncOutbox(instance))
        : null;
    verify.title = "Verifica locale";
    verify.setAttribute("aria-label", `Verifica locale ${instance.name}`);
    const spacer = el("span", "spacer");
    const remove = button("Rimuovi", "ghost danger-link", () =>
      confirmRemove(instance),
    );
    remove.title = "Rimuovi istanza";
    remove.setAttribute("aria-label", `Rimuovi ${instance.name}`);
    actions.append(open, manage, verify);
    if (sync) actions.append(sync);
    actions.append(spacer, remove);
    card.append(actions);

    card.addEventListener("click", (event) => {
      if (event.target.closest("button")) return;
      void openInstance(instance.url);
    });
    els.instances.append(card);
  }

  if (state.instances.length > 0) {
    els.instances.append(els.newInstanceCard);
    els.newInstanceCard.hidden = false;
  }
}

function policyLabel(value) {
  return value === "Server" ? "Server" : value === "Client" ? "Client" : "Auto";
}

function metaItem(label, value, dim = false) {
  const item = el("div", "meta-item");
  item.append(el("span", "meta-label", label));
  item.append(el("span", `meta-value${dim ? " dim" : ""}`, value));
  return item;
}

function renderTabs() {
  els.tabs.replaceChildren();
  for (const tab of state.tabs) {
    const node = el("button", "tab");
    node.type = "button";
    node.role = "tab";
    node.setAttribute("aria-selected", tab.active ? "true" : "false");
    node.style.setProperty("--env", envColor(tab.environment));
    node.title = tab.url;
    node.append(el("span", "tab-dot"));
    node.append(el("span", "tab-name", tab.name));
    const close = el("span", "tab-close", "×");
    close.title = "Chiudi scheda";
    close.setAttribute("aria-label", `Chiudi ${tab.name}`);
    close.addEventListener("click", (event) => {
      event.stopPropagation();
      void api.closeTab(tab.url);
    });
    node.append(close);
    node.addEventListener("click", () => void api.activate(tab.url));
    node.addEventListener("auxclick", (event) => {
      if (event.button === 1) void api.closeTab(tab.url);
    });
    els.tabs.append(node);
  }
}

function captureDrawerDraft() {
  const openDetails = els.drawerBody.querySelector(".new-tunnel[open]");
  if (!openDetails) return null;
  const url = openDetails.dataset.url;
  const port = openDetails.querySelector("input[type=number]");
  const search = openDetails.querySelector('input[type="search"]');
  return {
    url,
    selected: openDetails.dataset.selected || "",
    port: port?.value || "",
    query: search?.value || "",
    open: openDetails.open,
  };
}

function renderDrawer() {
  drawerDraft = captureDrawerDraft();
  const query = drawerSearch.trim().toLowerCase();
  const key = JSON.stringify({
    search: drawerSearch,
    tunnels: state.tunnels,
    auth: state.instances.map((instance) => [instance.url, instance.auth]),
    targets: Object.fromEntries(
      Object.entries(state.targets || {}).map(([url, servers]) => [
        url,
        (servers || []).map((server) => [
          server.serverId,
          (server.targets || []).map((target) => target.serverApplicationId),
          (server.services || []).map((service) => service.id),
        ]),
      ]),
    ),
    instances: state.instances.map((instance) => [
      instance.url,
      instance.name,
      instance.environment,
    ]),
  });
  if (key === lastDrawerKey && els.drawerBody.childElementCount > 0) return;
  lastDrawerKey = key;
  els.drawerBody.replaceChildren();

  if (state.instances.length === 0) {
    els.drawerBody.append(
      el(
        "p",
        "drawer-empty",
        "Nessuna istanza configurata. Aggiungi un’istanza per aprire tunnel.",
      ),
    );
    return;
  }

  let shown = 0;
  for (const instance of state.instances) {
    const own = state.tunnels.filter((item) => item.url === instance.url);
    const instanceHit =
      query.length === 0 ||
      [instance.name, instance.url, instance.environment].some((value) =>
        String(value || "").toLowerCase().includes(query),
      );
    const tunnels = instanceHit
      ? own
      : own.filter((tunnel) => tunnelMatches(tunnel, query));
    if (query && !instanceHit && tunnels.length === 0) continue;
    els.drawerBody.append(
      renderInstanceGroup(instance, tunnels, { searching: query.length > 0 }),
    );
    shown += 1;
  }
  if (query && shown === 0) {
    els.drawerBody.append(
      el("p", "drawer-empty", "Nessun tunnel corrisponde alla ricerca."),
    );
  }
}

function tunnelMatches(tunnel, query) {
  return [
    tunnel.instanceName,
    tunnel.serverName,
    tunnel.ip,
    tunnel.application,
    tunnel.source,
    tunnel.mode,
    tunnel.transport,
    tunnel.localPort,
    tunnel.remotePort,
  ].some((value) =>
    String(value ?? "").toLowerCase().includes(query),
  );
}

function renderInstanceGroup(instance, tunnels, { searching = false } = {}) {
  const group = el("section", "instance-group");
  group.dataset.instanceGroup = instance.url;

  const header = el("div", "instance-group-header");
  const title = el("div", "instance-group-title");
  const collapsed =
    collapsedInstances.has(instance.url) && !searching;
  const toggle = button(
    collapsed ? "▸" : "▾",
    "instance-toggle",
    () => {
      if (collapsed) collapsedInstances.delete(instance.url);
      else collapsedInstances.add(instance.url);
      lastDrawerKey = "";
      renderDrawer();
    },
    collapsed ? "Espandi istanza" : "Comprimi istanza",
  );
  toggle.setAttribute("aria-expanded", String(!collapsed));
  toggle.setAttribute("aria-label", `${toggle.title} ${instance.name}`);
  const mark = el("span", "instance-mark");
  mark.style.setProperty("--env", envColor(instance.environment));
  title.append(toggle, mark, el("span", "", instance.name));
  const policy = el(
    "span",
    "chip",
    `Connessioni: ${policyLabel(instance.connectionPolicy)}`,
  );
  policy.title =
    "Modalità connessioni dell'installazione: Server, Client o Auto.";
  title.append(policy);
  if (instance.offline) {
    const offline = el("span", "chip offline", "Offline");
    offline.title = `Dati locali del ${formatCachedAt(instance.cachedAt)}`;
    title.append(offline);
  }
  const info = el("div");
  info.append(title);
  const url = el("p", "instance-group-url", instance.url);
  info.append(url);
  header.append(info);

  const actions = el("div", "group-actions");
  actions.append(
    button("🔑", "mini-button", () => void openCredentials(instance), "Credenziali"),
  );
  actions.append(
    button("✓", "mini-button", () => openLocalCheck(instance), "Verifica locale"),
  );
  actions.append(
    button("⚙", "mini-button", () => openManage(instance.url), "Gestisci istanza"),
  );
  actions.append(
    button(
      "↗",
      "mini-button",
      () => void api.openTunnelsPage(instance.url),
      "Apri la pagina /tunnels dell’istanza",
    ),
  );
  header.append(actions);
  group.append(header);
  if (collapsed) return group;

  const auth = instance.auth || {};
  if (auth.status === "pending" || auth.status === "restoring") {
    const box = el("div", "auth-box");
    box.append(el("p", "auth-title", "Accesso in corso"));
    box.append(
      el(
        "p",
        "auth-copy",
        auth.message || "Completa l’accesso nel browser e torna qui.",
      ),
    );
    if (auth.userCode) {
      const code = el("div", "auth-code");
      code.append(el("code", "", auth.userCode));
      code.append(
        button(
          "Apri browser",
          "tiny-button",
          () => void api.authOpenVerification(instance.url),
        ),
      );
      box.append(code);
    }
    const row = el("div", "auth-actions");
    row.append(
      button("Annulla", "tiny-button", () => void api.authCancel(instance.url)),
    );
    box.append(row);
    group.append(box);
  } else if (!auth.tunnelAccess) {
    const box = el("div", "auth-box");
    box.append(
      el(
        "p",
        "auth-title",
        auth.status === "ready"
          ? "Sessione web attiva"
          : "Accedi ai tunnel dell’istanza",
      ),
    );
    box.append(
      el(
        "p",
        "auth-copy",
        auth.message ||
          "Keycloak device flow: il codice si conferma nel browser, i token restano nel processo principale.",
      ),
    );
    const row = el("div", "auth-actions");
    row.append(
      button(
        auth.status === "error" ? "Riprova" : "Accedi con Keycloak",
        "tiny-button",
        () => void authStart(instance.url),
      ),
    );
    row.append(
      button(
        "Pagina /tunnels",
        "tiny-button",
        () => void api.openTunnelsPage(instance.url),
      ),
    );
    if (auth.saved)
      row.append(
        button(
          "Dimentica",
          "tiny-button",
          () => void api.authLogout(instance.url),
        ),
      );
    box.append(row);
    group.append(box);
  }

  const grouped = new Map();
  for (const tunnel of tunnels) {
    const key = `${tunnel.serverName}::${tunnel.ip}`;
    if (!grouped.has(key)) grouped.set(key, []);
    grouped.get(key).push(tunnel);
  }

  if (grouped.size === 0 && auth.tunnelAccess && !searching) {
    group.append(
      el("p", "drawer-empty", "Nessun tunnel aperto per questa istanza."),
    );
  }

  for (const [key, rows] of grouped) {
    const server = el("div", "server-group");
    const titleRow = el("div", "server-group-title");
    const [serverName, ip] = key.split("::");
    const collapseKey = `${instance.url}\u001f${key}`;
    const collapsed = collapsedServers.has(collapseKey);
    const toggle = button(
      collapsed ? "▸" : "▾",
      "server-toggle",
      () => {
        if (collapsed) collapsedServers.delete(collapseKey);
        else collapsedServers.add(collapseKey);
        lastDrawerKey = "";
        renderDrawer();
      },
      collapsed ? "Espandi server" : "Comprimi server",
    );
    toggle.setAttribute("aria-expanded", String(!collapsed));
    toggle.setAttribute("aria-label", `${toggle.title} ${serverName}`);
    titleRow.append(toggle);
    titleRow.append(el("span", "server-name", serverName));
    const right = el("span", "server-group-actions");
    right.append(el("span", "server-count", String(rows.length)));
    right.append(el("span", "server-ip", ip || "—"));
    const serverId = rows.find((row) => row.serverId)?.serverId;
    if (serverId)
      right.append(
        button(
          "Anagrafica",
          "tiny-button",
          () => void api.openServerPage(instance.url, serverId),
          "Apri i servizi tunnel del server nella web app",
        ),
      );
    titleRow.append(right);
    server.append(titleRow);
    if (!collapsed) {
      for (const tunnel of rows) server.append(renderTunnelRow(tunnel));
    }
    group.append(server);
  }

  if (auth.tunnelAccess && !searching) group.append(renderNewTunnel(instance));
  return group;
}

function renderTunnelRow(tunnel) {
  const row = el("div", "tunnel-row");
  row.dataset.tunnelId = tunnel.id;
  row.dataset.status = tunnel.status;
  row.append(el("span", "status-dot"));

  const main = el("div");
  const context = el("div", "tunnel-context");
  const contextText = el(
    "span",
    "context-text",
    `${tunnel.instanceName} · ${tunnel.serverName} · ${tunnel.ip || "—"}`,
  );
  const contextPort = el(
    "span",
    "context-port",
    `:${tunnel.localPort ?? "—"}`,
  );
  context.append(contextText, contextPort);
  context.title = `${contextText.textContent} · ${contextPort.textContent}`;
  main.append(context);
  const sub = el("div", "tunnel-sub");
  sub.append(el("span", "", tunnel.source || tunnel.application || "Tunnel"));
  sub.append(el("span", "", `· ${tunnel.mode}${tunnel.remotePort ? ` :${tunnel.remotePort}` : ""}`));
  sub.append(
    el(
      "span",
      "",
      tunnel.transport === "Server" ? "· via server" : "· diretto",
    ),
  );
  sub.append(
    el(
      "span",
      "state",
      tunnel.status === "Active" ? "Attivo" : "Fermo",
    ),
  );
  main.append(sub);
  if (tunnel.error)
    main.append(el("p", "tunnel-error", tunnel.error));
  row.append(main);

  const actions = el("div", "tunnel-actions");
  if (tunnel.status === "Active") {
    if (tunnel.openUrl)
      actions.append(
        button(
          "Apri",
          "tiny-button",
          () => void api.openExternal(tunnel.openUrl),
          "Apri nel browser",
        ),
      );
    actions.append(
      button(
        "Ferma",
        "tiny-button stop",
        () => void stopTunnel(tunnel),
        "Ferma il tunnel",
      ),
    );
  } else {
    actions.append(
      button(
        "Avvia",
        "tiny-button",
        () => void restartTunnel(tunnel),
        "Riavvia il tunnel",
      ),
    );
    actions.append(
      button(
        "×",
        "tiny-button",
        () => void forgetTunnel(tunnel),
        "Rimuovi dalla vista",
      ),
    );
  }
  row.append(actions);
  return row;
}

function tunnelOptionRows(targets) {
  const rows = [];
  for (const server of targets) {
    const serverSearch = [
      server.serverName,
      server.environmentLabel,
      server.sshHost,
      server.username,
    ]
      .filter(Boolean)
      .join(" ")
      .toLowerCase();
    for (const target of server.targets || []) {
      rows.push({
        value: `target:${server.serverId}:${target.serverApplicationId}`,
        server,
        kind: "Deployment",
        label: `${target.application} :${target.targetPort}`,
        meta: target.version || "",
        port: target.suggestedLocalPort ?? target.targetPort ?? null,
        mode: "Local",
        search: [
          serverSearch,
          target.application,
          target.version || "",
          target.targetHost || "",
          target.targetPort || "",
        ]
          .join(" ")
          .toLowerCase(),
      });
    }
    for (const service of server.services || []) {
      rows.push({
        value: `service:${server.serverId}:${service.id}`,
        server,
        kind: "Servizio",
        label: service.name,
        meta: `${service.mode} ${service.kind}`,
        port: service.localPort ?? service.targetPort ?? null,
        mode: service.mode,
        search: [
          serverSearch,
          service.name,
          service.mode,
          service.kind,
          service.targetHost || "",
          service.targetPort || "",
          service.localPort || "",
          service.remotePort || "",
        ]
          .join(" ")
          .toLowerCase(),
      });
    }
  }
  return rows;
}

function renderNewTunnel(instance) {
  const targets = state.targets?.[instance.url];
  const details = el("details", "new-tunnel");
  details.dataset.url = instance.url;
  details.dataset.selected = "";
  details.append(el("summary", "", "Nuovo tunnel"));
  const form = el("div", "new-tunnel-form");

  if (!Array.isArray(targets) || targets.length === 0) {
    form.append(
      el(
        "p",
        "auth-copy",
        instance.offline
          ? "Nessun dato locale disponibile. Riconnetti l’istanza InfraOrgIT per caricare i server."
          : "Caricamento dei server dell’allowlist non disponibile. Apri la pagina /tunnels dell’istanza per il dettaglio.",
      ),
    );
    details.append(form);
    return details;
  }

  if (instance.offline) {
    details.append(
      el(
        "p",
        "field-hint",
        "Modalità offline: solo tunnel Client con chiave SSH nell’agente locale.",
      ),
    );
  }

  const rows = tunnelOptionRows(targets);
  const draft = drawerDraft?.url === instance.url ? drawerDraft : null;
  let optionButtons = new Map();
  let selectedValue = "";
  if (draft?.open) details.open = true;

  const search = el("input");
  search.type = "search";
  search.placeholder = "Cerca macchina, ambiente o servizio";
  search.setAttribute("aria-label", "Cerca macchina, ambiente o servizio");
  form.append(search);

  const list = el("div", "new-tunnel-options");
  list.setAttribute("role", "listbox");
  list.setAttribute("aria-label", "Server e servizio del tunnel");
  form.append(list);

  const row = el("div", "row");
  const port = el("input");
  port.type = "number";
  port.min = "1024";
  port.max = "65535";
  port.placeholder = "Porta locale";
  port.setAttribute("aria-label", "Porta locale");
  const start = button("Avvia", "tiny-button", () => {
    void startTunnel(instance, selectedValue, port.value);
  });
  start.disabled = true;
  row.append(port, start);
  form.append(row);

  const visibleRows = () => {
    const query = search.value.trim().toLowerCase();
    return query
      ? rows.filter((item) => item.search.includes(query))
      : rows;
  };

  const applySelection = (value, { keepPort = false } = {}) => {
    const visible = visibleRows();
    const previous = selectedValue;
    const selected =
      visible.find((item) => item.value === value) || visible[0] || null;
    selectedValue = selected?.value || "";
    details.dataset.selected = selectedValue;
    for (const [optionValue, node] of optionButtons)
      node.setAttribute(
        "aria-pressed",
        String(optionValue === selectedValue),
      );
    start.disabled = !selected;
    if (!selected) {
      if (!keepPort) {
        port.value = "";
        port.disabled = false;
      }
      return;
    }
    if (!keepPort || selectedValue !== previous) {
      port.value =
        selected.port === null || selected.port === undefined
          ? ""
          : String(selected.port);
      port.disabled = selected.mode === "Remote";
    }
  };

  const renderOptions = (value, { keepPort = false } = {}) => {
    const visible = visibleRows();
    list.replaceChildren();
    optionButtons = new Map();
    if (visible.length === 0) {
      list.append(
        el(
          "p",
          "new-tunnel-empty",
          "Nessuna macchina o servizio corrisponde alla ricerca.",
        ),
      );
      applySelection("", { keepPort });
      return;
    }

    const environments = new Map();
    for (const item of visible) {
      const environmentLabel =
        item.server.environmentLabel || "Senza ambiente";
      if (!environments.has(environmentLabel))
        environments.set(environmentLabel, new Map());
      const servers = environments.get(environmentLabel);
      const serverId = item.server.serverId;
      if (!servers.has(serverId))
        servers.set(serverId, { server: item.server, items: [] });
      servers.get(serverId).items.push(item);
    }

    for (const [environmentLabel, servers] of environments) {
      const section = el("section", "new-tunnel-env");
      section.append(el("h4", "new-tunnel-env-title", environmentLabel));
      list.append(section);
      for (const [serverId, entry] of servers) {
        const serverBlock = el("div", "new-tunnel-server");
        serverBlock.dataset.serverId = String(serverId);
        const head = el("div", "new-tunnel-server-head");
        head.append(
          el("span", "new-tunnel-server-name", entry.server.serverName),
        );
        head.append(
          el("span", "new-tunnel-server-ip", entry.server.sshHost || "—"),
        );
        serverBlock.append(head);
        section.append(serverBlock);
        for (const item of entry.items) {
          const option = el("button", "new-tunnel-option");
          option.type = "button";
          option.dataset.value = item.value;
          option.setAttribute("aria-pressed", "false");
          option.append(el("span", "new-tunnel-option-kind", item.kind));
          option.append(el("span", "new-tunnel-option-label", item.label));
          if (item.meta)
            option.append(el("span", "new-tunnel-option-meta", item.meta));
          if (item.port !== null && item.port !== undefined)
            option.append(
              el("span", "new-tunnel-option-port", `:${item.port}`),
            );
          option.addEventListener("click", () => applySelection(item.value));
          optionButtons.set(item.value, option);
          serverBlock.append(option);
        }
      }
    }
    applySelection(value, { keepPort });
  };

  search.addEventListener("input", () =>
    renderOptions(selectedValue, { keepPort: true }),
  );
  search.addEventListener("keydown", (event) => {
    if (event.key !== "ArrowDown") return;
    const first = optionButtons.values().next().value;
    if (first) {
      event.preventDefault();
      first.focus();
    }
  });

  if (draft?.query) search.value = draft.query;
  renderOptions(draft?.selected || "");
  if (draft?.port) {
    port.value = draft.port;
    const selected = rows.find((item) => item.value === selectedValue);
    port.disabled = selected?.mode === "Remote";
  }

  details.append(form);
  return details;
}

async function syncOutbox(instance) {
  try {
    const next = await api.syncOutbox(instance.url);
    const pending =
      next?.instances?.find((item) => item.url === instance.url)
        ?.pendingEvents || 0;
    toast(
      pending
        ? `${pending} eventi ancora in coda.`
        : "Eventi sincronizzati.",
      pending ? "info" : "success",
    );
  } catch (error) {
    toast(error.message || "Sincronizzazione non riuscita.", "error");
  }
}

async function startTunnel(instance, value, portValue) {
  const [kind, serverId, id] = String(value || "").split(":");
  if (!serverId || !id) {
    toast("Scegli un server o un servizio.", "error");
    return;
  }
  try {
    await api.startTunnel({
      url: instance.url,
      serverId: Number(serverId),
      targetId: kind === "target" ? Number(id) : null,
      serviceId: kind === "service" ? Number(id) : null,
      localPort: portValue ? Number(portValue) : null,
    });
    toast(`Tunnel avviato verso ${instance.name}.`, "success");
  } catch (error) {
    toast(error.message || "Avvio del tunnel non riuscito.", "error");
  }
}

async function stopTunnel(tunnel) {
  try {
    await api.stopTunnel(tunnel.id, tunnel.url);
  } catch (error) {
    toast(error.message || "Arresto del tunnel non riuscito.", "error");
  }
}

async function restartTunnel(tunnel) {
  try {
    await api.restartTunnel(tunnel.id, tunnel.url);
  } catch (error) {
    toast(error.message || "Riavvio del tunnel non riuscito.", "error");
  }
}

async function forgetTunnel(tunnel) {
  try {
    await api.forgetTunnel(tunnel.id);
  } catch (error) {
    toast(error.message || "Rimozione del tunnel non riuscita.", "error");
  }
}

async function authStart(url) {
  try {
    await api.authStart(url);
    toast("Completa l’accesso nel browser.", "info");
  } catch (error) {
    toast(error.message || "Accesso non riuscito.", "error");
  }
}

async function openInstance(url) {
  try {
    await api.activate(url);
  } catch (error) {
    toast(error.message || "Apertura dell’istanza non riuscita.", "error");
  }
}

function setDrawer(open) {
  drawerOpen = open;
  updateStage();
}

function openPalette() {
  palette.open = true;
  palette.query = "";
  palette.index = 0;
  els.paletteInput.value = "";
  els.palette.hidden = false;
  buildCommands();
  renderPaletteItems();
  api.setContentBounds({ ...boundsPayload(), visible: false });
  updateStage();
  requestAnimationFrame(() => els.paletteInput.focus());
}

function closePalette() {
  palette.open = false;
  els.palette.hidden = true;
  lastBounds = "";
  sendBounds();
  updateStage();
}

function boundsPayload() {
  const rect = els.slot.getBoundingClientRect();
  return {
    x: Math.round(rect.left),
    y: Math.round(rect.top),
    width: Math.round(rect.width),
    height: Math.round(rect.height),
  };
}

function buildCommands() {
  const commands = [];
  if (!state) {
    palette.commands = commands;
    return;
  }
  commands.push({
    id: "home",
    title: "Istanze",
    sub: "Torna alla schermata principale",
    icon: "⌂",
    kbd: `${modifierLabel} 0`,
    run: () => void api.home(),
  });
  commands.push({
    id: "open-connection",
    title: "Apri connessione",
    sub: "Istanze configurate o nuova connessione",
    icon: "→",
    kbd: `${modifierLabel} N`,
    run: () => openConnectionPicker(),
  });
  commands.push({
    id: "new",
    title: "Nuova connessione",
    sub: "Aggiungi un’installazione InfraOrgIT",
    icon: "+",
    run: () => openNewInstance(),
  });
  commands.push({
    id: "settings",
    title: "Impostazioni",
    sub: "Aggiornamenti e versione desktop",
    icon: "⚙",
    run: () => openSettings(),
  });
  commands.push({
    id: "update",
    title: "Controlla aggiornamenti",
    sub: "GitHub releases",
    icon: "↑",
    run: () => void checkUpdates(),
  });
  commands.push({
    id: "drawer",
    title: drawerOpen ? "Chiudi drawer Tunnel" : "Apri drawer Tunnel",
    sub: "Vista globale multi-istanza",
    icon: "⇄",
    kbd: `${modifierLabel} T`,
    run: () => setDrawer(!drawerOpen),
  });

  for (const instance of state.instances) {
    commands.push({
      id: `open-${instance.url}`,
      title: `Apri ${instance.name}`,
      sub: instance.url,
      icon: "→",
      run: () => void openInstance(instance.url),
    });
    commands.push({
      id: `manage-${instance.url}`,
      title: `Gestisci ${instance.name}`,
      sub: "Anagrafica istanza e servizi tunnel dei server",
      icon: "⚙",
      run: () => openManage(instance.url),
    });
    commands.push({
      id: `tunnels-${instance.url}`,
      title: `Pagina Tunnel · ${instance.name}`,
      sub: "Dettaglio del singolo server nella web app",
      icon: "↗",
      run: () => void api.openTunnelsPage(instance.url),
    });
    const auth = instance.auth || {};
    if (!auth.tunnelAccess) {
      commands.push({
        id: `auth-${instance.url}`,
        title: `Accedi ai tunnel · ${instance.name}`,
        sub: "Keycloak device flow",
        icon: "◎",
        run: () => void authStart(instance.url),
      });
    } else if (auth.saved) {
      const webSession = auth.method === "session";
      commands.push({
        id: `logout-${instance.url}`,
        title: `${webSession ? "Dimentica token" : "Esci dai tunnel"} · ${instance.name}`,
        sub: webSession
          ? "La sessione web resta attiva"
          : "Dimentica il refresh token salvato",
        icon: "×",
        run: () => void api.authLogout(instance.url),
      });
    }
  }

  for (const tunnel of state.tunnels) {
    if (tunnel.status !== "Active") continue;
    commands.push({
      id: `tunnel-open-${tunnel.id}`,
      title: `Apri tunnel ${tunnel.serverName}`,
      sub: `${tunnel.instanceName} · :${tunnel.localPort ?? "—"}`,
      icon: "↗",
      run: () => {
        if (tunnel.openUrl) void api.openExternal(tunnel.openUrl);
        else toast("Questo tunnel non espone un URL locale.", "info");
      },
    });
    commands.push({
      id: `tunnel-stop-${tunnel.id}`,
      title: `Ferma tunnel ${tunnel.serverName}`,
      sub: `${tunnel.instanceName} · :${tunnel.localPort ?? "—"}`,
      icon: "■",
      run: () => void stopTunnel(tunnel),
    });
  }

  if (state.activeUrl) {
    commands.push({
      id: "reload",
      title: "Ricarica istanza",
      sub: instanceByUrl(state.activeUrl)?.name || state.activeUrl,
      icon: "↻",
      run: () => void api.reload(state.activeUrl),
    });
    commands.push({
      id: "external",
      title: "Apri l’istanza nel browser",
      sub: state.activeUrl,
      icon: "↗",
      run: () => void api.openExternal(state.activeUrl),
    });
  }

  palette.commands = commands;
}

function filteredCommands() {
  const query = palette.query.trim().toLowerCase();
  if (!query) return palette.commands;
  return palette.commands.filter((command) =>
    `${command.title} ${command.sub || ""}`.toLowerCase().includes(query),
  );
}

function renderPaletteItems() {
  const commands = filteredCommands();
  if (palette.index >= commands.length) palette.index = Math.max(0, commands.length - 1);
  els.paletteList.replaceChildren();
  if (commands.length === 0) {
    els.paletteList.append(el("li", "palette-empty", "Nessun comando trovato."));
    els.paletteHint.textContent = "Prova con un nome di istanza o tunnel";
    return;
  }
  commands.forEach((command, index) => {
    const item = el("li", "palette-item");
    item.setAttribute("role", "option");
    item.setAttribute("aria-selected", index === palette.index ? "true" : "false");
    item.id = `palette-item-${command.id}`;
    item.append(el("span", "palette-item-icon", command.icon || "›"));
    const main = el("div", "palette-item-main");
    main.append(el("div", "palette-item-title", command.title));
    if (command.sub) main.append(el("div", "palette-item-sub", command.sub));
    item.append(main);
    if (command.kbd) item.append(el("kbd", "", command.kbd));
    item.addEventListener("mousemove", () => {
      palette.index = index;
      updatePaletteSelection();
    });
    item.addEventListener("click", () => runPaletteCommand(command));
    els.paletteList.append(item);
  });
  els.paletteHint.textContent = `${commands.length} comandi`;
  updatePaletteSelection();
}

function updatePaletteSelection() {
  const items = [...els.paletteList.querySelectorAll(".palette-item")];
  items.forEach((item, index) => {
    item.setAttribute("aria-selected", index === palette.index ? "true" : "false");
  });
  const active = items[palette.index];
  if (active) {
    els.paletteList.setAttribute("aria-activedescendant", active.id);
    active.scrollIntoView({ block: "nearest" });
  }
}

function runPaletteCommand(command) {
  closePalette();
  try {
    command.run();
  } catch (error) {
    toast(error.message || "Comando non riuscito.", "error");
  }
}

function openNewInstance() {
  const token = ++modalToken;
  openModal({
    eyebrow: "Configurazione",
    title: "Nuova istanza",
    body: buildInstanceForm(null),
    footer: (footer) => {
      footer.append(
        button("Annulla", "ghost", closeModal),
        button("Aggiungi istanza", "primary", async () => {
          const form = els.modalBody.querySelector("form");
          const data = formData(form);
          try {
            await api.addInstance(data);
            if (token === modalToken) closeModal();
            toast(`Istanza ${data.name} aggiunta.`, "success");
          } catch (error) {
            showFormError(form, error.message);
          }
        }),
      );
    },
  });
}

function buildInstanceForm(instance) {
  const form = el("form", "form");
  form.addEventListener("submit", (event) => event.preventDefault());

  form.append(
    field(
      "Nome istanza",
      input("text", "name", instance?.name || "", "Produzione"),
    ),
  );
  const url = input(
    "url",
    "url",
    instance?.url || "",
    "https://infra.example.com",
  );
  url.required = true;
  if (instance) url.readOnly = true;
  const urlField = field("URL", url);
  urlField.append(
    el(
      "p",
      "field-hint",
      instance
        ? "L’URL identifica l’istanza e non si modifica da qui."
        : "HTTPS per ogni host. HTTP è ammesso solo per localhost in sviluppo.",
    ),
  );
  form.append(urlField);

  const environment = input(
    "text",
    "environment",
    instance?.environment || "",
    "Produzione",
  );
  environment.setAttribute("list", "environment-options");
  const datalist = el("datalist");
  datalist.id = "environment-options";
  for (const value of ["Produzione", "Staging", "Test", "Locale"])
    datalist.append(option(value, value));
  const envField = field("Ambiente", environment);
  envField.append(datalist);
  envField.append(
    el("p", "field-hint", "Etichetta mostrata sulle card e nelle tab."),
  );
  form.append(envField);

  const startup = el("label", "checkbox");
  const checkbox = input("checkbox", "openAtStartup", "");
  checkbox.checked = Boolean(instance?.openAtStartup);
  const startupText = el("span");
  startupText.append(el("strong", "", "Apri all’avvio"));
  startupText.append(
    el("small", "", "Apre la scheda all’avvio dell’app, senza mostrare Keycloak se la sessione silenziosa è valida."),
  );
  startup.append(checkbox, startupText);
  form.append(startup);
  form.append(el("p", "inline-error", ""));
  form.querySelector(".inline-error").hidden = true;
  return form;
}

function field(labelText, control) {
  const wrapper = el("div", "field");
  const label = el("label", "", labelText);
  label.htmlFor = control.id;
  wrapper.append(label, control);
  return wrapper;
}

function input(type, name, value, placeholder = "") {
  const node = el("input");
  node.type = type;
  node.name = name;
  node.id = `field-${name}`;
  node.value = value;
  if (placeholder) node.placeholder = placeholder;
  if (name === "name" || name === "url" || name === "environment")
    node.required = name !== "environment";
  return node;
}

function option(value, text) {
  const node = el("option", "", text);
  node.value = value;
  return node;
}

function formData(form) {
  return {
    name: form.querySelector("[name=name]").value,
    url: form.querySelector("[name=url]").value,
    environment: form.querySelector("[name=environment]").value,
    openAtStartup: form.querySelector("[name=openAtStartup]").checked,
  };
}

function showFormError(form, message) {
  const error = form.querySelector(".inline-error");
  error.textContent = message || "Operazione non riuscita.";
  error.hidden = false;
}

async function openManage(url) {
  const instance = instanceByUrl(url);
  if (!instance) return;
  const token = ++modalToken;
  let servers = { loading: true, items: [], authenticated: false, message: "" };
  const section = el("section", "section");
  const header = el("div", "section-header");
  header.append(el("h3", "section-title", "Servizi tunnel dei server"));
  const list = el("div", "server-list");
  const note = el(
    "p",
    "section-note",
    "L’anagrafica dei tunnel si modifica nella scheda server, sezione Tunnel. Il desktop apre la pagina esistente e non duplica il form.",
  );
  section.append(header, note, list);

  openModal({
    eyebrow: "Anagrafica",
    title: instance.name,
    body: buildInstanceForm(instance),
    extra: section,
    footer: (footer) => {
      footer.append(
        button("Rimuovi", "danger", () => {
          closeModal();
          confirmRemove(instance);
        }),
        el("span", "spacer"),
        button("Annulla", "ghost", closeModal),
        button("Salva", "primary", async () => {
          const form = els.modalBody.querySelector("form");
          const data = formData(form);
          try {
            await api.updateInstance(url, {
              name: data.name,
              environment: data.environment,
              openAtStartup: data.openAtStartup,
            });
            if (token === modalToken) closeModal();
            toast("Istanza aggiornata.", "success");
          } catch (error) {
            showFormError(form, error.message);
          }
        }),
      );
    },
  });

  const renderServers = () => {
    if (token !== modalToken) return;
    list.replaceChildren();
    if (servers.loading) {
      list.append(el("p", "section-note", "Caricamento dei server…"));
      return;
    }
    if (!servers.authenticated) {
      list.append(
        el(
          "p",
          "section-note",
          servers.message ||
            "Accedi all’istanza per vedere i server e aprire l’anagrafica tunnel.",
        ),
      );
      list.append(
        button(
          "Accedi con Keycloak",
          "secondary",
          () => {
            closeModal();
            void authStart(url);
          },
          "Device flow",
        ),
      );
      return;
    }
    if (servers.items.length === 0) {
      list.append(el("p", "section-note", "Nessun server visibile."));
      return;
    }
    for (const server of servers.items) {
      const item = el("div", "server-item");
      const main = el("div", "server-item-main");
      main.append(el("div", "server-item-name", server.name));
      main.append(
        el(
          "div",
          "server-item-meta",
          `${server.environmentLabel || "—"} · ${server.ipAddress || server.hostname || "—"}:${server.sshPort || 22}`,
        ),
      );
      item.append(main);
      item.append(
        button(
          "Apri anagrafica",
          "tiny-button",
          () => {
            closeModal();
            void api.openServerPage(url, server.id);
          },
          "Apri la scheda server con la sezione Tunnel",
        ),
      );
      list.append(item);
    }
    const row = el("div", "modal-actions-row");
    row.append(
      button("Pagina /tunnels", "secondary", () => {
        closeModal();
        void api.openTunnelsPage(url);
      }),
    );
    list.append(row);
  };
  renderServers();

  try {
    servers = await api.serverList(url);
    servers.loading = false;
    renderServers();
  } catch (error) {
    servers = {
      loading: false,
      authenticated: false,
      items: [],
      message: error.message || "Server non disponibili.",
    };
    renderServers();
  }
}

function confirmRemove(instance) {
  const token = ++modalToken;
  openModal({
    eyebrow: "Rimozione",
    title: `Rimuovi ${instance.name}?`,
    body: (() => {
      const box = el("div");
      box.append(
        el(
          "p",
          "subtitle",
          "L’istanza viene rimossa da questo desktop. La sessione salvata per i tunnel viene dimenticata; l’installazione e i dati sul server non cambiano.",
        ),
      );
      box.append(el("p", "field-hint", instance.url));
      return box;
    })(),
    footer: (footer) => {
      footer.append(
        button("Annulla", "ghost", closeModal),
        button("Rimuovi istanza", "danger", async () => {
          try {
            await api.removeInstance(instance.url);
            if (token === modalToken) closeModal();
            toast("Istanza rimossa.", "success");
          } catch (error) {
            toast(error.message || "Rimozione non riuscita.", "error");
          }
        }),
      );
    },
  });
}

async function checkUpdates() {
  try {
    const next = await api.checkUpdates();
    updateState = next;
    if (state) state.updates = next;
    renderUpdate();
    if (els.modalBody.querySelector("[data-update-card]")) openSettings();
    toast(
      next.status === "update"
        ? `Disponibile la versione ${next.latestVersion}.`
        : next.message || "Controllo completato.",
    );
  } catch (error) {
    toast(error.message || "Controllo non riuscito.", "error");
  }
}

async function installUpdate() {
  if (!window.confirm("Installare l'aggiornamento e riavviare l'app?")) return;
  try {
    await api.installUpdate();
    toast("Installazione avviata: l'app si riavvierà.");
  } catch (error) {
    toast(error.message || "Installazione non riuscita.", "error");
  }
}

function openConnectionPicker() {
  const instances = state?.instances || [];
  if (instances.length === 0) {
    openNewInstance();
    return;
  }
  const body = el("div", "connection-picker");
  body.append(
    el(
      "p",
      "muted",
      "Scegli una connessione già configurata oppure creane una nuova.",
    ),
  );
  const list = el("div", "connection-list");
  for (const instance of instances) {
    const row = button("", "connection-row", () => {
      closeModal();
      void openInstance(instance.url);
    });
    row.append(el("span", "connection-name", instance.name));
    row.append(el("span", "connection-url", instance.url));
    if (instance.environment) {
      const chip = el("span", "chip env", instance.environment);
      chip.style.setProperty("--env", envColor(instance.environment));
      row.append(chip);
    }
    list.append(row);
  }
  body.append(list);
  openModal({
    eyebrow: "Connessioni",
    title: "Apri connessione",
    body,
    footer: (footer) => {
      footer.append(button("Annulla", "secondary", () => closeModal()));
      footer.append(
        button("Nuova connessione", "primary", () => openNewInstance()),
      );
    },
  });
}

async function openCredentials(instance) {
  const body = el("div", "connection-picker");
  const status = el("p", "field-hint", "Caricamento…");
  const list = el("div", "connection-list");
  body.append(status, list);
  openModal({
    eyebrow: "Credenziali",
    title: instance.name,
    body,
    footer: (footer) =>
      footer.append(button("Chiudi", "secondary", () => closeModal())),
  });

  const render = async () => {
    list.replaceChildren();
    status.textContent = "Caricamento…";
    try {
      const data = await api.listCredentials(instance.url);
      status.textContent = data.offline
        ? "Istanza non raggiungibile: solo credenziali locali."
        : "";
      if (!data.items.length) {
        list.append(el("p", "auth-copy", "Nessuna credenziale disponibile."));
        return;
      }
      for (const item of data.items) {
        const row = el("div", "connection-row");
        const info = el("div");
        info.append(el("div", "connection-name", item.name));
        info.append(
          el(
            "div",
            "connection-url",
            `${item.username} · ${item.authType} · ${
              item.owner === "personal" ? "Personale" : "Condivisa"
            }`,
          ),
        );
        info.append(
          el(
            "div",
            "field-hint",
            item.cached
              ? item.allowOffline
                ? "Salvata offline."
                : "Solo in memoria."
              : item.allowOffline
                ? "Consenti offline."
                : "Solo online.",
          ),
        );
        row.append(info);
        if (item.cached) {
          row.append(
            button("Dimentica", "ghost", async () => {
              try {
                await api.forgetCredential(instance.url, item.id);
                await render();
              } catch (error) {
                toast(error.message || "Rimozione non riuscita.", "error");
              }
            }),
          );
        } else {
          const download = button("Scarica", "secondary", async () => {
            download.disabled = true;
            try {
              await api.downloadCredential(instance.url, item.id);
              toast("Credenziale scaricata.", "success");
              await render();
            } catch (error) {
              toast(error.message || "Download non riuscito.", "error");
              download.disabled = false;
            }
          });
          download.disabled = data.offline;
          row.append(download);
        }
        list.append(row);
      }
    } catch (error) {
      status.textContent = error.message || "Credenziali non disponibili.";
    }
  };

  await render();
}

function openLocalCheck(instance) {
  const servers = Array.isArray(state.targets?.[instance.url])
    ? state.targets[instance.url]
    : [];
  const body = el("div", "local-check");
  const grid = el("div", "check-grid");
  const field = (labelText, control) => {
    const wrap = el("label", "field");
    wrap.append(el("span", "field-label", labelText), control);
    return wrap;
  };

  const serverLabel = (server) =>
    `${server.serverName}${server.environmentLabel ? ` · ${server.environmentLabel}` : ""}`;
  const serverListId = `check-servers-${Date.now()}`;
  const serverInput = el("input");
  serverInput.type = "text";
  serverInput.placeholder = servers.length
    ? "Cerca server…"
    : "Nessun server in cache";
  serverInput.disabled = servers.length === 0;
  serverInput.setAttribute("list", serverListId);
  const serverList = el("datalist");
  serverList.id = serverListId;
  for (const server of servers) {
    const option = el("option");
    option.value = serverLabel(server);
    serverList.append(option);
  }
  let selectedServerId = servers[0]?.serverId ?? null;
  if (servers[0]) serverInput.value = serverLabel(servers[0]);
  const selectServer = (value) => {
    const query = String(value || "").trim().toLowerCase();
    const match =
      servers.find((server) => serverLabel(server).toLowerCase() === query) ||
      servers.find((server) =>
        serverLabel(server).toLowerCase().startsWith(query),
      ) ||
      servers.find((server) => serverLabel(server).toLowerCase().includes(query));
    if (!match) {
      const current = servers.find(
        (server) => server.serverId === selectedServerId,
      );
      if (current) serverInput.value = serverLabel(current);
      return;
    }
    selectedServerId = match.serverId;
    serverInput.value = serverLabel(match);
    applyServer();
  };
  serverInput.addEventListener("change", () =>
    selectServer(serverInput.value),
  );
  serverInput.addEventListener("input", () => {
    const current = servers.find(
      (server) => serverLabel(server).toLowerCase() ===
        serverInput.value.trim().toLowerCase(),
    );
    if (current) selectServer(serverInput.value);
  });

  const kindSelect = el("select");
  for (const [value, label] of [
    ["tcp", "TCP"],
    ["tls", "TLS"],
    ["http", "HTTP"],
  ]) {
    const option = el("option", "", label);
    option.value = value;
    kindSelect.append(option);
  }

  const sourceSelect = el("select");
  for (const [value, label] of [
    ["local", "Dal Mac"],
    ["jump", "Via jump host"],
  ]) {
    const option = el("option", "", label);
    option.value = value;
    sourceSelect.append(option);
  }

  const hostInput = el("input");
  hostInput.type = "text";
  hostInput.placeholder = "Host o IP";
  const portInput = el("input");
  portInput.type = "number";
  portInput.min = "1";
  portInput.max = "65535";
  const pathInput = el("input");
  pathInput.type = "text";
  pathInput.value = "/";
  const secureInput = el("input");
  secureInput.type = "checkbox";
  const timeoutInput = el("input");
  timeoutInput.type = "number";
  timeoutInput.min = "1";
  timeoutInput.max = "30";
  timeoutInput.value = "5";

  const pathField = field("Percorso", pathInput);
  const secureField = field("HTTPS", secureInput);
  const serverField = field("Server", serverInput);
  serverField.append(serverList);
  grid.append(
    serverField,
    field("Tipo verifica", kindSelect),
    field("Origine", sourceSelect),
    field("Host", hostInput),
    field("Porta", portInput),
    field("Timeout (s)", timeoutInput),
    pathField,
    secureField,
  );

  const note = el(
    "p",
    "field-hint",
    "Il check via jump host usa il primo bastione della catena SSH e verifica solo TCP.",
  );
  const result = el("pre", "check-result");
  result.hidden = true;

  const selectedServer = () =>
    servers.find((server) => server.serverId === selectedServerId) ||
    servers[0] ||
    null;

  const applyServer = () => {
    const server = selectedServer();
    if (!server) return;
    hostInput.value = server.sshHost || "";
    portInput.value = String(server.sshPort || 22);
  };

  const syncForm = () => {
    const isHttp = kindSelect.value === "http";
    pathField.hidden = !isHttp;
    secureField.hidden = !isHttp;
    if (kindSelect.value !== "tcp") sourceSelect.value = "local";
    sourceSelect.disabled = servers.length === 0 || kindSelect.value !== "tcp";
    note.hidden = sourceSelect.value !== "jump";
  };

  const run = button("Esegui verifica", "primary", async () => {
    run.disabled = true;
    result.hidden = false;
    result.dataset.tone = "";
    result.textContent = "Verifica in corso…";
    try {
      const outcome = await api.runCheck(instance.url, {
        serverId: selectedServerId ? Number(selectedServerId) : null,
        kind: kindSelect.value,
        source: sourceSelect.value,
        host: hostInput.value,
        port: Number(portInput.value),
        path: pathInput.value,
        secure: secureInput.checked,
        timeoutMs: Number(timeoutInput.value || 5) * 1000,
      });
      result.dataset.tone = outcome.ok ? "ok" : "error";
      const target = `${outcome.kind.toUpperCase()} ${outcome.host}:${outcome.port}${outcome.path || ""}`;
      result.textContent =
        `${target} — ${outcome.ok ? "OK" : "KO"} · ${outcome.latencyMs} ms` +
        (outcome.status ? ` · ${outcome.status}` : "") +
        (outcome.detail ? `\n${outcome.detail}` : "");
    } catch (error) {
      result.dataset.tone = "error";
      result.textContent = error.message || "Verifica non riuscita.";
    } finally {
      run.disabled = false;
    }
  });

  kindSelect.addEventListener("change", syncForm);
  sourceSelect.addEventListener("change", syncForm);
  applyServer();
  syncForm();

  body.append(grid, note, run, result);
  openModal({
    eyebrow: "Verifica locale",
    title: instance.name,
    body,
    footer: (footer) =>
      footer.append(button("Chiudi", "secondary", () => closeModal())),
  });
}

function openSettings() {
  setDrawer(false);
  const current = state?.updates || updateState || { status: "idle" };
  const body = el("div", "update-card");
  body.dataset.updateCard = "true";
  const meta = el("div", "update-meta");
  meta.append(
    metaItem(
      "Versione installata",
      current.currentVersion || state?.version || "—",
    ),
  );
  meta.append(metaItem("Ultima release", current.latestVersion || "—"));
  meta.append(
    metaItem(
      "Ultimo controllo",
      current.checkedAt
        ? new Date(current.checkedAt).toLocaleString(shellLocale())
        : "—",
    ),
  );
  body.append(meta);
  body.append(
    el(
      "p",
      `update-status ${current.status || "idle"}`,
      current.message || "Nessun controllo eseguito.",
    ),
  );
  if (current.notes) body.append(el("pre", "update-notes", current.notes));
  const actions = el("div", "update-actions");
  actions.append(
    button("Controlla aggiornamenti", "secondary", () => void checkUpdates()),
  );
  if (current.status === "update") {
    actions.append(
      button("Installa e riavvia", "primary", () => void installUpdate()),
    );
  }
  if (current.releaseUrl) {
    actions.append(
      button("Apri release", "ghost", () =>
        void api.openExternal(current.releaseUrl),
      ),
    );
  }
  body.append(actions);
  const downloadBlock = el("div", "settings-download");
  downloadBlock.append(el("span", "meta-label", "Cartella download"));
  const downloadPath = el(
    "code",
    "settings-path",
    state?.downloadDirectory || "Predefinita (Download)",
  );
  const downloadActions = el("div", "settings-download-actions");
  downloadActions.append(
    button("Scegli…", "secondary", async () => {
      const selected = await api.chooseDownloadDirectory();
      if (selected) {
        downloadPath.textContent = selected;
        toast("Cartella download aggiornata.", "success");
      }
    }),
    button("Predefinita", "ghost", async () => {
      const selected = await api.resetDownloadDirectory();
      downloadPath.textContent = selected;
      toast("Cartella download aggiornata.", "success");
    }),
  );
  downloadBlock.append(downloadPath, downloadActions);
  body.append(downloadBlock);
  const localBlock = el("div", "settings-download");
  localBlock.append(el("span", "meta-label", "Istanze locali"));
  const localInstances = state?.localInstances || [];
  if (localInstances.length === 0) {
    localBlock.append(
      el("p", "field-hint", "Nessuna istanza locale configurata."),
    );
  }
  for (const local of localInstances) {
    const row = el("div", "settings-download-actions");
    row.append(el("span", "", local.name));
    row.append(
      el(
        "span",
        "field-hint",
        local.running ? "In esecuzione" : "Al prossimo avvio",
      ),
    );
    row.append(
      button("Rimuovi", "ghost", async () => {
        const next = await api.removeLocalInstance(local.id);
        state = next;
        render();
        openSettings();
        toast("Istanza locale rimossa.", "success");
      }),
    );
    localBlock.append(row);
    if (local.dataRoot)
      localBlock.append(el("p", "field-hint", local.dataRoot));
    if (local.keyStorage === "file") {
      localBlock.append(
        el(
          "p",
          "field-hint",
          "Archivio protetto non disponibile: la chiave delle credenziali è in un file locale 0600.",
        ),
      );
    }
  }
  const addRow = el("div", "settings-download-actions");
  const localName = input("text", "localName", "", "Nome istanza locale");
  addRow.append(
    localName,
    button("Aggiungi", "secondary", async () => {
      const name = localName.value.trim();
      if (!name) return;
      const response = await api.addLocalInstance(name);
      state = response.state;
      render();
      openSettings();
      toast("Istanza locale aggiunta: parte al prossimo avvio.", "success");
    }),
  );
  localBlock.append(addRow);
  localBlock.append(
    el(
      "p",
      "field-hint",
      "Ogni istanza ha database, backup e chiave separati sotto la cartella dati dell'app; le nuove istanze partono al prossimo avvio.",
    ),
  );
  if (state?.credentialKeyProtected) {
    localBlock.append(
      el(
        "p",
        "field-hint",
        "Chiave delle credenziali protetta dall'archivio di sistema.",
      ),
    );
  } else {
    const consent = document.createElement("label");
    consent.className = "settings-download-actions";
    const consentInput = document.createElement("input");
    consentInput.type = "checkbox";
    consentInput.checked = Boolean(state?.allowUnprotectedCredentialKey);
    consentInput.addEventListener("change", async () => {
      const enabled = await api.setAllowUnprotectedCredentialKey(
        consentInput.checked,
      );
      consentInput.checked = enabled;
      toast(
        enabled
          ? "Chiave credenziali non protetta consentita: riavvia l'app."
          : "Chiave credenziali non protetta non consentita.",
        "success",
      );
    });
    consent.append(
      consentInput,
      el("span", "", "Consenti chiave credenziali in file non protetto (0600)"),
    );
    localBlock.append(
      consent,
      el(
        "p",
        "field-hint",
        "Senza archivio protetto le istanze locali non partono finché non lo consenti.",
      ),
    );
  }
  body.append(localBlock);
  const languageBlock = el("div", "settings-language");
  const languageRow = el("div", "language-options");
  for (const option of window.desktopI18n?.languages || []) {
    const control = button(
      option.label,
      window.desktopI18n?.language === option.code ? "primary" : "secondary",
      () => window.desktopI18n?.setLanguage(option.code),
    );
    control.dataset.language = option.code;
    control.setAttribute(
      "aria-pressed",
      String(window.desktopI18n?.language === option.code),
    );
    languageRow.append(control);
  }
  languageBlock.append(el("span", "meta-label", "Lingua / Language"));
  languageBlock.append(languageRow);
  body.append(languageBlock);
  openModal({
    eyebrow: "Impostazioni",
    title: "Aggiornamenti",
    body,
    footer: (footer) =>
      footer.append(button("Chiudi", "secondary", () => closeModal())),
  });
}

function openModal({ eyebrow, title, body, footer, extra }) {
  els.modalEyebrow.textContent = eyebrow || "Istanza";
  els.modalTitle.textContent = title || "";
  els.modalBody.replaceChildren();
  if (body) els.modalBody.append(body);
  if (extra) els.modalBody.append(extra);
  els.modalFooter.replaceChildren();
  footer?.(els.modalFooter);
  els.modal.hidden = false;
  updateStage();
  requestAnimationFrame(() => {
    const focusable = els.modal.querySelector("input:not([readonly]), button.primary");
    focusable?.focus();
  });
}

function closeModal() {
  modalToken++;
  els.modal.hidden = true;
  lastBounds = "";
  sendBounds();
  updateStage();
}

els.settingsButton.addEventListener("click", () => openSettings());
els.updateButton.addEventListener("click", () => openSettings());
els.tabAdd.addEventListener("click", () => openConnectionPicker());
els.newInstance.addEventListener("click", () => openNewInstance());
els.newInstanceCard.addEventListener("click", () => openNewInstance());
els.home.addEventListener("click", () => void api.home());
els.tunnelToggle.addEventListener("click", () => setDrawer(!drawerOpen));
if (els.drawerSearch) {
  els.drawerSearch.value = drawerSearch;
  els.drawerSearch.addEventListener("input", () => {
    drawerSearch = els.drawerSearch.value;
    lastDrawerKey = "";
    renderDrawer();
  });
}
els.drawerClose.addEventListener("click", () => setDrawer(false));
els.drawerRefresh.addEventListener("click", async () => {
  for (const instance of state?.instances || []) {
    if (instance.auth?.tunnelAccess) await api.refresh(instance.url).catch(() => {});
  }
  toast("Tunnel aggiornati.", "success");
});
els.paletteTrigger.addEventListener("click", () => openPalette());
els.paletteInput.addEventListener("input", () => {
  palette.query = els.paletteInput.value;
  palette.index = 0;
  renderPaletteItems();
});
els.paletteInput.addEventListener("keydown", (event) => {
  const commands = filteredCommands();
  if (event.key === "ArrowDown") {
    event.preventDefault();
    palette.index = Math.min(commands.length - 1, palette.index + 1);
    updatePaletteSelection();
  } else if (event.key === "ArrowUp") {
    event.preventDefault();
    palette.index = Math.max(0, palette.index - 1);
    updatePaletteSelection();
  } else if (event.key === "Enter") {
    event.preventDefault();
    const command = commands[palette.index];
    if (command) runPaletteCommand(command);
  } else if (event.key === "Escape") {
    event.preventDefault();
    closePalette();
  }
});
els.palette.addEventListener("click", (event) => {
  if (event.target === els.palette) closePalette();
});
els.modal.addEventListener("click", (event) => {
  if (event.target === els.modal) closeModal();
});
els.modalClose.addEventListener("click", closeModal);

document.addEventListener("keydown", (event) => {
  const modifier = isMac ? event.metaKey : event.ctrlKey;
  if (modifier && event.key.toLowerCase() === "k") {
    event.preventDefault();
    if (palette.open) closePalette();
    else openPalette();
    return;
  }
  if (modifier && event.key === "0") {
    event.preventDefault();
    void api.home();
    return;
  }
  if (modifier && event.key.toLowerCase() === "t") {
    event.preventDefault();
    setDrawer(!drawerOpen);
    return;
  }
  if (modifier && event.key.toLowerCase() === "n") {
    event.preventDefault();
    openConnectionPicker();
    return;
  }
  if (event.key === "Escape") {
    if (!els.palette.hidden) closePalette();
    else if (!els.modal.hidden) closeModal();
    else if (drawerOpen) setDrawer(false);
  }
});

api.onCommand((command) => {
  if (command === "palette") {
    if (palette.open) closePalette();
    else openPalette();
  } else if (command === "open-connection") {
    openConnectionPicker();
  } else if (command === "new-instance") {
    openNewInstance();
  } else if (command === "toggle-drawer") {
    setDrawer(!drawerOpen);
  }
});

api.onState((next) => {
  state = next;
  updateState = next.updates || updateState;
  render();
});

api.onUpdate?.((next) => {
  updateState = next;
  if (state) state.updates = next;
  renderUpdate();
  if (els.modalBody.querySelector("[data-update-card]")) openSettings();
});

window.addEventListener("resize", () => {
  lastBounds = "";
  sendBounds();
});
new ResizeObserver(() => scheduleBounds()).observe(els.slot);

void api.state().then((next) => {
  state = next;
  updateState = next.updates || updateState;
  render();
});
