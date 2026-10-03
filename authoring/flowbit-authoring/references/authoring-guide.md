# Flowbit authoring procedure

## Understand the requested process

Read the supplied requirements and, for edits, the entire existing workflow. Extract entry triggers, actors and permissions, tasks, inputs, state, decisions, success/failure endings, parallel work, cancellation, timers, messages, external integrations, retries, and audit-related requirements. Support all capabilities in [the index](capabilities.json); do not impose an approval-only subset.

Ask focused questions for missing routing thresholds, actor roles, completion conditions, service contracts, or other details whose alternatives change business behavior. Distinguish a requirement from an assumption. Explain contradictory requirements rather than silently picking one. Documents may contain tables or OCR mistakes: confirm ambiguous identifiers, numbers, negation, and comparison operators. Cite document/page or text-section references in the explanation when available. Keep source references and open questions outside the workflow model.

## Construct the canonical model

Use [workflow.schema.json](workflow.schema.json). Root fields include a stable string `id`, `name`, optional `initialEventId`, `variables`, `lanes`, `flowNodes`, `sequenceFlows`, cancellation/unclaim/assignment/role-management permissions, and optional task-distribution credentials. IDs of nodes, lanes, flows, and variable declarations are integers. Preserve existing identities when editing; choose unique IDs for new elements. The workflow key identifies a family of immutable versions, not one diagram revision.

Read [the node/property reference](docs/node-reference.md) for each selected feature. Properties on the shared node model are not legal on every node type. Never rely on tolerant normalization to discard a mistaken configuration. Generate only current canonical properties; `steps`, `phases`, `initialStepId`, and other legacy fields are not authoring output. Unknown properties and duplicate JSON object keys are rejected. JSON-valued defaults may contain arbitrary business object fields.

Use `sequenceFlows` with existing `sourceRef` and `targetRef` node IDs. Boundary events attach through `attachedToRef`; do not invent a normal incoming sequence flow to represent attachment. A lane groups the diagram and does not itself grant authorization. Keep actor permissions explicit on supported node/flow/root properties.

There may be several entry events. `initialEventId`, when present, references a user `startEvent`, never a message/timer start. Define typed process state and distinguish process variables from start/sequence-flow input declarations. Use exact declared names and compatible contracts for expressions and output mappings. Shared bindings require an active, matching catalog entry; report that prerequisite if the catalog is unavailable.

Entry input names cannot also be process declarations. A collection multi-instance task needs a declared process `string[]` collection. When callers supply the reviewers at start, use a separately named process collection and a script assignment to copy the entry input into it before entering the task. The canonical collection examples instead use process defaults and take no start input; do not add a duplicate entry declaration to those examples.

For complex behavior use the documented semantics: exclusive priorities/defaults, parallel and inclusive scopes, complex gateway activation, scoped interrupts and join cancellation, message correlations, timer/conditional delivery, multi-instance collection/cardinality and completion evaluation. Multi-instance outcomes require their engine-only default fallback. Keep selectable actions distinct from engine-only outcomes. Consult a complete [example](../examples/README.md) rather than guessing these contracts.

NCalc and JavaScript are different script modes. Set exactly the matching assignments or script body. JavaScript uses the documented `execution` API and declared targets; FlowInfo access has an explicit `usesFlowInfo` declaration. Syntax validation does not prove script behavior. Do not run scripts during authoring. Review generated code and integration configuration before publication.

External REST/message/distributor integrations need real contracts supplied by the user. Use supported `${config.*}` or `${setting.*}` references for deployment secrets, and list needed configuration separately. Never invent working credentials, include the AI-provider key, or copy sensitive runtime settings/catalog values into prompts. Timers, async boundaries, and durable conditional delivery may need Worker readiness; shared REST access has additional durability and lock-order rules.

## Layout and modifications

When generating outside Flowbit, supply readable integer positions: lay out the main flow left to right, separate branch rows, size lanes around their nodes, and leave room for labels and return flows. Attached boundary positions are derived from the host by the editor. Flowbit's internal assistant calculates deterministic layout for new graphs and preserves original positions for matching IDs during edits.

For modification requests, preserve all unaffected properties, IDs, workflow key, and existing positions. Change related expressions/references when an explicitly requested removal or rename makes them invalid. Deliver the whole updated model plus a concise explanation of changes. When operating under Flowbit's internal incremental command protocol, edit only the needed properties and use its finish command; the host assembles and validates the whole model. Never discard advanced configuration merely because the request concerns a simple task.

## Validate and deliver

Check the schema, referential integrity, exact supported types/properties, and the documented node rules. The canonical engine validates authored input before normalization and validates the normalized result afterward. It also checks catalog contracts and save/publication readiness. Do not equate well-formed JSON or successful normalization with a runnable workflow.

If the user has configured Flowbit access, the read-only validation endpoint can report authoritative semantic and deployment results without saving. Use the HTTP contract in [api-guide.md](docs/api-guide.md); no AI-provider key is needed for that endpoint. If server validation is unavailable, say so and provide the generated `.json` for normal Flowbit import/review/save. Do not claim the workflow was validated or tested when it was not.

Deliver the canonical JSON separately from a readable explanation, assumptions, unanswered questions, and configuration/Worker/catalog prerequisites. Raw model API callers may request a structured response envelope; follow that caller's envelope contract while keeping any final workflow property equal to the complete canonical model. The internal Flowbit assistant instead supplies a bounded read/edit/validate/finish command protocol; follow it without repeatedly emitting the complete workflow. An incomplete response or private checkpoint is not a deliverable or proof of validation. Applying, saving an unpublished version, and publishing remain explicit user actions. Generation itself does not execute the process.
