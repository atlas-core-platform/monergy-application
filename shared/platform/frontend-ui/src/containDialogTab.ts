import type { KeyboardEvent } from 'react';

/** Close the Tab boundary before browser chrome can receive focus.
 * Ant Design still owns initial focus, popup focus, Escape and restoration.
 */
export function containDialogTab(event: KeyboardEvent<HTMLElement>): void {
  if (event.key !== 'Tab' || event.defaultPrevented) return;
  const dialog = event.currentTarget;
  // Portalled dropdowns retain their own keyboard behavior.
  if (!dialog.contains(event.target as Node)) return;
  const controls = Array.from(
    dialog.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex]'),
  ).filter(
    (element) =>
      element.tabIndex >= 0 &&
      !element.matches(':disabled') &&
      !element.closest('[hidden], [inert]') &&
      element.getClientRects().length > 0 &&
      getComputedStyle(element).visibility !== 'hidden',
  );
  const first = controls[0];
  const last = controls[controls.length - 1];
  if (
    first &&
    last &&
    ((!event.shiftKey && document.activeElement === last) ||
      (event.shiftKey && document.activeElement === first))
  ) {
    event.preventDefault();
    (event.shiftKey ? last : first).focus();
  }
}
