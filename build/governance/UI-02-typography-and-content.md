# UI-02 — Typography and content refinement

The platform owner requested a more polished, modern interface on 2026-10-08 after reviewing the local UAT recording. This implementation applies a shared typography and visual treatment to Application, its Access Management journeys, and the separately versioned System Expert interfaces.

## Design contract

- Bundle the unmodified Inter Latin variable WOFF2 from `@fontsource-variable/inter@5.3.0` with its SIL Open Font License. Assets load from the application itself; no remote font service or installed-font assumption. Other writing systems retain the system fallback.
- `tokens.json` owns the content scale: 32px page title, 20px section title, 15px body, 14px labels/actions, 13px supporting text, 1.6 body line height. Use regular, medium and semibold emphasis. Navigation retains its separate compact scale.
- Shared typography includes body content, native controls, Ant Design components and overlays mounted outside the shell. Placeholder and empty-state text use readable contrast.
- Navy/teal feature headers establish page context; white data panels, restrained elevation, clearer table hierarchy and consistent form spacing keep administration readable. Hover feedback, drawer transitions and brief staggered entry motion honor reduced-motion preferences.
- Short desktop viewports scroll the navigation independently so the collapse action remains reachable. Mobile search has an explicit accessible name.

## Review behavior

Create reviews display the entered name/code and selected permissions, capabilities/scopes, people or resource identifiers. Edit reviews display previous and proposed values for changed fields. Cancellation preserves the form and performs no write; confirmation sends the existing version-checked payload. Conflict handling retains the unsaved values. The commit button names the action (Create role, Create permission, Save changes).

## Verification

Local validation includes production build, TypeScript, lint, the frontend unit suite, browser navigation/import/report/search journeys, and a new browser scenario verifying the bundled font in portal content, full role review, cancellation and the exact confirmed payload. Desktop/mobile visual inspection and axe WCAG 2 A/AA + 2.1 AA checks cover overview, tenant connection, people, role drawer, review, mobile administration and reports.

The local browser uses synthetic API fixtures; hosted post-merge Local Docker UAT separately validates real owner boundaries and persisted administration. This delivery does not advance production acceptance or regenerate historical D01–D16 evidence.

System Expert consumes an immutable Application snapshot with file hashes, including the font bytes/license, tokens, typography and shell. Its static launcher, fallback and React build must ship the same assets.
