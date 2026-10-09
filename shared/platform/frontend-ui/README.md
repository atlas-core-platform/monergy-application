# Frontend UI Foundation

This D02 package is the bounded, business-neutral frontend foundation.

- `tokens.json` is the single semantic source for colour, typography, spacing,
  radii, elevation, focus indication and responsive breakpoints.
- `semantic-tokens.generated.css` exposes those values as CSS variables and
  Tailwind theme values.
- `theme.ts` maps the same source into Ant Design `ConfigProvider` tokens.
- `FoundationProvider.tsx` composes the provider and the one deliberate reset:
  Ant Design reset CSS. Tailwind Preflight is not imported.

The package does not wrap all Ant Design components and contains no Monergy
domain component. Tailwind utilities may compose layout and visual treatment,
but must not depend on undocumented Ant Design internals or routine important
overrides.

## Midnight appearance (MWP-03 candidate)

`tokens.json` also owns the opt-in `midnight` palette and `compact` density.
`midnightTheme` maps these to Ant Design; `midnight.css` applies the same values
to the shared shell. Use `appearance="midnight"` on `FoundationProvider` and
`WorkspaceShell`, and set `data-monergy-theme="midnight"` on the document root
so body-mounted drawers, menus and dialogs inherit the same semantic variables.
The application owner must update the root attribute when changing appearance.
The default remains `light`; other applications are not automatically migrated.

Access Management owns its domain components and authenticated lifecycle. The
foundation owns only appearance, navigation presentation, focus, motion and
responsive behavior. It must not decide tenant authority or authenticate users.

After changing tokens, run `pnpm --filter @monergy/ui-foundation generate:tokens`.
`check:tokens` rejects drift between the JSON source and generated CSS. Midnight
uses dark primary-button text, a separate control-border token, 13px body text,
36px desktop controls, an expandable 208px/64px sidebar, and 44px narrow/coarse
interaction targets. Browser zoom remains enabled. Reduced-motion preferences
disable shell, drawer and modal animation.
