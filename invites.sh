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
  create --uses <n> --source <text> [--owner <identity>] [--plan <plan id>] [--no-public-page]
      Makes a new code and prints it.
        --uses <n>             required: how many people can sign up with it (1-10000)
        --source <text>        required: free text noting where the code came from,
                               e.g. --source "spring campaign"
        --owner <identity>     optional: the identity the code belongs to, e.g. frodo.demo.rocks
        --plan <plan id>       optional: the hosting plan, and so the quota, people who sign up
                               with this code get, e.g. --plan pro-100gb. Without it, the
                               identity host picks the plan.
        --no-public-page       optional: people who sign up with this code do NOT get a public
                               home page on their identity. Without it, they do.

  list [--owner <identity>]
      Lists codes with how many uses each has left, and whether each is disabled: all of them, or
      just one owner's.

SEE HOW CODES ARE DOING  (create key)
  stats [--since <days>] [--json]
      Counts codes and signups: in total, by source, by owner, by plan, and per week.
        --since <days>      count only signups from the last <days> days. The code counts
                            (codes, uses allowed, never used, used up, disabled) are always
                            about every code as it is now.
        --json              print the service's JSON instead of the report (the report needs jq)

CHANGE A CODE  (create key)
  edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner]
              [--plan <plan id> | --no-plan] [--public-page | --no-public-page]
      Changes only what you give; everything else stays as it is.
        --uses <n>          the new total number of signups allowed (1-10000). Uses already taken
                            still count, so lowering it below them leaves none left.
        --source <text>     new note on where the code came from
        --owner <identity>  give the code to this identity
        --no-owner          take the code away from its owner
        --plan <plan id>    people who sign up with it from now on get this hosting plan
        --no-plan           leave the plan to the identity host
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

  ./invites.sh create --uses 3 --source "beta testers" --plan pro-100gb
      3 signups, each on the pro-100gb plan

  ./invites.sh list --owner frodo.demo.rocks

  ./invites.sh stats --since 30
      signups in the last 30 days, by source, owner, plan and week

  ./invites.sh edit K7QX-M2PD-9FHW --uses 20 --no-public-page
      allow 20 signups in total, and no public home page from now on

  ./invites.sh disable K7QX-M2PD-9FHW
  ./invites.sh validate K7QX-M2PD-9FHW

Every command prints the service's answer and the HTTP status, e.g. [200]. [401] means a wrong or
missing key; [400] means bad input, and the message says what; [404] on edit means no such code.
EOF
}

die() { printf '%s\n' "$@" >&2; exit 1; }

need() { [[ -n "${!1:-}" ]] || die "$1 is not set; run ./invites.sh setup"; }

check_uses() { [[ "$1" =~ ^[0-9]+$ ]] || die "--uses must be a number, e.g. --uses 5"; }

# A JSON string, quotes included
json_string() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"; }

call() { # method path [body]; sends $key
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS -X "$method" -H "Odin-Api-Key: $key" -w '\n[%{http_code}]\n')
  [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}" "$API$path"
}

code_body() { printf '{"code":%s}' "$(json_string "$1")"; }

# A code for use in the URL: spaces dropped, nothing but letters, digits and dashes
path_code() {
  local code="${1// /}"
  [[ "$code" =~ ^[A-Za-z0-9-]+$ ]] || return 1
  printf '%s' "$code"
}

cmd="${1:-}"
shift || true

case "$cmd" in
  create|list|stats|edit|disable|enable) need HOMEBASE_INVITES_CREATE_KEY; key="$HOMEBASE_INVITES_CREATE_KEY" ;;
  validate|use|undo) need HOMEBASE_INVITES_SIGNUP_KEY; key="$HOMEBASE_INVITES_SIGNUP_KEY" ;;
esac

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
    create_usage="usage: ./invites.sh create --uses <n> --source <text> [--owner <identity>] [--plan <plan id>] [--no-public-page]  (see ./invites.sh help)"
    uses=""; source_text=""; owner_json=null; plan_json=null; public_page=true
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --uses) uses="${2:-}"; shift 2 ;;
        --source) source_text="${2:-}"; shift 2 ;;
        --owner) [[ -n "${2:-}" ]] && owner_json="$(json_string "$2")"; shift 2 ;;
        --plan) [[ -n "${2:-}" ]] || die "$create_usage"
                plan_json="$(json_string "$2")"; shift 2 ;;
        --no-public-page) public_page=false; shift ;;
        *) die "unknown option: $1" "$create_usage" ;;
      esac
    done
    [[ -n "$uses" && -n "$source_text" ]] || die "$create_usage"
    check_uses "$uses"
    call POST / \
      "{\"owner\":$owner_json,\"maxUses\":$uses,\"source\":$(json_string "$source_text"),\"enablePublicWebPresence\":$public_page,\"planId\":$plan_json}"
    ;;
  list)
    list_usage="usage: ./invites.sh list [--owner <identity>]"
    query=""
    case "${1:-}" in
      "") ;;
      --owner) [[ -n "${2:-}" ]] || die "$list_usage"
               query="?owner=$(printf '%s' "$2" | jq -sRr @uri 2>/dev/null || printf '%s' "$2")" ;;
      *) die "$list_usage" ;;
    esac
    call GET "/$query"
    ;;
  stats)
    stats_usage="usage: ./invites.sh stats [--since <days>] [--json]"
    query=""; as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --since) [[ "${2:-}" =~ ^[0-9]+$ && "$2" -ge 1 ]] || die "--since must be a number of days, e.g. --since 30"
                 query="?sinceDays=$2"; shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$stats_usage" ;;
      esac
    done
    if [[ "$as_json" == true ]] || ! command -v jq >/dev/null; then
      call GET "/stats$query"
      exit 0
    fi
    response="$(call GET "/stats$query")"
    status="${response##*$'\n'}"; body="${response%$'\n'*}"
    [[ "$status" == "[200]" ]] || { printf '%s\n%s\n' "$body" "$status"; exit 1; }
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
        "by plan",
        (.byPlan[] | row(.name // "(no plan)")),
        "",
        "signups per week, from Monday",
        (if (.byWeek | length) == 0 then "  (none)"
         else (.byWeek[] | "  " + .week + (.signups | right(6)) + "  " + ("#" * ((.signups * 40 / $most) | ceil) // ""))
         end),
        "",
        "codes: \(.neverUsed) never used, \(.usedUp) used up, \(.disabled) disabled"'
    ;;
  edit)
    edit_usage="usage: ./invites.sh edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner] [--plan <plan id> | --no-plan] [--public-page | --no-public-page]"
    code="$(path_code "${1:-}")" || die "$edit_usage"
    shift
    fields=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --uses) check_uses "${2:-}"; fields+=("\"maxUses\":$2"); shift 2 ;;
        --source) [[ -n "${2:-}" ]] || die "$edit_usage"
                  fields+=("\"source\":$(json_string "$2")"); shift 2 ;;
        --owner) [[ -n "${2:-}" ]] || die "$edit_usage"
                 fields+=("\"owner\":$(json_string "$2")"); shift 2 ;;
        --no-owner) fields+=("\"owner\":\"\""); shift ;;
        --plan) [[ -n "${2:-}" ]] || die "$edit_usage"
                fields+=("\"planId\":$(json_string "$2")"); shift 2 ;;
        --no-plan) fields+=("\"planId\":\"\""); shift ;;
        --public-page) fields+=("\"enablePublicWebPresence\":true"); shift ;;
        --no-public-page) fields+=("\"enablePublicWebPresence\":false"); shift ;;
        *) die "unknown option: $1" "$edit_usage" ;;
      esac
    done
    [[ ${#fields[@]} -gt 0 ]] || die "nothing to change" "$edit_usage"
    call PATCH "/$code" "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  disable|enable)
    code="$(path_code "${1:-}")" || die "usage: ./invites.sh $cmd <code>"
    disabled=false; [[ "$cmd" == disable ]] && disabled=true
    call PATCH "/$code" "{\"disabled\":$disabled}"
    ;;
  validate|use|undo)
    path="/$cmd"; [[ "$cmd" == undo ]] && path=/undo-use
    call POST "$path" "$(code_body "${1:?code is required}")"
    ;;
  *)
    usage
    [[ -z "$cmd" || "$cmd" == "help" || "$cmd" == "-h" ]] || exit 1
    ;;
esac
