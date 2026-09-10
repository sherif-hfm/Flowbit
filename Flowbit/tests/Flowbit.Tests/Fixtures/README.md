# Test fixtures

The JSON files in this directory are inputs owned by the regression tests.
The test project copies them into `Fixtures/` beside the test assembly. Run
tests normally with `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj`;
no generated fixture directory or custom MSBuild target is required.

Six historical workflow snapshots preserve the node IDs, flow IDs, variables,
and legacy formats exercised by the existing validation, editor, and API tests.
They are byte-for-byte copies of the former repository-root files at commit
`70c6c6f26ac39e7a788f56492c3f3aaa150a853a`, immediately before those samples were
removed in `e7d6c06`. Keep them here as test data; the maintained public examples
and walkthroughs belong in the [example catalog](../../../../examples/README.md).

| Snapshot | Original Git blob |
| --- | --- |
| `workflow-message-start.json` | `11283999f071353a4c104702906d13b37b9e9c8d` |
| `parallel-gateway-simple.json` | `413fbb91a97d18cdc858651408156fc3ee8c45c2` |
| `parallel-gateway-complex.json` | `d6ba405a6748304541292c6164d1479e7aa23493` |
| `votes-users-list.json` | `123df74197e13eaaf7ad307312e686c62aa978b3` |
| `votes-sequence-users-list.json` | `aa5c05d7a1ef78b5ce97373aed3d271c36a0c269` |
| `votes-cardinality-approve-reject.json` | `eabce8d7d7a3a88a6b6d261e4616447b797dcb52` |

`inbox-visibility-conformance.json` contains shared inbox-predicate conformance
cases. The project also copies the current editor and public examples from
their maintained locations for tests that intentionally follow current behavior.
