#!/bin/bash
#
# Talk to the invitation-code service.
#
# Keys come from the environment, or from ~/.homebase-invites (never from this repo):
#   HOMEBASE_INVITES_CREATE_KEY=...
#   HOMEBASE_INVITES_SIGNUP_KEY=...
#   HOMEBASE_INVITES_URL=https://invites.ravenhosting.cloud   (optional; this is the default)
# Run `./invites.sh setup` once to write that file.
#
set -euo pipefail

CONFIG="$HOME/.homebase-invites"
[[ -f "$CONFIG" ]] && source "$CONFIG"
URL="${HOMEBASE_INVITES_URL:-https://invites.ravenhosting.cloud}"
API="$URL/api/v1/invitation-codes"

usage() {
  cat <<EOF
invites.sh - manage Homebase invitation codes at $URL

An invitation code lets people sign up for a Homebase identity. Each code can be used a set number
of times. Codes look like K7QX-M2PD-9FHW; you can type them in any case, with or without dashes.

FIRST TIME
  setup
      Asks for the two API keys (hidden as you type) and saves them to $CONFIG.
      The create key is for create and list; the signup key is for validate, use and undo.

CHECK THE SERVICE
  ping
      Prints "pong" if the service is up.

CREATE AND LIST CODES  (create key)
  create <uses> <source> [owner] [public-page]
      Makes a new code and prints it.
        uses         how many people can sign up with it (1-10000)
        source       free text noting where the code came from, e.g. "spring campaign"
        owner        optional: the identity the code belongs to, e.g. frodo.demo.rocks.
                     Pass "" to leave it out when you also set public-page.
        public-page  optional: true or false (default true). Whether people who sign up with
                     this code get a public home page on their identity.

  list [owner]
      Lists codes with how many uses each has left: all of them, or just one owner's.

TEST A CODE  (signup key; odin-core does these during a real signup)
  validate <code>
      Says whether the code exists and still has a use left. Does not use it up.
  use <code>
      Takes one use of the code, exactly as a real signup would. Careful on real codes.
  undo <code>
      Gives back the code's most recent use.

EXAMPLES
  ./invites.sh create 5 "spring campaign" frodo.demo.rocks
      5 signups, owned by frodo, new identities get a public home page

  ./invites.sh create 10 "conference booth" "" false
      10 signups, no owner, no public home page

  ./invites.sh list frodo.demo.rocks
  ./invites.sh validate K7QX-M2PD-9FHW

Every command prints the service's answer and the HTTP status, e.g. [200]. [401] means a wrong or
missing key; [400] means bad input, and the message says what.
EOF
}

need() { [[ -n "${!1:-}" ]] || { echo "$1 is not set; run ./invites.sh setup" >&2; exit 1; }; }

json_string() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

call() { # method path key [body]
  local method="$1" path="$2" key="$3" body="${4:-}"
  local args=(-sS -X "$method" -H "Odin-Api-Key: $key" -w '\n[%{http_code}]\n')
  [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}" "$API$path"
}

code_body() { printf '{"code":"%s"}' "$(json_string "$1")"; }

cmd="${1:-}"
shift || true

case "$cmd" in
  setup)
    read -rsp "Create key: " create; echo
    read -rsp "Signup key: " signup; echo
    umask 077
    printf 'HOMEBASE_INVITES_CREATE_KEY=%q\nHOMEBASE_INVITES_SIGNUP_KEY=%q\n' "$create" "$signup" > "$CONFIG"
    echo "Saved to $CONFIG (readable only by you)"
    ;;
  ping)
    curl -sS -w '\n[%{http_code}]\n' "$URL/ping"
    ;;
  create)
    need HOMEBASE_INVITES_CREATE_KEY
    [[ $# -ge 2 ]] || { echo "usage: ./invites.sh create <uses> <source> [owner] [public-page]  (see ./invites.sh help)" >&2; exit 1; }
    uses="$1"; source_text="$2"; owner="${3:-}"; public_page="${4:-true}"
    [[ "$uses" =~ ^[0-9]+$ ]] || { echo "uses must be a number, e.g. 5" >&2; exit 1; }
    [[ "$public_page" =~ ^(true|false)$ ]] || { echo "public-page must be true or false" >&2; exit 1; }
    owner_json=null
    [[ -n "$owner" ]] && owner_json="\"$(json_string "$owner")\""
    call POST / "$HOMEBASE_INVITES_CREATE_KEY" \
      "{\"owner\":$owner_json,\"maxUses\":$uses,\"source\":\"$(json_string "$source_text")\",\"enablePublicWebPresence\":$public_page}"
    ;;
  list)
    need HOMEBASE_INVITES_CREATE_KEY
    query=""
    [[ -n "${1:-}" ]] && query="?owner=$(printf '%s' "$1" | jq -sRr @uri 2>/dev/null || printf '%s' "$1")"
    call GET "/$query" "$HOMEBASE_INVITES_CREATE_KEY"
    ;;
  validate|use)
    need HOMEBASE_INVITES_SIGNUP_KEY
    call POST "/$cmd" "$HOMEBASE_INVITES_SIGNUP_KEY" "$(code_body "${1:?code is required}")"
    ;;
  undo)
    need HOMEBASE_INVITES_SIGNUP_KEY
    call POST /undo-use "$HOMEBASE_INVITES_SIGNUP_KEY" "$(code_body "${1:?code is required}")"
    ;;
  *)
    usage
    [[ -z "$cmd" || "$cmd" == "help" || "$cmd" == "-h" ]] || exit 1
    ;;
esac
