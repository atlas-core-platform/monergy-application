import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { AccessChangeReview } from '../src/access/AccessChangeReview';

afterEach(cleanup);
describe('access review comparisons', () => {
  it('compares stable identifiers even when role names match and labels unchanged values', () => {
    render(
      <AccessChangeReview
        current={[
          { label: 'Business role', value: 'Analyst', identity: 'R1' },
          { label: 'Account status', value: 'Active' },
        ]}
        proposed={[
          { label: 'Business role', value: 'Analyst', identity: 'R2' },
          { label: 'Account status', value: 'Active' },
        ]}
      />,
    );
    expect(screen.getByText('Business role').closest('.access-review-row')).toHaveTextContent(
      'ChangedCurrentAnalystProposedAnalyst',
    );
    expect(screen.getByText('Account status').closest('.access-review-row')).toHaveTextContent(
      'UnchangedCurrentActive',
    );
  });
  it('labels a new configuration without inventing previous values', () => {
    render(<AccessChangeReview proposed={[{ label: 'Name', value: 'Reviewer' }]} />);
    expect(screen.getByText('Name').closest('.access-review-row')).toHaveTextContent(
      'NewProposedReviewer',
    );
    expect(screen.queryByText('Current')).not.toBeInTheDocument();
  });
});
