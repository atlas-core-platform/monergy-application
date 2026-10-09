export interface ReviewValue {
  label: string;
  value: string;
  /** Compare stable identifiers when different objects can share a display name. */
  identity?: string;
}

export function AccessChangeReview({
  current,
  proposed,
}: {
  current?: ReviewValue[];
  proposed: ReviewValue[];
}) {
  return (
    <dl className="access-review">
      {proposed.map((item) => {
        const previous = current?.find((value) => value.label === item.label);
        const changed =
          Boolean(current) &&
          (previous?.identity ?? previous?.value) !== (item.identity ?? item.value);
        return (
          <div
            className={`access-review-row ${changed ? 'is-changed' : current ? 'is-unchanged' : 'is-new'}`}
            key={item.label}
          >
            <dt>
              {item.label}
              <span className="access-review-state">
                {changed ? 'Changed' : current ? 'Unchanged' : 'New'}
              </span>
            </dt>
            <dd>
              {changed && (
                <span className="access-review-before">
                  <span className="access-review-badge">Current</span>
                  <span>{previous?.value ?? 'None'}</span>
                </span>
              )}
              <span className="access-review-after">
                <span className="access-review-badge">
                  {current && !changed ? 'Current' : 'Proposed'}
                </span>
                <span>{item.value}</span>
              </span>
            </dd>
          </div>
        );
      })}
    </dl>
  );
}
