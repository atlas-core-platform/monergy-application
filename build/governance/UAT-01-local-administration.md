# UAT-01 — Local administration and tenant access

Candidate implementation authorized 2026-10-08. The owner selected the local Windows system and Docker Desktop for hands-on UAT. This implements the agreed sequence: shared Access Management UI, connected administration journeys, end-to-end tenant/access validation, and explicit production-readiness wiring.

## Runtime decisions

- Docker Desktop Linux containers, one named Compose project, loopback-only browser port, retained PostgreSQL volumes and a separate migration/bootstrap job.
- Service-owned databases and distinct owner, runtime, and delivery credentials. Bootstrap credentials are unavailable to runtime services. Initial credentials are generated once, stored outside source control, and retained across starts.
- A local shared network namespace preserves the existing loopback-only owner transport contract. Each service remains a separate process/container with its own configuration and database authority. This is a LOCAL topology, not a Production networking design.
- Start and stop are repeatable. Stop retains data. Reset requires an explicit destructive-data confirmation and affects only this Compose project's named volumes.
- Current tenant and session are verified by their owners. TenantAdmin remains separate from business permissions. Reference actor/body fields cannot substitute for live authorization in this profile.
- Runtime visibility distinguishes a running boundary, connected access enforcement, and implemented business operations. A worker readiness/probe endpoint is not proof of a completed business-consumer journey.

## Administration acceptance

Tenant context → permissions → role → group → CSV preview/stage/process → member access → resource checks → revocation → durable Audit evidence. Include invalid-file rejection, stale policy conflict, cross-tenant rejection, owner outage, restart/replay, and last-administrator protection. Credentials and session tokens must not appear in browser persistence, URLs, logs, test artifacts, or committed configuration.

## Release boundary

The localhost profile supports operator UAT on Development-only adapters. D01–D16 acceptance and SG-01/02/03/04 decisions are unchanged. Readiness output must retain blockers for Production identity, workload identity/TLS, placement, secrets rotation, durable worker/broker wiring, consent/provider integration, backup/restore/HA/DR evidence, capacity targets, and approved release gates until actual evidence resolves each item. A green LOCAL suite does not advance Production acceptance.
