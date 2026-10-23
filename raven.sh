#!/bin/bash
#
# Talk to Raven Hosting's service: invitation codes, plans and plan grants.
#
# Keys come from the environment, or from ~/.raven-hosting (never from this repo):
#   RAVEN_HOSTING_CREATE_KEY=...
#   RAVEN_HOSTING_SIGNUP_KEY=...
#   RAVEN_HOSTING_URL=https://invites.ravenhosting.cloud   (optional; this is the default)
# Run `./raven.sh setup` once to write that file.
#
set -euo pipefail

CONFIG="$HOME/.raven-hosting"
[[ -f "$CONFIG" ]] && source "$CONFIG"
URL="${RAVEN_HOSTING_URL:-https://invites.ravenhosting.cloud}"
API="$URL/api/v1/invitation-codes"
PLANS="$URL/api/v1/plans"
GRANTS="$URL/api/v1/plans/grants"

usage() {
  cat <<EOF
raven.sh - manage Raven Hosting's invitation codes, plans and plan grants at $URL

An invitation code lets people sign up for a Homebase identity. Each code can be used a set number
of times. Codes look like K7QX-M2PD-9FHW; you can type them in any case, with or without dashes.

FIRST TIME
  setup
      Asks for the two API keys (hidden as you type) and saves them to $CONFIG.
      The create key is for create and list; the signup key is for validate, use and undo.

CHECK THE SERVICE
  ping
      Prints "pong" if the service is up.

HOSTING PLANS  (create key)
  A plan is what an identity gets: a quota and whether it has a public home page. The identity
  hosts have no plans of their own; this service sends what a plan gives whenever it gives one.
  There are no built-in plans: create them before making codes for them.

  plans [--json]
      Lists the plans as a table: id, name, storage, public page, for sale, price and sort order.
        --json              print the service's JSON instead of the table (the table needs jq)

  plan-create --id <plan id> --name <text> --quota-bytes <n> --public-web-presence true|false
              [--for-sale true|false] [--price <text>] [--sort-order <n>] [--description <text>]
      Makes a new plan and prints it.
        --id <plan id>                 required: letters, digits, '.', '_' and '-', e.g. veterans.
                                       Any case finds it; it cannot be changed later
        --name <text>                  required: what owners see, e.g. "Veterans"
        --quota-bytes <n>              required: bytes it may store, more than 0, e.g. 1099511627776
                                       for 1 TB. There is no unlimited
        --public-web-presence true|false   required: whether it has a public home page
        --for-sale true|false          optional, false by default: whether owners can buy it on
                                       the upgrade page
        --price <text>                 optional: shown to buyers, e.g. "\$5 a month"
        --sort-order <n>               optional, 0 by default: plans are listed by it, then by id
        --description <text>           optional: a line shown on its card

  plan-edit <plan id> [--name <text>] [--quota-bytes <n>] [--public-web-presence true|false]
            [--for-sale true|false] [--price <text>] [--sort-order <n>] [--description <text>]
      Changes only what you give. Identities already on the plan keep what they were given; the
      change applies to the next signup or purchase. To retire a plan, use --for-sale false.

  plan-delete <plan id>
      Deletes a plan no code or grant uses. [409] means one does: mark it not for sale instead.

CREATE AND LIST CODES  (create key)
  create --uses <n> --source <text> --plan <plan id> [--owner <identity>]
      Makes a new code and prints it.
        --uses <n>             required: how many people can sign up with it (1-10000)
        --source <text>        required: free text noting where the code came from,
                               e.g. --source "spring campaign"
        --owner <identity>     optional: the identity the code belongs to, e.g. frodo.demo.rocks
        --plan <plan id>       required: the hosting plan people who sign up with this code get,
                               e.g. --plan registered. It must be one of the plans (see plans);
                               it decides their quota and whether they get a public home page.

  list [--owner <identity>] [--json]
      Lists codes as a table: plan, uses left of uses allowed, owner, source, and whether each is
      disabled. All of them, or just one owner's.
        --json              print the service's JSON instead of the table (the table needs jq)

SEE HOW CODES ARE DOING  (create key)
  stats [--since <days>] [--json]
      Counts codes, signups and the signups still left: in total, by source, by owner, by plan,
      and per week. "left" is what the codes still allow now; disabled codes count none.
        --since <days>      count only signups from the last <days> days. The code counts
                            (codes, uses allowed, left, never used, used up, disabled) are always
                            about every code as it is now.
        --json              print the service's JSON instead of the report (the report needs jq)

CHANGE A CODE  (create key)
  edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner]
              [--plan <plan id>]
      Changes only what you give; everything else stays as it is.
        --uses <n>          the new total number of signups allowed (1-10000). Uses already taken
                            still count, so lowering it below them leaves none left.
        --source <text>     new note on where the code came from
        --owner <identity>  give the code to this identity
        --no-owner          take the code away from its owner
        --plan <plan id>    people who sign up with it from now on get this hosting plan; it
                            must be one of the plans

  disable <code>
      Stops the code working, whatever uses it has left. Nothing else changes.
  enable <code>
      Lets a disabled code work again.

GRANT A PLAN  (create key)
  grant --id <identity> --plan <plan id> [--host <provisioning domain>] [--reference <ref>]
      Owes an identity a plan and sets it on the identity host that serves it, straight away. If no
      host can take it yet, the grant stays pending and the service keeps trying.
        --id <identity>        required: e.g. frodo.demo.rocks
        --plan <plan id>       required: one of the plans, e.g. veterans. The grant keeps what the
                               plan gives now and sets all of it on the host
        --host <domain>        optional: the provisioning domain of the identity's host, tried
                               first; only hosts the service knows are ever called
        --reference <ref>      optional: your id for it; the same reference twice is one grant

  grants [--id <identity>] [--status pending|applied|failed] [--json]
      Lists grants, newest first, as a table: id, identity, plan, status, host, attempts and the
      last error. "failed" means the host refused the plan (an empty plan id or a quota that is
      not more than 0); "pending" is retried by itself.
        --json              print the service's JSON instead of the table (the table needs jq)

  retry <grant id>
      Tries a pending or failed grant again now, e.g. once the identity is on a host.

TEST A CODE  (signup key; odin-core does these during a real signup)
  validate <code>
      Says whether the code exists, is not disabled, and still has a use left. Does not use it up.
  use <code>
      Takes one use of the code, exactly as a real signup would. Careful on real codes.
  undo <code>
      Gives back the code's most recent use.

EXAMPLES
  ./raven.sh plan-create --id veterans --name Veterans --quota-bytes 1099511627776
                          --public-web-presence true --for-sale true --price "\$5 a month"
      a 1 TB plan with a public home page, for sale on the upgrade page

  ./raven.sh plan-edit veterans --for-sale false
      stop selling it; identities on it keep it

  ./raven.sh create --uses 5 --source "spring campaign" --plan free --owner frodo.demo.rocks
      5 signups on the free plan, owned by frodo

  ./raven.sh create --uses 10 --source "conference booth" --plan registered
      10 signups on the registered plan, no owner

  ./raven.sh list --owner frodo.demo.rocks

  ./raven.sh stats --since 30
      signups in the last 30 days, by source, owner, plan and week

  ./raven.sh edit K7QX-M2PD-9FHW --uses 20 --plan veterans
      allow 20 signups in total, on the veterans plan from now on

  ./raven.sh disable K7QX-M2PD-9FHW
  ./raven.sh validate K7QX-M2PD-9FHW

  ./raven.sh grant --id frodo.demo.rocks --plan veterans
      give frodo the veterans plan on whichever host serves frodo

  ./raven.sh grants --status pending

Every command prints the service's answer and the HTTP status, e.g. [200]. [401] means a wrong or
missing key; [400] means bad input, and the message says what; [404] on edit means no such code
or plan; [409] on plan-create means the id is taken, on plan-delete that the plan is in use, on
grant that the reference is another grant's, on retry that the grant is already applied.
EOF
}

die() { printf '%s\n' "$@" >&2; exit 1; }

need() { [[ -n "${!1:-}" ]] || die "$1 is not set; run ./raven.sh setup"; }

check_uses() { [[ "$1" =~ ^[0-9]+$ ]] || die "--uses must be a number, e.g. --uses 5"; }

# A plan id for use in the URL
check_plan_id() { [[ "$1" =~ ^[A-Za-z0-9._-]{1,64}$ ]] || die "$2"; }

plan_fields() { # usage [option value]...; the plan options as JSON fields, in the array fields
  local usage="$1"; shift
  fields=()
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --name|--description|--price)
        [[ $# -ge 2 ]] || die "$usage"
        fields+=("\"${1#--}\":$(json_string "$2")"); shift 2 ;;
      --quota-bytes)
        [[ "${2:-}" =~ ^[0-9]+$ ]] || die "--quota-bytes must be a number of bytes, e.g. --quota-bytes 1099511627776"
        fields+=("\"quotaBytes\":$2"); shift 2 ;;
      --sort-order)
        [[ "${2:-}" =~ ^-?[0-9]+$ ]] || die "--sort-order must be a whole number, e.g. --sort-order 1"
        fields+=("\"sortOrder\":$2"); shift 2 ;;
      --public-web-presence|--for-sale)
        [[ "${2:-}" =~ ^(true|false)$ ]] || die "$1 must be true or false"
        name=publicWebPresence; [[ "$1" == --for-sale ]] && name=forSale
        fields+=("\"$name\":$2"); shift 2 ;;
      *) die "unknown option: $1" "$usage" ;;
    esac
  done
}

# A JSON string, quotes included
json_string() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"; }

call() { # method path [body]; sends $key, to $base (the codes API unless set)
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS -X "$method" -H "Odin-Api-Key: $key" -w '\n[%{http_code}]\n')
  [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}" "${base:-$API}$path"
}

code_body() { printf '{"code":%s}' "$(json_string "$1")"; }

render() { # as_json path jq-program; GETs path and prints the answer through jq, or as is with --json or no jq
  local as_json="$1" path="$2" program="$3" response status body
  if [[ "$as_json" == true ]] || ! command -v jq >/dev/null; then
    call GET "$path"
    return
  fi
  response="$(call GET "$path")"
  status="${response##*$'\n'}"; body="${response%$'\n'*}"
  [[ "$status" == "[200]" ]] || { printf '%s\n%s\n' "$body" "$status"; exit 1; }
  printf '%s' "$body" | jq -r "$program"
}

# A code for use in the URL: spaces dropped, nothing but letters, digits and dashes
path_code() {
  local code="${1// /}"
  [[ "$code" =~ ^[A-Za-z0-9-]+$ ]] || return 1
  printf '%s' "$code"
}

cmd="${1:-}"
shift || true

case "$cmd" in
  plans|plan-create|plan-edit|plan-delete|create|list|stats|edit|disable|enable|grant|grants|retry) need RAVEN_HOSTING_CREATE_KEY; key="$RAVEN_HOSTING_CREATE_KEY" ;;
  validate|use|undo) need RAVEN_HOSTING_SIGNUP_KEY; key="$RAVEN_HOSTING_SIGNUP_KEY" ;;
esac

case "$cmd" in
  setup)
    read -rsp "Create key: " create; echo
    read -rsp "Signup key: " signup; echo
    umask 077
    printf 'RAVEN_HOSTING_CREATE_KEY=%q\nRAVEN_HOSTING_SIGNUP_KEY=%q\n' "$create" "$signup" > "$CONFIG"
    echo "Saved to $CONFIG (readable only by you)"
    ;;
  ping)
    curl -sS -w '\n[%{http_code}]\n' "$URL/ping"
    ;;
  plans)
    [[ "${1:-}" == "--json" || -z "${1:-}" ]] || die "usage: ./raven.sh plans [--json]"
    as_json=false; [[ "${1:-}" == "--json" ]] && as_json=true
    base="$PLANS" render "$as_json" "/" '
      def pad(n): . + (" " * ([n - length, 0] | max));
      def clip(n): if length > n then .[0:n - 3] + "..." else . end;
      . as $plans
      | ["ID", "NAME", "STORAGE", "PUBLIC PAGE", "FOR SALE", "PRICE", "ORDER"] as $head
      | [$plans[] | [.id, (.name | clip(30)), .storage, (if .publicWebPresence then "yes" else "no" end),
                     (if .forSale then "yes" else "no" end), (if .price == "" then "-" else .price | clip(30) end),
                     "\(.sortOrder)"]] as $rows
      | ([$head] + $rows) as $table
      | [range(0; $head | length) as $i | [$table[][$i] | length] | max] as $widths
      | if ($rows | length) == 0 then "(no plans; make one with ./raven.sh plan-create)"
        else ($table[] | [range(0; length) as $i | .[$i] | pad($widths[$i])] | join("  ") | sub(" +$"; ""))
        end'
    ;;
  plan-create)
    plan_create_usage="usage: ./raven.sh plan-create --id <plan id> --name <text> --quota-bytes <n> --public-web-presence true|false [--for-sale true|false] [--price <text>] [--sort-order <n>] [--description <text>]  (see ./raven.sh help)"
    plan_id=""; rest=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --id) [[ -n "${2:-}" ]] || die "$plan_create_usage"
              plan_id="$2"; shift 2 ;;
        *) rest+=("$1"); shift ;;
      esac
    done
    plan_fields "$plan_create_usage" ${rest[@]+"${rest[@]}"}
    joined=" ${fields[*]-} "
    [[ -n "$plan_id" && "$joined" == *'"name":'* && "$joined" == *'"quotaBytes":'* &&
       "$joined" == *'"publicWebPresence":'* ]] || die "$plan_create_usage"
    fields+=("\"id\":$(json_string "$plan_id")")
    base="$PLANS" call POST / "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  plan-edit)
    plan_edit_usage="usage: ./raven.sh plan-edit <plan id> [--name <text>] [--quota-bytes <n>] [--public-web-presence true|false] [--for-sale true|false] [--price <text>] [--sort-order <n>] [--description <text>]"
    plan_id="${1:-}"
    check_plan_id "$plan_id" "$plan_edit_usage"
    shift
    plan_fields "$plan_edit_usage" "$@"
    [[ ${#fields[@]} -gt 0 ]] || die "nothing to change" "$plan_edit_usage"
    base="$PLANS" call PATCH "/$plan_id" "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  plan-delete)
    check_plan_id "${1:-}" "usage: ./raven.sh plan-delete <plan id>"
    base="$PLANS" call DELETE "/$1"
    ;;
  create)
    create_usage="usage: ./raven.sh create --uses <n> --source <text> --plan <plan id> [--owner <identity>]  (see ./raven.sh help)"
    uses=""; source_text=""; plan_id=""; owner_json=null
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --uses) uses="${2:-}"; shift 2 ;;
        --source) source_text="${2:-}"; shift 2 ;;
        --owner) [[ -n "${2:-}" ]] && owner_json="$(json_string "$2")"; shift 2 ;;
        --plan) [[ -n "${2:-}" ]] || die "$create_usage"
                plan_id="$2"; shift 2 ;;
        *) die "unknown option: $1" "$create_usage" ;;
      esac
    done
    [[ -n "$uses" && -n "$source_text" && -n "$plan_id" ]] || die "$create_usage"
    check_uses "$uses"
    call POST / \
      "{\"owner\":$owner_json,\"maxUses\":$uses,\"source\":$(json_string "$source_text"),\"planId\":$(json_string "$plan_id")}"
    ;;
  list)
    list_usage="usage: ./raven.sh list [--owner <identity>] [--json]"
    query=""; as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --owner) [[ -n "${2:-}" ]] || die "$list_usage"
                 query="?owner=$(printf '%s' "$2" | jq -sRr @uri 2>/dev/null || printf '%s' "$2")"; shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$list_usage" ;;
      esac
    done
    render "$as_json" "/$query" '
      def pad(n): . + (" " * ([n - length, 0] | max));
      def clip(n): if length > n then .[0:n - 3] + "..." else . end;
      . as $codes
      | ["CODE", "PLAN", "LEFT", "OWNER", "SOURCE", ""] as $head
      | [$codes[] | [.code, (.planId // "-"), "\(.usesLeft)/\(.maxUses)", (.owner // "-"),
                     (if .source == "" then "-" else .source | clip(40) end),
                     (if .disabled then "disabled" else "" end)]] as $rows
      | ([$head] + $rows) as $table
      | [range(0; $head | length) as $i | [$table[][$i] | length] | max] as $widths
      | [$codes[] | select(.disabled | not) | .usesLeft] as $left
      | if ($rows | length) == 0 then "(no codes)"
        else
          ($table[] | [range(0; length) as $i | .[$i] | pad($widths[$i])] | join("  ") | sub(" +$"; "")),
          "",
          "\($rows | length) code\(if ($rows | length) == 1 then "" else "s" end), \($left | add // 0) uses left on those not disabled"
        end'
    ;;
  stats)
    stats_usage="usage: ./raven.sh stats [--since <days>] [--json]"
    query=""; as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --since) [[ "${2:-}" =~ ^[0-9]+$ && "$2" -ge 1 ]] || die "--since must be a number of days, e.g. --since 30"
                 query="?sinceDays=$2"; shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$stats_usage" ;;
      esac
    done
    render "$as_json" "/stats$query" '
      def left(n): tostring | . + (" " * ([n - length, 0] | max));
      def right(n): tostring | (" " * ([n - length, 0] | max)) + .;
      def row(name): "  " + (name | left(34)) + (.codes | right(7)) + (.usesAllowed | right(9)) + (.signups | right(9)) + (.usesLeft | right(7));
      (([.byWeek[].signups] | max) // 1) as $most
      | "Signups " + (if .since then "since " + (.since / 1000 | floor | strftime("%Y-%m-%d")) else "of all time" end),
        "",
        "  " + ("" | left(34)) + ("codes" | right(7)) + ("allowed" | right(9)) + ("signups" | right(9)) + ("left" | right(7)),
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
    edit_usage="usage: ./raven.sh edit <code> [--uses <n>] [--source <text>] [--owner <identity> | --no-owner] [--plan <plan id>]"
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
        *) die "unknown option: $1" "$edit_usage" ;;
      esac
    done
    [[ ${#fields[@]} -gt 0 ]] || die "nothing to change" "$edit_usage"
    call PATCH "/$code" "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  disable|enable)
    code="$(path_code "${1:-}")" || die "usage: ./raven.sh $cmd <code>"
    disabled=false; [[ "$cmd" == disable ]] && disabled=true
    call PATCH "/$code" "{\"disabled\":$disabled}"
    ;;
  grant)
    grant_usage="usage: ./raven.sh grant --id <identity> --plan <plan id> [--host <provisioning domain>] [--reference <ref>]  (see ./raven.sh help)"
    fields=()
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --id|--plan|--host|--reference)
          [[ -n "${2:-}" ]] || die "$grant_usage"
          case "$1" in
            --id) name=identity ;; --plan) name=planId ;; --host) name=host ;; --reference) name=reference ;;
          esac
          fields+=("\"$name\":$(json_string "$2")"); shift 2 ;;
        *) die "unknown option: $1" "$grant_usage" ;;
      esac
    done
    [[ " ${fields[*]-} " == *'"identity":'* && " ${fields[*]-} " == *'"planId":'* ]] || die "$grant_usage"
    base="$GRANTS" call POST / "{$(IFS=,; echo "${fields[*]}")}"
    ;;
  grants)
    grants_usage="usage: ./raven.sh grants [--id <identity>] [--status pending|applied|failed] [--json]"
    query=(); as_json=false
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --id) [[ -n "${2:-}" ]] || die "$grants_usage"
              query+=("identity=$(printf '%s' "$2" | jq -sRr @uri 2>/dev/null || printf '%s' "$2")"); shift 2 ;;
        --status) [[ "${2:-}" =~ ^(pending|applied|failed)$ ]] || die "--status must be pending, applied or failed"
                  query+=("status=$2"); shift 2 ;;
        --json) as_json=true; shift ;;
        *) die "unknown option: $1" "$grants_usage" ;;
      esac
    done
    qs=""; [[ ${#query[@]} -gt 0 ]] && qs="?$(IFS='&'; echo "${query[*]}")"
    base="$GRANTS" render "$as_json" "/$qs" '
      def pad(n): . + (" " * ([n - length, 0] | max));
      def clip(n): if length > n then .[0:n - 3] + "..." else . end;
      . as $grants
      | ["ID", "IDENTITY", "PLAN", "STATUS", "HOST", "TRIES", "LAST ERROR"] as $head
      | [$grants[] | ["\(.id)", .identity, .planId, .status, (.host // .hostHint // "-"), "\(.attempts)",
                      ((.lastError // "") | clip(60))]] as $rows
      | ([$head] + $rows) as $table
      | [range(0; $head | length) as $i | [$table[][$i] | length] | max] as $widths
      | if ($rows | length) == 0 then "(no grants)"
        else ($table[] | [range(0; length) as $i | .[$i] | pad($widths[$i])] | join("  ") | sub(" +$"; ""))
        end'
    ;;
  retry)
    [[ "${1:-}" =~ ^[0-9]+$ ]] || die "usage: ./raven.sh retry <grant id>"
    base="$GRANTS" call POST "/$1/retry"
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
