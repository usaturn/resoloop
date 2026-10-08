/**
 * Pre-publication journal guard for the pinned meshy-cli 0.4.0 only.
 * operation-store -> atomic-file.writeJsonFile -> fs.writeFileSync(temp JSON).
 * Do not broaden the version check without reviewing the actual writer again.
 * No API interception, package edits, or post-write cleanup of secret bytes.
 */
import fs from "node:fs";
import { syncBuiltinESMExports } from "node:module";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const key = process.env.MESHY_API_KEY;
const config = process.env.MESHY_CONFIG_DIR;
function refuse() {
  const error = new Error("unsafe Meshy journal write refused");
  error.code = "meshy_journal_unsafe";
  throw error;
}
if (!key?.trim() || !config) refuse();
const secrets = [...new Set([key, key.trim()])].sort((a, b) => b.length - a.length);
const root = resolve(config, "operations");
const segment = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/;
const digest = /^[0-9a-f]{64}$/;
const states = new Set(["started", "accepted", "rejected", "not_submitted", "unknown"]);
const originalWrite = fs.writeFileSync;

function containsSecret(value) {
  return typeof value === "string" && secrets.some((secret) => value.includes(secret));
}
function redact(value) {
  for (const secret of secrets) value = value.replaceAll(secret, "<redacted>");
  return value;
}
function sanitize(value, top = false) {
  if (typeof value === "string") return redact(value);
  if (Array.isArray(value)) return value.map((item) => sanitize(item));
  if (value === null || typeof value !== "object") return value;
  return Object.fromEntries(Object.entries(value).flatMap(([name, item]) => {
    const normalized = name.replace(/[^a-z0-9]/gi, "").toLowerCase();
    // Only the validated root one-way digest is a non-secret credential field.
    if (!(top && name === "credential_fingerprint") &&
        /error|credential|authorization|authentication|apikey|token|password|secret|^auth$/.test(normalized)) {
      return [];
    }
    return [[redact(name), sanitize(item)]];
  }));
}
function journalBytes(path, data) {
  let record;
  try {
    if (typeof data !== "string" && !Buffer.isBuffer(data)) refuse();
    record = JSON.parse(data.toString());
  } catch {
    refuse(); // Never expose parse errors, which can quote the raw secret input.
  }
  if (!record || Array.isArray(record) || record.schema_version !== 1 ||
      record.cli_version !== "0.4.0" || typeof record.operation_id !== "string" ||
      !segment.test(record.operation_id) ||
      !states.has(record.state) ||
      !["text-to-3d", "image-to-3d"].includes(record.resource) ||
      !digest.test(record.credential_fingerprint) || !digest.test(record.payload_fingerprint) ||
      (record.task_id !== null && (typeof record.task_id !== "string" || !segment.test(record.task_id)))) {
    refuse();
  }
  // Redacting these would destroy identity/recovery; refuse rather than change them.
  for (const name of ["operation_id", "resource", "endpoint", "api_origin", "state", "credential_fingerprint", "payload_fingerprint", "task_id"]) {
    if (containsSecret(record[name])) refuse();
  }
  const target = `${record.operation_id}.json`;
  const file = basename(path);
  if (dirname(path) !== root ||
      (file !== target && !new RegExp(`^\\.${target.replace(".", "\\.")}\\.tmp-\\d+-[0-9a-f]{8}$`).test(file))) {
    refuse();
  }
  const bytes = `${JSON.stringify(sanitize(record, true), null, 2)}\n`;
  if (containsSecret(bytes)) refuse();
  return bytes;
}

fs.writeFileSync = function (file, data, options) {
  // The reviewed writer passes a pathname, never an fd. Other CLI writes (e.g.
  // downloaded artifacts) remain untouched; this is not a general fs sandbox.
  const path = typeof file === "string" ? resolve(file) :
    file instanceof URL ? resolve(fileURLToPath(file)) :
    Buffer.isBuffer(file) ? resolve(file.toString()) : null;
  if (path === root || path?.startsWith(root + "/")) {
    data = journalBytes(path, data);
    // atomic-file always uses utf8; don't let alternate encodings change redactions.
    options = { ...(typeof options === "object" ? options : {}), encoding: "utf8" };
  }
  return originalWrite.call(this, file, data, options);
};
// Update Node's named ESM bindings, including atomic-file's writeFileSync import.
syncBuiltinESMExports();
