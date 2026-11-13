const { spawnSync } = require("node:child_process");
const electron = require("electron");

const result = spawnSync(electron, [".", "--disable-gpu"], {
  stdio: "inherit",
  env: {
    ...process.env,
    PROTO_TEST: "1",
    PROTO_CLEAN: "1",
  },
});

if (result.error) {
  console.error(result.error.message);
  process.exit(1);
}
process.exit(result.status ?? 1);
