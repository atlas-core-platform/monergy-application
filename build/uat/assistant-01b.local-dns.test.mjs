import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { resolve } from 'node:path';
import { test } from 'node:test';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { createAssistantLocalLookup } from './assistant-01b.local-dns.mjs';

const run = promisify(execFile);
const preload = pathToFileURL(resolve('build/uat/assistant-01b.local-dns.mjs')).href;

test('only the four explicitly allowed LOCAL hostnames resolve to IPv4 loopback', async () => {
  const lookup = createAssistantLocalLookup(() => { throw new Error('Unexpected native lookup.'); });
  for (const host of ['platform.localhost', 't001.localhost', 't002.localhost', 'unknown.localhost']) {
    assert.deepEqual(await lookup(host), { address: '127.0.0.1', family: 4 });
    assert.deepEqual(await lookup(host, { all: true, family: 4 }), [{ address: '127.0.0.1', family: 4 }]);
    assert.deepEqual(await lookup(host, 4), { address: '127.0.0.1', family: 4 });
    await assert.rejects(lookup(host, { all: true, family: 6 }), { code: 'ENOTFOUND' });
  }
});

test('unlisted hosts and explicit unsupported options delegate unchanged', async () => {
  const calls = [];
  const answer = { address: '192.0.2.17', family: 4 };
  const lookup = createAssistantLocalLookup(async (...args) => { calls.push(args); return answer; });
  const options = { all: true, family: 4, order: 'verbatim' };
  for (const host of ['localhost', '127.0.0.1', 't003.localhost', 'platform.localhost.evil.test', 'PLATFORM.LOCALHOST', 'example.invalid']) {
    assert.equal(await lookup(host, options), answer);
    assert.deepEqual(calls.at(-1), [host, options]);
    assert.equal(calls.at(-1)[1], options);
  }
  const invalid = { family: 7 };
  assert.equal(await lookup('platform.localhost', invalid), answer);
  assert.deepEqual(calls.at(-1), ['platform.localhost', invalid]);
});

test('preload is inactive without both explicit gates and during discovery', async () => {
  for (const flags of [{}, { MONERGY_ASSISTANT_GATE_D_AUTHORIZED: 'true' }, { MONERGY_ASSISTANT_LOCAL_DNS: 'true' },
    { MONERGY_ASSISTANT_GATE_D_AUTHORIZED: 'true', MONERGY_ASSISTANT_LOCAL_DNS: 'true', listing: true }]) {
    const script = `import dns from 'node:dns';
      const before=dns.promises.lookup;
      ${flags.listing ? "process.argv.push('--list');" : ''}
      await import(${JSON.stringify(preload)});
      if(dns.promises.lookup!==before)throw new Error('Unexpected DNS replacement.');
      process.stdout.write('INACTIVE');`;
    const result = await run(process.execPath, ['--input-type=module', '-e', script], {
      env: { ...process.env, NODE_OPTIONS: '', MONERGY_ASSISTANT_GATE_D_AUTHORIZED: '', MONERGY_ASSISTANT_LOCAL_DNS: '', ...flags },
      timeout: 15000,
    });
    assert.equal(result.stdout, 'INACTIVE');
  }
});

test('real Playwright API transport retains hostname authority including unknown-host denial', async () => {
  const script = `import assert from 'node:assert/strict';
    import {createServer} from 'node:http';
    import {once} from 'node:events';
    import {request} from '@playwright/test';
    const seen=[];
    const server=createServer((req,res)=>{
      seen.push(req.headers.host);
      const allowed=['platform.localhost','t001.localhost','t002.localhost'].some(host=>req.headers.host===host+':'+server.address().port);
      res.writeHead(allowed?200:421,{'content-type':'application/json'});res.end(JSON.stringify({path:req.url}));
    });
    server.listen(0,'127.0.0.1');await once(server,'listening');
    const port=server.address().port;const client=await request.newContext();
    try{
      for(const host of ['platform.localhost','t001.localhost','t002.localhost','unknown.localhost']){
        const response=await client.get('http://'+host+':'+port+'/probe',{timeout:10000});
        assert.equal(response.status(),host==='unknown.localhost'?421:200);
        assert.equal(new URL(response.url()).hostname,host);
        assert.equal(seen.at(-1),host+':'+port);
      }
      process.stdout.write('HOST_AUTHORITY_PRESERVED');
    }finally{await client.dispose();await new Promise(resolve=>server.close(resolve));}`;
  const result = await run(process.execPath, ['--import', preload, '--input-type=module', '-e', script], {
    env: { ...process.env, NODE_OPTIONS: '', MONERGY_ASSISTANT_GATE_D_AUTHORIZED: 'true', MONERGY_ASSISTANT_LOCAL_DNS: 'true' },
    timeout: 30000,
  });
  assert.equal(result.stdout, 'HOST_AUTHORITY_PRESERVED');
});
