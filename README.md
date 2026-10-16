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
| `maxUses`, `usesLeft` | how many signups it allows, and how many are left |

## API (`/api/v1/invitation-codes`, key in the `Odin-Api-Key` header)

| Call | Key | Answer |
|---|---|---|
| `POST /validate {code}` | signup | `{valid}`: exists and has a use left |
| `POST /use {code}` | signup | `{used, code}`: takes a use, atomically, and returns the code |
| `POST /undo-use {code}` | signup | 204: gives the latest use back |
| `POST / {owner?, maxUses, source, enablePublicWebPresence}` | create | the new code |
| `GET /?owner=<odinId>` | create | the codes, all of them without `owner` |

Bad input (owner not a domain, `maxUses` outside 1..10000) is a 400. Creating a code:

```bash
curl -X POST https://<service>/api/v1/invitation-codes/ \
  -H 'Odin-Api-Key: <create key>' -H 'Content-Type: application/json' \
  -d '{"owner":"frodo.demo.rocks","maxUses":5,"source":"spring campaign","enablePublicWebPresence":true}'
```

## Configuration

All required:

| Key | Value |
|---|---|
| `InvitationCodes__DatabasePath` | e.g. `/data/invitation-codes.db`, on a persistent volume |
| `InvitationCodes__SignupApiKey` | the signup key, for the identity hosts |
| `InvitationCodes__CreateApiKey` | the create key, for admins |

SQLite means **one instance**, and the database file should be backed up.

Keys are 32 random bytes, base64url: `openssl rand -base64 32 | tr '+/' '-_' | tr -d '='`. Give the
signup key to Homebase-run identity hosts only (`Registry__InvitationCodeService__ApiKey` in odin-core),
never to self-hosted servers.

## Known gaps

- No rate limiting on validation.
- An identity host that crashes between taking a use and registering leaks that use.
- Codes stay valid after their owner is deleted, and cannot be changed or disabled.
