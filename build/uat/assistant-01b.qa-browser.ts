import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { expect, type Page } from '@playwright/test';
import { fixture, headers, origin, person } from './assistant-01b.fixture';

export async function configureAndAuthorizeQa(page: Page, sessions: { platform: string; customer: string; administrator: string }) {
  const folder = resolve('.artifacts/assistant-01b-private-fixtures', 'qa-' + fixture.profile);
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- fixed metadata file beneath the validated new profile's private QA fixture.
  const metadata = JSON.parse(readFileSync(resolve(folder, 'fixture.json'), 'utf8')) as {
    schemaVersion: number; profile: string; instanceId: string; endpoint: string; model: string;
    providerLabel: string; notice: string; authorizationFile: string;
  };
  if (metadata.schemaVersion !== 1 || metadata.profile !== fixture.profile || metadata.instanceId !== fixture.instanceId ||
      metadata.endpoint !== `https://assistant01b-qa-${fixture.instanceId}:8443/v1/chat/completions` ||
      resolve(metadata.authorizationFile) !== resolve(folder, 'authorization.json')) throw new Error('QA fixture binding mismatch.');
  // eslint-disable-next-line security/detect-non-literal-fs-filename -- exact matched private authorization file; value stays in memory and protected requests only.
  const authorization = JSON.parse(readFileSync(metadata.authorizationFile, 'utf8')) as { ApiKey: string };
  if (typeof authorization.ApiKey !== 'string' || authorization.ApiKey.length < 32) throw new Error('QA fixture authorization unavailable.');
  const platform = person('A950'); const customer = person('A100'); const administrator = person('A900');
  const current = await page.request.get(origin(platform) + '/platform-api/local/v1/integrations', { headers: headers(platform, sessions.platform) });
  expect(current.status()).toBe(200);
  const revision = (await current.json() as { revision: number }).revision;
  expect(Number.isSafeInteger(revision)).toBe(true);
  const configured = await page.request.put(origin(platform) + '/platform-api/local/v1/integrations/ai', {
    headers: headers(platform, sessions.platform), data: {
      operationId: randomUUID().replaceAll('-', ''), expectedRevision: revision, enabled: true, scope: 'Organization',
      endpoint: metadata.endpoint, clientId: null, callbackUri: null, model: metadata.model,
      options: { tenantId: 'T001', providerLabel: metadata.providerLabel, notice: metadata.notice },
      secretValue: authorization.ApiKey, clearSecret: false, subscriptionStatus: 'Configured',
    },
  });
  expect(configured.status()).toBe(200);
  const proposed = await page.request.post(origin(customer) + '/access-api/v1/access-requests/customer.financial-qa/submit', {
    headers: headers(customer, sessions.customer), data: { operationId: randomUUID().replaceAll('-', '') },
  });
  expect(proposed.status()).toBe(200);
  const request = await proposed.json() as { requestId: string; target: string; state: string };
  expect(request.target).toBe('customer.financial-qa'); expect(request.state).toBe('Submitted');
  if (!/^[0-9a-f-]{32,36}$/i.test(request.requestId)) throw new Error('Governed QA access request identity unavailable.');
  const approved = await page.request.post(origin(administrator) + '/access-api/v1/administration/access-requests/' + request.requestId + '/approve', {
    headers: headers(administrator, sessions.administrator), data: { operationId: randomUUID().replaceAll('-', '') },
  });
  expect(approved.status()).toBe(200);
  expect((await approved.json() as { state: string }).state).toBe('Fulfilled');
}

export async function verifyQaContinuity(page: Page, customerSession: string) {
  const customer = person('A100'); const auth = headers(customer, customerSession);
  const access = await page.request.get(origin(customer) + '/access-api/v1/access-requests', { headers: auth });
  expect(access.status()).toBe(200);
  const items = (await access.json() as { items: { target: string; state: string; hasAccess: boolean }[] }).items;
  expect(items.find(item => item.target === 'customer.financial-qa')).toMatchObject({ state: 'Fulfilled', hasAccess: true });
  const configuration = await page.request.get(origin(customer) + '/product-api/v1/financial-qa/configuration', { headers: auth });
  expect(configuration.status()).toBe(200);
  const value = await configuration.json() as { available: boolean; revision: string; providerLabel: string; notice: string };
  expect(value.available).toBe(true); expect(value.revision).toMatch(/^[0-9a-f]{64}$/);
  expect(value.providerLabel.length).toBeGreaterThan(0); expect(value.notice.length).toBeGreaterThan(0);
  const operationId = randomUUID().replaceAll('-', '');
  const response = await page.request.post(origin(customer) + '/product-api/v1/financial-qa/questions', {
    headers: auth, data: { operationId, question: 'What investment source is recorded?',
      configurationRevision: value.revision, allowModelProcessing: true },
  });
  expect(response.status()).toBe(200);
  const answer = await response.json() as { operationId: string; state: string; claims: unknown[] };
  expect(answer.operationId).toBe(operationId); expect(answer.state).toBe('INSUFFICIENT_DATA'); expect(answer.claims).toHaveLength(0);
  // This is authenticated continuity on an empty profile, not source-grounded
  // financial truth, external model approval or a non-empty SOURCES journey.
}

export function providerCallCount() {
  const output = execFileSync('pwsh', ['-NoProfile', '-File', resolve('build/uat/assistant-01b.qa-provider.ps1'),
    '-Action', 'Count', '-Profile', fixture.profile, '-ExpectedNewInstance', fixture.instanceId, '-ConfirmGatesABC'],
  { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 300000 });
  const result = JSON.parse(output) as { profile: string; instanceId: string; calls: number };
  if (result.profile !== fixture.profile || result.instanceId !== fixture.instanceId || !Number.isSafeInteger(result.calls) || result.calls < 0)
    throw new Error('QA call counter identity unavailable.');
  return result.calls;
}
