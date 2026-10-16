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
Usage: ./invites.sh <command> [args]

  setup                                   save the keys to $CONFIG
  ping                                    is the service up?

  create <uses> <source> [owner] [web]    create a code; web = true|false (default true)
  list [owner]                            all codes, or one owner's

  validate <code>                         exists and has a use left? (does not use it)
  use <code>                              take one use (signup does this)
  undo <code>                             give the last use back

Examples:
  ./invites.sh create 5 "spring campaign" frodo.demo.rocks
  ./invites.sh create 10 conference "" false
  ./invites.sh list frodo.demo.rocks
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
    uses="${1:?uses is required}"; source_text="${2:?source is required}"; owner="${3:-}"; web="${4:-true}"
    [[ "$uses" =~ ^[0-9]+$ ]] || { echo "uses must be a number" >&2; exit 1; }
    [[ "$web" =~ ^(true|false)$ ]] || { echo "web must be true or false" >&2; exit 1; }
    owner_json=null
    [[ -n "$owner" ]] && owner_json="\"$(json_string "$owner")\""
    call POST / "$HOMEBASE_INVITES_CREATE_KEY" \
      "{\"owner\":$owner_json,\"maxUses\":$uses,\"source\":\"$(json_string "$source_text")\",\"enablePublicWebPresence\":$web}"
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
