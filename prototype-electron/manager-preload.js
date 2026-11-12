const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("desktopManager", {
  list: () => ipcRenderer.invoke("manager:list"),
  open: (instance) => ipcRenderer.invoke("manager:open", instance),
  add: (value) => ipcRenderer.invoke("manager:add", value),
  remove: (url) => ipcRenderer.invoke("manager:remove", url),
});
