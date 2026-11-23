import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { Buffer } from 'node:buffer';
import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

/** @param {{listing:boolean, repository:string, fixturePath?:string, outputPath?:string}} options */
export function privateOutputDirectory(options) {
  if (options.listing) return resolve(options.repository, '.artifacts/assistant-01b-private-fixtures/discovery-unused');
  if (!options.fixturePath || !options.outputPath) throw new Error('A prepared protected fixture and raw artifact directory are required.');
  // PowerShell validates every path component, matching fixture ancestry and
  // actual OS permissions. It reads no fixture content and creates nothing here.
  const verified = execFileSync('pwsh', ['-NoProfile', '-File', resolve(options.repository, 'build/uat/assistant-01b.private-artifacts.ps1'),
    '-Mode', 'Verify', '-FixtureFile', options.fixturePath, '-OutputDirectory', options.outputPath],
  { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 15000 }).trim();
  if (verified !== resolve(options.outputPath)) throw new Error('Private artifact verification did not match the requested directory.');
  return verified;
}

/**
 * Error messages/stacks can contain credentials. The only output is a new file
 * in the OS-verified private Playwright run directory, never public evidence.
 * @param {{listing:boolean, repository:string, fixturePath?:string, outputPath?:string,
 * testId?:string, status?:string, errors?:unknown[]}} options
 */
export function writePrivateFailure(options) {
  if (options.listing) return null; // Listing must not inspect paths or errors.
  try {
    if (!options.testId || !/^(?:D-[A-Z0-9-]{1,100}|UNLABELLED)$/.test(options.testId) ||
        !options.status || !['passed','failed','timedOut','interrupted','skipped'].includes(options.status) || !Array.isArray(options.errors))
      throw new Error('Invalid private failure metadata.');
    const folder = privateOutputDirectory(options);
    let remaining = 131072; let truncated = options.errors.length > 8;
    /** @param {unknown} value @param {number} maximum */
    function boundedText(value, maximum) {
      if (typeof value !== 'string') return undefined;
      const count = Math.min(value.length, maximum, remaining);
      if (count < value.length) truncated = true;
      remaining -= count; return value.slice(0,count);
    }
    /** @param {unknown} error @param {number} depth @returns {Record<string,unknown>} */
    function project(error, depth = 0) {
      if (!error || typeof error !== 'object' || Array.isArray(error)) return {};
      const value = /** @type {Record<string,unknown>} */ (error);
      /** @type {Record<string,unknown>} */
      const record = { message: boundedText(value.message,16384), stack: boundedText(value.stack,65536),
        snippet: boundedText(value.snippet,16384), value: boundedText(value.value,16384) };
      if (value.location && typeof value.location === 'object' && !Array.isArray(value.location)) {
        const location = /** @type {Record<string,unknown>} */ (value.location);
        record.location = { file: boundedText(location.file,4096),
          line: typeof location.line === 'number' && Number.isSafeInteger(location.line) ? location.line : undefined,
          column: typeof location.column === 'number' && Number.isSafeInteger(location.column) ? location.column : undefined };
      }
      if (value.cause !== undefined) {
        if (depth < 3) record.cause = project(value.cause,depth+1); else truncated = true;
      }
      return record;
    }
    const errors = options.errors.slice(0,8).map(error => project(error));
    const payload = JSON.stringify({ schemaVersion: 1, testId: options.testId, status: options.status,
      errorCount: options.errors.length, truncated, errors },null,2)+'\n';
    if (Buffer.byteLength(payload,'utf8') > 1048576) throw new Error('Private diagnostic exceeds its bound.');
    const filename = `${options.testId}-${randomUUID()}.error.json`;
    // eslint-disable-next-line security/detect-non-literal-fs-filename -- verified private run directory, closed test ID, generated UUID, exclusive create and 0600/inherited private ACL.
    writeFileSync(resolve(folder,filename),payload,{flag:'wx',mode:0o600});
    return { filename, truncated };
  } catch { throw new Error('Protected failure diagnostic could not be stored; private details are suppressed.'); }
}

/** @param {unknown} value */
export function requireUnactivatedPlatform(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value) ||
      !('activated' in value) || value.activated !== false)
    throw new Error('Platform credentials already exist or their initial state is unavailable. No welcome link may be issued.');
}

/** @param {{status:string}[]} rows */
export function scenarioCounts(rows) {
  const pass = rows.filter(row => row.status === 'passed').length;
  const fail = rows.filter(row => row.status === 'failed' || row.status === 'timedOut').length;
  const interrupted = rows.filter(row => row.status === 'interrupted').length;
  const skipped = rows.filter(row => row.status === 'skipped').length;
  return { reported: rows.length, executed: pass + fail + interrupted, pass, fail, interrupted, skipped };
}

/** @param {{capturedAt:string,expiresAt:string}} capture @param {number} now */
export function requireNaturalExpiry(capture, now) {
  const captured = Date.parse(capture.capturedAt);
  const expires = Date.parse(capture.expiresAt);
  if (!Number.isFinite(captured) || !Number.isFinite(expires) || !Number.isFinite(now) ||
      expires - captured < 20 * 60_000 || expires - captured > 31 * 60_000)
    throw new Error('A normal thirty-minute owner session capture is required.');
  if (now <= expires + 1000)
    throw new Error('The captured session has not naturally expired. Run other phases and retry after its owner-returned expiry.');
}
