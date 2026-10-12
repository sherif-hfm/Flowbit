# Gateway examples

These definitions are small, valid Flowbit workflows designed to make one
gateway behavior easy to observe at a time. Load any JSON file directly into
`flowbit-editor.html`, or publish it and run an instance through the API/UI.

## Examples

| File | What it demonstrates |
| --- | --- |
| `01-exclusive-priority-and-default.json` | An Exclusive split evaluates non-default routes by ascending `conditionPriority` and selects only the first match. The separate Exclusive merge has multiple incoming flows and one unconditional outgoing flow. |
| `02-parallel-fork-and-join.json` | A Parallel split creates Finance, Legal, and Security work simultaneously. The Parallel join waits for one token from every incoming branch. |
| `03-inclusive-conditional-split-and-merge.json` | An Inclusive split selects every true condition. Its default is used only when no condition matches, and the merge waits only for branches selected by the active split. |
| `04-complex-two-of-three-merge.json` | A Parallel fork creates three reviews, while a Complex merge activates when `TotalIncomingCount() >= 2`. Its `joinCancellation` cancels the unfinished review when the merge fires, before the coordinator finalizes. |
| `05-complex-start-reset-cycle.json` | A Complex split uses `IncomingCount(flowId)` for activation and `[gateway.waitingForStart]` to choose start-phase versus reset-phase work across cycles. |
| `06-scoped-interrupt-parallel-branch.json` | Flowbit's `scopedInterruptEvent` references a Parallel split with `gatewayRef`; triggering it cancels sibling work in the nearest active activation and continues on an emergency path. |
| [`07-nested-scope-cancellation.json`](07-nested-scope-cancellation.json) | An inner Parallel activation contains sequential MI work, message/timer waits, and a supervisor trigger. Interrupting it preserves independent outer work and continues through recovery to the outer join. |
| [`08-terminate-live-siblings.json`](08-terminate-live-siblings.json) | A controller chooses ordinary end or terminate while ordinary sibling work and a timer are live; there is no cancelling join to obscure which operation cancels work. |

## Suggested inputs and actions

### Exclusive routing

Start `01-exclusive-priority-and-default.json` with:

- `urgent=true` and any amount to select **Expedited Review**, because priority
  1 is evaluated before the amount rule.
- `urgent=false` and `amount=25000` to select **Manager Approval**.
- `urgent=false` and `amount=500` to take **Standard Processing**, the default.

### Inclusive routing

Start `03-inclusive-conditional-split-and-merge.json` with:

- both flags `true` to create Legal and Security work and make the merge wait
  for both;
- exactly one flag `true` to create and wait for only that branch;
- both flags `false` to create only the default Lightweight Review branch.

### Complex quorum merge

In `04-complex-two-of-three-merge.json`, complete any two reviewer tasks. The
Complex merge activates and creates **Finalize Quorum Decision**. The merge's
`joinCancellation` cancels the unfinished third review in that same transition.
Inspect its cancelled state before finalizing: the later terminate end is not
responsible for that cancellation.

### Complex phases

In `05-complex-start-reset-cycle.json`, take **Demonstrate reset phase** from the
first Start-Phase task. The next activation routes to Reset-Phase work. Take
**Begin the next cycle** to observe the gateway return to Start-Phase work with
an incremented cycle. Either phase also offers a finish action.

### Scoped interrupt

In `06-scoped-interrupt-parallel-branch.json`, leave **Normal Investigation**
active and take **Trigger scoped interrupt** from **Raise Emergency**. The
investigation task is cancelled and the promoted token rests at **Emergency
Response**.

### Nested scope cancellation

Publish `07-nested-scope-cancellation.json` and start as `ExampleRequester`.
`caseKey` defaults to `CASE-INNER`. Configure the seeded message client ID and
`config.exampleMessageClientSecret` as described in the
[message guide](../messages/README.md#required-configuration). Run a Worker to
exercise the timer, or start it after interrupting so the queued timer remains
available for inspection beforehand.

1. Confirm **Independent Outer Work** is active for `ExampleReviewer`. The inner
   scope also has **Sequential Inner Review** with one active assigned item
   (`inner-analyst`) and two pending items (`inner-manager`, `inner-director`),
   **Wait for Inner Message**, **Wait 15 Seconds**, and **Interrupt Inner Work**.
   Inner reviewers would need `Reviewer` as well as their assigned identity.
2. Before the inner timer fires, take **Stop only inner scope** (flow `801`) as
   `Supervisor`. The interrupt references inner split `4`, not outer split `2`.
   It cancels the inner active/pending items, message wait, timer wait, and timer
   job; **Recover Inner Work** opens. The outer task remains active and actionable.
3. Take **Recovery finished** (`1001`) as `Supervisor`. The outer join still waits
   for **Independent Outer Work**; leave that task active during inspection.
4. Inspect cancelled task/execution and job evidence. Keep the Worker running
   past the cancelled timer's due time; it must not recreate inner work. A valid
   late message to the cancelled wait (correct credentials, `X-Correlation`, a
   fresh delivery key, and `{}` body) is rejected while the instance is running.
   Finally finish outer work with flow `301` as `ExampleReviewer`.

The continuation passes through a recovery task before the outer join because
a scoped interrupt cannot directly target a Parallel merge. This definition
does not cancel the outer activation or its independent branch.

Selecting **Cancel Inner Scope** in the editor shows the advisory that its
continuation can reach Parallel merge `11`, whose interrupted siblings might
not satisfy the join. In this example the interrupt targets inner split `4`;
merge `11` belongs to the outer work from split `2`. Its two inputs remain
reachable through recovery and the surviving independent task, as the runtime
walkthrough demonstrates. The advisory does not distinguish those scopes.

### Normal end versus terminate with live siblings

Publish `08-terminate-live-siblings.json` and start two runs as
`ExampleRequester`; no inputs or external configuration are needed. Each run
opens **Ordinary Sibling Work** for `ExampleReviewer`, a 15-second timer, and
**Choose How to End** for `Supervisor`.

- In the first run, take **End controller normally** (`501`) as `Supervisor`.
  The instance remains running. The ordinary task stays actionable and the
  timer completes through the Worker. Finish ordinary work with flow `301`;
  the instance completes only once all branches have ended.
- In the second run, take **Terminate all work** (`502`) before the timer fires.
  The instance completes immediately, cancelling the ordinary task, timer wait,
  and timer job. Keep the Worker running beyond the due time and confirm that
  no sibling work reappears.

For an unhurried inspection, start these runs with the Worker stopped, make
the controller choice, then start the Worker. This changes no definition or
clock and makes the normal-run timer's completion observable. Unlike the
quorum example, this graph has no merge or `joinCancellation`; termination
itself causes the sibling cancellation.

## Gateway authoring rules illustrated here

- Gateway direction is inferred from topology. A split has exactly one incoming
  and at least two outgoing flows; a merge has at least two incoming and exactly
  one outgoing flow.
- Exclusive and Inclusive splits require exactly one unconditional default.
- Exclusive non-default flows require unique positive priorities; Inclusive
  flows do not use priorities.
- Parallel flows are unconditional.
- Complex gateways require an `activationCondition`; their non-default outgoing
  flows also require conditions.
- A scoped interrupt must be structurally reachable from the referenced
  Parallel, Inclusive, or Complex split.

## Scope-aware cancelling joins

A merge can opt into Flowbit's cancelling-join extension by referencing the
upstream split activation whose unfinished branches it owns:

```json
"joinCancellation": {
  "gatewayRef": 2
}
```

Omit `joinCancellation` to retain the gateway's existing merge behavior.
The option is valid only on an Exclusive, Parallel, Inclusive, or Complex merge,
and `gatewayRef` must name a structurally upstream Parallel, Inclusive, or
Complex split that contains every incoming path to the merge. It does not make
the merge fire earlier: Exclusive remains first-arrival, Parallel waits for all
static inputs, Inclusive applies its unpaired enabling rule, and Complex applies
its activation condition. Once the merge fires, unfinished work inside that
specific split activation is cancelled atomically; work in outer or unrelated
scopes is preserved. A missing common active activation is a `409 Conflict`, and
the transition rolls back without following the outgoing flow.
