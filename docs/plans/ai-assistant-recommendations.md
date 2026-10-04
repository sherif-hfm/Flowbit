# AI assistant recommendations

Agreed 2026-10-04. These recommendations describe planned improvements, not
claims about shipped behavior. See the [staged implementation plan](ai-assistant-improvement-plan.md)
for items 1–4 and 7 and their verification status.

1. **Fix the known context-cache defect.** Prevent excerpts for different draft
   entities from replacing one another; measure repeated reads afterward.
2. **Check business requirements explicitly.** Compare the completed workflow
   against requested steps, conditions, roles, and outcomes. Structural validation
   alone does not establish business correctness. Unresolved requirements block
   applicable proposals in the reviewed execution path.
3. **Add controlled parallel AI calls.** Begin with independent source analysis
   and final review, with at most two workers and one coordinator owning draft
   mutations. Concurrent branch generation is a later extension requiring shared
   contracts, reserved IDs, and conflict handling.
4. **Improve tracing and performance visibility.** Record safe timing, usage,
   read, edit, retry, and repair metadata to identify actual bottlenecks.
5. **Make authoring sessions durable.** Eventually support recovery after browser
   refresh or server restart using persisted checkpoints, without storing provider
   keys. This is outside the current implementation scope.
6. **Evaluate specialist agents where useful.** Consider broader analysis,
   construction, and review roles if evidence justifies them. Keep Flowbit's
   deterministic validator and atomic edits authoritative. General agent
   infrastructure is outside the current scope.
7. **Benchmark improvements consistently.** Freeze model/provider settings,
   inputs, code, and acceptance policies; compare correctness, time, usage,
   preservation, and cancellation/recovery with independent checkers.
8. **Evaluate a framework as an optional integration.** Microsoft Agent Framework
   is a possible .NET candidate, but adoption requires evidence of benefit over
   custom orchestration. No framework dependency is part of this change.

Implementation order: measurement baseline, cache fix, serial requirements
review, controlled parallelism, independent evaluation. Implement and verify
one stage at a time. Live billed evaluation requires separate authorization.

[Documentation home](../index.md) · [AI authoring](../ai-authoring.md)
