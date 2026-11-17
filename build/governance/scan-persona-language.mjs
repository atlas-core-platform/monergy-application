import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const auditPath = resolve(root, 'build/governance/UX-DEMO-01-PERSONA-LANGUAGE-AUDIT.json');

const surfaces = [
  ['Shared entry and navigation', 'all persona routes', 'apps/customer-web/src/App.tsx'],
  ['Shared entry and navigation', 'all organization and product routes', 'apps/customer-web/src/workspace/accessNavigation.tsx'],
  ['Shared entry and navigation', 'all persona routes', 'apps/customer-web/src/access/WorkspaceEntry.tsx'],
  ['Customer', '/', 'apps/customer-web/src/workspace/WorkspaceHome.tsx'],
  ['Organization Administrator', '/access', 'apps/customer-web/src/access/AccessExperience.tsx'],
  ['Organization Administrator', '/access', 'apps/customer-web/src/access/AccessEditor.tsx'],
  ['Organization Administrator / Operations Support', '/access/processing and /operations-support', 'apps/customer-web/src/access/ProcessingStatus.tsx'],
  ['Financial Advisor / Relationship Manager', '/advisor', 'apps/customer-web/src/advisor/AdvisorWorkspace.tsx'],
  ['DSA / Case Manager', '/dsa', 'apps/customer-web/src/cases/DsaCaseWorkspace.tsx'],
  ['Lender Professional', '/lender', 'apps/customer-web/src/lender/LenderWorkspace.tsx'],
  ['Financial Professional / Chartered Accountant', '/financial-professional', 'apps/customer-web/src/financial-professional/FinancialProfessionalWorkspace.tsx'],
  ['Property Valuer', '/property-valuer', 'apps/customer-web/src/property-valuer/PropertyValuerWorkspace.tsx'],
  ['Legal Advisor / Advocate', '/legal-advisor', 'apps/customer-web/src/legal-advisor/LegalAdvisorWorkspace.tsx'],
  ['Operations Support', '/operations-support', 'apps/customer-web/src/operations-support/OperationsSupportWorkspace.tsx'],
  ['Platform Administrator', '/platform', 'apps/customer-web/src/platform-administration/PlatformAdministratorPortal.tsx'],
  ['Shared copy and formatting', 'all persona routes', 'apps/customer-web/src/ux/personaCopy.ts'],
];

const technicalTerms = [
  'tenant', 'actor', 'subject', 'capability', 'resource grant', 'policy version',
  'subject version', 'customer version', 'consent version', 'authorization decision',
  'access evaluation', 'owner service', 'service owner', 'service boundary', 'runtime',
  'reference adapter', 'reference actor', 'persistence', 'postgresql', 'seaweedfs',
  'database', 'schema', 'migration', 'endpoint', 'http', 'payload', 'json',
  'correlation id', 'topology', 'dependency', 'container', 'docker', 'clamav',
  'ocr pipeline', 'revision conflict', 'idempotent', 'operation receipt', 'source lock',
  'source version', 'source identity', 'resource id', 'platform control plane',
  'technical acceptance', 'data durability', 'failure code', 'job reference',
];

const diagnosticAllowlist = [
  {
    file: 'apps/customer-web/src/App.tsx',
    functionName: 'ToolchainFoundation',
    route: '/foundation',
    reason: 'Dedicated implementation/toolchain evidence surface; it is not linked from normal persona navigation.',
  },
];

const wordPattern = (value) => new RegExp(`\\b${value.replaceAll(/[.*+?^${}()|[\]\\]/g, '\\$&').replaceAll('\\ ', '\\s+')}\\b`, 'i');

function sourceAt(file, base) {
  if (!base) return readFileSync(resolve(root, file), 'utf8');
  try {
    return execFileSync('git', ['show', `${base}:${file}`], {
      cwd: root,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    });
  } catch {
    return '';
  }
}

function toolchainRange(source) {
  const start = source.indexOf('function ToolchainFoundation()');
  const end = source.indexOf('\nfunction ', start + 1);
  return [start, end === -1 ? source.length : end];
}

function isAllowed(file, source, offset) {
  if (file !== 'apps/customer-web/src/App.tsx') return null;
  const [start, end] = toolchainRange(source);
  return offset >= start && offset < end ? diagnosticAllowlist[0] : null;
}

function visibleSegments(line) {
  const segments = [];
  const jsx = />([^<>{}]+)</gu;
  for (const match of line.matchAll(jsx)) {
    const text = match[1].trim();
    if (text) segments.push({ text, column: (match.index ?? 0) + 1 });
  }

  const literal = /(['"`])((?:\\.|(?!\1).)*)\1/gu;
  for (const match of line.matchAll(literal)) {
    const text = (match[1] === '`' ? match[2].replaceAll(/\$\{[^}]*\}/gu, ' ') : match[2]).trim();
    if (!text || text.startsWith('/') || /^[-a-z0-9.]+$/u.test(text)) continue;
    const prefix = line.slice(0, match.index ?? 0);
    const directUiProperty = /(?:title|description|label|message|emptyText|guide|headline|introduction|assurance|workspaceName|action)\s*[:=]\s*$/iu.test(prefix);
    const feedbackCall = /set(?:Notice|Failure|Error)\s*\([^)]*$/iu.test(prefix);
    const renderedTemplate = /(?:title|description|label|message|emptyText)\s*=\s*\{?[^}]*$/iu.test(prefix);
    if (directUiProperty || feedbackCall || renderedTemplate)
      segments.push({ text, column: (match.index ?? 0) + 1 });
  }
  return segments;
}

function inspect(file, source) {
  const reviewed = [];
  const violations = [];
  const allowed = [];
  let offset = 0;
  for (const [index, line] of source.split(/\r?\n/u).entries()) {
    const segments = /^\s*(?:\/\/|\*)/u.test(line) ? [] : visibleSegments(line);
    for (const segment of segments) {
      reviewed.push({ line: index + 1, text: segment.text });
      for (const term of technicalTerms) {
        if (!wordPattern(term).test(segment.text)) continue;
        const match = { file, line: index + 1, term, excerpt: segment.text };
        const allow = isAllowed(file, source, offset + segment.column);
        if (allow) allowed.push({ ...match, reason: allow.reason, route: allow.route });
        else violations.push(match);
      }
    }
    offset += line.length + 1;
  }
  return { reviewed, violations, allowed };
}

function uniqueStrings(rows) {
  return new Set(rows.map((row) => row.text.replaceAll(/\s+/gu, ' ')));
}

const renderedSourceFiles = execFileSync(
  'git',
  ['ls-files', 'apps/customer-web/src/**/*.tsx'],
  { cwd: root, encoding: 'utf8' },
).trim().split(/\r?\n/u).filter(Boolean);
const files = [...new Set([...renderedSourceFiles, ...surfaces.map(([, , file]) => file)])];
const before = new Map();
const after = new Map();
for (const file of files) {
  before.set(file, inspect(file, sourceAt(file, 'HEAD')));
  after.set(file, inspect(file, sourceAt(file)));
}

let reviewedBefore = 0;
let reviewedAfter = 0;
let changed = 0;
for (const file of files) {
  const prior = uniqueStrings(before.get(file).reviewed);
  const current = uniqueStrings(after.get(file).reviewed);
  reviewedBefore += prior.size;
  reviewedAfter += current.size;
  changed += [...prior].filter((value) => !current.has(value)).length;
}

const violationsBefore = [...before.values()].flatMap((item) => item.violations);
const violationsAfter = [...after.values()].flatMap((item) => item.violations);
const allowlistedAfter = [...after.values()].flatMap((item) => item.allowed);
const artifact = {
  workstream: 'UX-DEMO-01 — Persona Language & Demo Review',
  generatedAt: new Date().toISOString(),
  baseCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim(),
  surfaces: surfaces.map(([persona, route, file]) => ({ persona, route, file })),
  counts: {
    sourceFiles: files.length,
    userFacingSourceRowsReviewedBefore: reviewedBefore,
    userFacingSourceRowsReviewedAfter: reviewedAfter,
    changedOrRemovedSourceRows: changed,
    technicalTermMatchesBefore: violationsBefore.length,
    technicalTermMatchesAfter: violationsAfter.length,
    diagnosticMatchesAllowlistedAfter: allowlistedAfter.length,
  },
  bannedTerms: technicalTerms,
  violationsBefore,
  violationsAfter,
  diagnosticAllowlist,
  allowlistedMatchesAfter: allowlistedAfter,
};

writeFileSync(auditPath, `${JSON.stringify(artifact, null, 2)}\n`, 'utf8');
console.log(JSON.stringify({ output: relative(root, auditPath), ...artifact.counts }, null, 2));
if (violationsAfter.length) {
  for (const violation of violationsAfter)
    console.error(`${violation.file}:${violation.line}: ${violation.term}: ${violation.excerpt}`);
  process.exitCode = 1;
}
