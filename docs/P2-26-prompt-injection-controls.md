# P2-26 — Prompt-injection and unsupported-answer controls

P2-26 advances `M2-WS07-E03-F02` in the existing Customer Financial Q&A
journey. Questions and canonical owner claims are normalized and screened for
bounded, high-confidence instruction-override, authority-message, prompt
disclosure, tool-execution and credential-exfiltration patterns before they can
reach the configured model provider.

The Q&A application records a safe `UNSUPPORTED` result without citations when
the question is unsafe. Retrieved source content is revalidated against its
owner and current authorization, but instruction-like content is neither sent
to the provider nor retained as a retrieved claim or released to the Customer.
The Integration Gateway independently rejects unsafe direct model-adapter
requests, so bypassing the Q&A application fails closed.

Released `SOURCES` answers remain limited to exact canonical claim identifiers.
Model prose, invented or duplicate citations, contradictory states and
authoritative model arithmetic remain prohibited. Missing or incomplete owner
grounding continues to produce `INSUFFICIENT_DATA`; out-of-scope questions and
screened content produce a safe `UNSUPPORTED` response.

This is deterministic source protection, not a claim of general adversarial
model robustness. OD-11 model approval, live-provider evaluation, semantic or
vector retrieval, broader model-quality evaluation, physical persistence,
installed-browser, retained LOCAL and live acceptance remain separate gates.
