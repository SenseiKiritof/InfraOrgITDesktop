(() => {
  const form = document.querySelector("#add-form");
  const nameInput = document.querySelector("#name");
  const urlInput = document.querySelector("#url");
  const status = document.querySelector("#status");
  const list = document.querySelector("#instances");
  const empty = document.querySelector("#empty");
  const count = document.querySelector("#count");

  const tunnelPanel = document.querySelector("#tunnel-panel");
  const tunnelTitle = document.querySelector("#tunnel-title");
  const tunnelClose = document.querySelector("#tunnel-close");
  const tokenForm = document.querySelector("#token-form");
  const tokenInput = document.querySelector("#token");
  const keycloakLogin = document.querySelector("#keycloak-login");
  const keycloakLogout = document.querySelector("#keycloak-logout");
  const sessionState = document.querySelector("#session-state");
  const autoStartToggle = document.querySelector("#auto-start");
  const deviceLogin = document.querySelector("#device-login");
  const deviceCode = document.querySelector("#device-code");
  const deviceOpen = document.querySelector("#device-open");
  const deviceCancel = document.querySelector("#device-cancel");
  const tunnelStatus = document.querySelector("#tunnel-status");
  const tunnelTargets = document.querySelector("#tunnel-targets");
  const activeTunnels = document.querySelector("#active-tunnels");
  const noTunnels = document.querySelector("#no-tunnels");

  const updateCurrent = document.querySelector("#update-current");
  const updateCheck = document.querySelector("#update-check");
  const updateDownload = document.querySelector("#update-download");
  const updateConfirm = document.querySelector("#update-confirm");
  const updateOpen = document.querySelector("#update-open");
  const updateStatus = document.querySelector("#update-status");
  const updateRelease = document.querySelector("#update-release");
  const updateNotes = document.querySelector("#update-notes");
  const updatePath = document.querySelector("#update-path");

  let instances = [];
  let busy = false;
  let adding = false;
  let tunnelInstance = null;
  let targets = [];
  let loginActive = false;
  let deviceAuth = null;
  let releaseUrl = null;
  let downloadAssetName = null;

  const send = (payload) => {
    window.external.sendMessage(JSON.stringify(payload));
  };

  const autoStartValue = () => autoStartToggle.checked;

  const modeLabels = {
    Local: "Locale",
    Dynamic: "Dinamico",
    Remote: "Remoto",
  };

  const modeLabel = (mode) => modeLabels[mode] ?? mode;

  const setStatus = (message, isError = false) => {
    status.textContent = message;
    status.classList.toggle("error", isError);
  };

  const setTunnelStatus = (message, isError = false) => {
    tunnelStatus.textContent = message;
    tunnelStatus.classList.toggle("error", isError);
  };

  const setUpdateStatus = (message, tone = "") => {
    updateStatus.textContent = message;
    updateStatus.classList.toggle("error", tone === "error");
    updateStatus.classList.toggle("warning", tone === "warning");
  };

  const resetUpdateDownload = () => {
    downloadAssetName = null;
    updateDownload.hidden = true;
    updateDownload.disabled = false;
    updateConfirm.hidden = true;
    updateConfirm.disabled = false;
    updatePath.hidden = true;
    updatePath.textContent = "";
  };

  const setSessionState = (label) => {
    sessionState.textContent = label ?? "";
    sessionState.hidden = !label;
    keycloakLogout.hidden = !label;
  };

  const resetDeviceLogin = () => {
    loginActive = false;
    deviceAuth = null;
    deviceLogin.hidden = true;
    deviceCode.textContent = "";
    deviceOpen.disabled = true;
    keycloakLogin.disabled = false;
  };

  const cancelDeviceLogin = () => {
    if (!loginActive) {
      return;
    }

    const url = deviceAuth?.url ?? tunnelInstance?.url;
    if (url) {
      send({ type: "tunnels.login.cancel", url });
    }
    resetDeviceLogin();
  };

  const setBusy = (value) => {
    busy = value;
    for (const button of form.querySelectorAll("button")) {
      button.disabled = value;
    }
  };

  const render = () => {
    list.replaceChildren();
    empty.hidden = instances.length > 0;
    count.textContent =
      instances.length === 1 ? "1 istanza" : `${instances.length} istanze`;

    for (const instance of instances) {
      const item = document.createElement("li");
      item.className = "instance";

      const text = document.createElement("div");
      const title = document.createElement("h3");
      title.textContent = instance.name;
      const url = document.createElement("p");
      url.textContent = instance.url;
      text.append(title, url);

      const actions = document.createElement("div");
      actions.className = "instance-actions";

      const tunnel = document.createElement("button");
      tunnel.type = "button";
      tunnel.className = "secondary";
      tunnel.textContent = "Tunnel";
      tunnel.addEventListener("click", () => openTunnelPanel(instance));

      const open = document.createElement("button");
      open.type = "button";
      open.textContent = "Apri";
      open.addEventListener("click", () => {
        setBusy(true);
        setStatus(`Apertura di ${instance.name}…`);
        send({ type: "open", url: instance.url });
      });

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "secondary danger";
      remove.textContent = "Rimuovi";
      remove.addEventListener("click", () => {
        if (tunnelInstance?.url === instance.url) {
          closeTunnelPanel();
        }
        setBusy(true);
        send({
          type: "save",
          instances: instances.filter((item) => item.url !== instance.url),
        });
      });

      actions.append(tunnel, open, remove);
      item.append(text, actions);
      list.append(item);
    }
  };

  const openTunnelPanel = (instance) => {
    cancelDeviceLogin();
    tunnelInstance = instance;
    tunnelTitle.textContent = `Tunnel — ${instance.name}`;
    targets = [];
    tokenForm.reset();
    resetDeviceLogin();
    setSessionState("");
    renderTargets();
    setTunnelStatus("");
    tunnelPanel.hidden = false;
    tunnelPanel.scrollIntoView({ block: "start" });
    send({
      type: "tunnels.restore",
      url: instance.url,
      name: instance.name,
      autoStart: autoStartValue(),
    });
  };

  const closeTunnelPanel = () => {
    cancelDeviceLogin();
    tunnelInstance = null;
    targets = [];
    tunnelFormReset();
    setSessionState("");
    tunnelPanel.hidden = true;
  };

  const tunnelFormReset = () => {
    tokenForm.reset();
    setTunnelStatus("");
    renderTargets();
  };

  const servicePorts = (service) =>
    service.mode === "Remote"
      ? `remota :${service.remotePort ?? "—"}`
      : `locale :${service.localPort ?? "auto"}`;

  const createServiceRow = (server, service) => {
    const row = document.createElement("div");
    row.className = "target-row service-row";

    const text = document.createElement("div");
    const name = document.createElement("p");
    name.className = "service-name";
    const label = document.createElement("span");
    label.textContent = service.name;
    const badge = document.createElement("span");
    badge.className = `badge mode-${(service.mode ?? "").toLowerCase()}`;
    badge.textContent = modeLabel(service.mode);
    name.append(label, badge);
    if (service.autoStart) {
      const auto = document.createElement("span");
      auto.className = "badge auto";
      auto.textContent = "auto";
      name.append(auto);
    }

    const meta = document.createElement("p");
    meta.className = "service-meta";
    const target =
      service.mode === "Dynamic"
        ? "SOCKS"
        : `${service.targetHost ?? ""}:${service.targetPort ?? ""}`;
    meta.textContent = `${target} · ${servicePorts(service)} · ${service.kind ?? ""}`;
    text.append(name, meta);

    const controls = document.createElement("div");
    controls.className = "port";

    const start = document.createElement("button");
    start.type = "button";
    start.textContent = "Avvia";
    start.disabled = service.enabled === false;
    start.addEventListener("click", () => {
      setTunnelStatus(`Avvio del servizio ${service.name}…`);
      send({
        type: "tunnels.start",
        url: tunnelInstance.url,
        serverId: server.serverId,
        serviceId: service.id,
      });
    });
    controls.append(start);

    row.append(text, controls);
    return row;
  };

  const renderTargets = () => {
    tunnelTargets.replaceChildren();
    if (!tunnelInstance || targets.length === 0) {
      const message = document.createElement("p");
      message.className = "empty";
      message.textContent = tunnelInstance
        ? "Nessun target disponibile per questo token."
        : "";
      if (message.textContent) {
        tunnelTargets.append(message);
      }
      return;
    }

    for (const server of targets) {
      const card = document.createElement("div");
      card.className = "target-server";

      const title = document.createElement("h4");
      title.textContent = server.serverName;
      const meta = document.createElement("p");
      meta.className = "meta";
      let detail = `${server.environmentLabel} · ${server.username}@${server.sshHost}:${server.sshPort}`;
      const jumps = server.jumps?.length
        ? server.jumps
        : server.jump
          ? [server.jump]
          : [];
      if (jumps.length > 0) {
        const chain = jumps
          .map((jump) => `${jump.username}@${jump.host}:${jump.port}`)
          .join(" → ");
        detail += ` · jump ${chain}`;
      }
      meta.textContent = detail;
      card.append(title, meta);

      for (const target of server.targets ?? []) {
        const row = document.createElement("div");
        row.className = "target-row";

        const description = document.createElement("p");
        description.textContent = `${target.application} ${target.version} · ${target.targetHost}:${target.targetPort}`;

        const controls = document.createElement("div");
        controls.className = "port";

        const port = document.createElement("input");
        port.type = "number";
        port.min = "1024";
        port.max = "65535";
        port.placeholder = "auto";
        port.value = target.suggestedLocalPort ?? "";
        port.setAttribute(
          "aria-label",
          `Porta locale per ${target.application} ${target.version}`,
        );

        const start = document.createElement("button");
        start.type = "button";
        start.textContent = "Apri";
        start.addEventListener("click", () => {
          let localPort = null;
          if (port.value.trim() !== "") {
            localPort = Number(port.value);
            if (
              !Number.isInteger(localPort) ||
              localPort < 1024 ||
              localPort > 65535
            ) {
              setTunnelStatus("La porta locale deve essere tra 1024 e 65535.", true);
              return;
            }
          }

          setTunnelStatus(`Avvio del tunnel per ${target.application}…`);
          send({
            type: "tunnels.start",
            url: tunnelInstance.url,
            serverId: server.serverId,
            targetId: target.serverApplicationId,
            localPort,
          });
        });

        controls.append(port, start);
        row.append(description, controls);
        card.append(row);
      }

      const services = server.services ?? [];
      if (services.length > 0) {
        const serviceTitle = document.createElement("p");
        serviceTitle.className = "services-title";
        serviceTitle.textContent = "Servizi personalizzati";
        card.append(serviceTitle);
        for (const service of services) {
          card.append(createServiceRow(server, service));
        }
      }

      tunnelTargets.append(card);
    }
  };

  const renderActiveTunnels = (items) => {
    activeTunnels.replaceChildren();
    noTunnels.hidden = items.length > 0;

    for (const tunnel of items) {
      const item = document.createElement("li");
      item.className = "instance";

      const text = document.createElement("div");
      const mode = tunnel.mode ?? "Local";
      const title = document.createElement("h3");
      title.textContent = `${tunnel.application} · ${tunnel.serverName}`;
      const badge = document.createElement("span");
      badge.className = `badge mode-${mode.toLowerCase()}`;
      badge.textContent = modeLabel(mode);
      title.append(" ", badge);

      const detail = document.createElement("p");
      detail.className = "detail";
      if (mode === "Remote") {
        detail.textContent = `${tunnel.instanceName} · remota :${tunnel.remotePort} → ${tunnel.targetHost}:${tunnel.targetPort}`;
      } else if (mode === "Dynamic") {
        detail.textContent = `${tunnel.instanceName} · SOCKS 127.0.0.1:${tunnel.localPort}`;
      } else {
        detail.textContent = `${tunnel.instanceName} · 127.0.0.1:${tunnel.localPort} → ${tunnel.targetHost ?? ""}:${tunnel.targetPort}`;
      }
      text.append(title, detail);

      const actions = document.createElement("div");
      actions.className = "instance-actions";

      if (tunnel.openUrl) {
        const open = document.createElement("button");
        open.type = "button";
        open.className = "secondary";
        open.textContent = "Apri";
        open.title = "Apri nel browser";
        open.addEventListener("click", () => {
          send({ type: "open-external", url: tunnel.openUrl });
        });
        actions.append(open);
      }

      const stop = document.createElement("button");
      stop.type = "button";
      stop.className = "secondary danger";
      stop.textContent = "Chiudi";
      stop.addEventListener("click", () => {
        send({ type: "tunnels.stop", id: tunnel.id });
      });

      actions.append(stop);

      item.append(text, actions);
      activeTunnels.append(item);
    }
  };

  const onMessage = (raw) => {
    let message;
    try {
      message = JSON.parse(raw);
    } catch {
      setBusy(false);
      setStatus("Risposta non valida dall'app.", true);
      return;
    }

    setBusy(false);

    switch (message.type) {
      case "instances":
        instances = message.instances ?? [];
        if (message.version) {
          updateCurrent.textContent = `Versione ${message.version}`;
        }
        render();
        setStatus("");
        if (adding) {
          adding = false;
          form.reset();
        }
        if (
          tunnelInstance &&
          !instances.some((item) => item.url === tunnelInstance.url)
        ) {
          closeTunnelPanel();
        }
        break;
      case "opened":
        setStatus("Istanza aperta in una nuova finestra.");
        break;
      case "error":
        adding = false;
        updateCheck.disabled = false;
        setStatus(message.message ?? "Errore sconosciuto.", true);
        break;
      case "update.status": {
        updateCheck.disabled = false;
        resetUpdateDownload();
        if (message.currentVersion) {
          updateCurrent.textContent = `Versione ${message.currentVersion}`;
        }
        if (message.failed) {
          releaseUrl = null;
          updateOpen.hidden = true;
          updateRelease.hidden = true;
          updateNotes.hidden = true;
          setUpdateStatus(
            message.message ?? "Controllo aggiornamenti non riuscito.",
            "error",
          );
          break;
        }
        if (!message.isNewer) {
          releaseUrl = null;
          updateOpen.hidden = true;
          updateRelease.hidden = true;
          updateNotes.hidden = true;
          setUpdateStatus(message.message ?? "Hai già l'ultima versione.");
          break;
        }
        releaseUrl = message.releaseUrl ?? null;
        updateOpen.hidden = !releaseUrl;
        downloadAssetName = message.downloadAssetName ?? null;
        updateDownload.hidden = !downloadAssetName;
        updateRelease.textContent =
          message.releaseName ?? `Versione ${message.latestVersion}`;
        updateRelease.hidden = false;
        updateNotes.textContent = message.releaseNotes ?? "";
        updateNotes.hidden = !message.releaseNotes;
        setUpdateStatus(
          message.message ??
            `Nuova versione disponibile: ${message.latestVersion}.`,
        );
        break;
      }
      case "update.download.progress": {
        const percent = message.percent;
        setUpdateStatus(
          Number.isInteger(percent)
            ? `Download in corso… ${percent}%`
            : "Download in corso…",
        );
        break;
      }
      case "update.download.status": {
        updateDownload.disabled = false;
        updateConfirm.disabled = false;
        if (message.requiresConfirmation) {
          updateConfirm.hidden = false;
          setUpdateStatus(
            message.message ??
              "Checksum SHA-256 non disponibile: conferma il download.",
            "warning",
          );
          break;
        }
        updateConfirm.hidden = true;
        if (!message.succeeded) {
          setUpdateStatus(
            message.message ?? "Download non riuscito.",
            "error",
          );
          break;
        }
        if (message.path) {
          updatePath.textContent = `File salvato: ${message.path}`;
          updatePath.hidden = false;
        }
        setUpdateStatus(
          message.message ?? "Download completato.",
          message.checksumVerified ? "" : "warning",
        );
        break;
      }
      case "external.error":
        setUpdateStatus(
          message.message ?? "Apertura del browser non riuscita.",
          "error",
        );
        break;
      case "tunnels.auth":
        if (!tunnelInstance || message.url !== tunnelInstance.url) {
          break;
        }
        if (message.status === "pending") {
          loginActive = true;
          deviceAuth = {
            url: message.url,
            verificationUri: message.verificationUri,
            verificationUriComplete:
              message.verificationUriComplete ?? message.verificationUri,
          };
          deviceCode.textContent = message.userCode ?? "";
          deviceLogin.hidden = false;
          deviceOpen.disabled = false;
          keycloakLogin.disabled = true;
          setTunnelStatus("In attesa di conferma nel browser…");
        } else if (message.status === "restoring") {
          setTunnelStatus("Ripristino della sessione salvata…");
        } else if (message.status === "none") {
          setTunnelStatus(
            "Nessuna sessione salvata. Accedi con Keycloak o incolla un token.",
          );
        } else if (message.status === "restored") {
          resetDeviceLogin();
          setSessionState(
            message.saved
              ? "Sessione salvata · ripristinata"
              : "Sessione attiva · ripristinata",
          );
          setTunnelStatus(message.message ?? "Sessione ripristinata.");
        } else if (message.status === "success") {
          resetDeviceLogin();
          setSessionState(
            message.saved ? "Sessione salvata" : "Sessione attiva (non salvata)",
          );
          setTunnelStatus(message.message ?? "Accesso riuscito.");
        } else if (message.status === "logout") {
          resetDeviceLogin();
          targets = [];
          renderTargets();
          tokenForm.reset();
          setSessionState("");
          setTunnelStatus(message.message ?? "Sessione eliminata.");
        } else if (message.status === "error") {
          resetDeviceLogin();
          if (message.requiresLogin) {
            targets = [];
            renderTargets();
            setSessionState("");
          }
          setTunnelStatus(message.message ?? "Accesso non riuscito.", true);
        }
        break;
      case "tunnels.targets":
        if (tunnelInstance && message.url === tunnelInstance.url) {
          targets = message.items ?? [];
          renderTargets();
          if (sessionState.hidden) {
            setSessionState("Sessione attiva");
          }
          setTunnelStatus(
            targets.length === 0
              ? "Nessun target disponibile per questo token."
              : "Target e servizi caricati. Avvia un tunnel o un servizio.",
          );
        }
        break;
      case "tunnels.state":
        renderActiveTunnels(message.items ?? []);
        break;
      case "tunnels.log":
        setTunnelStatus(message.message ?? "");
        break;
      case "tunnels.warning":
        if (tunnelInstance) {
          setTunnelStatus(message.message ?? "", false);
        } else {
          setStatus(message.message ?? "");
        }
        break;
      case "tunnels.error":
        resetDeviceLogin();
        setTunnelStatus(message.message ?? "Errore tunnel.", true);
        break;
      default:
        setStatus("Risposta non riconosciuta.", true);
        break;
    }
  };

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    if (busy) {
      return;
    }

    const name = nameInput.value.trim();
    const url = urlInput.value.trim();
    if (!name || !url) {
      setStatus("Compila nome e URL.", true);
      return;
    }

    setBusy(true);
    setStatus("Salvataggio…");
    adding = true;
    send({
      type: "save",
      instances: [...instances, { name, url }],
    });
  });

  tokenForm.addEventListener("submit", (event) => {
    event.preventDefault();
    if (!tunnelInstance) {
      return;
    }

    const token = tokenInput.value.trim();
    if (!token) {
      setTunnelStatus("Inserisci il token personale.", true);
      return;
    }

    tokenInput.value = "";
    cancelDeviceLogin();
    targets = [];
    renderTargets();
    setSessionState("");
    setTunnelStatus("Caricamento target…");
    send({
      type: "tunnels.list",
      url: tunnelInstance.url,
      name: tunnelInstance.name,
      token,
      autoStart: autoStartValue(),
    });
  });

  keycloakLogin.addEventListener("click", () => {
    if (!tunnelInstance) {
      return;
    }

    cancelDeviceLogin();
    targets = [];
    renderTargets();
    tokenForm.reset();
    resetDeviceLogin();
    loginActive = true;
    keycloakLogin.disabled = true;
    setTunnelStatus("Avvio dell'accesso con Keycloak…");
    send({
      type: "tunnels.login",
      url: tunnelInstance.url,
      name: tunnelInstance.name,
      autoStart: autoStartValue(),
    });
  });

  keycloakLogout.addEventListener("click", () => {
    if (!tunnelInstance) {
      return;
    }

    cancelDeviceLogin();
    targets = [];
    renderTargets();
    tokenForm.reset();
    setSessionState("");
    setTunnelStatus("Rimozione della sessione…");
    send({ type: "tunnels.logout", url: tunnelInstance.url });
  });

  deviceOpen.addEventListener("click", () => {
    const url =
      deviceAuth?.verificationUriComplete ?? deviceAuth?.verificationUri;
    if (!url) {
      return;
    }
    send({ type: "auth.open", url });
  });

  deviceCancel.addEventListener("click", () => {
    cancelDeviceLogin();
    setTunnelStatus("Accesso annullato.");
  });

  updateCheck.addEventListener("click", () => {
    updateCheck.disabled = true;
    releaseUrl = null;
    updateOpen.hidden = true;
    updateRelease.hidden = true;
    updateNotes.hidden = true;
    resetUpdateDownload();
    setUpdateStatus("Controllo aggiornamenti…");
    send({ type: "update.check" });
  });

  updateDownload.addEventListener("click", () => {
    if (!downloadAssetName) {
      return;
    }
    updateDownload.disabled = true;
    updateConfirm.hidden = true;
    updatePath.hidden = true;
    setUpdateStatus(`Download di ${downloadAssetName}…`);
    send({ type: "update.download" });
  });

  updateConfirm.addEventListener("click", () => {
    updateConfirm.disabled = true;
    updateDownload.disabled = true;
    setUpdateStatus("Download senza verifica dello SHA-256…", "warning");
    send({ type: "update.download", confirm: true });
  });

  updateOpen.addEventListener("click", () => {
    if (!releaseUrl) {
      return;
    }
    send({ type: "open-external", url: releaseUrl });
  });

  tunnelClose.addEventListener("click", closeTunnelPanel);

  if (!window.external) {
    setStatus("Questa pagina va aperta dall'app desktop.", true);
    return;
  }

  window.external.receiveMessage(onMessage);
  send({ type: "list" });
})();
