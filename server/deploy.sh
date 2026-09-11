#!/usr/bin/env bash
# Takes whatever is on the branch and puts it live.
#
#   ./server/deploy.sh          only rebuilds if the branch actually moved
#   ./server/deploy.sh --force  rebuilds regardless
#
# Safe to run on a timer: with nothing new it does a fetch and exits.
# The data folder is never touched — it lives outside the published folder.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="${CW_APP_DIR:-/opt/cw-orders}"
SERVICE="${CW_SERVICE:-cw-orders}"
FORCE="${1:-}"

cd "$REPO"

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

# ---------------------------------------------------------------- 1. fetch

BRANCH="$(git rev-parse --abbrev-ref HEAD)"
BEFORE="$(git rev-parse HEAD)"

git fetch --quiet origin "$BRANCH"
AFTER="$(git rev-parse "origin/$BRANCH")"

if [ "$BEFORE" = "$AFTER" ] && [ "$FORCE" != "--force" ]; then
  exit 0
fi

say "Updating $BEFORE -> $AFTER"

# --ff-only on purpose. If the server has local edits this stops rather than
# quietly throwing them away, and you find out now instead of later.
git merge --ff-only "origin/$BRANCH"

# ---------------------------------------------------------------- 2. build

say "Building"

STAGING="$APP_DIR.new"
rm -rf "$STAGING"

dotnet publish server/ClaudeWatch.Orders.csproj -c Release -o "$STAGING" --nologo -v quiet

if [ ! -f "$STAGING/ClaudeWatch.Orders.dll" ]; then
  echo "The build produced nothing. Nothing was changed; the old version is still running." >&2
  rm -rf "$STAGING"
  exit 1
fi

# ---------------------------------------------------------------- 3. swap

say "Swapping it in"

sudo systemctl stop "$SERVICE" || true

rm -rf "$APP_DIR.old"
[ -d "$APP_DIR" ] && mv "$APP_DIR" "$APP_DIR.old"
mv "$STAGING" "$APP_DIR"

sudo chown -R cworders:cworders "$APP_DIR" 2>/dev/null || true
sudo systemctl start "$SERVICE"

# ---------------------------------------------------------------- 4. check

sleep 3

PORT="${CW_PORT:-5080}"
CODE="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/api/health" || true)"

if [ "$CODE" = "200" ]; then
  say "Live. $(git log -1 --format='%h %s')"
  rm -rf "$APP_DIR.old"
  exit 0
fi

# ------------------------------------------------------------ 5. roll back

echo "The new build did not answer on /api/health (got ${CODE:-nothing}). Rolling back." >&2

sudo systemctl stop "$SERVICE" || true
rm -rf "$APP_DIR"
mv "$APP_DIR.old" "$APP_DIR"
sudo systemctl start "$SERVICE"

echo "The previous version is running again. Logs:  journalctl -u $SERVICE -n 50" >&2
exit 1
