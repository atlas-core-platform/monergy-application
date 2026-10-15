import { createContext, useContext } from 'react';

export interface OnboardingFeatures {
  customerRelationships: boolean;
}
export const OnboardingContext = createContext<OnboardingFeatures>({
  customerRelationships: false,
});
export const useOnboarding = () => useContext(OnboardingContext);

export type SetupAccessPolicyState = 'Ready' | 'UpdateAvailable' | 'Customized' | 'Blocked';

export interface SetupAccessPolicyView {
  state: SetupAccessPolicyState;
  changeCount: number;
  roleChangeCount: number;
  permissionChangeCount: number;
  conflictCount: number;
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
  accessPolicy?: SetupAccessPolicyView;
  customerRelationships: boolean;
}
