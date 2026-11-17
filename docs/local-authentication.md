# Local users and shared-key authentication

Set `Authentication:Mode=Local` on both the API and UI to use Flowbit's PostgreSQL-backed user directory. This mode is supported in Development, Staging and Production. A deployment selects Local or [OpenID Connect](openid-connect.md); it does not combine their issuers. The standalone editor needs neither mode.

## Identity and trust

Locally generated HS256 JWTs contain the exact, case-sensitive claim name **`Username`**, set to the immutable login username. `sub` contains the same value. `given_name` and `family_name` hold first and last names; the configured role claim defaults to `roles`. Explicit user claims and mapped attributes are also included.

```json
{"sub":"alice","Username":"alice","given_name":"Alice","family_name":"Example","roles":["Reviewer"],"depId":"Finance"}
```

Local bearer validation checks the HS256 signature, issuer, audience, lifetime and actor identity. It does **not** look up the actor in the user directory. Other trusted systems with the signing key can issue tokens for external-only users or for inactive local usernames; their token roles and claims remain authoritative. No delegated scope is required in Local mode. Existing workflow permissions still apply. Possession of the shared key grants token-issuance authority, including authority to assert administrator roles.

All modes default to `Username`, then `ClaimTypes.Name` (`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name`), then `sub`/mapped `ClaimTypes.NameIdentifier`. Only absent claims fall through; invalid present claims fail authentication. The database engine setting `Authentication.UserIdentityClaim` takes precedence and is read at API startup. Leave it unset for the fallback chain; setting it to `Username` requires that claim strictly. Generated local tokens additionally emit the username under a configured custom identity claim. Preserve existing actor strings when switching modes: assignments, claims and history are not automatically migrated. [API identity rules](developer-guide.md#authenticate-the-actual-actor) and `GET /api/auth/context` describe/check the resulting actor.

## Configuration and first administrator

Apply the additive `AddLocalIdentity` migration before enabling Local mode on deployed hosts. Development startup applies migrations automatically. All new tables live in `flowbit`: `local_users`, `user_attribute_definitions`, and `local_refresh_sessions`. The migration seeds `Users.RequiredRole=admin` only when missing.

The [built-in engine settings reference](engine-settings.md) owns the full rules for [`Users.RequiredRole`](engine-settings.md#usersrequiredrole) and [`Authentication.UserIdentityClaim`](engine-settings.md#authenticationuseridentityclaim), including role replacement, defaults, startup restrictions, and restart requirements. The table below describes host configuration, not engine-setting rows.

| Setting | Host | Default / requirement |
|---|---|---|
| `Authentication:Mode` | API, UI | Set to `Local`. |
| `Authentication:Local:Issuer` | API | `flowbit-local`; external issuers must match. |
| `Authentication:Local:Audience` | API | `flowbit-api`. |
| `Authentication:Local:SigningKey` | API only | Required; at least 32 UTF-8 bytes; use a strong secret. No default. |
| `Authentication:Local:RoleClaimType` | API | `roles`. |
| `Authentication:Local:AccessTokenLifetime` | API | `00:15:00`; permitted range 1–60 minutes. |
| `Authentication:Local:SessionLifetime` | API | `08:00:00`; at least the access lifetime and at most 24 hours. |
| `Authentication:Local:MaxFailedAttempts` | API | `5`; allowed range 1–100. |
| `Authentication:Local:LockoutDuration` | API | `00:05:00`; allowed range 1 second–24 hours. |
| `Authentication:Local:BootstrapUsername` | API | Required when the directory is empty. |
| `Authentication:Local:BootstrapPassword` | API | Required when empty; no default password. |
| `Authentication:Session:*` | UI | Eight-hour absolute lifetime; revalidation defaults to one minute and must be 1–60 seconds. |
| `Authentication:AllowDevelopmentHttp` | UI | Defaults to `false`. `true` is accepted only in Local mode in Development and permits sign-in over plain HTTP. |
| `Authentication:DataProtection*` | UI | Same persisted/protected key-ring requirements as [OIDC sessions](openid-connect.md#session-behavior-and-operations). |
| `WorkflowApi:BaseUrl`, `AllowedHosts`, `ReverseProxy:KnownProxies` | UI | Configure the trusted API origin, public hostname and proxies as in the deployment guide. |

Only the API receives the signing key, database connection and bootstrap password. The UI receives tokens through server-to-server HTTP. HTTPS is required for production account operations and secure UI cookies. Configure the API's trusted proxy settings when terminating TLS upstream.

Passwords are required, with no minimum/maximum length or character-composition policy. This applies to bootstrap, user creation, administrator resets and password changes; login accepts the complete password without truncation. Standard HTTP request/form size limits still apply. The former `Authentication:Local:MinimumPasswordLength` setting is ignored and can be removed from existing configuration. The signing key's 32-byte minimum is separate and remains required.

Bootstrap creates one active `Flowbit Administrator` account with the requested username and the current management role(s), plus `admin`. It runs under a PostgreSQL transaction lock, does nothing once any account exists, and never overwrites passwords. Remove bootstrap credentials from deployment configuration after initialization. Change the initial password through the UI. There is no anonymous setup page or recovery endpoint.

For host-based Development testing, start API and UI in separate shells. Use your own generated secret and initial password. The existing database and HTTPS development certificate prerequisites are in [getting started](getting-started.md).

PowerShell API shell:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Authentication__Mode = 'Local'
$env:Authentication__Local__SigningKey = '<your-random-secret-at-least-32-bytes>'
$env:Authentication__Local__BootstrapUsername = 'admin'
$env:Authentication__Local__BootstrapPassword = '<your-initial-password>'
dotnet run --project Flowbit/src/Flowbit.Api --launch-profile https
```

PowerShell UI shell:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Authentication__Mode = 'Local'
$env:WorkflowApi__BaseUrl = 'https://localhost:7291'
dotnet run --project Flowbit/src/Flowbit.Ui --launch-profile https
```

Bash API shell:

```bash
export DOTNET_ENVIRONMENT=Development
export Authentication__Mode=Local
export Authentication__Local__SigningKey='<your-random-secret-at-least-32-bytes>'
export Authentication__Local__BootstrapUsername=admin
export Authentication__Local__BootstrapPassword='<your-initial-password>'
dotnet run --project Flowbit/src/Flowbit.Api --launch-profile https
```

Bash UI shell:

```bash
export DOTNET_ENVIRONMENT=Development
export Authentication__Mode=Local
export WorkflowApi__BaseUrl=https://localhost:7291
dotnet run --project Flowbit/src/Flowbit.Ui --launch-profile https
```

Open `https://localhost:7019/auth/login`. The ordinary Compose stack continues to select DevelopmentToken explicitly; the new mode does not change its defaults.

## Docker Compose with password login

Merge [compose.local.yaml](../compose.local.yaml) with the base [Compose stack](deployment.md#local-docker-compose-stack) to run PostgreSQL, API, UI and Worker with built-in username/password login. Keycloak is not required. This is a **local Development setup**: the browser uses HTTPS and secure cookies, while the UI calls the API over the private Docker network. The API's host port remains bound to loopback. Production still requires the deployment, HTTPS, secret and Data Protection configuration above.

The override reuses the same `flowbit` Compose project and PostgreSQL volume. Existing workflows and accounts remain intact. Switching authentication does not migrate actor identities; preserve existing usernames when assigning roles and work. An empty directory gets the bootstrap administrator; an existing directory keeps its users and passwords.

Copy [.env.example](../.env.example) to `.env` if it does not already exist; preserve any existing database/port overrides. Uncomment and fill in its Local-login block:

```dotenv
COMPOSE_PATH_SEPARATOR=,
COMPOSE_FILE=compose.yaml,compose.local.yaml
FLOWBIT_LOCAL_SIGNING_KEY='<your-random-secret-at-least-32-bytes>'
FLOWBIT_LOCAL_BOOTSTRAP_USERNAME=admin
FLOWBIT_LOCAL_BOOTSTRAP_PASSWORD='<your-initial-password>'
FLOWBIT_HTTPS_CERT_PASSWORD='<your-exported-certificate-password>'
```

Use independent random signing-key, administrator-password and certificate-password values. Only the API receives the Local signing key and bootstrap password. `.env` and `*.pfx` files are ignored by Git and excluded from image builds. `COMPOSE_FILE` makes subsequent ordinary `docker compose` commands select both files; the explicit comma separator works on Windows and Unix. Without this setting, select them with `docker compose -f compose.yaml -f compose.local.yaml`.

Export a trusted localhost development certificate using the .NET SDK on the host. The export password must match `FLOWBIT_HTTPS_CERT_PASSWORD` in `.env`; use `localhost` in the browser to match the certificate. If using `FLOWBIT_HTTPS_CERT_DIR`, substitute that directory below. Do not overwrite an existing certificate without checking which local services use it.

PowerShell:

```powershell
New-Item -ItemType Directory -Force dev/https | Out-Null
dotnet dev-certs https --trust
dotnet dev-certs https --export-path dev/https/flowbit-local.pfx --password '<your-exported-certificate-password>'
```

Bash:

```bash
mkdir -p dev/https
dotnet dev-certs https --trust
dotnet dev-certs https --export-path dev/https/flowbit-local.pfx --password '<your-exported-certificate-password>'
```

Make the certificate readable by the UI container's non-root `app` user. Certificate trust support depends on the host OS/browser; complete local trust setup before signing in. With `.env` configured, these commands work in both shells:

```text
docker compose config --quiet
docker compose stop api worker ui
docker compose up --build -d --wait
docker compose ps
```

Open [Flowbit sign-in](https://localhost:15153/auth/login) and use the bootstrap username/password, or an existing local account. `FLOWBIT_UI_HTTPS_PORT` changes the HTTPS port (default `15153`); the old HTTP UI port redirects to HTTPS. The **Users** page manages accounts after login. The test-identity screen is unavailable in this mode. Restarting the UI ends its browser sessions.

The override probes liveness and the Blazor script directly over container-loopback HTTPS, so an HTTP redirect cannot masquerade as a successful asset check. Only those anonymous development probes skip certificate trust validation; browser certificate validation and secure session cookies remain enabled.

The development UI writes its Data Protection keys to its writable container home and encrypts them with the mounted certificate. This overrides host-specific paths in appsettings. The keys are container-local and recreation discards them; the PostgreSQL user directory persists. Use a persisted protected key ring for deployed hosts.

After the first successful login, change the initial password, remove `FLOWBIT_LOCAL_BOOTSTRAP_PASSWORD` from `.env`, and run `docker compose up -d --wait` to remove it from the API container. Bootstrap never resets an existing account. To return to the development-token tutorial, remove `COMPOSE_FILE` and `COMPOSE_PATH_SEPARATOR` from `.env`, then stop API/Worker/UI and run `docker compose up --build -d --wait`; the database is retained. Do not use `down --volumes` when switching modes.

### Password login over HTTP on a local network

Use [compose.local-http.yaml](../compose.local-http.yaml) for plain HTTP on a trusted development network. It publishes only the UI on all host IPv4 addresses (`0.0.0.0`), with port `15152` by default. The API, PostgreSQL and Worker ports bind to loopback. This override uses the same accounts, signing key and database as the HTTPS override. HTTP carries passwords, session cookies and workflow data without encryption. Browser features that require a secure context, such as asynchronous clipboard access, may be unavailable on the network address.

In `.env`, use the Local signing key and account settings above, with these Compose settings. This override needs Docker Compose 2.24.4 or later and no HTTPS certificate.

```dotenv
COMPOSE_PATH_SEPARATOR=,
COMPOSE_FILE=compose.yaml,compose.local-http.yaml
```

From the repository root in PowerShell or Bash, run:

```text
docker compose config --quiet
docker compose up --build -d --wait
docker compose port ui 8080
```

The port command should report `0.0.0.0:15152` (or the value of `FLOWBIT_UI_PORT`). Open `http://localhost:15152` on the host. From another PC, use a reachable IPv4 address of the Docker host, for example `http://192.168.1.20:15152`. If a host firewall blocks access, allow inbound TCP on the UI port from the intended subnet. On Windows, add a rule from an elevated PowerShell when an existing Docker Desktop rule does not already allow the traffic:

```powershell
New-NetFirewallRule -DisplayName 'Flowbit UI (local subnet)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 15152 -RemoteAddress LocalSubnet
```

On Linux, scope access through the host's Docker-aware firewall configuration. Keep this port off the public internet. The UI disables HTTPS redirection and uses a separate HttpOnly session cookie that can be sent over HTTP. Set `COMPOSE_FILE=compose.yaml,compose.local.yaml` again to restore the localhost HTTPS setup, then run `docker compose up -d --wait` with the exported certificate settings.

## Manage users and fields

The **Users** and **User attributes** navigation entries are available in Local mode. Their APIs require a role matching the comma-separated dynamic setting `Users.RequiredRole` (missing/blank defaults to `admin`). External signed administrator tokens can also manage this directory.

Create users with username, first/last names, password, active status, roles, claims and custom attributes. Usernames are trimmed, case-insensitively unique, immutable and limited to 300 UTF-16 code units, matching the existing workflow actor contract. Login returns the stored spelling. First/last names and role/claim names are bounded to 300 Unicode scalars; roles are trimmed and deduplicated case-insensitively. Each account permits 100 roles and 32 explicit string-valued claims; claim values have a 1,000-character limit. Repeated claim names are supported. Read DTOs never include passwords or hashes.

Edit a profile, activate/deactivate it, or reset its password through the Users screen. Status/password operations are separate from profile saves. Changes require the current revision and stale requests return `409`; reload before editing again. Removing the last active directory user with a current user-management role is rejected. When changing `Users.RequiredRole`, provision the replacement role before changing the setting.

Define up to 32 attributes with an immutable key/type, label, required flag and optional claim mapping. Keys use letters, digits, underscores or hyphens, up to 100 characters. Types are `text` (up to 1,000 characters), `number` (decimal), `boolean`, `date` (`yyyy-MM-dd`), and `dropdown` (1–100 choices). Optional empty values may be omitted. Zero and false satisfy required validation. Required fields are enforced on every create/profile edit; older profiles missing a newly required field can still log in. A dropdown choice cannot be removed while an existing profile uses it.

Mapped values become string JWT claims; Boolean values use `true`/`false`, dates use ISO dates, and numbers use JSON numeric formatting. Unmapped values remain profile data. Reserved JWT/identity/role claims, including `Username`, cannot be authored as custom claims or mapping targets. A mapping cannot collide with an explicit claim or another mapping. Generated access tokens have a 24 KB size limit; profile and mapping updates that would exceed it are rejected.

## HTTP contracts

All routes below are Local-mode operations; authenticated requests in other modes return `404`. Normal authorization can return `401` before mode filtering. Account operations outside Development require HTTPS.

| Method and route | Input | Success |
|---|---|---|
| `POST /api/auth/token` | `{ "username": "alice", "password": "..." }` | `200` token pair. |
| `POST /api/auth/refresh` | `{ "refreshToken": "..." }` | `200` rotated token pair. |
| `POST /api/auth/logout` | `{ "refreshToken": "..." }` | `204`; revokes that refresh family. |
| `GET /api/auth/session` | Locally issued bearer with session `sid`. | `200` `{ "user": "alice", "roles": [...] }` if its session/user is active. |
| `POST /api/auth/change-password` | Local bearer plus `{ "currentPassword": "...", "newPassword": "..." }`. | `204`; revokes all local sessions. |
| `GET /api/users` | `search`, `active`, `page`, `pageSize`. | `200` standard `PagedResult<LocalUserDto>`. Page defaults 1; size defaults 50, bounded 1–200; username/id ordering. |
| `POST /api/users` | `username`, `firstName`, `lastName`, `password`, `isActive`, `roles`, `claims`, `attributes`. | `201` profile with Location. |
| `GET /api/users/{id}` | User id. | `200` profile. |
| `PUT /api/users/{id}` | `firstName`, `lastName`, `roles`, `claims`, `attributes`, `expectedRevision`. | `200` updated profile. |
| `POST /api/users/{id}/status` | `isActive`, `expectedRevision`. | `200` updated profile. |
| `POST /api/users/{id}/password` | `newPassword`, `expectedRevision`. | `204`; reload the profile for its new revision. |
| `GET /api/user-attributes` | None. | `200` definition array. |
| `POST /api/user-attributes` | `key`, `type`, `label`, `required`, `choices`, `claimType`. | `201` definition with Location. |
| `PUT /api/user-attributes/{id}` | Same fields plus `expectedRevision`. | `200` updated definition. |

Claims use `{ "type": "team", "value": "one" }` entries. Attributes are a JSON object keyed by field key, containing typed JSON values. Profiles return `id`, `username`, names, `isActive`, roles, claims, attributes, `revision`, `createdAt` and `updatedAt`. Definitions return `id`, their editable configuration and `revision`.

Token responses contain `accessToken`, `tokenType` (`Bearer`), `expiresAt`, `refreshToken`, `refreshExpiresAt`, and `actor` (`user`, `roles`). Token-bearing responses are not cacheable. Send access credentials using `Authorization: Bearer <accessToken>`. Refresh credentials are not bearer tokens. The token/refresh/logout routes do not require a bearer; refresh/logout authenticate the supplied opaque refresh credential.

Errors are generally `{ "code": "...", "error": "..." }`: invalid configuration `400`, invalid credentials/session `401`, missing management permission `403`, missing resource/wrong mode `404`, duplicate username/attribute or stale revision `409`, and throttling `429`. A body that cannot be bound returns `{ "error": "..." }` without a code, and a framework authentication failure can have an empty body. Login, refresh and logout share a per-IP limit of 30 requests/minute per API process; database lockout is shared across replicas. Status and password changes do not automatically replay failed workflow commands.

## Sessions and operational limits

Local login creates an independent UI session for that browser. Cookies are HttpOnly and require HTTPS by default. The [development HTTP option](#password-login-over-http-on-a-local-network) allows the session cookie on HTTP. Access and refresh tokens remain protected in server memory. Login/logout forms use antiforgery tokens. One UI process is supported, as with OIDC; restarting it signs browsers out. PostgreSQL refresh state survives API restarts. Copy your own current access token through `/_info`; the response never includes refresh credentials and retains private/no-store headers. Administrators cannot mint tokens as another user through a management endpoint.

Refresh tokens rotate atomically and retain the original absolute deadline. Reusing an already consumed token revokes its entire refresh family; independent logins remain valid. Password changes/resets and deactivation revoke every local refresh session for that user. Revalidation ends idle UI sessions within the configured interval; outgoing UI API calls also check session state. Existing access tokens remain usable until expiry. Role/claim changes appear in newly issued tokens; existing tokens retain their previous contents.

Directory mutations and refresh rotation use a PostgreSQL transaction-scoped advisory lock, so bootstrap, field changes, lockout and last-administrator checks remain consistent across replicas. This lock is independent of workflow execution. Expired refresh rows are deleted during subsequent committed local account operations. Passwords use ASP.NET Core Identity hashing; no plaintext passwords are stored. Shared keys, passwords and token bodies must not be added to request/response logging.

No public registration, email recovery, MFA, username renaming, user deletion, role catalog, production pasted-token UI login, or automatic identity migration is provided. OIDC is still available for deployments requiring provider-managed identity.
