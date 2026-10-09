import { readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const tokenPath = resolve(packageRoot, 'src/tokens.json');
const outputPath = resolve(packageRoot, 'src/semantic-tokens.generated.css');
const tokens = JSON.parse(await readFile(tokenPath, 'utf8'));

const midnightCss = Object.entries(tokens.midnight)
  .map(
    ([key, value]) =>
      `  --monergy-midnight-${key.replace(/[A-Z]/g, (letter) => '-' + letter.toLowerCase())}: ${value};`,
  )
  .join('\n');
const compactCss = Object.entries(tokens.compact)
  .map(
    ([key, value]) =>
      `  --monergy-compact-${key.replace(/[A-Z]/g, (letter) => '-' + letter.toLowerCase())}: ${typeof value === 'number' ? value + (key.endsWith('Ms') ? 'ms' : 'px') : value};`,
  )
  .join('\n');
const css = `/* Generated from tokens.json. Run pnpm generate:tokens; do not edit directly. */
:root {
${midnightCss}
${compactCss}
  --monergy-color-brand-primary: ${tokens.color.brand.primary};
  --monergy-color-brand-primary-hover: ${tokens.color.brand.primaryHover};
  --monergy-color-brand-on-primary: ${tokens.color.brand.onPrimary};
  --monergy-color-neutral-0: ${tokens.color.neutral['0']};
  --monergy-color-neutral-50: ${tokens.color.neutral['50']};
  --monergy-color-neutral-100: ${tokens.color.neutral['100']};
  --monergy-color-neutral-300: ${tokens.color.neutral['300']};
  --monergy-color-neutral-600: ${tokens.color.neutral['600']};
  --monergy-color-neutral-800: ${tokens.color.neutral['800']};
  --monergy-color-neutral-950: ${tokens.color.neutral['950']};
  --monergy-color-success: ${tokens.color.state.success};
  --monergy-color-warning: ${tokens.color.state.warning};
  --monergy-color-error: ${tokens.color.state.error};
  --monergy-color-info: ${tokens.color.state.info};
  --monergy-font-sans: ${tokens.typography.fontFamily};
  --monergy-text-body: ${tokens.typography.baseSize};
  --monergy-text-small: ${tokens.typography.smallSize};
  --monergy-text-label: ${tokens.typography.labelSize};
  --monergy-text-section: ${tokens.typography.sectionSize};
  --monergy-text-title: ${tokens.typography.titleSize};
  --monergy-leading: ${tokens.typography.lineHeight};
  --monergy-font-mono: ${tokens.typography.fontFamilyMono};
  --monergy-space-unit: ${tokens.spacing.unit};
  --monergy-radius-control: ${tokens.radius.control}px;
  --monergy-radius-panel: ${tokens.radius.panel}px;
  --monergy-shadow-panel: ${tokens.elevation.panel};
  --monergy-shadow-dialog: ${tokens.elevation.dialog};
  --monergy-focus-color: ${tokens.focus.color};
  --monergy-focus-width: ${tokens.focus.width};
  --monergy-focus-offset: ${tokens.focus.offset};
}

@theme inline {
  --color-brand-primary: var(--monergy-color-brand-primary);
  --color-brand-on-primary: var(--monergy-color-brand-on-primary);
  --color-surface: var(--monergy-color-neutral-0);
  --color-canvas: var(--monergy-color-neutral-50);
  --color-subtle: var(--monergy-color-neutral-100);
  --color-border: var(--monergy-color-neutral-300);
  --color-muted: var(--monergy-color-neutral-600);
  --color-strong: var(--monergy-color-neutral-800);
  --color-ink: var(--monergy-color-neutral-950);
  --color-success: var(--monergy-color-success);
  --color-warning: var(--monergy-color-warning);
  --color-error: var(--monergy-color-error);
  --color-info: var(--monergy-color-info);
  --font-sans: var(--monergy-font-sans);
  --font-mono: var(--monergy-font-mono);
  --spacing: ${tokens.spacing.unit};
  --radius-control: var(--monergy-radius-control);
  --radius-panel: var(--monergy-radius-panel);
  --shadow-panel: var(--monergy-shadow-panel);
  --shadow-dialog: var(--monergy-shadow-dialog);
  --breakpoint-sm: ${tokens.breakpoint.sm};
  --breakpoint-md: ${tokens.breakpoint.md};
  --breakpoint-lg: ${tokens.breakpoint.lg};
  --breakpoint-xl: ${tokens.breakpoint.xl};
}
`;

if (process.argv.includes('--check')) {
  const current = await readFile(outputPath, 'utf8').catch(() => '');
  if (current !== css) {
    throw new Error('semantic-tokens.generated.css is stale; run pnpm generate:tokens.');
  }
} else {
  await writeFile(outputPath, css, 'utf8');
}
