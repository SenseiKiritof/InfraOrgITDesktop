const test = require("node:test");
const assert = require("node:assert/strict");

const {
  DownloadTracker,
  MAX_FILE_NAME,
  sanitizeFileName,
} = require("../../downloads");

test("sanitizeFileName removes separators, control chars and leading dots", () => {
  assert.equal(sanitizeFileName("../../etc/passwd"), "_.._etc_passwd");
  assert.equal(sanitizeFileName(" lead\ntask.sql.gz "), "lead_task.sql.gz");
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
