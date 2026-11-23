/** @param {unknown} value @returns {value is Record<string,unknown>} */
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const fields = new Set(['code', 'displayName', 'category', 'enabled', 'scope', 'endpoint', 'clientId', 'callbackUri',
  'model', 'options', 'secretConfigured', 'secretUpdatedAt', 'subscriptionStatus', 'readiness', 'revision', 'updatedBy', 'updatedAt']);

/**
 * Read-only validation of the safe Platform view. Never return configuration or
 * interpolate unexpected response fields into diagnostics.
 * @param {unknown} snapshot
 * @param {{endpoint:string,model:string,providerLabel:string,notice:string}} expected
 */
export function requireExistingQaProvider(snapshot, expected) {
  const fail = () => { throw new Error('Existing governed QA configuration does not match the protected fixture or safe response contract.'); };
  if (!object(snapshot) || Object.keys(snapshot).some(key => key !== 'revision' && key !== 'providers') ||
      !Number.isSafeInteger(snapshot.revision) || Number(snapshot.revision) < 1 ||
      !Array.isArray(snapshot.providers) || snapshot.providers.length > 20) return fail();
  const providers = snapshot.providers;
  for (const provider of providers) {
    if (!object(provider) || Object.keys(provider).some(key => !fields.has(key)) || !object(provider.options) ||
        Object.keys(provider.options).some(key => /password|secret|credential|authorization|token|api.?key|connection.?string|private.?key/i.test(key))) return fail();
  }
  const matches = providers.filter(provider => object(provider) && provider.code === 'ai');
  if (matches.length !== 1) return fail();
  const provider = /** @type {Record<string,unknown>} */ (matches[0]);
  const options = /** @type {Record<string,unknown>} */ (provider.options);
  if (provider.enabled !== true || provider.scope !== 'Organization' || provider.endpoint !== expected.endpoint ||
      provider.model !== expected.model || provider.clientId !== null || provider.callbackUri !== null ||
      provider.secretConfigured !== true || typeof provider.secretUpdatedAt !== 'string' || !Number.isFinite(Date.parse(provider.secretUpdatedAt)) ||
      provider.subscriptionStatus !== 'Configured' || provider.readiness !== 'Ready' || provider.updatedBy !== 'A950' ||
      !Number.isSafeInteger(provider.revision) || Number(provider.revision) < 1 || Number(provider.revision) > Number(snapshot.revision) ||
      Object.keys(options).sort().join(',') !== 'notice,providerLabel,tenantId' || options.tenantId !== 'T001' ||
      options.providerLabel !== expected.providerLabel || options.notice !== expected.notice) return fail();
}
