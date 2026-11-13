const { contextBridge, ipcRenderer } = require("electron");

// The bridge belongs to the instance origin only. A Keycloak login page or an
// external link loaded in the same tab never sees it, even during the OAuth
// redirects. The value is passed as an additional argument so no IPC is needed
// before the document exists.
const argument = process.argv.find((value) =>
  value.startsWith("--infraorgit-instance="),
);
const instanceOrigin = argument
  ? argument.slice("--infraorgit-instance=".length)
  : "";

if (instanceOrigin && window.location.origin === instanceOrigin) {
  const bridge = {
    sendMessage: (message) => {
      if (typeof message === "string")
        ipcRenderer.send("desktop-message", message);
    },
    receiveMessage: (handler) => {
      if (typeof handler !== "function") return;
      ipcRenderer.on("desktop-message", (_event, message) => handler(message));
    },
  };
  contextBridge.exposeInMainWorld("infraorgitDesktop", bridge);
}
