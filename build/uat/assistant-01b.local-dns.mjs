import dns from 'node:dns';

const localHosts = new Set(['platform.localhost', 't001.localhost', 't002.localhost', 'unknown.localhost']);

/**
 * Resolve only the four Gate D HTTP hostnames to this machine. The request URL,
 * Host header and trusted-host routing are untouched. Unknown.localhost remains
 * an unknown Gateway host: resolving its socket must never grant tenant context.
 * @param {(hostname:string, options?:import('node:dns').LookupOptions|number) => Promise<import('node:dns').LookupAddress|import('node:dns').LookupAddress[]>} original
 */
export function createAssistantLocalLookup(original) {
  /** @param {string} hostname @param {import('node:dns').LookupOptions|number} [options] */
  return async (hostname, options) => {
    if (!localHosts.has(hostname)) return original(hostname, options);
    const family = typeof options === 'number' ? options : options?.family ?? 0;
    if (family !== 0 && family !== 4 && family !== 6 && family !== 'IPv4' && family !== 'IPv6')
      return original(hostname, options);
    // LOCAL publishes only IPv4 loopback. Do not invent a reachable IPv6 service.
    if (family === 6 || family === 'IPv6')
      throw Object.assign(new Error('No IPv6 address for the isolated LOCAL test host.'), { code: 'ENOTFOUND', syscall: 'getaddrinfo', hostname });
    const address = { address: '127.0.0.1', family: 4 };
    return typeof options === 'object' && options.all ? [address] : address;
  };
}

// The launcher scopes these flags and NODE_OPTIONS to one explicitly authorized
// physical phase. Import/discovery alone never changes process DNS behavior.
if (process.env.MONERGY_ASSISTANT_GATE_D_AUTHORIZED === 'true' &&
    process.env.MONERGY_ASSISTANT_LOCAL_DNS === 'true' && !process.argv.includes('--list')) {
  const original = dns.promises.lookup;
  const lookup = createAssistantLocalLookup((hostname, options) =>
    Reflect.apply(original, dns.promises, options === undefined ? [hostname] : [hostname, options]));
  dns.promises.lookup = /** @type {typeof original} */ (lookup);
}
