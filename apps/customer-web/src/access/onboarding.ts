import { createContext, useContext } from 'react';

export interface OnboardingFeatures {
  customerRelationships: boolean;
}
export const OnboardingContext = createContext<OnboardingFeatures>({
  customerRelationships: false,
});
export const useOnboarding = () => useContext(OnboardingContext);

export interface DefaultAccessPackState {
  installedVersion: string | null;
  installedIdentity: string | null;
  installedAt: string | null;
  policyVersion: number;
  ready: boolean;
}
export interface DefaultAccessPackChange {
  kind: string;
  objectId: string;
  before: string | null;
  after: string | null;
}
export interface DefaultAccessPackPlan {
  installedVersion: string | null;
  targetVersion: string;
  policyVersion: number;
  planHash: string;
  alreadyInstalled: boolean;
  changes: DefaultAccessPackChange[];
  conflicts: string[];
}
export interface DefaultAccessPackAdoptionResult {
  packVersion: string;
  policyVersion: number;
  operationId: string;
  planHash: string;
  replayed: boolean;
}

export interface TenantSetupState {
  enabled: boolean;
  tenantId: string;
  actorId?: string;
  policyVersion?: number;
  organizationName?: string;
  countryCode?: string;
  timeZone?: string;
  initialAdministratorEmail?: string;
  state?: 'Requested' | 'Provisioning' | 'ProvisioningFailed' | 'ReadyForAdmin' | 'Active';
  receiptReference?: string | null;
  customerRelationships: boolean;
  accessPack?: DefaultAccessPackState;
}
