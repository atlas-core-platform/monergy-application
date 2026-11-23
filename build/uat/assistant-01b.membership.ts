import { randomUUID } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { expect, type Page } from '@playwright/test';
import { fixture, headers, origin, person } from './assistant-01b.fixture';

interface Member { actorId: string; status: string; businessRoleId: string | null; tenantAdmin: boolean; subjectVersion: number }
interface Members { policyVersion: number; items: Member[] }
export async function withTemporaryAdministrativeWorkspace(page: Page, session: string, action: () => Promise<void>) {
  const administrator = person('A900'); const auth = headers(administrator, session);
  const path = origin(administrator) + '/access-api/v1/administration/members';
  const read = async () => {
    const response = await page.request.get(path + '?limit=20', { headers: auth });
    expect(response.status()).toBe(200);
    return await response.json() as Members;
  };
  const before = await read(); const original = before.items.find(item => item.actorId === 'A200');
  if (original?.status !== 'Active' || original.tenantAdmin || !original.businessRoleId)
    throw new Error('The new-profile Customer fixture is not in the expected original membership state.');
  const backupPath = resolve(dirname(process.env.MONERGY_ASSISTANT_UAT_FIXTURE ?? ''), 'workspace-member-' + randomUUID() + '.json');
  const record = { schemaVersion: 1, instanceId: fixture.instanceId, actorId: 'A200', original,
    policyVersion: before.policyVersion, state: 'Prepared' };
  const save = () => {
    // eslint-disable-next-line security/detect-non-literal-fs-filename -- generated metadata filename inside the protected fixture parent; no secret content.
    writeFileSync(backupPath, JSON.stringify(record), { mode: 0o600 });
  };
  const restore = async () => {
    // A lost PUT acknowledgement is indeterminate. Re-read the actual owner and
    // restore only this temporary authority; never overwrite a concurrent role change.
    const current = await read(); const member = current.items.find(item => item.actorId === 'A200');
    if (member?.status !== original.status || member.businessRoleId !== original.businessRoleId)
      throw new Error('Membership changed outside the bounded fixture; automatic restoration refused.');
    if (member.tenantAdmin) {
      const response = await page.request.put(path + '/A200', { headers: auth, data: {
        expectedPolicyVersion: current.policyVersion, businessRoleId: original.businessRoleId,
        active: true, tenantAdmin: original.tenantAdmin,
      } });
      expect(response.status()).toBe(200);
    }
    const final = (await read()).items.find(item => item.actorId === 'A200');
    expect(final).toMatchObject({ actorId: original.actorId, status: original.status,
      businessRoleId: original.businessRoleId, tenantAdmin: original.tenantAdmin });
    record.state = 'Restored'; save();
  };
  save();
  try {
    const response = await page.request.put(path + '/A200', { headers: auth, data: {
      expectedPolicyVersion: before.policyVersion, businessRoleId: original.businessRoleId, active: true, tenantAdmin: true,
    } });
    expect(response.status()).toBe(200); record.state = 'Applied'; save();
    await action();
  } finally { await restore(); }
}
