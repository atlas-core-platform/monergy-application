# Evidence Service

Evidence owns uploaded bytes, document version metadata and provenance. The LOCAL onboarding customer upload path requires current owner/capability authorization at the Gateway and the dedicated Evidence workload token for byte intake.

T1-48 checks file bytes with the pinned LOCAL ClamAV sidecar before staging. Only a current hash-bound successful check can register a customer-intake reference. Rejection, stale definitions, incompatible metadata, overload, cancellation and scanner errors cannot become accepted content. The in-memory check receipt is not a durable scan audit or certification of existing fixtures/object-store paths. No customer-supplied scan status is trusted.

[Operator setup/live acceptance](../../build/uat/Document-Security.md) and [scope/native evidence](../../build/governance/t1-48-evidence-malware-gate.md) distinguish passing native protocol/HTTP tests from the unexecuted live Docker/engine test. Classification, OCR, quarantine/rescanning and production security acceptance remain open.
