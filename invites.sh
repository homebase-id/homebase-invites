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
  create --uses <n> --source <text> [--owner <identity>] [--no-public-page]
      Makes a new code and prints it.
        --uses <n>             required: how many people can sign up with it (1-10000)
        --source <text>        required: free text noting where the code came from,
                               e.g. --source "spring campaign"
        --owner <identity>     optional: the identity the code belongs to, e.g. frodo.demo.rocks
        --no-public-page       optional: people who sign up with this code do NOT get a public
                               home page on their identity. Without it, they do.

  list [--owner <identity>]
      Lists codes with how many uses each has left, and whether each is disabled: all of them, or
      just one owner's.

SEE HOW CODES ARE DOING  (create key)
  stats [--since <days>] [--json]
      Counts codes and signups: in total, by source, by owner, and per week.
        --since <days>      count only signups from the last <days> days. The code counts
                            (codes, uses allowed, never used, used up, disabled) are always
                            about every code as it is now.
        --json              print the service's JSON instead of the report (the report needs jq)

CHANGE A CODE  (create key)
  edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner]
              [--public-page | --no-public-page]
      Changes only what you give; everything else stays as it is.
        --uses <n>          the new total number of signups allowed (1-10000). Uses already taken
                            still count, so lowering it below them leaves none left.
        --source <text>     new note on where the code came from
        --owner <identity>  give the code to this identity
        --no-owner          take the code away from its owner
        --public-page       people who sign up with it get a public home page
        --no-public-page    they do not

  disable <code>
      Stops the code working, whatever uses it has left. Nothing else changes.
  enable <code>
      Lets a disabled code work again.

TEST A CODE  (signup key; odin-core does these during a real signup)
  validate <code>
      Says whether the code exists, is not disabled, and still has a use left. Does not use it up.
  use <code>
      Takes one use of the code, exactly as a real signup would. Careful on real codes.
  undo <code>
      Gives back the code's most recent use.

EXAMPLES
  ./invites.sh create --uses 5 --source "spring campaign" --owner frodo.demo.rocks
      5 signups, owned by frodo, new identities get a public home page

  ./invites.sh create --uses 10 --source "conference booth" --no-public-page
      10 signups, no owner, no public home page

  ./invites.sh list --owner frodo.demo.rocks

  ./invites.sh stats --since 30
      signups in the last 30 days, by source, owner and week

  ./invites.sh edit K7QX-M2PD-9FHW --uses 20 --no-public-page
      allow 20 signups in total, and no public home page from now on

  ./invites.sh disable K7QX-M2PD-9FHW
  ./invites.sh validate K7QX-M2PD-9FHW

Every command prints the service's answer and the HTTP status, e.g. [200]. [401] means a wrong or
missing key; [400] means bad input, and the message says what; [404] on edit means no such code.
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

# A code for use in the URL: spaces dropped, nothing but letters, digits and dashes
path_code() {
  local code="${1// /}"
  [[ "$code" =~ ^[A-Za-z0-9-]+$ ]] || return 1
  printf '%s' "$code"
}

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
    create_usage="usage: ./invites.sh create --uses <n> --source <text> [--owner <identity>] [--no-public-page]  (see ./invites.sh help)"
    uses=""; source_text=""; owner=""; public_page=true
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --uses) uses="${2:-}"; shift 2 ;;
        --source) source_text="${2:-}"; shift 2 ;;
        --owner) owner="${2:-}"; shift 2 ;;
        --no-public-page) public_page=false; shift ;;
        *) echo "unknown option: $1" >&2; echo "$create_usage" >&2; exit 1 ;;
      esac
    done
    [[ -n "$uses" && -n "$source_text" ]] || { echo "$create_usage" >&2; exit 1; }
    [[ "$uses" =~ ^[0-9]+$ ]] || { echo "--uses must be a number, e.g. --uses 5" >&2; exit 1; }
    owner_json=null
    [[ -n "$owner" ]] && owner_json="\"$(json_string "$owner")\""
    call POST / "$HOMEBASE_INVITES_CREATE_KEY" \
      "{\"owner\":$owner_json,\"maxUses\":$uses,\"source\":\"$(json_string "$source_text")\",\"enablePublicWebPresence\":$public_page}"
    ;;
  list)
    need HOMEBASE_INVITES_CREATE_KEY
    query=""
    case "${1:-}" in
      "") ;;
      --owner) [[ -n "${2:-}" ]] || { echo "usage: ./invites.sh list [--owner <identity>]" >&2; exit 1; }
               query="?owner=$(printf '%s' "$2" | jq -sRr @uri 2>/dev/null || printf '%s' "$2")" ;;
      *) echo "usage: ./invites.sh list [--owner <identity>]" >&2; exit 1 ;;
    esac
    call GET "/$query" "$HOMEBASE_INVITES_CREATE_KEY"
    ;;
  stats)
    need HOMEBASE_INVITES_CREATE_KEY
    stats_usage="usage: ./invites.sh stats [--since <days>] [--json]"
    query=""; as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --since) [[ "${2:-}" =~ ^[0-9]+$ && "$2" -ge 1 ]] || { echo "--since must be a number of days, e.g. --since 30" >&2; exit 1; }
                 query="?sinceDays=$2"; shift 2 ;;
        --json) as_json=true; shift ;;
        *) echo "unknown option: $1" >&2; echo "$stats_usage" >&2; exit 1 ;;
      esac
    done
    if [[ "$as_json" == true ]] || ! command -v jq >/dev/null; then
      call GET "/stats$query" "$HOMEBASE_INVITES_CREATE_KEY"
      exit 0
    fi
    response="$(curl -sS -H "Odin-Api-Key: $HOMEBASE_INVITES_CREATE_KEY" -w '\n%{http_code}' "$API/stats$query")"
    status="${response##*$'\n'}"; body="${response%$'\n'*}"
    [[ "$status" == 200 ]] || { printf '%s\n[%s]\n' "$body" "$status"; exit 1; }
    printf '%s' "$body" | jq -r '
      def left(n): tostring | . + (" " * ([n - length, 0] | max));
      def right(n): tostring | (" " * ([n - length, 0] | max)) + .;
      def row(name): "  " + (name | left(34)) + (.codes | right(7)) + (.usesAllowed | right(9)) + (.signups | right(9));
      (([.byWeek[].signups] | max) // 1) as $most
      | "Signups " + (if .since then "since " + (.since / 1000 | floor | strftime("%Y-%m-%d")) else "of all time" end),
        "",
        "  " + ("" | left(34)) + ("codes" | right(7)) + ("allowed" | right(9)) + ("signups" | right(9)),
        (.totals | row("total")),
        "",
        "by source",
        (.bySource[] | row(if .name == "" then "(no source)" else .name end)),
        "",
        "by owner",
        (if (.byOwner | length) == 0 then "  (no code has an owner)" else (.byOwner[] | row(.name)) end),
        "",
        "signups per week, from Monday",
        (if (.byWeek | length) == 0 then "  (none)"
         else (.byWeek[] | "  " + .week + (.signups | right(6)) + "  " + ("#" * ((.signups * 40 / $most) | ceil) // ""))
         end),
        "",
        "codes: \(.neverUsed) never used, \(.usedUp) used up, \(.disabled) disabled"'
    ;;
  edit)
    need HOMEBASE_INVITES_CREATE_KEY
    edit_usage="usage: ./invites.sh edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner] [--public-page | --no-public-page]"
    code="$(path_code "${1:-}")" || { echo "$edit_usage" >&2; exit 1; }
    shift
    fields=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --uses) [[ "${2:-}" =~ ^[0-9]+$ ]] || { echo "--uses must be a number, e.g. --uses 5" >&2; exit 1; }
                fields+=("\"maxUses\":$2"); shift 2 ;;
        --source) [[ -n "${2:-}" ]] || { echo "$edit_usage" >&2; exit 1; }
                  fields+=("\"source\":\"$(json_string "$2")\""); shift 2 ;;
        --owner) [[ -n "${2:-}" ]] || { echo "$edit_usage" >&2; exit 1; }
                 fields+=("\"owner\":\"$(json_string "$2")\""); shift 2 ;;
        --no-owner) fields+=("\"owner\":\"\""); shift ;;
        --public-page) fields+=("\"enablePublicWebPresence\":true"); shift ;;
        --no-public-page) fields+=("\"enablePublicWebPresence\":false"); shift ;;
        *) echo "unknown option: $1" >&2; echo "$edit_usage" >&2; exit 1 ;;
      esac
    done
    [[ ${#fields[@]} -gt 0 ]] || { echo "nothing to change" >&2; echo "$edit_usage" >&2; exit 1; }
    call PATCH "/$code" "$HOMEBASE_INVITES_CREATE_KEY" "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  disable|enable)
    need HOMEBASE_INVITES_CREATE_KEY
    code="$(path_code "${1:-}")" || { echo "usage: ./invites.sh $cmd <code>" >&2; exit 1; }
    disabled=false; [[ "$cmd" == disable ]] && disabled=true
    call PATCH "/$code" "$HOMEBASE_INVITES_CREATE_KEY" "{\"disabled\":$disabled}"
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
