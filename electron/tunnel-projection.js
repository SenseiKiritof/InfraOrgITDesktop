const HEARTBEAT_MS = 30_000;
const MAX_DETAIL = 500;

function cleanDetail(value) {
  if (typeof value !== "string" || !value.trim()) return null;
  return value.replace(/Bearer\s+\S+/gi, "Bearer ***").slice(0, MAX_DETAIL);
}

function basePayload(row) {
  return {
    serverId: row.serverId,
    serverApplicationId: row.targetId ?? null,
    mode: row.mode,
    serviceId: row.serviceId ?? null,
    tunnelServiceId: row.serviceId ?? null,
    targetHost: row.targetHost ?? null,
    targetPort: row.targetPort ?? null,
    localPort: row.localPort ?? null,
    remotePort: row.remotePort ?? null,
  };
}

function eventPayload(row, kind, { errorCode = null, detail = null } = {}) {
  return {
    ...basePayload(row),
    kind,
    errorCode,
    detail: cleanDetail(detail),
    correlationId: row.id,
  };
}

function sessionPayload(row, status, { errorCode = null, detail = null } = {}) {
  return {
    ...basePayload(row),
    sessionKey: row.id,
    status,
    errorCode,
    detail: cleanDetail(detail),
  };
}

/**
 * Keeps the central `/tunnels` projection in sync with the local SSH
 * processes. The transport is injected so the manager stays testable and the
 * main process can choose the BFF session with CSRF.
 */
class TunnelProjection {
  constructor({
    post,
    intervalMs = HEARTBEAT_MS,
    setIntervalFn = setInterval,
    clearIntervalFn = clearInterval,
    logger = console,
  } = {}) {
    if (typeof post !== "function")
      throw new TypeError("TunnelProjection richiede post.");
    this.post = post;
    this.intervalMs = intervalMs;
    this.setIntervalFn = setIntervalFn;
    this.clearIntervalFn = clearIntervalFn;
    this.logger = logger;
    this.states = new Map();
  }

  sync(rows = []) {
    const current = new Set();
    for (const row of rows) {
      if (!row || !row.id || !row.url) continue;
      const key = `${row.url}\u001f${row.id}`;
      current.add(key);
      const state = this.states.get(key);
      if (row.status === "Active") {
        if (!state) {
          const next = { row, started: false, timer: null };
          next.timer = this.setIntervalFn(() => {
            if (next.row.status === "Active") void this.publish(next, "active");
          }, this.intervalMs);
          next.timer?.unref?.();
          this.states.set(key, next);
          void this.publish(next, "active");
        } else {
          state.row = row;
        }
        continue;
      }
      if (state) {
        this.states.delete(key);
        this.clearIntervalFn(state.timer);
        state.row = row;
        void this.publish(state, row.error ? "error" : "stop");
      }
    }
    for (const [key, state] of [...this.states]) {
      if (current.has(key)) continue;
      this.states.delete(key);
      this.clearIntervalFn(state.timer);
    }
  }

  async publish(state, kind) {
    const row = state.row;
    try {
      if (kind === "active") {
        if (!state.started) {
          state.started = await this.post(
            row,
            "event",
            eventPayload(row, "start"),
          );
        }
        await this.post(row, "session", sessionPayload(row, "active"));
        return;
      }
      const errorCode = kind === "error" ? "ssh_failed" : null;
      if (state.started) {
        await this.post(
          row,
          "event",
          eventPayload(row, kind, { errorCode, detail: row.error }),
        );
      }
      await this.post(
        row,
        "session",
        sessionPayload(row, kind === "error" ? "error" : "stopped", {
          errorCode,
          detail: row.error,
        }),
      );
    } catch (error) {
      this.logger.warn("Tunnel projection failed:", error.message);
    }
  }

  dispose() {
    for (const state of this.states.values())
      this.clearIntervalFn(state.timer);
    this.states.clear();
  }
}

module.exports = {
  TunnelProjection,
  cleanDetail,
  eventPayload,
  sessionPayload,
};
