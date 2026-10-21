# homebase-invites

Invitation codes for Homebase signups: a minimal API that issues codes and counts their uses.

The identity hosts (odin-core) call it at signup to check a code and take a use; who signed up with a code
is kept on the new identity's registration, not here. Admins create and list codes with curl.

## Run it

```bash
dotnet run --project src/Odin.InvitationCodes.Hosting     # http://localhost:5246, Development config
dotnet test

./build-docker-image.sh && ./run-attached.sh              # the image, on 127.0.0.1:5246, database in ./data
```

## Command line

`./invites.sh` wraps the API. `./invites.sh setup` saves both keys to `~/.homebase-invites` (outside the
repo, readable only by you); then `./invites.sh create --uses 5 --source "spring campaign" --plan pro-100gb --owner frodo.demo.rocks`,
`./invites.sh list` (a table; `--json` for the service's answer), `stats`, `edit`, `disable`, `enable`, `validate`, `use`, `undo`, and for plans `grant --id frodo.demo.rocks --plan veterans`, `grants`, `retry`. `./invites.sh help` explains each command and its arguments.

## Tests

`dotnet test` runs the store tests, every API endpoint over HTTP in process, and `invites.sh` with bash against
the service on a real port. The script tests need `jq`: without it they are skipped locally and fail under CI.
`.github/workflows/test.yml` builds and runs them on every pull request and push to main.

## Deploy

`.github/workflows/build-and-deploy.yml` (run by hand) builds, tests and pushes
`ghcr.io/homebase-id/homebase-invites-internal:<branch>`, then asks the ansible controller to deploy it to
blackbird-aux, behind Caddy at https://invites.ravenhosting.cloud, the same way homebase-social-sync deploys. The controller only deploys what the DevOps repo
allowlists, so that has to be set up first.

The container listens on 8080 and keeps its database in `/data`: mount a volume there.

## A code

| Field | |
|---|---|
| `code` | 12 Crockford base32 characters, shown as `K7QX-M2PD-9FHW`; input is case-insensitive and ignores dashes and spaces |
| `owner` | the identity the code belongs to; optional |
| `source` | free text: where the code came from |
| `enablePublicWebPresence` | **deprecated**: whether an identity that signs up with it gets a public home page, on identity hosts from before odin-core #1974. Later hosts ignore it and take it from the identity's plan. Optional, on by default; still stored and sent for those older hosts |
| `planId` | the hosting plan, and so the quota, an identity that signs up with it gets; required when a code is made, and can be changed but not removed. Letters, digits, `.`, `_` and `-`, up to 64. `null` only on codes made before it was required, which leave the plan to the identity host |
| `maxUses`, `usesLeft` | how many signups it allows, and how many are left |
| `disabled` | a disabled code cannot be used, whatever uses it has left |

## API (`/api/v1/invitation-codes`, key in the `Odin-Api-Key` header)

| Call | Key | Answer |
|---|---|---|
| `POST /validate {code}` | signup | `{valid}`: exists and has a use left |
| `POST /use {code}` | signup | `{used, code}`: takes a use, atomically, and returns the code |
| `POST /undo-use {code}` | signup | 204: gives the latest use back |
| `POST / {owner?, maxUses, source, planId, enablePublicWebPresence?}` | create | the new code |
| `GET /?owner=<odinId>` | create | the codes, all of them without `owner` |
| `GET /stats?sinceDays=<n>` | create | codes and signups in total, by source, by owner, by plan (`name: null` for old codes without one) and by week (Monday, UTC). `sinceDays` limits only the signups counted; the code counts (`neverUsed`, `usedUp`, `disabled`, codes, uses allowed, `usesLeft`) are always about every code as it is now. `usesLeft` on each line is the signups its codes still allow, disabled codes counting none: watch `byPlan[].usesLeft` to see when a plan needs more codes |
| `PATCH /<code> {maxUses?, source?, owner?, enablePublicWebPresence?, planId?, disabled?}` | create | the code as it is now; 404 if there is none. Fields left out stay as they are; `"owner": ""` removes the owner |

Bad input (owner not a domain, a missing or bad `planId`, `maxUses` outside 1..10000) is a 400. Creating a code:

```bash
curl -X POST https://<service>/api/v1/invitation-codes/ \
  -H 'Odin-Api-Key: <create key>' -H 'Content-Type: application/json' \
  -d '{"owner":"frodo.demo.rocks","maxUses":5,"source":"spring campaign","planId":"pro-100gb"}'
```

## Plan grants

The service also gives identities plans: a **grant** says identity X is owed plan P, and the service sets it on
the identity host that serves X. Admins make grants now (`./invites.sh grant`); the payment flow will make
`purchase` grants the same way, once payment is taken. A plan is set only through a grant, never by a link.

### Finding the host

The service knows the identity hosts by their provisioning domains (`PlanService__Hosts`), and calls only those,
with the one plan key they all take (`PlanService__ApiKey`; odin-core's `Registry__PlanService__ApiKey`). To find
the host that serves X it asks, in order: the host the request named (`host`, only if it is a known one; owner
consoles send it as `?host=` in the upgrade link), the host that last had X, then every other known host. The
first to answer `200` has it; `404` means not there. The host API (odin-core, on the provisioning domain):

| Call | Answer |
|---|---|
| `GET /api/plans/v1/identities/<identity>` | `{domain, planId}`; 404 if the host does not serve it |
| `PUT /api/plans/v1/identities/<identity>/plan {planId, reference}` | `{domain, planId}`; 400 for a plan the host does not have; 404 as above. Setting the plan it already has changes nothing |

### Statuses

| Status | |
|---|---|
| `pending` | not set yet: no host has the identity, a host is down, or it answered something else. Tried again after 1, 2, 4... minutes, at most every 6 hours, for as long as it takes: a paid plan is never given up on |
| `applied` | the host has it; `host` says which, `appliedAt` when |
| `failed` | the host refused the plan (`400`: not one of its plans). Not retried by itself; fix the plan on the host, then `retry` |

A background task tries due grants every minute (`PlanService__RetryIntervalSeconds`) and, once a day
(`PlanService__ReconcileIntervalHours`), reconciles: for each identity it asks the host for the plan it has, and
if that is not the plan of its latest applied grant, sets it back. So a plan changed by hand on a host with
`odin-admin tenant set-plan` is undone the next day for an identity that has a grant; give such an identity a
new grant instead.

### API (`/api/v1/plans/grants`, create key)

| Call | Answer |
|---|---|
| `POST / {identity, planId, source?, reference?, host?}` | the grant, after trying to set it straight away. `source` is `code`, `purchase` or `admin` (the default); `reference` is the caller's id for it, e.g. a payment id, made up when left out. The same `reference` again is the same grant (not set twice); for another identity or plan it is a 409 |
| `GET /?identity=<odinId>&status=<status>` | the grants, newest first; both filters optional |
| `POST /<id>/retry` | tries a pending or failed grant again now; 404 if there is none, 409 if it is already applied |

```bash
curl -X POST https://<service>/api/v1/plans/grants/ \
  -H 'Odin-Api-Key: <create key>' -H 'Content-Type: application/json' \
  -d '{"identity":"frodo.demo.rocks","planId":"veterans","source":"admin"}'
```

## Upgrade page

Where an owner gets a bigger plan. Their owner console sends them to
`https://<PlanService__PublicDomain>/upgrade?id=<identity>&host=<provisioning domain>` (set odin-core's
`Registry__UpgradeUrl` to `https://<PlanService__PublicDomain>/upgrade`; the host adds `id` and `host`).

1. **Sign in with YouAuth**, as a domain client, done on this server (odin-core
   `docs/youauth-unified-authorization.md`). The service makes a P-384 key pair and a single-use state that
   lasts 10 minutes, kept in the database with the private key sealed by data protection, and sends the browser
   to `https://<identity>/api/owner/v1/youauth/authorize` (`client_id` = the public domain, `client_type=domain`,
   `cipher=aes-gcm`). The owner is usually already signed in to their identity, so there is no password; the
   first time there is a consent screen naming this service.
2. **Callback** (`/upgrade/youauth-callback`): the state is taken once; the service derives the exchange secret,
   trades its digest at `https://<identity>/api/owner/v1/youauth/token`, and opens the token (AES-GCM; an
   identity answering `aes-cbc` is refused as a downgrade). A token that opens is the proof the owner of that
   identity is here. The session is a data-protected cookie on `/upgrade`, for an hour.
3. **The page** shows the identity's plan (asked of its host, as grants find it) and the plans for sale, each
   with a **Buy** button.
4. **Buy is a stand-in for payment**: it makes a `purchase` grant for the plan (see Plan grants), which sets it on
   the identity's host straight away or keeps trying. A plan not in `PlanService__PlansForSale` cannot be bought.

`/.well-known/youauth-client.json` names the service (`PlanService__DisplayName`) and its callback, so the
identity's consent screen shows the name and accepts no other callback.

Security:

- `id` and `host` are hints anyone can write into a link. The identity is the one the service stored with the
  state and then completed the token exchange with, never `id` or the callback's `identity`; `host` is used only
  if it is a known host, and only to decide which host to ask first.
- Forms carry an antiforgery token; the session and antiforgery cookies are `Secure`, `HttpOnly`, on `/upgrade`.
- The data-protection keys are in the database (`data_protection_keys`), so sessions and logins in progress
  survive a restart. They are not encrypted at rest (the service logs a warning saying so): protect the
  database file.

## Configuration

| Key | Value |
|---|---|
| `InvitationCodes__DatabasePath` | required; e.g. `/data/invitation-codes.db`, on a persistent volume |
| `InvitationCodes__SignupApiKey` | required; the signup key, for the identity hosts |
| `InvitationCodes__CreateApiKey` | required; the create key, for admins |
| `InvitationCodes__RequestsPerMinutePerIp` | optional, default 300; more than that from one client is a 429 |
| `PlanService__Hosts__0`, `__1`, ... | optional; the identity hosts' provisioning domains, e.g. `provisioning.demo.dominion.id`. None: grants stay pending |
| `PlanService__ApiKey` | required with hosts; the plan key, the same on every host (odin-core `Registry__PlanService__ApiKey`). Give it only to Homebase-run hosts |
| `PlanService__RetryIntervalSeconds` | optional, default 60; how often due grants are tried |
| `PlanService__ReconcileIntervalHours` | optional, default 24 |
| `PlanService__PublicDomain` | optional; the service's own public domain, e.g. `invites.ravenhosting.cloud`: its YouAuth client id and where `/upgrade` is. None: the upgrade page is off (404) |
| `PlanService__DisplayName` | optional; what the identity's consent screen calls the service. Defaults to the public domain |
| `PlanService__PlansForSale__0__Id`, `__Name`, `__Description`, `__Price`, ... | optional; the plans the upgrade page sells. `Id` is a plan Id on the identity hosts; `Price` is only shown. Needs `PublicDomain` |

SQLite means **one instance**, and the database file should be backed up.

Against junk traffic: a request without the right key is refused before its body is read, a body over 4 KB
is a 413, and each client IP gets `RequestsPerMinutePerIp`. The client IP comes from Caddy's `X-Forwarded-For`,
which is trusted only from loopback and private addresses. Volume floods have to be stopped before the box,
by a firewall or a CDN.

Keys are 32 random bytes, base64url: `openssl rand -base64 32 | tr '+/' '-_' | tr -d '='`. Give the
signup key to Homebase-run identity hosts only (`Registry__InvitationCodeService__ApiKey` in odin-core),
never to self-hosted servers.

## Known gaps

- Grants are kept in this service: it now holds which identities were given which plans, and why.
- The plan key lets its holder set any identity's plan on every host; it cannot do anything else there.
- The upgrade page takes no payment: Buy grants the plan. It must not be turned on for real owners until payment
  replaces the stand-in.

- Requests are limited per client IP. Only odin-core can limit the people behind its public signup page, so
  that limit belongs there.
- An identity host that crashes between taking a use and registering leaks that use.
- Codes stay valid after their owner is deleted, until someone disables them.
