interface AdministratorWelcomeReceipt {
  welcomeUrl: string;
  expiresAt: string;
  email: string;
}

interface AdministratorWelcomeOrganization {
  organizationCode: string;
  displayName: string;
  hostname: string;
  initialAdministrator: string;
}

export function prepareAdministratorWelcomeUrl(
  receipt: AdministratorWelcomeReceipt,
  organization: AdministratorWelcomeOrganization,
  currentLocation: Pick<Location, 'protocol' | 'port'>,
) {
  const welcome = new URL(receipt.welcomeUrl);
  const invitation = new URLSearchParams(welcome.hash.slice(1));
  if (welcome.hostname !== organization.hostname || welcome.pathname !== '/welcome' ||
      welcome.protocol !== currentLocation.protocol || welcome.port !== currentLocation.port ||
      invitation.get('tenant') !== organization.organizationCode ||
      !/^[0-9a-f]{64}$/.test(invitation.get('ticket') ?? '') ||
      receipt.email !== organization.initialAdministrator)
    throw new Error('The administrator setup page was not valid.');
  invitation.set('organization', organization.displayName);
  invitation.set('email', receipt.email);
  welcome.hash = invitation.toString();
  return welcome;
}
