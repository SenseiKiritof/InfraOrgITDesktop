const { contextBridge, ipcRenderer } = require("electron");

async function invoke(channel, ...args) {
  const result = await ipcRenderer.invoke(channel, ...args);
  if (result && result.__managerIpc === true) {
    if (!result.ok) throw new Error(result.error || "Operazione non riuscita.");
    return result.value;
  }
  return result;
}

function subscribe(channel, handler) {
  if (typeof handler !== "function") return () => {};
  const listener = (_event, payload) => handler(payload);
  ipcRenderer.on(channel, listener);
  return () => ipcRenderer.removeListener(channel, listener);
}

contextBridge.exposeInMainWorld("desktopManager", {
  platform: process.platform,
  state: () => invoke("manager:state"),
  onState: (handler) => subscribe("shell:state", handler),
  onCommand: (handler) => subscribe("shell:command", handler),
  setContentBounds: (bounds) => ipcRenderer.send("manager:content-bounds", bounds),

  addInstance: (value) => invoke("manager:add", value),
  updateInstance: (url, value) => invoke("manager:update", url, value),
  removeInstance: (url) => invoke("manager:remove", url),
  activate: (url) => invoke("manager:activate", url),
  home: () => invoke("manager:home"),
  closeTab: (url) => invoke("manager:close-tab", url),
  reload: (url) => invoke("manager:reload", url),
  refresh: (url) => invoke("manager:refresh", url),
  openExternal: (url) => invoke("manager:open-external", url),

  authStart: (url) => invoke("manager:auth-start", url),
  authCancel: (url) => invoke("manager:auth-cancel", url),
  authLogout: (url) => invoke("manager:auth-logout", url),
  authOpenVerification: (url) =>
    invoke("manager:auth-open-verification", url),

  startTunnel: (input) => invoke("manager:start-tunnel", input),
  stopTunnel: (id, url) => invoke("manager:stop-tunnel", id, url),
  restartTunnel: (id, url) => invoke("manager:restart-tunnel", id, url),
  forgetTunnel: (id) => invoke("manager:forget-tunnel", id),

  openTunnelsPage: (url) => invoke("manager:open-tunnels-page", url),
  openServerPage: (url, serverId) =>
    invoke("manager:open-server-page", url, serverId),
  serverList: (url) => invoke("manager:server-list", url),
});
