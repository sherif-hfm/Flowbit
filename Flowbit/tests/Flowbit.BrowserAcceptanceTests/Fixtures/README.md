# Durable browser acceptance fixtures

`instance-detail-audit.json` is the Stage 5 instance-detail fixture. Each scenario
replaces its workflow key and shared catalog key with run-local values. Setup
creates the catalog through the authenticated shared-variable HTTP API before
publishing the definition. Its shared string value is a sentinel that must not
appear in the value-free instance binding table.

The audit scenario publishes two versions with one stable workflow key and
unchanged waiting-task contracts. A real Worker prepares and executes an upgrade,
downgrade and two instance-variable updates. Preparation and confirmation use
different administrators. Browser assertions cover chronological ordering,
captured actors/roles, JSON values, catalog metadata, section navigation and
the actual completed-batch destinations.

The attribution scenario claims the Reviewer task as `acceptance-owner`, creates
a workflow-scoped grant through HTTP, then completes the task as
`acceptance-delegate` through its real task screen. API and Worker configuration
must allowlist `department` and `region` in `WorkflowAudit:AllowedClaims`. Those
claims are entered through the token screen, including HTML-looking strings;
`notSelected` must remain absent from recorded claims. Test identity drafts are
cleared between actors.

Representative-state coverage reuses the smoke project's `runtime-navigation`,
`runtime-gateway` and `runtime-mi` JSON files copied into the test output. One
run-local MI variant changes only the completion condition to an early quorum,
so its final snapshot contains one completed child and two cancelled children.
Every Stage 5 scenario runs at 1440×900, 1024×768 and 390×844. Screenshots and
`scenario-evidence.json` identify real HTTP-created entities; screenshots require
visual review and are not pixel-comparison baselines.

No setup writes application tables or fabricates completed DTOs. The disposable
database and all API/UI/Worker hosts belong to the acceptance fixture.
