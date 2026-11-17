# UX-DEMO-01 Persona Language Audit

## Scope and method

This audit covers the normal entry, navigation, home and primary working surfaces for all ten accepted personas. It compares the accepted Application baseline `a0a889e022996cc9c770a7a4484aad733f432d96` with the UX-DEMO-01 candidate and checks presentation-boundary strings rather than internal identifiers, transport contracts, tests or fixtures.

The companion machine-readable audit is `UX-DEMO-01-PERSONA-LANGUAGE-AUDIT.json`. Regenerate it with:

```powershell
node build/governance/scan-persona-language.mjs
```

The scan reviewed 2,843 baseline and 2,873 candidate user-facing source strings across all 94 rendered Customer Web source files. It identified 162 changed or removed baseline strings. Non-diagnostic technical-term matches fell from 37 to 0. One match remains explicitly allowlisted on `/foundation`, the dedicated toolchain evidence page, which is outside normal persona navigation.

## Persona audit

| Persona / route | Previous wording examples | Revised wording / category | Reason | Deliberately retained |
| --- | --- | --- | --- | --- |
| Customer `/` | “Financial clarity”; “bounded attention items”; “exact calculation citations” | “Welcome back”; “Your financial overview”; “tasks that may need your attention”; links to supporting calculations | Makes the home page personal and tells the customer what to do next | “Source” is retained where provenance matters, with document context and no verification claim |
| Organization Administrator `/access` | “Resource grants”; raw person reference; “resource lookup”; technical access-conflict language | “Individual access”; “Business record”; “People”, “Roles”, “Permissions”, “Teams”; human refresh/review guidance | Frames administration around people and the effect of a change | Formal role names, “Consent”, “permissions” and “sessions” remain because they are meaningful business/security concepts; protected Audit detail identifiers remain available for investigation |
| Financial Advisor `/advisor` | “Authorized customer contexts”; “source-backed advisory note”; policy/assignment terminology | “My Customers”; “Customers available to review”; “Advisory note saved”; natural assignment and customer-sharing explanations | Makes the workspace read like advisory work without changing access checks | “Assignment” and “customer permission” remain because they explain the real boundary |
| DSA / Case Manager `/dsa` | “DSA Case Workspace”; durability and internal case-reference language | “Cases”; “Cases waiting for you”; “Accept case”; human conflict and history language | Keeps the journey focused on picking up and managing cases | Formal “DSA / Case Manager” appears only where role identity matters |
| Lender Professional `/lender` | “Assigned application resources”; durability/version wording | “Applications to Review”; “Required documents”; “Review document”; “Decision recorded” | Presents a bounded human review and makes Monergy’s non-decision role clear | Application reference remains as a business-facing reference |
| Financial Professional `/financial-professional` | “Assigned source review”; raw source versions and durability | “Financial Reviews”; “Documents to review”; “Professional notes”; “Certification history” | Centers the professional’s review and recorded conclusion | “Certification” remains bounded by text stating the professional owns the conclusion |
| Property Valuer `/property-valuer` | Assignment identifiers, source versions and data durability | “Property Valuations”; “Supporting documents”; “Mark reviewed”; formatted value/date and valuation history | Separates customer-reported figures from the valuer’s own conclusion | “Professional valuation” is retained to preserve the authority distinction |
| Legal Advisor `/legal-advisor` | Assignment/version/durability detail | “Legal Reviews”; “Matter”; “Supporting documents”; “Review outcome”; “Review history” | Makes the human-authored legal review clear without implying filing or court submission | “Legal Advisor” remains where formal role identity matters |
| Operations Support `/operations-support` | “Operational recovery”; job references; failure codes; raw change/actor references | “Tasks needing attention”; “Processing problem”; “Try again”; “Cancel task”; human status/history | Lets operations staff recover work without engineering vocabulary or customer financial detail | Task timing and attempt counts remain because they help an operator decide safely |
| Platform Administrator `/platform` | “Platform control plane”; implementation boundary; raw gate IDs/revisions and actor IDs | “Monergy Administration”; “System status”; “Available”; “Needs attention”; “Release readiness”; operational notes and history | Keeps specialist status useful while removing source-code and infrastructure presentation | Environment label and specialist status are retained; no ports, hosts, containers or credentials are shown |

## Shared presentation layer

- `personaCopy.ts` centralizes persona entry headings, introductions, assurances, workspace names, common failure copy and India-facing date/money formatting.
- Internal role names, route paths, capabilities, assignments, authorization decisions and persistence contracts are unchanged.
- User errors are presentation-safe; technical details remain available only in protected/internal diagnostics and logs.
- Existing headings, labels and accessible names remain descriptive. Button text was made more explicit rather than shortened to icon-only actions.

## Diagnostic allowlist

| Surface | Allowed vocabulary | Justification |
| --- | --- | --- |
| `/foundation` in `ToolchainFoundation` | `Runtime` | Dedicated implementation/toolchain evidence surface, not linked from normal persona navigation |

No whole-file or test-fixture allowlist is used. The scanner fails when a banned term appears on a reviewed normal persona surface.

## Nonclaims

This is a presentation-boundary refinement. It does not change authorization, role membership, permissions, assignments, customer ownership, Consent, decision ownership, schemas, storage, providers, calculations or workflow state machines. Historical P3-10 and A–P evidence remains unchanged.
