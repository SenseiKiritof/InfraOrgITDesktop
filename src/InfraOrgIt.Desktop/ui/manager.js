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
  const deviceLogin = document.querySelector("#device-login");
  const deviceCode = document.querySelector("#device-code");
  const deviceOpen = document.querySelector("#device-open");
  const deviceCancel = document.querySelector("#device-cancel");
  const tunnelStatus = document.querySelector("#tunnel-status");
  const tunnelTargets = document.querySelector("#tunnel-targets");
  const activeTunnels = document.querySelector("#active-tunnels");
  const noTunnels = document.querySelector("#no-tunnels");

  let instances = [];
  let busy = false;
  let adding = false;
  let tunnelInstance = null;
  let targets = [];
  let loginActive = false;
  let deviceAuth = null;

  const send = (payload) => {
    window.external.sendMessage(JSON.stringify(payload));
  };

  const setStatus = (message, isError = false) => {
    status.textContent = message;
    status.classList.toggle("error", isError);
  };

  const setTunnelStatus = (message, isError = false) => {
    tunnelStatus.textContent = message;
    tunnelStatus.classList.toggle("error", isError);
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
    renderTargets();
    setTunnelStatus("");
    tunnelPanel.hidden = false;
    tunnelPanel.scrollIntoView({ block: "start" });
  };

  const closeTunnelPanel = () => {
    cancelDeviceLogin();
    tunnelInstance = null;
    targets = [];
    tunnelFormReset();
    tunnelPanel.hidden = true;
  };

  const tunnelFormReset = () => {
    tokenForm.reset();
    setTunnelStatus("");
    renderTargets();
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
      if (server.jump) {
        detail += ` · jump ${server.jump.username}@${server.jump.host}:${server.jump.port}`;
      }
      meta.textContent = detail;
      card.append(title, meta);

      for (const target of server.targets) {
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
      const title = document.createElement("h3");
      title.textContent = `${tunnel.application} · ${tunnel.serverName}`;
      const detail = document.createElement("p");
      detail.className = "detail";
      detail.textContent = `${tunnel.instanceName} · 127.0.0.1:${tunnel.localPort} → :${tunnel.targetPort}`;
      text.append(title, detail);

      const stop = document.createElement("button");
      stop.type = "button";
      stop.className = "secondary danger";
      stop.textContent = "Chiudi";
      stop.addEventListener("click", () => {
        send({ type: "tunnels.stop", id: tunnel.id });
      });

      const actions = document.createElement("div");
      actions.className = "instance-actions";
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
        setStatus(message.message ?? "Errore sconosciuto.", true);
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
        } else if (message.status === "success") {
          resetDeviceLogin();
          setTunnelStatus(message.message ?? "Accesso riuscito.");
        } else if (message.status === "error") {
          resetDeviceLogin();
          setTunnelStatus(message.message ?? "Accesso non riuscito.", true);
        }
        break;
      case "tunnels.targets":
        if (tunnelInstance && message.url === tunnelInstance.url) {
          targets = message.items ?? [];
          renderTargets();
          setTunnelStatus(
            targets.length === 0
              ? "Nessun target disponibile per questo token."
              : "Target caricati. Scegli la porta locale e apri il tunnel.",
          );
        }
        break;
      case "tunnels.state":
        renderActiveTunnels(message.items ?? []);
        break;
      case "tunnels.log":
        setTunnelStatus(message.message ?? "");
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
    setTunnelStatus("Caricamento target…");
    send({
      type: "tunnels.list",
      url: tunnelInstance.url,
      name: tunnelInstance.name,
      token,
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
    });
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

  tunnelClose.addEventListener("click", closeTunnelPanel);

  if (!window.external) {
    setStatus("Questa pagina va aperta dall'app desktop.", true);
    return;
  }

  window.external.receiveMessage(onMessage);
  send({ type: "list" });
})();
