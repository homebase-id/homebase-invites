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
`./invites.sh list`, `stats`, `edit`, `disable`, `enable`, `validate`, `use`, `undo`. `./invites.sh help` explains each command and its arguments.

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
| `enablePublicWebPresence` | whether an identity that signs up with it gets a public home page |
| `planId` | the hosting plan, and so the quota, an identity that signs up with it gets; required when a code is made, and can be changed but not removed. Letters, digits, `.`, `_` and `-`, up to 64. `null` only on codes made before it was required, which leave the plan to the identity host |
| `maxUses`, `usesLeft` | how many signups it allows, and how many are left |
| `disabled` | a disabled code cannot be used, whatever uses it has left |

## API (`/api/v1/invitation-codes`, key in the `Odin-Api-Key` header)

| Call | Key | Answer |
|---|---|---|
| `POST /validate {code}` | signup | `{valid}`: exists and has a use left |
| `POST /use {code}` | signup | `{used, code}`: takes a use, atomically, and returns the code |
| `POST /undo-use {code}` | signup | 204: gives the latest use back |
| `POST / {owner?, maxUses, source, enablePublicWebPresence, planId}` | create | the new code |
| `GET /?owner=<odinId>` | create | the codes, all of them without `owner` |
| `GET /stats?sinceDays=<n>` | create | codes and signups in total, by source, by owner, by plan (`name: null` for old codes without one) and by week (Monday, UTC). `sinceDays` limits only the signups counted; the code counts (`neverUsed`, `usedUp`, `disabled`, codes, uses allowed) are always about every code as it is now |
| `PATCH /<code> {maxUses?, source?, owner?, enablePublicWebPresence?, planId?, disabled?}` | create | the code as it is now; 404 if there is none. Fields left out stay as they are; `"owner": ""` removes the owner |

Bad input (owner not a domain, a missing or bad `planId`, `maxUses` outside 1..10000) is a 400. Creating a code:

```bash
curl -X POST https://<service>/api/v1/invitation-codes/ \
  -H 'Odin-Api-Key: <create key>' -H 'Content-Type: application/json' \
  -d '{"owner":"frodo.demo.rocks","maxUses":5,"source":"spring campaign","enablePublicWebPresence":true,"planId":"pro-100gb"}'
```

## Configuration

| Key | Value |
|---|---|
| `InvitationCodes__DatabasePath` | required; e.g. `/data/invitation-codes.db`, on a persistent volume |
| `InvitationCodes__SignupApiKey` | required; the signup key, for the identity hosts |
| `InvitationCodes__CreateApiKey` | required; the create key, for admins |
| `InvitationCodes__RequestsPerMinutePerIp` | optional, default 300; more than that from one client is a 429 |

SQLite means **one instance**, and the database file should be backed up.

Against junk traffic: a request without the right key is refused before its body is read, a body over 4 KB
is a 413, and each client IP gets `RequestsPerMinutePerIp`. The client IP comes from Caddy's `X-Forwarded-For`,
which is trusted only from loopback and private addresses. Volume floods have to be stopped before the box,
by a firewall or a CDN.

Keys are 32 random bytes, base64url: `openssl rand -base64 32 | tr '+/' '-_' | tr -d '='`. Give the
signup key to Homebase-run identity hosts only (`Registry__InvitationCodeService__ApiKey` in odin-core),
never to self-hosted servers.

## Known gaps

- Requests are limited per client IP. Only odin-core can limit the people behind its public signup page, so
  that limit belongs there.
- An identity host that crashes between taking a use and registering leaks that use.
- Codes stay valid after their owner is deleted, until someone disables them.
