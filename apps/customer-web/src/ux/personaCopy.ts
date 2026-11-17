export type PersonaArea =
  | 'administration'
  | 'product'
  | 'kyc-review'
  | 'advisor'
  | 'dsa'
  | 'lender'
  | 'financial-professional'
  | 'property-valuer'
  | 'legal-advisor'
  | 'operations-support';

export interface PersonaEntryCopy {
  eyebrow: string;
  headline: [string, string];
  introduction: string;
  assurance: string;
  workspaceName: string;
}

export const personaEntryCopy: Record<PersonaArea, PersonaEntryCopy> = {
  administration: {
    eyebrow: 'ORGANIZATION SETTINGS',
    headline: ['Manage your people.', 'Keep access clear.'],
    introduction: 'Manage people, roles and permissions for your organization.',
    assurance: 'Organization Settings opens after we confirm your identity and administrator role.',
    workspaceName: 'Organization Settings',
  },
  product: {
    eyebrow: 'YOUR FINANCIAL WORKSPACE',
    headline: ['Welcome back.', 'Your finances in one place.'],
    introduction: 'Bring your financial information together in a private, easy-to-follow workspace.',
    assurance: 'Your workspace opens after we confirm your identity and customer account.',
    workspaceName: 'Financial Workspace',
  },
  'kyc-review': {
    eyebrow: 'IDENTITY REVIEW',
    headline: ['Review customer details.', 'Record a clear outcome.'],
    introduction: 'Review the customer information assigned to you and record the outcome.',
    assurance: 'Only reviews assigned to you are available here.',
    workspaceName: 'Identity Review',
  },
  advisor: {
    eyebrow: 'FINANCIAL ADVISOR',
    headline: ['Support your customers.', 'Respect their choices.'],
    introduction: 'Work with customers assigned to you and view information they have agreed to share.',
    assurance: 'A customer appears only while your role, assignment and their permission to share are current.',
    workspaceName: 'My Customers',
  },
  dsa: {
    eyebrow: 'CASE MANAGER',
    headline: ['Review new cases.', 'One at a time.'],
    introduction: 'Pick up a submitted assistance case and follow its progress.',
    assurance: 'Financial information stays protected unless it is separately shared with you.',
    workspaceName: 'Cases',
  },
  lender: {
    eyebrow: 'LENDER PROFESSIONAL',
    headline: ['Review applications.', 'Own the decision.'],
    introduction: 'Review applications assigned to you and record your decision.',
    assurance: 'Only applications assigned to you are shown. Monergy does not make the lending decision.',
    workspaceName: 'Applications to Review',
  },
  'financial-professional': {
    eyebrow: 'FINANCIAL PROFESSIONAL',
    headline: ['Review financial information.', 'Record your conclusion.'],
    introduction: 'Review the information shared with you and record your own professional conclusion.',
    assurance: 'Only assigned reviews with current customer permission are shown.',
    workspaceName: 'Financial Reviews',
  },
  'property-valuer': {
    eyebrow: 'PROPERTY VALUER',
    headline: ['Review the property.', 'Record your valuation.'],
    introduction: 'Review an assigned property and its supporting documents before recording your valuation.',
    assurance: 'Only properties assigned to you with current customer permission are shown.',
    workspaceName: 'Property Valuations',
  },
  'legal-advisor': {
    eyebrow: 'LEGAL ADVISOR',
    headline: ['Review the matter.', 'Record your findings.'],
    introduction: 'Review an assigned matter and its supporting documents before recording your outcome.',
    assurance: 'Only matters assigned to you with current customer permission are shown.',
    workspaceName: 'Legal Reviews',
  },
  'operations-support': {
    eyebrow: 'OPERATIONS SUPPORT',
    headline: ['Resolve processing problems.', 'Keep work moving.'],
    introduction: 'Review tasks that need attention and safely try again or cancel eligible work.',
    assurance: 'This workspace shows task status only; customer financial information remains private.',
    workspaceName: 'Operations',
  },
};

export const readableDate = (value: string) => new Intl.DateTimeFormat('en-IN', {
  day: '2-digit', month: 'short', year: 'numeric',
}).format(new Date(value));

export const readableDateTime = (value: string) => new Intl.DateTimeFormat('en-IN', {
  day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit',
}).format(new Date(value));

export const readableMoney = (value: number, currency: string) => new Intl.NumberFormat('en-IN', {
  style: 'currency', currency, maximumFractionDigits: 2,
}).format(value);

export const friendlyActionFailure = 'We could not save that change. Refresh the page and try again.';
