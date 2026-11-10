(() => {
  const form = document.querySelector("#add-form");
  const nameInput = document.querySelector("#name");
  const urlInput = document.querySelector("#url");
  const status = document.querySelector("#status");
  const list = document.querySelector("#instances");
  const empty = document.querySelector("#empty");
  const count = document.querySelector("#count");

  let instances = [];
  let busy = false;
  let adding = false;

  const send = (payload) => {
    window.external.sendMessage(JSON.stringify(payload));
  };

  const setStatus = (message, isError = false) => {
    status.textContent = message;
    status.classList.toggle("error", isError);
  };

  const setBusy = (value) => {
    busy = value;
    for (const button of document.querySelectorAll("button")) {
      button.disabled = value;
    }
  };

  const render = () => {
    list.replaceChildren();
    empty.hidden = instances.length > 0;
    count.textContent = instances.length === 1 ? "1 istanza" : `${instances.length} istanze`;

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
        setBusy(true);
        send({
          type: "save",
          instances: instances.filter((item) => item.url !== instance.url),
        });
      });

      actions.append(open, remove);
      item.append(text, actions);
      list.append(item);
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
        break;
      case "opened":
        setStatus("Istanza aperta in una nuova finestra.");
        break;
      case "error":
        adding = false;
        setStatus(message.message ?? "Errore sconosciuto.", true);
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

  if (!window.external) {
    setStatus("Questa pagina va aperta dall'app desktop.", true);
    return;
  }

  window.external.receiveMessage(onMessage);
  send({ type: "list" });
})();
