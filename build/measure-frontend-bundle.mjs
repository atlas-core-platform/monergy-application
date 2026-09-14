import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { stripVTControlCharacters } from 'node:util';

const repositoryRoot = path.resolve(process.argv[2] ?? process.cwd());
const frontendRoot = path.join(repositoryRoot, 'apps', 'customer-web');
const assetRoot = path.join(frontendRoot, 'dist', 'assets');

const vitePath = path.join(frontendRoot, 'node_modules', 'vite', 'dist', 'node', 'index.js');
const { build, createLogger } = await import(pathToFileURL(vitePath).href);
const reporterLines = [];
const logger = createLogger('info', { allowClearScreen: false });
const originalInfo = logger.info.bind(logger);
logger.info = (message, options) => {
  reporterLines.push(stripVTControlCharacters(message));
  originalInfo(message, options);
};

await build({ root: frontendRoot, customLogger: logger, clearScreen: false });

const files = await readdir(assetRoot);

function requireSingle(pattern, label) {
  const matches = files.filter((file) => pattern.test(file));
  if (matches.length !== 1) {
    throw new Error(`Expected one ${label}; found ${matches.length}: ${matches.join(', ')}`);
  }
  return matches[0];
}

const initialFile = requireSingle(/^index-.+\.js$/, 'initial application-shell chunk');
const runtimeFile = requireSingle(/^rolldown-runtime-.+\.js$/, 'runtime chunk');
const vs02File = requireSingle(/^Vs02Experience-.+\.js$/, 'VS-02 lazy-route chunk');
const cssFile = requireSingle(/^index-.+\.css$/, 'application stylesheet');
const javascriptFiles = files.filter((file) => file.endsWith('.js')).sort();
const cssFiles = files.filter((file) => file.endsWith('.css')).sort();

const round = (value) => Math.round((value + Number.EPSILON) * 100) / 100;
const toRawSize = (buffers) => {
  const minifiedBytes = buffers.reduce((total, buffer) => total + buffer.length, 0);
  return {
    minifiedBytes,
  };
};

const reporterSizes = new Map();
for (const line of reporterLines.flatMap((entry) => entry.split(/\r?\n/))) {
  const match = line.match(/assets\/(\S+)\s+([\d.]+) kB\s+│ gzip:\s+([\d.]+) kB/);
  if (match) {
    reporterSizes.set(match[1], {
      minifiedKb: Number.parseFloat(match[2]),
      gzipKb: Number.parseFloat(match[3]),
    });
  }
}

const requireReporterSize = (file) => {
  const size = reporterSizes.get(file);
  if (!size) {
    throw new Error(`Vite reporter did not provide minified/gzip size for ${file}.`);
  }
  return size;
};

const sumReporterSizes = (selectedFiles) => ({
  minifiedKb: round(
    selectedFiles.reduce((total, file) => total + requireReporterSize(file).minifiedKb, 0),
  ),
  gzipKb: round(selectedFiles.reduce((total, file) => total + requireReporterSize(file).gzipKb, 0)),
});

const buffers = new Map(
  await Promise.all(files.map(async (file) => [file, await readFile(path.join(assetRoot, file))])),
);
const initialText = buffers.get(initialFile).toString('utf8');
const vs02Text = buffers.get(vs02File).toString('utf8');
const indexHtml = await readFile(path.join(frontendRoot, 'dist', 'index.html'), 'utf8');
const vs02Sentinel = 'NOT AUTHORITATIVE FINANCIAL TRUTH';
const routeIsolation = {
  initialHtmlReferencesShellChunk: indexHtml.includes(`/assets/${initialFile}`),
  initialShellReferencesLazyChunk: initialText.includes(vs02File),
  vs02SentinelExcludedFromInitialShell: !initialText.includes(vs02Sentinel),
  vs02SentinelPresentInLazyChunk: vs02Text.includes(vs02Sentinel),
};
routeIsolation.passed = Object.values(routeIsolation).every(Boolean);
if (!routeIsolation.passed) {
  throw new Error(`VS-02 lazy-route isolation failed: ${JSON.stringify(routeIsolation)}`);
}

const d02Baseline = { minifiedKb: 628.65, gzipKb: 202.69 };
const initialApplicationShell = requireReporterSize(initialFile);
const absoluteDelta = {
  minifiedKb: round(initialApplicationShell.minifiedKb - d02Baseline.minifiedKb),
  gzipKb: round(initialApplicationShell.gzipKb - d02Baseline.gzipKb),
};
const percentageDelta = {
  minifiedPercent: round((absoluteDelta.minifiedKb / d02Baseline.minifiedKb) * 100),
  gzipPercent: round((absoluteDelta.gzipKb / d02Baseline.gzipKb) * 100),
};

const evidence = {
  schemaVersion: '1.0.0',
  measurementMethod: 'Vite 8.3 production reporter; minified and gzip sizes; decimal kB',
  d02BaselineInitialApplicationShell: d02Baseline,
  d03: {
    initialApplicationShell: {
      file: initialFile,
      ...toRawSize([buffers.get(initialFile)]),
      ...initialApplicationShell,
    },
    runtimeChunk: {
      file: runtimeFile,
      ...toRawSize([buffers.get(runtimeFile)]),
      ...requireReporterSize(runtimeFile),
    },
    vs02LazyRouteChunk: {
      file: vs02File,
      ...toRawSize([buffers.get(vs02File)]),
      ...requireReporterSize(vs02File),
    },
    totalJavaScript: {
      files: javascriptFiles,
      ...toRawSize(javascriptFiles.map((file) => buffers.get(file))),
      ...sumReporterSizes(javascriptFiles),
    },
    totalCss: {
      files: cssFiles,
      ...toRawSize(cssFiles.map((file) => buffers.get(file))),
      ...sumReporterSizes(cssFiles),
    },
    totalJavaScriptAndCss: {
      files: [...javascriptFiles, ...cssFiles],
      ...toRawSize([...javascriptFiles, ...cssFiles].map((file) => buffers.get(file))),
      ...sumReporterSizes([...javascriptFiles, ...cssFiles]),
    },
  },
  d02ToD03InitialShellDelta: {
    absolute: absoluteDelta,
    percentage: percentageDelta,
  },
  routeIsolation,
  performanceThreshold: 'NOT_DEFINED',
  clientCommitment: 'NONE_INFERRED',
};

const outputRoot = path.join(repositoryRoot, '.artifacts', 'components');
await mkdir(outputRoot, { recursive: true });
const outputPath = path.join(outputRoot, 'frontend-bundle.json');
await writeFile(outputPath, `${JSON.stringify(evidence, null, 2)}\n`, 'utf8');

console.log(
  `Bundle evidence PASS: initial ${initialApplicationShell.minifiedKb} kB / ${initialApplicationShell.gzipKb} kB gzip; ` +
    `VS-02 ${evidence.d03.vs02LazyRouteChunk.minifiedKb} kB / ${evidence.d03.vs02LazyRouteChunk.gzipKb} kB gzip; ` +
    `initial delta ${absoluteDelta.minifiedKb} kB (${percentageDelta.minifiedPercent}%) / ` +
    `${absoluteDelta.gzipKb} kB gzip (${percentageDelta.gzipPercent}%); lazy isolation PASS.`,
);
