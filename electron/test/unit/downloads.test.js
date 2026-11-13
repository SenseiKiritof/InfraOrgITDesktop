const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const {
  DownloadTracker,
  MAX_FILE_NAME,
  fileNameFromUrl,
  sanitizeFileName,
  uniqueFilePath,
} = require("../../downloads");

test("sanitizeFileName removes separators and control chars", () => {
  assert.equal(sanitizeFileName("../../etc/passwd"), ".._.._etc_passwd");
  assert.equal(sanitizeFileName(" lead\ntask.sql.gz "), "lead_task.sql.gz");
  assert.equal(sanitizeFileName(".bashrc"), ".bashrc");
  assert.equal(sanitizeFileName("..."), "download");
  assert.equal(sanitizeFileName(""), "download");
  assert.equal(sanitizeFileName("a".repeat(200)).length, MAX_FILE_NAME);
});

test("DownloadTracker tracks progress and prunes only finished downloads", () => {
  let now = 1_000;
  const tracker = new DownloadTracker({ now: () => now });
  const active = tracker.add({ fileName: "dump.sql.gz", totalBytes: 100 });
  const done = tracker.add({ fileName: "report.pdf", totalBytes: 10 });
  assert.equal(tracker.list().length, 2);

  now = 2_000;
  tracker.update(active.id, { receivedBytes: 40 });
  tracker.finish(done.id, "completed", { receivedBytes: 10 });
  assert.deepEqual(
    tracker.list().map((entry) => [entry.fileName, entry.state]),
    [
      ["dump.sql.gz", "progressing"],
      ["report.pdf", "completed"],
    ],
  );

  now = 3_100;
  tracker.prune(1_000);
  assert.deepEqual(
    tracker.list().map((entry) => entry.fileName),
    ["dump.sql.gz"],
  );

  tracker.finish(active.id, "failed");
  now = 10_000;
  tracker.prune(1_000);
  assert.equal(tracker.list().length, 0);
});

test("uniqueFilePath keeps the extension and avoids overwriting", () => {
  const directory = path.join("root", "downloads");
  const existing = new Set([
    path.join(directory, "report.pdf"),
    path.join(directory, "report (1).pdf"),
  ]);
  assert.equal(
    uniqueFilePath(directory, "report.pdf", (name) => existing.has(name)),
    path.join(directory, "report (2).pdf"),
  );
  assert.equal(
    uniqueFilePath(directory, "../x.txt", () => false),
    path.join(directory, ".._x.txt"),
  );
});

test("fileNameFromUrl keeps dotfiles and ignores other URLs", () => {
  assert.equal(
    fileNameFromUrl(
      "https://localhost/api/servers/37/files/download?path=%2Fhome%2Fx3solutions%2F.viminfo",
    ),
    ".viminfo",
  );
  assert.equal(fileNameFromUrl("https://localhost/api/health"), "");
  assert.equal(fileNameFromUrl("not a url"), "");
});
