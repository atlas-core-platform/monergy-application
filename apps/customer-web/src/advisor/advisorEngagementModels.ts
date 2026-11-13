export type AdvisorActivityKind = 'NOTE' | 'COMMUNICATION' | 'APPLICATION_STAGE';
export type AdvisorApplicationStage = 'Not started' | 'Preparing' | 'Documents requested' | 'Ready for submission';

export interface AdvisorActivity {
  activityId: string;
  kind: AdvisorActivityKind;
  body: string | null;
  sourceFactIds: string[];
  applicationStage: AdvisorApplicationStage | null;
  version: number;
  occurredAt: string;
}

export interface AdvisorEngagement {
  customerId: string;
  version: number;
  applicationStage: AdvisorApplicationStage;
  updatedAt: string | null;
  activities: AdvisorActivity[];
  dataDurability: 'Temporary data' | 'Durable test data';
}

const identifier = (value: unknown, maximum = 128): value is string =>
  typeof value === 'string' && value.length <= maximum && /^[A-Za-z0-9][A-Za-z0-9_.:-]*$/.test(value);
const date = (value: unknown): value is string =>
  typeof value === 'string' && Number.isFinite(Date.parse(value));
const containsControlCharacter = (value: string): boolean => {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code < 32 || code === 127) return true;
  }
  return false;
};
const record = (value: unknown): Record<string, unknown> => {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('ADVISOR_ENGAGEMENT_INVALID');
  return value as Record<string, unknown>;
};
const stages: AdvisorApplicationStage[] =
  ['Not started', 'Preparing', 'Documents requested', 'Ready for submission'];
const kinds: AdvisorActivityKind[] = ['NOTE', 'COMMUNICATION', 'APPLICATION_STAGE'];

export function advisorEngagement(value: unknown, customerId: string): AdvisorEngagement {
  const raw = record(value);
  if (raw.customerId !== customerId || typeof raw.version !== 'number' ||
      !Number.isSafeInteger(raw.version) || raw.version < 0 || raw.version > 200 ||
      !stages.includes(raw.applicationStage as AdvisorApplicationStage) ||
      !(raw.updatedAt === null || date(raw.updatedAt)) || !Array.isArray(raw.activities) ||
      raw.activities.length !== raw.version ||
      (raw.dataDurability !== 'Temporary data' && raw.dataDurability !== 'Durable test data'))
    throw new Error('ADVISOR_ENGAGEMENT_INVALID');
  const activities = raw.activities.map((entry, index) => {
    const activity = record(entry);
    if (!/^[0-9a-f]{32}$/.test(String(activity.activityId)) ||
        !kinds.includes(activity.kind as AdvisorActivityKind) ||
        !(activity.body === null || typeof activity.body === 'string' && activity.body.length >= 1 &&
          activity.body.length <= 600 && !containsControlCharacter(activity.body)) ||
        !Array.isArray(activity.sourceFactIds) || activity.sourceFactIds.length > 20 ||
        !activity.sourceFactIds.every(source => identifier(source)) ||
        new Set(activity.sourceFactIds).size !== activity.sourceFactIds.length ||
        !(activity.applicationStage === null || stages.includes(activity.applicationStage as AdvisorApplicationStage)) ||
        activity.version !== index + 1 || !date(activity.occurredAt))
      throw new Error('ADVISOR_ENGAGEMENT_INVALID');
    if (activity.kind === 'NOTE' && (activity.body === null || activity.sourceFactIds.length === 0) ||
        activity.kind === 'COMMUNICATION' && (activity.body === null || activity.sourceFactIds.length !== 0) ||
        activity.kind === 'APPLICATION_STAGE' &&
          (activity.body !== null || activity.sourceFactIds.length !== 0 || activity.applicationStage === null))
      throw new Error('ADVISOR_ENGAGEMENT_INVALID');
    return activity as unknown as AdvisorActivity;
  });
  return { ...raw, activities } as unknown as AdvisorEngagement;
}

export const nextAdvisorStage = (current: AdvisorApplicationStage): AdvisorApplicationStage | null => {
  const index = stages.indexOf(current);
  return index >= 0 && index + 1 < stages.length ? stages[index + 1] ?? null : null;
};
