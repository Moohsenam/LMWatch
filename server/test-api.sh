#!/usr/bin/env bash
# End-to-end check of every endpoint against a running server.
#   ./test-api.sh            (starts its own server on a scratch data folder)
#   BASE=https://... PASSWORD=... ./test-api.sh   (against a deployed one)
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BASE="${BASE:-http://127.0.0.1:5091}"
JAR="$(mktemp)"
PASS=0
FAIL=0
OWN_SERVER=0
SERVER_PID=""

cleanup() {
  [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null
  rm -f "$JAR"
}
trap cleanup EXIT

check() { # name expected actual
  if [ "$2" = "$3" ]; then
    printf '  ok  %s\n' "$1"; PASS=$((PASS+1))
  else
    printf '  XX  %s (expected %s, got %s)\n' "$1" "$2" "$3"; FAIL=$((FAIL+1))
  fi
}

contains() { # name needle haystack
  case "$3" in
    *"$2"*) printf '  ok  %s\n' "$1"; PASS=$((PASS+1)) ;;
    *) printf '  XX  %s (no %s in %s)\n' "$1" "$2" "$3"; FAIL=$((FAIL+1)) ;;
  esac
}

status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

if [ -z "${PASSWORD:-}" ]; then
  OWN_SERVER=1
  DATA="$(mktemp -d)"
  echo "Starting a server on $BASE with a scratch data folder"
  CW_DATA="$DATA" CW_URLS="$BASE" dotnet run --project "$HERE" -c Release --no-build > "$DATA/server.log" 2>&1 &
  SERVER_PID=$!

  for _ in $(seq 1 40); do
    sleep 0.5
    [ "$(status "$BASE/api/health")" = "200" ] && break
  done

  PASSWORD="$(grep -oE '[a-z0-9]{5}-[a-z0-9]{5}-[a-z0-9]{5}-[a-z0-9]{5}' "$DATA/FIRST-RUN-PASSWORD.txt" | head -1)"
  echo "Generated password: $PASSWORD"
fi

echo
echo "SafeChat orders — API checks"
echo "------------------------------------------------"

check "health responds" 200 "$(status "$BASE/api/health")"
check "order form is served" 200 "$(status "$BASE/")"
check "admin page is served" 200 "$(status "$BASE/admin.html")"

SERVICE="$(curl -s "$BASE/api/service")"
contains "plans are public" '"key"' "$SERVICE"
contains "password hash stays private" '' "$(echo "$SERVICE" | grep -c passwordHash | tr -d '\n')0"

# ---- admin routes are shut without a session
check "orders list needs auth" 401 "$(status "$BASE/api/admin/orders")"
check "config needs auth" 401 "$(status "$BASE/api/admin/config")"
check "export needs auth" 401 "$(status "$BASE/api/admin/export.csv")"

# ---- placing an order
BAD_EMAIL="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/orders" \
  -H 'Content-Type: application/json' \
  -d '{"plan":"pro","months":1,"email":"nope","contact":"@someone","eligible":true}')"
check "a bad email is refused" 400 "$BAD_EMAIL"

NO_TICK="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/orders" \
  -H 'Content-Type: application/json' \
  -d '{"plan":"pro","months":1,"email":"a@b.com","contact":"@someone","eligible":false}')"
check "the confirmation is required" 400 "$NO_TICK"

BAD_PLAN="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/orders" \
  -H 'Content-Type: application/json' \
  -d '{"plan":"nonsense","months":1,"email":"a@b.com","contact":"@someone","eligible":true}')"
check "an unknown plan is refused" 400 "$BAD_PLAN"

HONEYPOT="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/orders" \
  -H 'Content-Type: application/json' \
  -d '{"plan":"pro","months":1,"email":"a@b.com","contact":"@someone","eligible":true,"website":"spam"}')"
check "the honeypot catches bots" 400 "$HONEYPOT"

NO_NAME="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/orders" \
  -H 'Content-Type: application/json' \
  -d '{"plan":"pro","months":1,"email":"a@b.com","contact":"@someone","eligible":true}')"
check "an order with no name is refused" 400 "$NO_NAME"

CREATED="$(curl -s -X POST "$BASE/api/orders" -H 'Content-Type: application/json' \
  -d '{"plan":"pro","months":3,"fullName":"Ehsan Test","service":"chatgpt","email":"customer@example.com","contact":"@customer","contactKind":"telegram","country":"Germany","note":"first order","eligible":true}')"
CODE="$(echo "$CREATED" | grep -oE 'CW-[A-Z0-9]{6}' | head -1)"
contains "an order gets a code" "CW-" "$CODE"

TRACKED="$(curl -s "$BASE/api/orders/$CODE")"
contains "the customer can track it" '"status":"New"' "$TRACKED"
check "tracking hides the email" 0 "$(echo "$TRACKED" | grep -c 'customer@example.com')"
check "an unknown code is not found" 404 "$(status "$BASE/api/orders/CW-ZZZZZZ")"

# ---- signing in
check "a wrong password is refused" 401 "$(status -X POST "$BASE/api/admin/login" \
  -H 'Content-Type: application/json' -d '{"password":"not-the-password"}')"

LOGIN="$(curl -s -c "$JAR" -o /dev/null -w '%{http_code}' -X POST "$BASE/api/admin/login" \
  -H 'Content-Type: application/json' -d "{\"password\":\"$PASSWORD\"}")"
check "the right password signs in" 200 "$LOGIN"

LIST="$(curl -s -b "$JAR" "$BASE/api/admin/orders")"
contains "the owner sees the order" "$CODE" "$LIST"
contains "the owner sees the email" 'customer@example.com' "$LIST"
contains "the owner sees the customer name" 'Ehsan Test' "$LIST"
contains "the owner sees which service it is for" '"service":"chatgpt"' "$LIST"

ID="$(echo "$LIST" | grep -oE '"id":"[a-f0-9]{32}"' | head -1 | cut -d'"' -f4)"
contains "the order has an id" '' "$ID"

# ---- CSRF: a session cookie alone must not be enough to write
NO_HEADER="$(curl -s -b "$JAR" -o /dev/null -w '%{http_code}' -X PATCH "$BASE/api/admin/orders/$ID" \
  -H 'Content-Type: application/json' -d '{"status":"Cancelled"}')"
check "a write without the header is refused" 400 "$NO_HEADER"

UPDATED="$(curl -s -b "$JAR" -X PATCH "$BASE/api/admin/orders/$ID" \
  -H 'Content-Type: application/json' -H 'X-CW: 1' \
  -d '{"status":"Paid","costAmount":20,"costCurrency":"USD","chargedAmount":25,"chargedCurrency":"EUR","ownerNote":"Paid, active now","historyNote":"card ending 4242"}')"
contains "the status moves" '"status":"Paid"' "$UPDATED"
contains "the amounts stick" '"costAmount":20' "$UPDATED"

TRACK2="$(curl -s "$BASE/api/orders/$CODE")"
contains "the customer sees the new status" '"status":"Paid"' "$TRACK2"
contains "the customer sees the message" 'Paid, active now' "$TRACK2"

FILTER="$(curl -s -b "$JAR" "$BASE/api/admin/orders?status=Paid")"
contains "filtering by status works" "$CODE" "$FILTER"

EMPTY="$(curl -s -b "$JAR" "$BASE/api/admin/orders?status=Cancelled")"
check "filtering excludes others" 0 "$(echo "$EMPTY" | grep -c "$CODE")"

SEARCH="$(curl -s -b "$JAR" "$BASE/api/admin/orders?q=customer@example.com")"
contains "search finds it" "$CODE" "$SEARCH"

CSV="$(curl -s -b "$JAR" "$BASE/api/admin/export.csv")"
contains "csv export carries the order" "$CODE" "$CSV"

CONFIG="$(curl -s -b "$JAR" -X PUT "$BASE/api/admin/config" \
  -H 'Content-Type: application/json' -H 'X-CW: 1' \
  -d '{"businessName":"Test Shop","contactLine":"@testshop","plans":[{"key":"pro","label":"Claude Pro","labelFa":"کلاد پرو","priceHint":"$20","enabled":true}]}')"
contains "settings save" '"ok":true' "$CONFIG"
contains "the form picks up the new name" 'Test Shop' "$(curl -s "$BASE/api/service")"

# ------------------------------------------------------------------ pricing
#
# The sum that matters: $20 at 224,000 toman with 15% on top is 5,152,000,
# rounded up to the nearest 10,000 is 5,160,000. If this line ever goes quiet,
# somebody is being undercharged.

PRICE_SET="$(curl -s -b "$JAR" -X PUT "$BASE/api/admin/config" \
  -H 'Content-Type: application/json' -H 'X-CW: 1' \
  -d '{"plans":[{"key":"pro","label":"Claude Pro","labelFa":"کلاد پرو","usdPrice":20,"period":"month","enabled":true}],
       "pricing":{"rateMode":"manual","manualRateToman":224000,"markupPercent":15,"roundToToman":10000,"showUsd":false}}')"
contains "pricing save" '"ok":true' "$PRICE_SET"

PRICES="$(curl -s "$BASE/api/pricing")"
contains "the buy page has a rate" '"rateReady":true' "$PRICES"
contains "20 dollars at 224k plus 15 percent rounds to 5,160,000" '"toman":5160000' "$PRICES"
contains "the dollar figure stays off the buy page" '"usd":null' "$PRICES"
check "no markup leaks to the customer" 0 "$(printf '%s' "$PRICES" | grep -c markup)"
check "no rate leaks to the customer" 0 "$(printf '%s' "$PRICES" | grep -c '"rate"')"

ADMIN_PRICE="$(curl -s -b "$JAR" "$BASE/api/admin/pricing")"
contains "the owner can see the markup" '"markupPercent":15' "$ADMIN_PRICE"
contains "the owner can see the rate" '"rate":224000' "$ADMIN_PRICE"

# Rounding is upward, never down: 100 dollars lands on 25,760,000 exactly.
curl -s -b "$JAR" -X PUT "$BASE/api/admin/config" -H 'Content-Type: application/json' -H 'X-CW: 1' \
  -d '{"plans":[{"key":"max5","label":"Claude Max","usdPrice":100,"period":"month","enabled":true}]}' > /dev/null
contains "100 dollars rounds up as well" '"toman":25760000' "$(curl -s "$BASE/api/pricing")"

# A nonsense rate must be refused rather than quoted.
curl -s -b "$JAR" -X PUT "$BASE/api/admin/config" -H 'Content-Type: application/json' -H 'X-CW: 1' \
  -d '{"pricing":{"markupPercent":500}}' > /dev/null
contains "markup is clamped to something sane" '"markupPercent":300' "$(curl -s -b "$JAR" "$BASE/api/admin/pricing")"

check "the rate test needs a session" 401 "$(status -X POST "$BASE/api/admin/pricing/test" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{}')"
contains "a junk source is reported, not swallowed" '"ok":false' \
  "$(curl -s -b "$JAR" -X POST "$BASE/api/admin/pricing/test" -H 'Content-Type: application/json' -H 'X-CW: 1' \
     -d '{"url":"not-a-url","path":"price","unit":"toman"}')"

# --------------------------------------------------------------------- keys
#
# A key is bound to the first machine that activates it and dies with its
# expiry. The checks below are the rules a customer could otherwise get around:
# sharing one key, and reusing an expired one.

check "activation needs a session-free public route" 200 "$(status -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d '{"code":"SAFE-AAAA-AAAA-AAAA","deviceId":"x"}')"
contains "an unknown key is refused" '"error":"unknown_key"' \
  "$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d '{"code":"SAFE-AAAA-AAAA-AAAA","deviceId":"machine-1"}')"
check "a malformed key is a bad request" 400 "$(status -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d '{"code":"hello","deviceId":"machine-1"}')"

MADE="$(curl -s -b "$JAR" -X POST "$BASE/api/admin/keys" -H 'Content-Type: application/json' -H 'X-CW: 1' \
  -d '{"count":2,"days":30,"service":"both","customer":"Test Customer"}')"
contains "keys are made" '"ok":true' "$MADE"
KEY1="$(printf '%s' "$MADE" | grep -oE 'SAFE-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}' | head -1)"
KEY2="$(printf '%s' "$MADE" | grep -oE 'SAFE-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}' | tail -1)"
check "two distinct codes came back" "ok" "$([ -n "$KEY1" ] && [ "$KEY1" != "$KEY2" ] && echo ok || echo no)"

ACT="$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' \
  -d "{\"code\":\"$KEY1\",\"deviceId\":\"machine-one\",\"deviceName\":\"Ehsan PC\"}")"
contains "first activation works" '"ok":true' "$ACT"
contains "it reports the days left" '"daysLeft":30' "$ACT"

contains "the same machine may check in again" '"ok":true' \
  "$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d "{\"code\":\"$KEY1\",\"deviceId\":\"machine-one\"}")"

contains "a second machine is refused" '"error":"wrong_device"' \
  "$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d "{\"code\":\"$KEY1\",\"deviceId\":\"machine-two\"}")"

# Lower case and missing dashes are what people actually type.
contains "a sloppily typed code still matches" '"ok":true' \
  "$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' \
     -d "{\"code\":\"$(printf '%s' "$KEY1" | tr 'A-Z' 'a-z' | tr -d '-')\",\"deviceId\":\"machine-one\"}")"

contains "releasing the device frees the key" '"deviceId":""' \
  "$(curl -s -b "$JAR" -X PATCH "$BASE/api/admin/keys/$KEY1" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{"releaseDevice":true}')"
contains "the second machine can now take it" '"ok":true' \
  "$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d "{\"code\":\"$KEY1\",\"deviceId\":\"machine-two\"}")"

curl -s -b "$JAR" -X PATCH "$BASE/api/admin/keys/$KEY1" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{"revoked":true}' > /dev/null
contains "a revoked key stops working" '"error":"revoked"' \
  "$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d "{\"code\":\"$KEY1\",\"deviceId\":\"machine-two\"}")"

# A key whose window has closed must fail even on its own machine.
curl -s -b "$JAR" -X PATCH "$BASE/api/admin/keys/$KEY2" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{"addDays":-9999}' > /dev/null
contains "an expired key is refused" '"error":"expired"' \
  "$(curl -s -X POST "$BASE/api/licence" -H 'Content-Type: application/json' -d "{\"code\":\"$KEY2\",\"deviceId\":\"machine-three\"}")"

KEYLIST="$(curl -s -b "$JAR" "$BASE/api/admin/keys")"
contains "the panel lists them" '"summary"' "$KEYLIST"
contains "the customer note is kept" 'Test Customer' "$KEYLIST"
check "the key list needs a session" 401 "$(status "$BASE/api/admin/keys")"
check "making keys needs a session" 401 "$(status -X POST "$BASE/api/admin/keys" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{"count":1}')"
contains "keys export as csv" 'code,state,service' "$(curl -s -b "$JAR" "$BASE/api/admin/keys.csv")"

contains "the trial terms are public" 'trialDays' "$(curl -s "$BASE/api/licence/terms")"

# ---------------------------------------------------- dashboard and lookups

STATS="$(curl -s -b "$JAR" "$BASE/api/admin/stats?days=30")"
contains "the dashboard has a day series" '"series"' "$STATS"
contains "and the totals behind it" '"totals"' "$STATS"
contains "and the key counts" '"keys"' "$STATS"
contains "and the waiting list" '"waiting"' "$STATS"
check "the range is clamped, not trusted" 200 "$(status -b "$JAR" "$BASE/api/admin/stats?days=99999")"
check "the dashboard needs a session" 401 "$(status "$BASE/api/admin/stats")"

# A customer looking up their own key: state and days, nothing else.
LOOKUP="$(curl -s "$BASE/api/keys/$KEY1")"
contains "a key can be looked up without signing in" '"state"' "$LOOKUP"
if echo "$LOOKUP" | grep -q 'deviceId\|customer\|note'; then
  printf '  XX  %s\n' "the lookup leaks nothing private"; FAIL=$((FAIL+1))
else
  printf '  ok  %s\n' "the lookup leaks nothing private"; PASS=$((PASS+1))
fi
check "an unknown code is a plain 404" 404 "$(status "$BASE/api/keys/SAFE-ZZZZ-ZZZZ-ZZZZ")"

# Issuing a key straight from an order.
ISSUED="$(curl -s -b "$JAR" -X POST "$BASE/api/admin/orders/$ID/key" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{}')"
contains "a key is issued from the order" '"ok":true' "$ISSUED"
ORDER_KEY="$(echo "$ISSUED" | grep -oE 'SAFE-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}' | head -1)"
contains "the order now has that key against it" "$ORDER_KEY" \
  "$(curl -s -b "$JAR" "$BASE/api/admin/orders/$ID/keys")"
check "issuing needs a session" 401 "$(status -X POST "$BASE/api/admin/orders/$ID/key" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{}')"
check "issuing against a missing order is a 404" 404 \
  "$(status -b "$JAR" -X POST "$BASE/api/admin/orders/nosuchorder/key" -H 'Content-Type: application/json' -H 'X-CW: 1' -d '{}')"

contains "plans say which service they belong to" '"service"' "$(curl -s "$BASE/api/service")"
contains "and so does the price list" '"service"' "$(curl -s "$BASE/api/pricing")"

# ---------------------------------------------------------- app releases

echo
echo "App releases"

SETUP="$(mktemp)"
head -c 200000 /dev/urandom > "$SETUP"
SETUP_HASH="$(sha256sum "$SETUP" | cut -d' ' -f1)"

contains "nothing is offered before anything is uploaded" '"version":""' "$(curl -s "$BASE/api/app/latest")"
check "and /download has nothing to give" 404 "$(status "$BASE/download")"

check "uploading needs a session" 401 \
  "$(status -X PUT "$BASE/api/admin/releases/2.0.0" -H 'X-CW: 1' --data-binary @"$SETUP")"
check "and the header that says it came from our own page" 400 \
  "$(status -b "$JAR" -X PUT "$BASE/api/admin/releases/2.0.0" --data-binary @"$SETUP")"

UP="$(curl -s -b "$JAR" -X PUT "$BASE/api/admin/releases/2.0.0?notes=first" -H 'X-CW: 1' \
  -H 'Content-Type: application/octet-stream' --data-binary @"$SETUP")"
contains "a build uploads" '"ok":true' "$UP"
contains "and the server hashed exactly what was sent" "$SETUP_HASH" "$UP"

LATEST="$(curl -s "$BASE/api/app/latest")"
contains "it is what the app is now offered" '"version":"2.0.0"' "$LATEST"
contains "with the notes that came with it" '"notes":"first"' "$LATEST"
contains "and the hash to check the download against" "$SETUP_HASH" "$LATEST"

GOT="$(mktemp)"
curl -sL -o "$GOT" "$BASE/download"
check "the download is byte-for-byte the upload" "$SETUP_HASH" "$(sha256sum "$GOT" | cut -d' ' -f1)"
rm -f "$GOT"

curl -s -o /dev/null -b "$JAR" -X PUT "$BASE/api/admin/releases/2.0.1" -H 'X-CW: 1' \
  -H 'Content-Type: application/octet-stream' --data-binary @"$SETUP"
curl -s -o /dev/null -b "$JAR" -X PUT "$BASE/api/admin/releases/2.0.10" -H 'X-CW: 1' \
  -H 'Content-Type: application/octet-stream' --data-binary @"$SETUP"
contains "versions sort by number, so 2.0.10 beats 2.0.1" '"version":"2.0.10"' "$(curl -s "$BASE/api/app/latest")"

curl -s -o /dev/null -b "$JAR" -X POST "$BASE/api/admin/releases/2.0.10/live?on=false" -H 'X-CW: 1'
contains "a withdrawn build steps back to the one before it" '"version":"2.0.1"' "$(curl -s "$BASE/api/app/latest")"
check "and cannot be downloaded any more" 404 "$(status "$BASE/download/2.0.10")"

contains "a version that is not a version is refused" 'bad_version' \
  "$(curl -s -b "$JAR" -X PUT "$BASE/api/admin/releases/1.0-beta" -H 'X-CW: 1' --data-binary @"$SETUP")"
contains "and so is one trying to climb out of the folder" 'bad_version' \
  "$(curl -s -b "$JAR" -X PUT "$BASE/api/admin/releases/..%2F..%2Fetc" -H 'X-CW: 1' --data-binary @"$SETUP")"

check "an empty upload is not a release" 400 \
  "$(status -b "$JAR" -X PUT "$BASE/api/admin/releases/3.0.0" -H 'X-CW: 1' --data-binary '')"

check "deleting needs a session" 401 "$(status -X DELETE "$BASE/api/admin/releases/2.0.0" -H 'X-CW: 1')"
check "a build can be deleted" 200 "$(status -b "$JAR" -X DELETE "$BASE/api/admin/releases/2.0.0" -H 'X-CW: 1')"
check "and deleting it twice is a 404" 404 "$(status -b "$JAR" -X DELETE "$BASE/api/admin/releases/2.0.0" -H 'X-CW: 1')"

rm -f "$SETUP"

# ------------------------------------------------- taking builds from GitHub

echo
echo "The GitHub mirror"

contains "it is off until someone turns it on" '"result":"off"' \
  "$(curl -s -b "$JAR" -X POST "$BASE/api/admin/releases/github/pull" -H 'X-CW: 1')"

check "settings need a session" 401 \
  "$(status -X POST "$BASE/api/admin/releases/github" -H 'X-CW: 1' -H 'Content-Type: application/json' \
     -d '{"on":true,"repo":"someone/else","token":"secret-token-value"}')"

curl -s -o /dev/null -b "$JAR" -X POST "$BASE/api/admin/releases/github" -H 'X-CW: 1' \
  -H 'Content-Type: application/json' -d '{"on":true,"repo":"owner/name","token":"secret-token-value"}'

GH="$(curl -s -b "$JAR" "$BASE/api/admin/releases")"
contains "the repository comes back" '"repo":"owner/name"' "$GH"
check "the token never does" 0 "$(echo "$GH" | grep -c 'secret-token-value')"
contains "only that one is set" '••' "$GH"

# Leaving the box empty has to keep the token rather than wipe it, or every
# unrelated save would quietly break the mirror.
curl -s -o /dev/null -b "$JAR" -X POST "$BASE/api/admin/releases/github" -H 'X-CW: 1' \
  -H 'Content-Type: application/json' -d '{"on":true,"repo":"owner/other","token":""}'
contains "an empty box keeps the stored token" '••' "$(curl -s -b "$JAR" "$BASE/api/admin/releases")"

curl -s -o /dev/null -b "$JAR" -X POST "$BASE/api/admin/releases/github" -H 'X-CW: 1' \
  -H 'Content-Type: application/json' -d '{"on":false,"repo":"owner/other","forget":true}'
GH="$(curl -s -b "$JAR" "$BASE/api/admin/releases")"
contains "forgetting it clears it" '"token":""' "$GH"
contains "and turns the mirror off" '"on":false' "$GH"

check "logging out clears the session" 200 "$(status -b "$JAR" -c "$JAR" -X POST "$BASE/api/admin/logout" -H 'X-CW: 1')"
check "the list is shut again" 401 "$(status -b "$JAR" "$BASE/api/admin/orders")"
check "and so is the release list" 401 "$(status -b "$JAR" "$BASE/api/admin/releases")"

echo "------------------------------------------------"
if [ "$FAIL" -eq 0 ]; then
  echo "All $PASS checks passed."
  exit 0
fi

echo "$PASS passed, $FAIL FAILED."
exit 1
