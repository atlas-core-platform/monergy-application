import { writeFileSync } from 'node:fs';

// Observes Vitest's supported reporter API; it does not handle/suppress errors.
export default class LifecycleReporter {
  onTestRunEnd(_modules, unhandledErrors, reason) {
    writeFileSync(
      process.env.MONERGY_D05_LIFECYCLE_REPORT,
      JSON.stringify(
        {
          reason,
          unhandledErrors,
          unhandledErrorCount: unhandledErrors.length,
        },
        null,
        2,
      ),
    );
  }
}
