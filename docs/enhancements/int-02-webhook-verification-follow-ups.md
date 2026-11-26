# INT-02: Webhook verification follow-ups

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Integration verification |
| Effort | M |
| Priority | Not ranked |
| Depends on | [INT-01](int-01-outbound-lifecycle-webhooks.md) |

## Summary

Complete the remaining performance, acceptance, producer-coverage, and recovery verification for outbound lifecycle webhooks. On 2026-11-10, the user approved deferring this work and marking INT-01 Done for its revised first-release scope. This item preserves the deferred checks; it does not claim that they passed or schedule their implementation.

## Why it adds value

- Measures capture overhead and delivery capacity under representative load.
- Adds repeatable evidence for process crashes, receiver deduplication, producer paths, and key recovery.

## First release acceptance criteria

- [ ] Record capture cost with 0, 1, and 100 subscriptions and a 1,000-child multi-instance commit. Measure delivery throughput against the INT-01 targets: at least 25 deliveries per second for one subscription and 100 across subscriptions for one Worker, using defaults and a receiver answering in 100 ms. Record database transactions per delivery and investigate any unmet target.
- [ ] Add real-browser coverage for the full subscription and delivery lifecycle, including management, signing-key rotation, failures, and retry/discard recovery.
- [ ] Run process-level acceptance with multiple Workers and a fixture-owned receiver, including crash/restart after receiver acceptance, lease recovery, stable redelivery identifiers, and receiver deduplication.
- [ ] Assert captured event types, identities, and payloads for message-start and administrative-action producer paths, including unchanged retries and rolled-back mutations.
- [ ] Rehearse restoring the key ring on another host and the certificate-loss recovery procedure in the [deployment guide](../deployment.md#signing-keys-and-the-key-ring), recording prerequisites, steps, and outcomes.

## Later releases

None identified. New webhook capabilities remain outside this verification item.

## Current state

Recorded on 2026-11-10 following the user-approved deferral: this follow-up has not started. INT-01 has existing PostgreSQL integration tests and recorded manual browser evidence; its [current state](int-01-outbound-lifecycle-webhooks.md#current-state) distinguishes completed checks from the work deferred here.

Browser smoke tests now cover webhook time-zone labels, the capture-setting warning/navigation, and, since [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md), the subscription form's signal broadcast types (R17, unsaved). They do not cover the full subscription and delivery lifecycle, which here now includes the `signalBroadcast.*` types as well. EVENT-04D brought the PostgreSQL producer assertions for its own types (`SignalBroadcastLifecycleEventTests`). Dispatch integration tests cover competing acquirers, lease loss, crashes, and stale finalizers, but the process-level multi-Worker acceptance run remains outstanding. Key-protection tests cover shared rings, previous certificates, and unavailable protection; they do not replace the recovery rehearsal.

Part of the first criterion was measured on 2026-11-25 with the [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md#rest-service-tasks-and-webhooks), its stub as the receiver answering in 100 ms, default settings, and one Worker on a 4-processor development machine; the figures are in [capacity planning](../deployment.md#webhooks). Capture and fanout with delivery off cost 7% of purchase-request throughput with 1 subscription, 10% with 10, and 11% with 100. Delivery reached 22.6 per second for one subscription and 94.7 across ten, with the host 40–66% busy, so both targets were missed narrowly and the cause is not investigated. No delivery repeated or failed and every signature verified. Still open: the 1,000-child multi-instance commit, transactions per delivery, and the investigation of the missed targets.

## Builds on

- [INT-01](int-01-outbound-lifecycle-webhooks.md), its existing capture, dispatch, signing, and key-protection tests, and its recorded verification.
- The [Chromium smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [Worker-enabled acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md).
- The webhook operations and recovery procedures in the [deployment guide](../deployment.md#lifecycle-webhooks).

## Open questions

- Which representative deployment resources and workload should accompany the recorded performance results?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan). Use synthetic local data and a fixture-owned receiver; no production endpoints or credentials are needed.
