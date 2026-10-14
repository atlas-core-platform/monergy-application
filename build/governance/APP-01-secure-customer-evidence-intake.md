# APP-01 — Secure customer workspace and evidence intake

Status: implemented candidate; hosted and Docker acceptance pending. Production accepted: **no**.

Authority: accepted Monergy Midnight direction, AR-002 → AM-08 → UI-05 → CP-01 → customer/advisor relationship access → Tenant Admin setup, followed by the product-owner direction to begin the actual Monergy application on 2026-10-14.

## Product outcome

APP-01 replaces the LOCAL/UAT customer shell with the first bounded product journey:

1. A customer establishes the existing C&I tenant session.
2. The gateway validates that session with C&I and resolves the active customer owned by the actor.
3. Access Management evaluates current customer-scoped product capabilities. The browser neither selects nor infers a customer identifier.
4. The protected Financial Workspace opens with only relevant destinations: Overview, Evidence, Search and Reports.
5. Evidence intake accepts PDF, PNG, JPEG or plain text up to 1 MB, shows an exact review drawer, and submits only after confirmation.
6. The gateway revalidates the session, ownership and `evidence.document.upload` capability before staging content.
7. The Evidence service verifies the staged descriptor, creates an idempotent document version and returns an owner-confirmed receipt.

Access Management remains a separately gated administration module under `/access`. Customer navigation does not expose Access Management, System Expert, Engineering Foundation or UAT operations. Tenant Administrator authority still grants no business access.

## Boundary decisions

| Concern          | Decision                                                                                                                                                             |
| ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Authentication   | Preserve the existing LOCAL C&I reference-key/session architecture. APP-01 does not introduce a Production identity design.                                          |
| Customer context | C&I resolves one active customer owned by the authenticated actor from its tenant database. Unknown ownership fails closed.                                          |
| Authorization    | AM remains the capability and relationship decision owner. Evidence repeats the current decision at its service boundary.                                            |
| Browser trust    | Tenant/session headers identify the live session. Customer ID, actor, authorization decision, document IDs and evidence IDs are constructed or resolved server-side. |
| Evidence content | A separate workload credential protects the LOCAL reference staging endpoint. The credential never reaches the browser.                                              |
| Idempotency      | The browser creates one opaque operation ID per selected file. Staging and evidence registration replay the same content safely; conflicting reuse returns `409`.    |
| Navigation       | Product and administration use the same Midnight foundation while retaining strict module navigation and authority boundaries.                                       |
| Data durability  | Uploaded content and evidence metadata use the running reference adapter and can reset with the runtime. The UI and API state `Runtime only`.                        |

## Explicit exclusions

- No Production deployment, Production identity provider, MFA, invitation delivery, storage, retention, backup, HA/DR or release acceptance.
- No resolution of OD-07 retention or OD-10 Production identity decisions.
- No document extraction, automated categorization, financial profile mutation, score methodology, advice, AI or provider connection is claimed by a successful upload.
- No advisor-facing customer selector is introduced. APP-01 is customer-owner entry; advisor product navigation remains a later bounded slice.
- Search and Reports retain their existing governed reference behavior. Their presence does not claim broader channel strategy or Production data.
- The session upload list is deliberately labeled **This session**; it is not represented as a durable evidence library.

## LOCAL/UAT use

Start the existing onboarding profile and retrieve the customer key:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile onboarding-final -EnableOnboarding
.\build\uat\Invoke-MonergyUat.ps1 -Action Credentials -Profile onboarding-final -Tenant T001 -Actor A100
```

Open `http://127.0.0.1:4173/` (or the profile's configured port), enter tenant `T001` and the private A100 key, then open **Evidence**. Tenant administration remains at `/access` and uses A900.

An existing onboarding profile receives one new evidence-intake workload token when it is next prepared or started. Fixture users that have no business role receive the conservative default Customer or Financial Advisor role once; an existing operator-selected role is never overwritten.

## Acceptance

The candidate must pass:

- customer-web typecheck, lint, unit and production build;
- C&I/Platform/Evidence/gateway compilation and relevant unit regressions;
- authentication isolation for customer and administration direct routes;
- server-resolved customer context, no-owner denial and Tenant Admin product denial;
- allowed upload, invalid type/size/name rejection, access denial, cross-customer denial, exact idempotent replay and conflicting replay;
- Evidence metadata readback for the created document;
- product-navigation isolation, review-before-write, keyboard file selection, drag/drop, compact viewport and reduced motion;
- impact-aware continuous verification, tenant-access owner integration and Local Docker UAT acceptance on the published candidate.

Passing LOCAL/UAT checks accepts only this bounded candidate. Production release remains separately governed.
