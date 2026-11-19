import { readFileSync, lstatSync } from 'node:fs';
import { resolve, relative, isAbsolute } from 'node:path';
import { expect, type Page } from '@playwright/test';

export interface Persona {
  actor: string; tenant: 'T001' | 'T002' | null; email: string; password: string; path: string;
}
export interface Fixture {
  schemaVersion: number; profile: string; instanceId: string; port: number; profilePath: string;
  personas: Persona[]; createdAt: string;
}
const privateRoot = resolve('.artifacts/assistant-01b-private-fixtures');
let loaded: Fixture | undefined;
function loadFixture(): Fixture {
  if (loaded) return loaded;
  if (process.env.MONERGY_ASSISTANT_GATE_D_AUTHORIZED !== 'true') throw new Error('Physical Gate D is not authorized.');
  const file = resolve(process.env.MONERGY_ASSISTANT_UAT_FIXTURE ?? 'FIXTURE_REQUIRED');
  const relativeFile = relative(privateRoot, file);
  if (relativeFile.startsWith('..') || isAbsolute(relativeFile))
    throw new Error('The fixture must be an ordinary file in the protected private fixture directory.');
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- bounded to the private root above; runner validates the file ACL.
  if (lstatSync(file).isSymbolicLink()) throw new Error('Linked fixtures are forbidden.');
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- bounded private fixture path, never browser-selected.
  const value = JSON.parse(readFileSync(file, 'utf8')) as Fixture;
  if (value.schemaVersion !== 1 || !(value.profile === 'assistant01b' || value.profile.startsWith('assistant01b-')) ||
      !/^[a-z0-9-]{1,31}$/.test(value.profile) ||
      !/^[a-f0-9]{32}$/.test(value.instanceId) || !Number.isInteger(value.port) ||
      value.port < 1024 || value.port > 65535 || !Array.isArray(value.personas))
    throw new Error('Invalid isolated Assistant fixture.');
  const actualProfilePath = resolve('.artifacts/uat', value.profile, 'profile.json');
  if (resolve(value.profilePath) !== actualProfilePath) throw new Error('Fixture profile path mismatch.');
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- generated from the validated isolated profile name, not supplied file content.
  const current = JSON.parse(readFileSync(actualProfilePath, 'utf8')) as { instanceId: string; port: number };
  if (current.instanceId !== value.instanceId || current.port !== value.port)
    throw new Error('Fixture does not match the isolated profile instance.');
  loaded = value; return value;
}
// Import/discovery never reads profiles, creates credentials, invokes Docker or
// contacts services. The fixture is loaded only when a running test accesses it.
export const fixture = new Proxy({} as Fixture, { get: (_target, property: keyof Fixture) => loadFixture()[property] });
export const phase = process.env.MONERGY_ASSISTANT_PHASE ?? 'baseline';
export const personas = ['A900', 'A100', 'A200', 'A300', 'A400', 'A500', 'A600', 'A700', 'A800', 'A850', 'A950'];
export const activationPersonas = [...personas.map(actor => ({ actor, tenant: actor === 'A950' ? null : 'T001' as const })),
  { actor: 'A900', tenant: 'T002' as const }, { actor: 'A100', tenant: 'T002' as const }];
export function person(actor: string, tenant: 'T001' | 'T002' | null = actor === 'A950' ? null : 'T001') {
  const selected = fixture.personas.find(item => item.actor === actor && item.tenant === tenant);
  if (!selected || selected.password.length < 15 || selected.password.length > 128)
    throw new Error('Required normal-login persona fixture is unavailable.');
  return selected;
}
export function origin(value: Persona) {
  return `http://${value.tenant === null ? 'platform' : value.tenant.toLowerCase()}.localhost:${String(fixture.port)}`;
}
export function base(value: Persona) { return value.tenant === null ? '/platform-api/local/v1/assistant' : '/assistant-api/local/v1'; }
export function headers(value: Persona, session: string) {
  return { [value.tenant === null ? 'X-Monergy-Platform-Session' : 'X-Monergy-Session']: session };
}
export async function login(page: Page, value: Persona, workspace?: string) {
  await page.goto(origin(value) + value.path);
  await expect(page.getByRole('button', { name: 'Ask Monergy', exact: true })).toHaveCount(0);
  await page.getByLabel(value.tenant === null ? 'Email' : 'Email address', { exact: true }).fill(value.email);
  await page.getByLabel('Password', { exact: true }).fill(value.password);
  const pending = page.waitForResponse(response => new URL(response.url()).pathname ===
    (value.tenant === null ? '/platform-api/local/v1/sessions' : '/identity-api/local/v1/persona-credentials/sign-in') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const response = await pending;
  expect(response.status(), 'Normal password authentication').toBe(200);
  const body = await response.json() as { session?: string; authenticationContextId?: string };
  const session = value.tenant === null ? body.session : body.authenticationContextId;
  if (!session) throw new Error('Normal authenticated session missing.');
  if (workspace) {
    await expect(page.getByRole('heading', { name: 'Choose workspace', exact: true })).toBeVisible();
    await page.getByRole('button', { name: workspace, exact: true }).click();
  }
  // A new tenant must complete its genuine governed setup before baseline runs.
  await expect(page.getByRole('button', { name: 'Ask Monergy', exact: true })).toBeVisible();
  return session;
}
export async function open(page: Page) {
  const ready = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/status') &&
    new URL(response.url()).pathname.includes('assistant'));
  await page.getByRole('button', { name: 'Ask Monergy', exact: true }).click();
  expect((await ready).status()).toBe(200);
  await expect(page.locator('#assistant-question')).toBeVisible();
}
export async function ask(page: Page, question: string) {
  await page.locator('#assistant-question').fill(question);
  const pending = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/answers'));
  await page.getByRole('button', { name: 'Ask', exact: true }).click();
  const response = await pending;
  expect(response.status()).toBe(200);
  expect(response.headers()['cache-control']).toContain('no-store');
  return await response.json() as { state: string; answer: string[]; sources: string[]; canSuggest: boolean };
}
