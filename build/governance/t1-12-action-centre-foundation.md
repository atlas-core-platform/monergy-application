# T1-12 — Customer Action Centre Foundation

Status: bounded implementation candidate under AR-003. This document defines the exact derivation rules implemented by T1-12; it does not create a task engine, recommendation system, or new business owner.

## Purpose

Provide the Customer with a truthful **Action Centre** that summarizes only attention states derivable from already-implemented Documents and trusted Financial information.

The page is an experience projection over existing product APIs. It owns no task state, event state, priority, due date, workflow, financial assessment, or recommendation.

## Inputs

T1-12 reads only:

- current authorized Document library count from the existing Evidence product API;
- current authorized Financial Profile facts from the existing Financial Profile product API;
- current product capabilities already returned by the product context.

No new capability, permission, owner contract, database table, queue, or event is introduced.

## Exact derivation rules

Rules are evaluated in this order and are intentionally bounded:

1. **No Documents**
   - Condition: Document read is available and the current Document count is exactly zero.
   - Presentation: “No financial documents have been added yet.”
   - Allowed action: Add document only when document upload is already authorized.
   - Stop after this rule; do not also claim missing Income/Expense.

2. **Documents exist but no trusted supported financial information**
   - Condition: Documents are successfully loaded with count > 0; Financial Profile is successfully loaded; trusted fact count is zero.
   - Presentation: review Documents and explicitly add only checked Income/Expense details.
   - This is not a claim that extraction exists in every Document.

3. **Trusted supported information exists but Income is absent**
   - Condition: Financial Profile successfully loaded; at least one trusted supported fact exists; trusted Income count is zero.
   - Presentation: no trusted Income information is currently represented.
   - Optional action: Review Documents when Document access exists.

4. **Trusted supported information exists but Expense is absent**
   - Same rule as Income, for Expense.

5. **No current bounded attention**
   - Condition: none of the rules above are true.
   - Presentation explicitly states that this does **not** mean the customer’s overall finances are complete.

## Availability and failure semantics

- An owner/API failure is **unknown**, never zero.
- Unknown Document or Financial state must not generate a missing-data attention item.
- Partial successful state may remain visible with a warning for the unavailable owner.
- The page is protected by the existing customer workspace/session gate.
- No browser-selected customer identity is introduced.

## Explicit exclusions

T1-12 does not implement:

- persisted tasks, assignments, due dates, reminders, task history, task completion, or notifications;
- urgency, priority ranking, risk ranking, recommendation, advice, or Financial Health scoring;
- missing-document inference from future loan/lender/professional requirements;
- provider refresh/error tasks;
- KYC, Consent, lending, professional review, family/estate, or application workflow tasks;
- background event ingestion or a new Action Centre service.

Future Action Centre expansion requires an exact owner/event/task contract and the applicable permission/Consent gates before new attention types are added.

## Product language

Use **Action Centre**, **Current attention**, **Documents**, and **trusted financial information**. The page must state that attention is deterministic and not ranked, and that attention is not advice.

## Acceptance evidence

Focused component evidence must prove:

- no-Document state;
- Documents-with-no-trusted-information state;
- one-supported-category-missing state;
- both-supported-categories-present clear state;
- owner-unavailable state does not become a false zero/missing-data item;
- protected route and existing product navigation remain intact.

No additional manually-triggered hosted CI cycle is required solely for this bounded candidate; execution evidence and product acceptance remain separate CTO decisions.
