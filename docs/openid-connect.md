# OpenID Connect authentication

[Documentation home](index.md) · [Deployment](deployment.md#authentication-boundaries) · [UI guide](ui-guide.md) · [API reference](api-guide.md#http-conventions-and-authentication)

Flowbit.Ui signs users in through an external OpenID Connect provider using the authorization-code flow. PKCE with S256 is enabled by default; an explicit compatibility option permits confidential authorization code without PKCE. Flowbit.Api validates the resulting JWT access token using the provider's discovery document and signing keys. The API does not perform browser redirects or issue tokens, and never accepts an ID token as an API credential.

The UI remains a Blazor Server application. The browser receives an HttpOnly session cookie; access and refresh tokens stay in the UI server's protected in-memory session store. API calls, including AI authoring streams, attach the current user's access token server-side. A request is sent once: authentication refresh does not replay workflow commands or AI requests.

## Modes and configuration

Set `Authentication:Mode` consistently on the UI and API. `OpenIdConnect` uses the external provider. `DevelopmentToken` enables the existing symmetric JWT test-identity workflow **only in the Development environment**. A non-Development host configured for `DevelopmentToken` fails startup. In OIDC mode `/token` is unavailable and the development signing key is not a fallback trust source.

JSON keys use colons below; environment variables replace each colon with `__`. Keep client secrets, certificate passwords, and production keys in protected deployment configuration, not source control.

| Setting | Host | Value / behavior |
| --- | --- | --- |
| `Authentication:Mode` | API, UI | `OpenIdConnect` or `DevelopmentToken`. Checked-in Development settings select OpenIdConnect with the local Keycloak realm; ordinary Compose explicitly overrides this to DevelopmentToken. |
| `Authentication:Oidc:Authority` | API, UI | Exact trusted issuer base URL used for discovery; normally HTTPS. |
| `Authentication:Oidc:ClientId` | UI | Confidential web-client registration, distinct from the API audience. |
| `Authentication:Oidc:ClientSecret` | UI | Server-held client secret for both PKCE and compatibility code flows. |
| `Authentication:Oidc:UsePkce` | UI | `true` by default; S256. Set `false` only for an explicitly configured confidential compatibility client. There is no automatic downgrade. |
| `Authentication:Oidc:Scopes` | UI | Requested scopes, including `openid`, `profile`, and the provider's API scope. Include `offline_access` when the provider uses it to issue refresh tokens. |
| `Authentication:Oidc:Audience` | API | Expected API access-token audience, for example `flowbit-api`. |
| `Authentication:Oidc:NameClaimType` | API | `flowbit_user` by default. The canonical actor contract is described below. |
| `Authentication:Oidc:RoleClaimType` | API | `roles` by default; emit a flat string/array claim rather than provider-specific nested role JSON. |
| `Authentication:Oidc:RequiredScope` | API | `flowbit.api` by default; required for user bearer access in addition to existing endpoint/task permissions. |
| `Authentication:Oidc:ScopeClaimType` | API | `scope` by default; set `scp` for a provider that uses that claim. |
| `Authentication:Oidc:AllowHttpMetadata` | API, UI | `false` by default. `true` permits a loopback HTTP authority only in Development. |
| `Authentication:Session:AbsoluteLifetime` | UI | `08:00:00` by default; absolute local sign-in limit, independent of provider token expiry. |
| `Authentication:Session:RevalidationInterval` | UI | `00:01:00` by default; existing circuits check their local session periodically. |
| `Authentication:DataProtectionKeysPath` | UI | Writable persisted key-ring directory; required outside Development. Protect directory access. |
| `Authentication:DataProtectionCertificatePath`, `Authentication:DataProtectionCertificatePassword` | UI | Certificate used to protect the persisted key ring; required outside Development on non-Windows deployments. Windows uses DPAPI when no certificate is configured. |
| `ReverseProxy:KnownProxies` | API, UI | Explicit trusted proxy IP addresses in addition to the framework's loopback defaults. Forwarded headers are processed before HTTPS and authentication; arbitrary external proxies are not trusted. |
| `AllowedHosts` | API, UI | The same semicolon-separated list filters direct and forwarded hosts. The UI requires explicit public hostnames outside Development; an empty list or wildcard `*` fails UI startup. The API retains its optional `*` default; configure its public hostname when exposed through a proxy. With the UI container's loopback health probe, use e.g. `flowbit.example.com;127.0.0.1`. |
| `WorkflowApi:BaseUrl` | UI | Trusted server-side API origin. The browser does not call the API directly. |

One issuer and one UI client are supported per deployment. The provider must supply JWT access tokens for the configured API audience and scope. Opaque-token introspection, browser/public clients, machine client-credentials acquisition, dynamic provider selection, and provider back-channel logout are outside this integration.

The implementation uses ASP.NET Core's OIDC/cookie/JWT handlers and Duende's open-source access-token manager. PKCE complements confidential-client authentication; the client secret is still required. See [Microsoft's OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0) and [Blazor token storage](https://docs.duendesoftware.com/accesstokenmanagement/blazor-server/).

## Provider registration and actor continuity

Register the UI as a confidential web application with authorization code enabled, a client secret, and exact HTTPS redirect URLs:

- Sign-in callback: `https://<ui-host>/signin-oidc`.
- Signed-out callback: `https://<ui-host>/signout-callback-oidc`.
- API resource audience: a separate identifier such as `flowbit-api`.
- Delegated API scope: `flowbit.api`, or the configured equivalent.

Issue a provider-controlled `flowbit_user` value matching the actor strings already stored in Flowbit assignments, claims, delegations, and history. Do not switch existing installations from usernames to provider-generated `sub` values inadvertently. Display names and email addresses are not an automatic identity migration. The optional database engine setting `Authentication.UserIdentityClaim` remains authoritative when configured; changing it requires restarting every API replica and reconciling existing ownership. `GET /api/auth/context` is the authoritative HTTP check for the resolved actor and roles.

Map relevant provider roles into the configured flat role claim in **access tokens**. UI display is not an authorization decision: the API still enforces workflow roles, administrative settings, inbox visibility, assignment, delegation, and claim rules. `admin` does not automatically grant workflow roles. `WorkflowContext:AllowedClaims` and `WorkflowAudit:AllowedClaims` retain their independent expression and audit meanings.

Issuer, signature, audience, lifetime, required API scope, and actor validation fail closed. An invalid credential returns `401`; an authenticated bearer missing the required scope or operation permission receives `403`. Existing routes that intentionally hide resources may still return `404`. Message, task-distributor, and shared-variable client-header authentication remain separate contracts; the API does not globally replace those routes with browser login.

## Local Keycloak walkthrough

This optional setup runs Keycloak in Docker and runs the API/UI on the host using their HTTPS launch profiles. Use the same `localhost` spelling throughout. Docker-internal names are not interchangeable with the public issuer URL.

The [API Development settings](../Flowbit/src/Flowbit.Api/appsettings.Development.json) and [UI Development settings](../Flowbit/src/Flowbit.Ui/appsettings.Development.json) already select `OpenIdConnect` and authority `http://localhost:18080/realms/flowbit`. The API expects audience `flowbit-api`, scope `flowbit.api`, actor claim `flowbit_user`, and flat `roles`. The UI uses client `flowbit-ui`, the public local secret `flowbit-ui-local-only`, S256 PKCE, and API base URL `https://localhost:7291`. HTTP provider metadata is enabled only in these Development files; production configuration must supply its own HTTPS authority and protected client secret.

Use `--launch-profile https` for both projects: the realm registers the UI callback at `https://localhost:7019/signin-oidc`, not the HTTP profile's port. Existing environment variables or user secrets take precedence over appsettings; remove stale authentication/API overrides before following these commands. Database configuration remains independent.

The separate [compose.oidc.yaml](../compose.oidc.yaml) pins Keycloak **26.8.1**, imports [the development realm](../dev/keycloak/flowbit-realm.json), and binds only `127.0.0.1:18080`. It does not replace the ordinary four-service Compose stack. The realm contains public sample credentials and disables password grants, implicit flow, public clients, and user registration. Its development database persists in the `flowbit-oidc_keycloak-data` Docker volume, and the container restarts with Docker Desktop unless explicitly stopped. This remains local development configuration, not a production identity service. The pinned version is published by [Keycloak](https://www.keycloak.org/downloads.html).

From the repository root, in PowerShell or Bash:

```text
docker compose -f compose.oidc.yaml up -d --wait
dotnet dev-certs https --trust
```

Use the isolated PostgreSQL setup from [Getting started](getting-started.md#start-an-isolated-postgresql-database), or start only the database from the ordinary Compose stack with `docker compose up -d --wait postgres`. For the latter, the commands below use its default `flowbit` database and `flowbit-local-only` password. Adjust the connection string if you used the isolated tutorial database instead. Do not run an unrelated API/Worker against this database while migrating it.

Start the API in its own terminal. These Development commands apply migrations automatically; do not substitute a production database.

PowerShell:

```powershell
$env:ConnectionStrings__Flowbit = 'Host=127.0.0.1;Port=55439;Database=flowbit;Username=flowbit;Password=flowbit-local-only'
dotnet run --project Flowbit/src/Flowbit.Api --launch-profile https
```

Bash:

```bash
export ConnectionStrings__Flowbit='Host=127.0.0.1;Port=55439;Database=flowbit;Username=flowbit;Password=flowbit-local-only'
dotnet run --project Flowbit/src/Flowbit.Api --launch-profile https
```

Start the UI in a second terminal:

PowerShell:

```powershell
dotnet run --project Flowbit/src/Flowbit.Ui --launch-profile https
```

Bash:

```bash
dotnet run --project Flowbit/src/Flowbit.Ui --launch-profile https
```

Open [Flowbit.Ui](https://localhost:7019) and sign in with one of these development users:

| Username | Password | Flowbit roles |
| --- | --- | --- |
| `alice` | `111` | `admin`, `Requester`, `Reviewer`, `WorkflowAdministrator` |
| `bob` | `111` | `Reviewer` |
| `carol` | `111` | `Requester` |
| `supervisor` | `111` | `Reviewer`, `Supervisor` |
| `finance` | `111` | `Finance` |
| `manager` | `111` | `Manager`, `RoleManager` |

The realm maps the non-self-editable username to `flowbit_user`, emits a flat `roles` claim, and puts audience `flowbit-api` and scope `flowbit.api` in the access token. All six users also have Keycloak's `offline_access` role so the sample can obtain refresh tokens; this role grants no built-in Flowbit administration permission. Alice can follow the [UI approval walkthrough](ui-guide.md#complete-your-first-approval); the actor shown will be `alice`, not the tutorial's `docs.reviewer`. Only Alice has Flowbit's default administrative `admin` role. The other roles grant the permissions authored into each workflow, not global administration: Carol can start the [roles, claim, and bypass example](../examples/user-tasks/01-roles-claim-and-bypass.json), Bob can claim its review, Supervisor can use its protected bypass, and Alice's `WorkflowAdministrator` role permits its cancellation. Finance can approve the [variable-role example](../examples/user-tasks/05-variable-roles-and-role-management.json); Manager can view that task and manage its captured roles through `RoleManager`, but initially cannot use its Finance-only approval action. Separate browser profiles or private windows demonstrate isolated sessions. The UI does not expose a raw OIDC token; external HTTP clients must obtain their own delegated access tokens through their registered authorization-code flow.

To exercise explicit code flow without PKCE, stop only the UI, change `Authentication__Oidc__ClientId` to `flowbit-ui-no-pkce`, `Authentication__Oidc__ClientSecret` to `flowbit-ui-legacy-local-only`, and `Authentication__Oidc__UsePkce` to `false` in the same shell, then start it again. The API settings are unchanged. Restore all three values to use the PKCE-enforcing client. Setting `UsePkce=false` against `flowbit-ui` must be rejected by Keycloak.

Keycloak administration is at [localhost:18080](http://localhost:18080), with `flowbit-admin` / `flowbit-keycloak-local-only`. This Keycloak administrator is separate from the Flowbit application users. Startup import skips an existing realm, so editing the JSON does not update existing users; use the admin console for an installed realm. `docker compose -f compose.oidc.yaml down` removes the container but keeps its database volume; the next `up -d --wait` retains realm settings, users, and passwords. To intentionally erase all local Keycloak data and reimport the sample, use `docker compose -f compose.oidc.yaml down -v`, then start it again. These commands do not remove the separate Flowbit PostgreSQL volume. If upgrading from the former volume-free Compose setup, export any needed realm data before recreating that old container. See [Keycloak realm import behavior](https://www.keycloak.org/server/importExport).

### Flowbit login branding

The local realm uses the `flowbit` login theme: the shared Flowbit mark and favicon,
navy brand header, blue buttons, Segoe UI/system typography, and a responsive light
sign-in card. The theme extends Keycloak 26.8.1's `keycloak.v2`; its authentication
forms, validation messages, password visibility control, and scripts remain inherited.
Only styling, English brand copy, and the decorative footer are customized. Other
languages and authentication screens retain inherited content; additional identity
flows should be exercised when enabled. The admin and account consoles retain their
existing themes.

Compose mounts [the theme](../dev/keycloak/themes/flowbit/login/theme.properties)
read-only at `/opt/keycloak/themes/flowbit`, and separately mounts the canonical
[brand assets](../Flowbit/src/Flowbit.Ui/wwwroot/branding/) at
`/opt/keycloak/themes/flowbit/login/resources/img/branding`. Both mounts are needed;
the theme does not keep a second copy of the logo. For a separate installation,
package the same assets at that relative location with the theme. See the
[brand guide](branding.md#keycloak-login) and [Keycloak theme documentation](https://www.keycloak.org/ui-customization/themes).

New imports select the theme automatically. For an **existing realm**, run
`docker compose -f compose.oidc.yaml up -d --wait` from the repository root to apply
the mounts, then in the Keycloak admin console select **flowbit → Realm settings →
Themes → Login theme → flowbit → Save**. A client-specific Login theme overrides
the realm selection; leave it unset to inherit. Sign out of Flowbit and start a
fresh sign-in at `https://localhost:7019` to see the page. Existing SSO sessions may
otherwise skip it. Selecting `keycloak.v2` in the same dropdown rolls back the
appearance without changing users, roles, clients, or tokens.

After editing theme resources, restart the local container with
`docker compose -f compose.oidc.yaml restart keycloak` and reload the login page.
These Compose commands work in PowerShell and Bash. When upgrading Keycloak, verify
the inherited markup and resource imports, keyboard focus, password toggle, error
messages, successful sign-in/sign-out, and narrow-screen layout against the new version.

## Session behavior and operations

Run **one UI instance**. Sessions and tokens live in that process; a restart signs every UI user out. A shared Data Protection key ring does not make those records distributed. API and Worker replicas remain possible with matching trusted issuer and runtime configuration. Multi-replica UI sessions require a separate distributed-store and refresh-lock implementation.

API tokens refresh before expiry when the provider has issued a usable refresh token. Each local login has independent token state and serialized refresh. Missing/expired local sessions and terminal refresh failures require sign-in. Transient provider failures surface an error; they do not switch to development credentials. Authentication problems do not automatically replay requests. Sign-in and sign-out use full HTTP navigation because cookies cannot be changed by an established Blazor circuit.

Logout invalidates the local record and clears the cookie, then attempts provider sign-out where supported. Other tabs observe invalidation on the next API token request or scheduled circuit revalidation. Local logout does not revoke already-issued self-contained API JWTs globally; a provider-side logout is not a back-channel logout notification to Flowbit. Token lifetime and provider session policies remain relevant.

Use HTTPS for the UI, API, and production authority. Set `AllowedHosts` to the public UI hostname, configure exact proxy IP allowlists and the external HTTPS callback origin, and keep session cookies Secure and HttpOnly. Do not globally force OIDC correlation/nonce cookies to `SameSite=Strict`. Mount a protected writable Data Protection key directory and, on Linux, the required PKCS#12 certificate before starting the UI. Keep the certificate password in secret configuration. Restrict access to logs and do not enable raw token, authorization-header, provider-error-body, or authentication-cookie capture.

Outside Development, the OIDC UI checks HTTPS after processing trusted forwarded headers. Plain HTTP authentication routes, OIDC callbacks, Blazor connections, and authorization-protected endpoints receive `400` when they reach this check, even if HTTPS redirection has no configured destination port. Anonymous liveness and static assets remain available for local probes. A TLS-terminating proxy must supply the correct forwarded HTTPS scheme from an allowed proxy address; `AllowedHosts` also restricts the forwarded host used for callback URLs.

The UI emits the `flowbit.ui.authentication.outcomes` counter from the `Flowbit.Ui.Authentication` meter, tagged only with fixed `operation` and `outcome` codes. The API emits `flowbit.api.authentication.outcomes` from `Flowbit.Api.Authentication`, with the fixed `outcome` values `validated` and `invalid_token`. Subscribe to these meters in your monitoring configuration. Neither counter records tokens, claims, identities, or error descriptions. The UI suppresses verbose OIDC and token-manager logging. Discovery and signing keys are managed by the framework; preserve an overlap between old and new provider signing keys during rotation and test a new-key token before retiring the old key.

`GET /health/live` on the UI is anonymous liveness only. It does not check the API, identity provider, or database. The container health check also loads `/_framework/blazor.web.js` to detect missing interactive assets; include `127.0.0.1` in the UI's `AllowedHosts` for that loopback probe. API development OpenAPI and the Worker's operational probes keep their separate documented meanings.

Before changing an existing deployment, inventory the effective actor setting and compare `GET /api/auth/context` before/after for representative users. Preserve their actor values, provision matching role/scope claims, configure provider callback URLs, and deploy API/UI settings together. There is no authentication database migration. Rollback cannot restore lost in-memory sessions; users sign in again. DevelopmentToken is not a production rollback option.

| Symptom | Check |
| --- | --- |
| Startup refuses DevelopmentToken | Use Development only for the local utility; configure OpenIdConnect for deployed hosts. |
| Issuer/discovery or callback failure | Exact authority spelling, provider availability, HTTPS trust, registered callback URL, and trusted proxy scheme. |
| `401` from the API | Token issuer/audience/signature/lifetime, access-token rather than ID-token use, and canonical actor claim. |
| `403` from the API | Required API scope first, then the endpoint's actual role/task authorization. |
| UI signs out after restart | Expected: the single-process session store was lost. |
| Existing task belongs to an unrecognized actor | Stop the rollout and reconcile actor mappings; changing to `sub` does not migrate ownership. |
| Session fails after a long idle period | UI absolute lifetime and provider refresh/session limits; sign in again. |

Use the checked-in local Keycloak scenario and authentication tests to validate provider interoperability. Other providers must satisfy the same registration, claims, token, refresh, and logout contracts; configuring an authority alone does not establish compatibility.
