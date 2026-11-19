import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { FullConfig, FullResult, Reporter, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { scenarioCounts, writePrivateFailure } from './assistant-01b.guards.mjs';

// Playwright errors can contain fill arguments, response bodies or welcome URLs.
// Only fixed test IDs, outcomes and durations cross into acceptance evidence.
export default class SanitizedAssistantReporter implements Reporter {
  private rows: { id: string; status: string; durationMs: number; errorCount: number }[] = [];
  private readonly listing = process.argv.includes('--list');
  private privateOutput: string | undefined;
  private diagnosticFailures = 0;
  onBegin(config: FullConfig, suite: Suite) {
    if (this.listing) {
      process.stdout.write(`Prepared/discovered scenarios: ${String(suite.allTests().length)}; executed: 0.\n`);
      for (const test of suite.allTests()) process.stdout.write((/^D-[A-Z0-9-]+/.exec(test.title)?.[0] ?? 'UNLABELLED') + '\n');
      return;
    }
    if (config.projects.length === 1) this.privateOutput = config.projects[0]?.outputDir;
  }
  onTestEnd(test: TestCase, result: TestResult) {
    if (this.listing) return;
    const id = /^D-[A-Z0-9-]+/.exec(test.title)?.[0] ?? 'UNLABELLED';
    if (result.errors.length || ['failed','timedOut','interrupted'].includes(result.status)) {
      try {
        writePrivateFailure({ listing: false, repository: resolve('.'),
          fixturePath: process.env.MONERGY_ASSISTANT_UAT_FIXTURE, outputPath: this.privateOutput,
          testId: id, status: result.status, errors: result.errors });
      } catch {
        this.diagnosticFailures++;
        process.stdout.write('HARNESS_PRIVATE_DIAGNOSTIC_UNAVAILABLE: protected capture failed; no pass claimed.\n');
      }
    }
    this.rows.push({ id, status: result.status, durationMs: result.duration, errorCount: result.errors.length });
    process.stdout.write(`${id}: ${result.status}\n`);
  }
  onError() { process.stdout.write('HARNESS_ERROR: protected details suppressed; no pass claimed.\n'); }
  onEnd(result: FullResult) {
    if (this.listing) return;
    const phase = process.env.MONERGY_ASSISTANT_PHASE ?? 'baseline';
    if (!/^[a-z-]+$/.test(phase)) throw new Error('Invalid evidence phase.');
    const folder = resolve('.artifacts/assistant-01b-testing/browser');
    mkdirSync(folder, { recursive: true });
    const counts = scenarioCounts(this.rows);
    const status = this.diagnosticFailures ? 'failed' : result.status;
    // eslint-disable-next-line security/detect-non-literal-fs-filename -- constant evidence directory; phase is allowlisted and timestamp is generated.
    writeFileSync(resolve(folder, `${phase}-${new Date().toISOString().replaceAll(/[:.]/g, '-')}.json`),
      JSON.stringify({ phase, status, counts, privateDiagnosticFailures: this.diagnosticFailures, scenarios: this.rows }, null, 2) + '\n');
    process.stdout.write(`Gate D ${phase}: ${JSON.stringify(counts)}; ${status}\n`);
    if (this.diagnosticFailures) return Promise.resolve({ status: 'failed' as const });
  }
}
