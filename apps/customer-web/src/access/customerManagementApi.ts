import { AccessApiError } from './accessApi';
export interface AdminCustomer { tenantId: string; customerId: string; displayName: string; email: string | null; secondaryLabel: string | null; active: boolean; version: number; ownerLinked: boolean; activationAvailable: boolean }
export interface AdminCustomerPage { tenantId: string; items: AdminCustomer[]; nextCursor: string | null; total: number; active: number; inactive: number; matching: number }
export interface StatusChange { operationId: string; expectedVersion: number; active: boolean; reason: string }
export interface StatusReceipt { tenantId: string; customerId: string; operationId: string; before: AdminCustomer; after: AdminCustomer; reason: string; recordedBy: string; recordedByLabel: string; recordedAt: string }
export interface StatusHistory { tenantId: string; customerId: string; items: StatusReceipt[]; nextBeforeVersion: number | null }
const id = (value: unknown): value is string => typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(value);
export function boundCustomer(c: AdminCustomer, tenant: string, customer?: string) {
  return c.tenantId === tenant && id(c.customerId) && (!customer || c.customerId === customer) && typeof c.displayName === 'string' && c.displayName.trim().length > 0 && c.displayName.length <= 120 && !/[\p{Cc}]/u.test(c.displayName) && (c.email === null || typeof c.email === 'string' && c.email.length <= 320 && c.email.includes('@') && !/[\p{Cc}]/u.test(c.email)) && (c.secondaryLabel === null || typeof c.secondaryLabel === 'string' && c.secondaryLabel.length <= 120 && !/[\p{Cc}]/u.test(c.secondaryLabel)) && typeof c.active === 'boolean' && Number.isSafeInteger(c.version) && c.version > 0 && typeof c.ownerLinked === 'boolean' && typeof c.activationAvailable === 'boolean';
}
export function boundReceipt(r: StatusReceipt, tenant: string, customer: string) {
 return r.tenantId === tenant && r.customerId === customer && /^[0-9a-f]{32}$/.test(r.operationId) && boundCustomer(r.before, tenant, customer) && boundCustomer(r.after, tenant, customer) && r.after.version === r.before.version + 1 && r.after.active !== r.before.active && r.before.ownerLinked === r.after.ownerLinked && r.before.displayName === r.after.displayName && r.before.email === r.after.email && r.before.secondaryLabel === r.after.secondaryLabel && r.before.activationAvailable === r.after.activationAvailable && id(r.recordedBy) && typeof r.recordedByLabel === 'string' && r.recordedByLabel.trim().length > 0 && r.recordedByLabel.length <= 320 && !/[\p{Cc}]/u.test(r.recordedByLabel) && typeof r.reason === 'string' && r.reason.trim() === r.reason && r.reason.length > 0 && r.reason.length <= 500 && !/[\p{Cc}]/u.test(r.reason) && Number.isFinite(Date.parse(r.recordedAt)) && Date.parse(r.recordedAt) >= 0 && Date.parse(r.recordedAt) <= Date.now() + 5000;
}
export function invalidCustomerResponse(): never { throw new AccessApiError(503, 'CUSTOMER_RESPONSE_INVALID'); }
// In-memory recovery is scoped to the same tenant and actor after reauthentication.
// No session credential or customer metadata is persisted in browser storage.
export const pendingCustomerChanges = new Map<string, { customer: AdminCustomer; change: StatusChange }>();
