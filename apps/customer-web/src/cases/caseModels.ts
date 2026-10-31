export interface AssistanceCase {
  caseId: string;
  requestType: 'Financial assistance';
  state: 'Submitted' | 'Accepted';
  version: number;
  createdAt: string;
  acceptedAt: string | null;
  assignedToMe: boolean;
  dataDurability: 'Temporary data';
}

export interface AssistanceCaseEvent {
  eventId: string;
  action: 'Submitted' | 'Accepted';
  version: number;
  occurredAt: string;
}

const identifier = (value: unknown, maximum = 128): value is string =>
  typeof value === 'string' && value.length <= maximum &&
  /^[A-Za-z0-9][A-Za-z0-9_.:-]*$/.test(value);
const date = (value: unknown): value is string =>
  typeof value === 'string' && Number.isFinite(Date.parse(value));
const record = (value: unknown): Record<string, unknown> => {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error('CASE_OWNER_RESPONSE_INVALID');
  return value as Record<string, unknown>;
};

export function caseItem(value: unknown): AssistanceCase {
  const item = record(value);
  if (!identifier(item.caseId, 64) || item.requestType !== 'Financial assistance' ||
      item.state !== 'Submitted' && item.state !== 'Accepted' ||
      typeof item.version !== 'number' || !Number.isSafeInteger(item.version) || item.version < 1 ||
      !date(item.createdAt) || !(item.acceptedAt === null || date(item.acceptedAt)) ||
      typeof item.assignedToMe !== 'boolean' || item.dataDurability !== 'Temporary data' ||
      item.state === 'Submitted' && item.acceptedAt !== null ||
      item.state === 'Accepted' && item.acceptedAt === null)
    throw new Error('CASE_OWNER_RESPONSE_INVALID');
  return item as unknown as AssistanceCase;
}

export function casePage(value: unknown, tenant: string, actor: string): AssistanceCase[] {
  const page = record(value);
  if (page.tenantId !== tenant || page.actorId !== actor || !Array.isArray(page.items) || page.items.length > 100)
    throw new Error('CASE_OWNER_RESPONSE_INVALID');
  const items = page.items.map(caseItem);
  if (new Set(items.map(item => item.caseId)).size !== items.length)
    throw new Error('CASE_OWNER_RESPONSE_INVALID');
  return items;
}

export function caseHistory(value: unknown, caseId: string): AssistanceCaseEvent[] {
  const history = record(value);
  if (history.caseId !== caseId || history.dataDurability !== 'Temporary data' ||
      !Array.isArray(history.events) || history.events.length < 1 || history.events.length > 10)
    throw new Error('CASE_HISTORY_INVALID');
  const events = history.events.map(raw => {
    const event = record(raw);
    if (!/^[0-9a-f]{32}$/.test(String(event.eventId)) ||
        event.action !== 'Submitted' && event.action !== 'Accepted' ||
        typeof event.version !== 'number' || !Number.isSafeInteger(event.version) || event.version < 1 ||
        !date(event.occurredAt)) throw new Error('CASE_HISTORY_INVALID');
    return event as unknown as AssistanceCaseEvent;
  });
  if (events.some((event, index) => event.version !== index + 1))
    throw new Error('CASE_HISTORY_INVALID');
  return events;
}

export const operationId = () => crypto.randomUUID().replaceAll('-', '');
export const displayDate = (value: string) => new Intl.DateTimeFormat(undefined, {
  dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC',
}).format(new Date(value));
