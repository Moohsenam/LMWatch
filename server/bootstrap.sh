#!/usr/bin/env bash
# Brings the orders service up on a bare Linux server, start to finish.
#
#   export GH_TOKEN=github_pat_...       # needs Contents: read on the repo
#   export DOMAIN=orders.example.com     # optional. Empty = plain HTTP on :5080
#   sudo -E bash bootstrap.sh
#
# Safe to run again: it pulls, rebuilds, and restarts without touching the data.
set -euo pipefail

REPO="${REPO:-Moohsenam/LMWatch}"
DOMAIN="${DOMAIN:-}"
GH_TOKEN="${GH_TOKEN:-}"

REPO_DIR=/srv/claude-watch
APP_DIR=/opt/cw-orders
DATA_DIR=/var/lib/cw-orders

say() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

if [ "$(id -u)" != "0" ]; then
  echo "Run this with sudo -E so the environment variables carry through." >&2
  exit 1
fi

# ------------------------------------------------------------ 1. packages

if   command -v apt-get >/dev/null 2>&1; then PM=apt
elif command -v dnf     >/dev/null 2>&1; then PM=dnf
elif command -v yum     >/dev/null 2>&1; then PM=yum
else echo "No apt, dnf or yum here. Tell me what this server runs." >&2; exit 1
fi

say "Installing git and curl ($PM)"
if [ "$PM" = "apt" ]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq git curl ca-certificates gnupg
else
  $PM install -y -q git curl ca-certificates gnupg2 || $PM install -y -q git curl ca-certificates gnupg
fi

# --------------------------------------------------------- 2. .NET 8 SDK

# The SDK, not just the runtime: the server builds what it pulls.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
  say "Installing the .NET 8 SDK"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
fi
say "SDK: $(dotnet --version)"

# ------------------------------------------------------------- 3. source

say "Fetching the code"
if [ -d "$REPO_DIR/.git" ]; then
  # The token was stored on the first run, so re-running this needs nothing.
  git -C "$REPO_DIR" pull --ff-only
else
  if [ -z "$GH_TOKEN" ]; then
    echo "First run on this machine, so it needs the token:" >&2
    echo "  export GH_TOKEN=github_pat_...  &&  sudo -E bash bootstrap.sh" >&2
    exit 1
  fi

  git clone --quiet "https://x-access-token:$GH_TOKEN@github.com/$REPO.git" "$REPO_DIR"
  # The token does not stay in the repo config. It goes in a root-only file.
  git -C "$REPO_DIR" remote set-url origin "https://github.com/$REPO.git"
  printf 'https://x-access-token:%s@github.com\n' "$GH_TOKEN" > /root/.git-credentials
  chmod 600 /root/.git-credentials
  git -C "$REPO_DIR" config credential.helper 'store --file=/root/.git-credentials'
fi

# -------------------------------------------------- 4. user and folders

say "Service user and folders"
id -u cworders >/dev/null 2>&1 || useradd --system --home "$APP_DIR" --shell /usr/sbin/nologin cworders
mkdir -p "$APP_DIR" "$DATA_DIR"
chown -R cworders:cworders "$DATA_DIR"

# -------------------------------------------------------------- 5. build

say "Building"
rm -rf "$APP_DIR.new"
dotnet publish "$REPO_DIR/server/ClaudeWatch.Orders.csproj" -c Release -o "$APP_DIR.new" --nologo -v quiet

if [ ! -f "$APP_DIR.new/ClaudeWatch.Orders.dll" ]; then
  echo "The build produced nothing. Nothing was changed." >&2
  exit 1
fi

systemctl stop cw-orders 2>/dev/null || true
rm -rf "$APP_DIR.old"
if [ -d "$APP_DIR" ]; then mv "$APP_DIR" "$APP_DIR.old"; fi
mv "$APP_DIR.new" "$APP_DIR"
chown -R cworders:cworders "$APP_DIR"

# ------------------------------------------------------------ 6. service

if [ -n "$DOMAIN" ]; then BIND="http://127.0.0.1:5080"; else BIND="http://0.0.0.0:5080"; fi

say "systemd service"
cat > /etc/systemd/system/cw-orders.service <<UNIT
[Unit]
Description=SafeChat orders
After=network.target

[Service]
Type=simple
User=cworders
WorkingDirectory=$APP_DIR
ExecStart=/usr/bin/dotnet $APP_DIR/ClaudeWatch.Orders.dll
Environment=CW_DATA=$DATA_DIR
Environment=CW_URLS=$BIND
Environment=DOTNET_EnableDiagnostics=0
Restart=always
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now cw-orders
sleep 5

# --------------------------------------------------- 6b. hands-off updates

# From here on a push is the whole deployment. This checks every two minutes,
# and on a quiet repo it is one `git fetch` and nothing else.
say "Auto-update every two minutes"

cat > /usr/local/bin/cw-update <<'UPDATE'
#!/usr/bin/env bash
# Pulls, and rebuilds only when the commit actually moved.
set -euo pipefail

REPO_DIR=/srv/claude-watch
APP_DIR=/opt/cw-orders

before="$(git -C "$REPO_DIR" rev-parse HEAD)"
git -C "$REPO_DIR" fetch --quiet origin main
after="$(git -C "$REPO_DIR" rev-parse origin/main)"

if [ "$before" = "$after" ]; then
  exit 0
fi

git -C "$REPO_DIR" merge --ff-only --quiet origin/main

echo "cw-update: ${before:0:7} -> ${after:0:7}, building"

rm -rf "$APP_DIR.new"
dotnet publish "$REPO_DIR/server/ClaudeWatch.Orders.csproj" -c Release -o "$APP_DIR.new" --nologo -v quiet

if [ ! -f "$APP_DIR.new/ClaudeWatch.Orders.dll" ]; then
  echo "cw-update: the build produced nothing, leaving the running version alone" >&2
  rm -rf "$APP_DIR.new"
  exit 1
fi

systemctl stop cw-orders
rm -rf "$APP_DIR.old"
mv "$APP_DIR" "$APP_DIR.old"
mv "$APP_DIR.new" "$APP_DIR"
chown -R cworders:cworders "$APP_DIR"
systemctl start cw-orders

# A push that does not answer is put back, rather than left down until someone
# notices. Ten seconds is long enough for the service to be up or not.
sleep 10
if ! curl -fs --max-time 5 http://127.0.0.1:5080/api/health > /dev/null; then
  echo "cw-update: the new build does not answer, rolling back" >&2
  systemctl stop cw-orders
  rm -rf "$APP_DIR.bad"
  mv "$APP_DIR" "$APP_DIR.bad"
  mv "$APP_DIR.old" "$APP_DIR"
  systemctl start cw-orders
  exit 1
fi

echo "cw-update: now on ${after:0:7}"
UPDATE

chmod 755 /usr/local/bin/cw-update

cat > /etc/systemd/system/cw-update.service <<'UNIT'
[Unit]
Description=SafeChat orders — pull and rebuild if the repo moved
After=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/cw-update
UNIT

cat > /etc/systemd/system/cw-update.timer <<'UNIT'
[Unit]
Description=Check the repo for new commits

[Timer]
OnBootSec=2min
OnUnitActiveSec=2min
AccuracySec=30s

[Install]
WantedBy=timers.target
UNIT

systemctl daemon-reload
systemctl enable --now cw-update.timer

# -------------------------------------------------------------- 7. HTTPS

if [ -n "$DOMAIN" ]; then
  say "Caddy and a certificate for $DOMAIN"
  if ! command -v caddy >/dev/null 2>&1; then
    if [ "$PM" = "apt" ]; then
      curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
        | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
      curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
        > /etc/apt/sources.list.d/caddy-stable.list
      apt-get update -qq && apt-get install -y -qq caddy
    else
      $PM install -y -q caddy
    fi
  fi
  printf '%s {\n    encode gzip\n    reverse_proxy 127.0.0.1:5080\n}\n' "$DOMAIN" > /etc/caddy/Caddyfile
  systemctl reload caddy 2>/dev/null || systemctl restart caddy
  URL="https://$DOMAIN"
else
  IP="$(curl -s -4 --max-time 5 ifconfig.me 2>/dev/null || hostname -I | awk '{print $1}')"
  URL="http://$IP:5080"
fi

# ------------------------------------------------------------- 8. report

say "Result"
systemctl is-active cw-orders || true
echo "health: $(curl -s --max-time 5 http://127.0.0.1:5080/api/health || echo 'no answer')"
echo
echo "  Order form:  $URL"
echo "  Admin panel: $URL/admin.html"
echo "  Updates:     automatic, within two minutes of a push"
echo "               watch them with:  journalctl -u cw-update -f"
echo "  First password:"
cat "$DATA_DIR/FIRST-RUN-PASSWORD.txt" 2>/dev/null | sed 's/^/    /' || echo "    see $DATA_DIR/FIRST-RUN-PASSWORD.txt"
echo
if [ -z "$DOMAIN" ]; then
  echo "  No domain given, so this is plain HTTP. Fine for testing."
  echo "  Change the password once HTTPS is on, since it crosses the network in the clear."
fi
