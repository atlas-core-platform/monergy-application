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
