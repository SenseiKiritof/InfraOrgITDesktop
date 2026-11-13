#!/usr/bin/env node
const fs = require("fs");
fs.appendFileSync(process.env.FAKE_SSH_LOG || "/tmp/fake-ssh.log", JSON.stringify(process.argv.slice(2)) + "\n");
setInterval(() => {}, 1000);
