const { contextBridge, ipcRenderer } = require("electron");

const bridge = {
  sendMessage: (message) => ipcRenderer.send("desktop-message", message),
  receiveMessage: (handler) => {
    ipcRenderer.on("desktop-message", (_event, message) => handler(message));
  },
};
contextBridge.exposeInMainWorld("infraorgitDesktop", bridge);
// Compatibility for the Photino-era helper; Electron's built-in external may
// be read-only, so the explicit name is the primary bridge.
contextBridge.exposeInMainWorld("external", bridge);
