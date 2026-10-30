# P2-25 — Authorization-aware Search scopes

P2-25 advances `M2-WS07-E02-F01` for the Customer Search journey. Search access
remains a separate capability, while each visible source scope also requires
the current owner permission for that source:

- Documents: `search.query.execute` and `evidence.document.read`
- Financial information: `search.query.execute` and
  `financial-profile.profile.read`
- All: all three permissions

The gateway resolves this fixed mapping. The browser cannot submit a capability,
permission ID, actor, tenant or Customer. Access Management is called before the
owner read and again before results are released, so authority loss during the
query fails closed.

Customer Web preserves an authorized source scope when another source permission
is missing. The missing action exposes its existing canonical Request Access
target: Documents or Financial information. It does not submit the protected
operation and fall through to a normal raw 403.

Search remains lexical and rebuilt from current authorized owner data. This
increment does not add semantic/vector search, natural-language generation,
new Access Management targets, Consent approval, provider access, cached fallback
or broader Search indexing. Physical owner execution, installed-browser,
retained LOCAL and live acceptance remain pending.
