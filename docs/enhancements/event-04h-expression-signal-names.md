# EVENT-04H: Expression signal names

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M |
| Priority | Not ranked; eighth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md), [EVENT-04F](event-04f-signal-catalog.md), [EVENT-01](event-01-expression-timers.md) |

## Summary

A signal catch or boundary may build its signal name from an expression over instance variables, such as a prefix followed by a customer number. The name is evaluated once, when the wait starts.

Decide after [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md) whether this item is still wanted: a business-key scope covers the most common case.

## Why it adds value

- One definition can wait for a signal that is addressed to its own data, and the thrower needs no instance ID.
- It covers values that are not the business key, such as a customer number that instances of several workflows share.

## First release acceptance criteria

- [ ] A literal `signal.name` keeps its rule and behavior. An expression name is a separate, explicit choice in the JSON and in the editor.
- [ ] The expression reads declared, stored instance variables. It is evaluated once when the subscription is created, the result must pass the literal name rule, and the subscription saves it.
- [ ] A name that cannot be evaluated, or a result that breaks the rule, fails the activation with an error that contains no values. A synchronous request rolls back, and a Worker job retries and then opens an incident, as expression timers do.
- [ ] A saved name never changes. Later variable writes do not move the subscription.
- [ ] A version change compares the authored expression text, and an open subscription keeps its evaluated name.
- [ ] What happens when two boundaries on one host evaluate to the same name is defined and tested, because saving can no longer detect it.
- [ ] The editor validates the expression with the API's messages, and instance detail and the read-only diagram show the evaluated name.

## Later releases

- Expression names on throw events ([EVENT-04B](event-04b-signal-throw-and-end-events.md)).
- A condition on the payload in place of a name built from data.

## Current state

Verified on 2026-11-17: not implemented.

- `signal.name` is literal text. It is not a template and is not trimmed, and [SignalEventRules.cs](../../Flowbit/src/Flowbit.Shared/Models/SignalEventRules.cs) rejects anything outside lowercase letters, digits, `.`, `_`, and `-` ([signal configuration](../node-reference.md#signal-configuration)).
- A subscription stores its `SignalName`, so delivery would not need to evaluate anything.
- `SignalEventRules.BoundaryNameProblems` rejects a repeated name on one host when a definition is saved.
- The subscription summary returns at most 200 rows of name and workflow family. Names that contain data would fill it quickly.

## Builds on

- Expression timers ([EVENT-01](event-01-expression-timers.md)): the classification of a fixed value and an expression, analysis when a definition is saved, one evaluation per activation, and value-free failures.
- The subscription row, which already holds the name a broadcast is matched against.

## Open questions

- Is the item still needed once a throw can name a business key?
- Values such as order numbers often contain uppercase letters, which the name rule rejects. Does the expression have to lowercase them, or does an evaluated name get a wider rule?
- How do names built from data fit a signal catalog ([EVENT-04F](event-04f-signal-catalog.md)), which lists fixed names? EVENT-04F checks only literal names and leaves entries for built names, such as a prefix entry, to a [later release](event-04f-signal-catalog.md#later-releases).

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
