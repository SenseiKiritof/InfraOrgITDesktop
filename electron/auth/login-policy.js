/**
 * Decides how the desktop signs in to an instance. Device flow is the default:
 * the system browser confirms the code and the app stores an offline token.
 * "web" keeps the embedded Keycloak login as the only path.
 */
function planDeviceLogin({
  mode,
  loginMode = "device",
  saved = false,
  attempted = false,
}) {
  if (loginMode !== "device" || attempted) return null;
  if (mode === "none" || mode === "expired" || mode === "revoked")
    return "inline";
  // A valid web session still lacks the offline token: upgrade it in the
  // background without blocking the tab.
  if (mode === "session" && !saved) return "background";
  return null;
}

module.exports = { planDeviceLogin };
