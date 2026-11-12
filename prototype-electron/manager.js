const form = document.querySelector("#add-form");
const nameInput = document.querySelector("#name");
const urlInput = document.querySelector("#url");
const list = document.querySelector("#instances");
const empty = document.querySelector("#empty");
const count = document.querySelector("#count");
const status = document.querySelector("#status");

let instances = [];

function setStatus(message, error = false) {
  status.textContent = message;
  status.classList.toggle("error", error);
}

function render() {
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
    open.addEventListener("click", () => void window.desktopManager.open(instance));

    const tunnels = document.createElement("button");
    tunnels.type = "button";
    tunnels.className = "secondary";
    tunnels.textContent = "Tunnel";
    tunnels.addEventListener("click", () =>
      void window.desktopManager.open({
        name: `${instance.name} — Tunnel`,
        url: `${instance.url.replace(/\/$/, "")}/tunnels`,
      }),
    );

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "danger";
    remove.textContent = "Rimuovi";
    remove.addEventListener("click", async () => {
      instances = await window.desktopManager.remove(instance.url);
      render();
    });

    actions.append(tunnels, open, remove);
    item.append(text, actions);
    list.append(item);
  }
}

async function refresh() {
  instances = await window.desktopManager.list();
  render();
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  setStatus("");
  try {
    instances = await window.desktopManager.add({
      name: nameInput.value,
      url: urlInput.value,
    });
    form.reset();
    render();
    setStatus("Istanza aggiunta.");
  } catch (error) {
    setStatus(error.message || "Aggiunta non riuscita.", true);
  }
});

void refresh();
