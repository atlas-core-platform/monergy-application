import assert from 'node:assert/strict';
import { test } from 'node:test';
import { requireExistingQaProvider } from './assistant-01b.qa-readback.mjs';

const expected = { endpoint: 'https://assistant01b-qa-synthetic:8443/v1/chat/completions', model: 'synthetic-local-contract',
  providerLabel: 'Synthetic LOCAL provider', notice: 'Synthetic LOCAL contract only.' };
const valid = () => ({ revision: 2, providers: [{ code: 'ai', displayName: 'AI', category: 'Model', enabled: true,
  scope: 'Organization', endpoint: expected.endpoint, clientId: null, callbackUri: null, model: expected.model,
  options: { tenantId: 'T001', providerLabel: expected.providerLabel, notice: expected.notice },
  secretConfigured: true, secretUpdatedAt: '2026-11-19T00:00:00Z', subscriptionStatus: 'Configured', readiness: 'Ready',
  revision: 2, updatedBy: 'A950', updatedAt: '2026-11-19T00:00:00Z' }] });

test('existing ready provider is accepted without exposing credential material', () => {
  assert.equal(requireExistingQaProvider(valid(), expected), undefined);
});

for (const [name, change] of [
  ['wrong tenant', value => { value.providers[0].options.tenantId = 'T002'; }],
  ['wrong endpoint', value => { value.providers[0].endpoint = 'https://elsewhere.invalid'; }],
  ['wrong model', value => { value.providers[0].model = 'other'; }],
  ['wrong provider label', value => { value.providers[0].options.providerLabel = 'other'; }],
  ['wrong notice', value => { value.providers[0].options.notice = 'other'; }],
  ['not ready', value => { value.providers[0].readiness = 'Needs attention'; }],
  ['missing credential reference', value => { value.providers[0].secretConfigured = false; }],
  ['duplicate AI provider', value => { value.providers.push({ ...value.providers[0] }); }],
  ['raw secret field', value => { value.providers[0].secretValue = 'SYNTHETIC_PRIVATE_CANARY'; }],
  ['secret nested in options', value => { value.providers[0].options.apiKey = 'SYNTHETIC_PRIVATE_CANARY'; }],
]) test(`existing provider readback fails closed for ${name}`, () => {
  const value = valid(); change(value);
  assert.throws(() => requireExistingQaProvider(value, expected), error =>
    error instanceof Error && error.message === 'Existing governed QA configuration does not match the protected fixture or safe response contract.');
});
