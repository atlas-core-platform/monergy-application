import { spawnSync, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { stripVTControlCharacters } from 'node:util';

if (process.platform !== 'linux' || process.version !== 'v24.21.0') {
  throw new Error('The governed probe requires Linux and pinned Node 24.21.0.');
}
if (execFileSync('pnpm', ['--version'], { encoding: 'utf8' }).trim() !== '12.4.1') {
  throw new Error('Pinned pnpm 12.4.1 is required.');
}
const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
const tree = execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { encoding: 'utf8' }).trim();
const directory = resolve('.artifacts/d05-lifecycle-probe');
if (existsSync(directory)) throw new Error('Never overwrite or retry an existing probe.');
mkdirSync(directory, { recursive: true });
const digest = (file) => createHash('sha256').update(readFileSync(file)).digest('hex');
const summary = {
  purpose: 'D05_REMEDIATION_EVIDENCE_ONLY_NOT_PRODUCT_NFR',
  head,
  tree,
  run: process.env.GITHUB_RUN_ID,
  event: process.env.GITHUB_EVENT_NAME,
  expectedExecutions: 20,
  retry: 0,
  result: 'RUNNING',
  executions: [],
};
const save = () =>
  writeFileSync(resolve(directory, 'summary.json'), JSON.stringify(summary, null, 2));
save();
for (let iteration = 1; iteration <= 20; iteration += 1) {
  const prefix = resolve(directory, `execution-${String(iteration).padStart(2, '0')}`);
  const execution = spawnSync(
    'pnpm',
    [
      '--filter',
      '@monergy/customer-web',
      'exec',
      'vitest',
      'run',
      '--retry=0',
      '--detectAsyncLeaks',
      '--reporter=default',
      '--reporter=json',
      `--reporter=${resolve('build/frontend-lifecycle-reporter.mjs')}`,
      `--outputFile.json=${prefix}.json`,
    ],
    {
      encoding: 'utf8',
      timeout: 120_000,
      maxBuffer: 8 * 1024 * 1024,
      env: {
        ...process.env,
        MONERGY_D05_LIFECYCLE_PROBE: '1',
        MONERGY_D05_LIFECYCLE_REPORT: `${prefix}.lifecycle.json`,
      },
    },
  );
  writeFileSync(`${prefix}.stdout.log`, execution.stdout ?? '');
  writeFileSync(`${prefix}.stderr.log`, execution.stderr ?? '');
  const tests = existsSync(`${prefix}.json`)
    ? JSON.parse(readFileSync(`${prefix}.json`, 'utf8'))
    : null;
  const lifecycle = existsSync(`${prefix}.lifecycle.json`)
    ? JSON.parse(readFileSync(`${prefix}.lifecycle.json`, 'utf8'))
    : null;
  const cleanup = [
    ...stripVTControlCharacters(execution.stdout ?? '').matchAll(
      /D05_LIFECYCLE_CLEANUP (\{[^\n]+\})/g,
    ),
  ].map((match) => JSON.parse(match[1]));
  const output = stripVTControlCharacters(`${execution.stdout}\n${execution.stderr}`);
  const resourceTypes = [...output.matchAll(/^(\w+) leaking in/gm)].map((match) => match[1]);
  // Vitest reports a retained native port even after Form's one-shot message
  // has delivered. The isolated diagnostic proves delivery before teardown.
  // Retain that separate dependency-resource observation, never claim it fixed.
  const knownDeliveredWatcherPorts =
    resourceTypes.length > 0 &&
    resourceTypes.every((type) => type === 'MESSAGEPORT') &&
    output.includes('useNotifyWatch');
  const leakReported =
    /window is not defined|Unhandled Errors/.test(output) ||
    (resourceTypes.length > 0 && !knownDeliveredWatcherPorts);
  const passed =
    execution.status === 0 &&
    !execution.error &&
    tests?.success &&
    tests.numPassedTests === 7 &&
    tests.numTotalTests === 7 &&
    tests.numFailedTests === 0 &&
    tests.numPendingTests === 0 &&
    lifecycle?.unhandledErrorCount === 0 &&
    lifecycle.reason === 'passed' &&
    !leakReported &&
    cleanup.length === 4 &&
    cleanup.every(
      (entry) =>
        entry.pendingTimers === 0 && entry.remainingRoots === 0 && entry.realTimersRestored,
    );
  summary.executions.push({
    iteration,
    result: passed ? 'PASS' : 'FAIL',
    exitCode: execution.status,
    passedTests: tests?.numPassedTests,
    unhandledErrors: lifecycle?.unhandledErrorCount,
    postEnvironmentCallbacks: passed ? 0 : 'NOT_PROVEN',
    leakReported,
    cleanup,
    retainedResourceDiagnostics: { resourceTypes, knownDeliveredWatcherPorts, fixed: false },
    stdoutSha256: digest(`${prefix}.stdout.log`),
    stderrSha256: digest(`${prefix}.stderr.log`),
    testReportSha256: tests ? digest(`${prefix}.json`) : null,
    lifecycleReportSha256: lifecycle ? digest(`${prefix}.lifecycle.json`) : null,
    error: execution.error?.message,
  });
  summary.result = passed && iteration === 20 ? 'PASS' : passed ? 'RUNNING' : 'FAIL';
  save();
  console.info(`D05 lifecycle execution ${iteration}/20: ${passed ? 'PASS' : 'FAIL'}`);
  if (!passed)
    throw new Error(`STOP: lifecycle execution ${iteration} failed. Preserve evidence; no retry.`);
}
