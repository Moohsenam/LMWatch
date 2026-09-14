#!/usr/bin/env bash
# Brings the orders service up on a bare Linux server, start to finish.
#
#   export GH_TOKEN=github_pat_...       # needs Contents: read on the repo
#   sudo -E bash bootstrap.sh
#
# It sets up two names over HTTPS unless you say otherwise:
#
#   app.safechat.ir   the service — order form, key lookup, API, admin panel
#   safechat.ir       the landing page, served as plain files from a folder
#
# The root is deliberately not the service. It is a folder you copy a site into
# whenever the site exists; until then it holds a placeholder.
#
#   export DOMAIN=orders.example.com     # a different name for the service
#   export DOMAIN=                       # none at all: plain HTTP on the port
#   export SITE_DOMAIN=                  # skip the landing page entirely
#   export SITE_ROOT=/var/www/safechat   # where the landing page's files live
#   export CW_PORT=5080                  # the port the service itself listens on
#   export CERT_EMAIL=you@example.com    # where the certificate authority writes
#
# A server that already has sites on 443 keeps them: nginx, Apache and Caddy are
# each added to rather than replaced, and these become two more sites.
#
# Safe to run again: it pulls, rebuilds, and restarts without touching the data,
# and it never overwrites a landing page that is already there.
set -euo pipefail

REPO="${REPO:-Moohsenam/LMWatch}"
DOMAIN="${DOMAIN-app.safechat.ir}"
SITE_DOMAIN="${SITE_DOMAIN-safechat.ir}"
SITE_ROOT="${SITE_ROOT:-/var/www/safechat}"
GH_TOKEN="${GH_TOKEN:-}"
PORT="${CW_PORT:-5080}"
CERT_EMAIL="${CERT_EMAIL:-}"

REPO_DIR=/srv/claude-watch
APP_DIR=/opt/cw-orders
DATA_DIR=/var/lib/cw-orders
CONF_FILE=/etc/cw-orders.conf

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

# Behind a web server it only needs to answer that web server, so it stays on
# the loopback where nothing outside the machine can reach it directly.
if [ -n "$DOMAIN" ]; then BIND="http://127.0.0.1:$PORT"; else BIND="http://0.0.0.0:$PORT"; fi

# The port lives in one file. Change it here, restart, and the service, the
# health check and the update timer all follow.
say "Settings file ($CONF_FILE), port $PORT"
cat > "$CONF_FILE" <<CONF
# SafeChat orders. Edit, then:  systemctl restart cw-orders
CW_DATA=$DATA_DIR
CW_PORT=$PORT
CW_URLS=$BIND
DOTNET_EnableDiagnostics=0
CONF
chmod 644 "$CONF_FILE"

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
EnvironmentFile=$CONF_FILE
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

# Same file the service reads, so the health check below is never on the wrong
# port after someone moves it.
CW_PORT=5080
# shellcheck disable=SC1091
[ -f /etc/cw-orders.conf ] && . /etc/cw-orders.conf
PORT="${CW_PORT:-5080}"

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
if ! curl -fs --max-time 5 "http://127.0.0.1:$PORT/api/health" > /dev/null; then
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

IP="$(curl -s -4 --max-time 5 ifconfig.me 2>/dev/null || hostname -I | awk '{print $1}')"

# Whoever already answers on 443 keeps 443. This adds sites to it.
web_server() {
  if   systemctl is-active --quiet nginx   2>/dev/null; then echo nginx
  elif systemctl is-active --quiet apache2 2>/dev/null; then echo apache
  elif systemctl is-active --quiet httpd   2>/dev/null; then echo apache
  elif systemctl is-active --quiet caddy   2>/dev/null; then echo caddy
  elif ss -ltn 2>/dev/null | grep -qE ':443[[:space:]]'; then echo other
  else echo none
  fi
}

certbot_ready() {
  command -v certbot >/dev/null 2>&1 && return 0
  say "Installing certbot"
  if [ "$PM" = "apt" ]; then
    apt-get install -y -qq certbot "python3-certbot-$1" >/dev/null 2>&1 || return 1
  else
    $PM install -y -q certbot "python3-certbot-$1" >/dev/null 2>&1 || return 1
  fi
  command -v certbot >/dev/null 2>&1
}

# Only the names that actually point here. One that does not resolve would fail
# the whole request and take the others down with it.
cert_names() {
  local list="" address
  for name in "$@"; do
    [ -n "$name" ] || continue
    address="$(getent hosts "$name" 2>/dev/null | awk '{print $1}' | head -1)"
    if [ -n "$address" ]; then
      list="$list -d $name"
    else
      echo "  ! $name does not resolve, so it is left out of the certificate." >&2
    fi
  done
  echo "$list"
}

get_cert() {
  local plugin="$1"; shift
  local names; names="$(cert_names "$@")"

  if [ -z "$names" ]; then
    echo "  ! No name here points at $IP yet, so no certificate was requested."
    echo "    Fix the A records and run this again."
    return 0
  fi

  if ! certbot_ready "$plugin"; then
    echo "  ! certbot is not installed, so these are on plain HTTP."
    echo "    Then run:  certbot --$plugin$names"
    return 0
  fi

  local mail="--register-unsafely-without-email"
  [ -n "$CERT_EMAIL" ] && mail="-m $CERT_EMAIL"

  # shellcheck disable=SC2086
  certbot "--$plugin" $names --non-interactive --agree-tos --redirect $mail \
    || echo "  ! certbot did not finish. Plain HTTP for now; try:  certbot --$plugin$names"
}

# ------------------------------------------- the landing page's own folder

# safechat.ir is the page people arrive at, so the root is a folder to drop
# files into rather than anything this script owns. It writes a holding page
# once and never touches it again, so the real site replaces it by copying
# over it.
if [ -n "$SITE_DOMAIN" ]; then
  mkdir -p "$SITE_ROOT"

  if [ ! -e "$SITE_ROOT/index.html" ]; then
    say "Holding page in $SITE_ROOT"
    cat > "$SITE_ROOT/index.html" <<HTML
<!doctype html>
<html lang="fa" dir="rtl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>سیف‌چت</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh;
    display: grid; place-items: center; padding: 24px;
    background: radial-gradient(120% 90% at 50% 0%, #241a16 0%, #14110f 55%, #0f0d0c 100%);
    color: #efe9e5;
    font-family: Vazirmatn, "Segoe UI", Tahoma, system-ui, sans-serif;
  }
  main { width: 100%; max-width: 520px; text-align: center; }
  .mark {
    width: 58px; height: 58px; margin: 0 auto 26px;
    border-radius: 17px; display: grid; place-items: center;
    background: linear-gradient(150deg, #D97757, #b85c3f);
    box-shadow: 0 14px 40px rgba(217, 119, 87, .28);
    font-size: 27px; font-weight: 700; color: #fff;
  }
  h1 { margin: 0; font-size: 34px; font-weight: 800; color: #D97757; letter-spacing: -.5px; }
  p  { margin: 16px auto 0; max-width: 420px; font-size: 15.5px; line-height: 2.1; color: #b6aca6; }
  .soon { margin-top: 30px; font-size: 13px; color: #7c726d; }
  a.cta {
    display: inline-block; margin-top: 30px; padding: 13px 30px;
    border-radius: 11px; text-decoration: none; font-size: 15px; font-weight: 700;
    color: #fff; background: #D97757;
    box-shadow: 0 10px 28px rgba(217, 119, 87, .26);
    transition: transform .16s ease, box-shadow .16s ease;
  }
  a.cta:hover { transform: translateY(-2px); box-shadow: 0 14px 34px rgba(217, 119, 87, .34); }
</style>
</head>
<body>
  <main>
    <div class="mark">S</div>
    <h1>سیف‌چت</h1>
    <p>
      نگهبان ویندوز برای کلاد و چت جی‌پی‌تی. تا وقتی تونل وصل است و ساعت سیستم
      روی منطقه کاری شماست برنامه باز می‌ماند، و لحظه‌ای که تونل بیفتد بسته می‌شود.
    </p>
    <a class="cta" href="https://$DOMAIN">ثبت سفارش و خرید اشتراک</a>
    <div class="soon">صفحه معرفی کامل به‌زودی همین‌جا</div>
  </main>
</body>
</html>
HTML
  fi

  chmod 755 "$SITE_ROOT"
  chmod 644 "$SITE_ROOT"/*.html 2>/dev/null || true
fi

# --------------------------------------------------- the two sites themselves

if [ -z "$DOMAIN" ]; then
  URL="http://$IP:$PORT"
  WEB=none
else
  WEB="$(web_server)"
  say "HTTPS (port 443 is held by: $WEB)"
  echo "  $DOMAIN       -> the service on 127.0.0.1:$PORT"
  [ -n "$SITE_DOMAIN" ] && echo "  $SITE_DOMAIN           -> files in $SITE_ROOT"

  URL="https://$DOMAIN"

  case "$WEB" in

    nginx)
      if [ -d /etc/nginx/sites-available ]; then
        SITE=/etc/nginx/sites-available/safechat
        ln -sf "$SITE" /etc/nginx/sites-enabled/safechat
      else
        SITE=/etc/nginx/conf.d/safechat.conf
      fi

      # Plain HTTP first. certbot reads these blocks, adds the 443 ones beside
      # them and leaves every other site on the box alone.
      cat > "$SITE" <<NGINX
server {
    listen 80;
    listen [::]:80;
    server_name $DOMAIN;

    location / {
        proxy_pass http://127.0.0.1:$PORT;
        proxy_set_header Host              \$host;
        proxy_set_header X-Real-IP         \$remote_addr;
        proxy_set_header X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }
}
NGINX

      if [ -n "$SITE_DOMAIN" ]; then
        cat >> "$SITE" <<NGINX

server {
    listen 80;
    listen [::]:80;
    server_name $SITE_DOMAIN www.$SITE_DOMAIN;

    root $SITE_ROOT;
    index index.html;

    location / {
        try_files \$uri \$uri/ /index.html;
    }
}
NGINX
      fi

      if nginx -t; then
        systemctl reload nginx
      else
        echo "  ! nginx refused the config, so nothing was reloaded. Check:  nginx -t" >&2
      fi

      get_cert nginx "$DOMAIN" ${SITE_DOMAIN:+"$SITE_DOMAIN" "www.$SITE_DOMAIN"}
      ;;

    apache)
      if [ -d /etc/apache2/sites-available ]; then
        SITE=/etc/apache2/sites-available/safechat.conf
        a2enmod proxy proxy_http ssl rewrite >/dev/null 2>&1 || true
      else
        SITE=/etc/httpd/conf.d/safechat.conf
      fi

      cat > "$SITE" <<APACHE
<VirtualHost *:80>
    ServerName $DOMAIN
    ProxyPreserveHost On
    ProxyPass        / http://127.0.0.1:$PORT/
    ProxyPassReverse / http://127.0.0.1:$PORT/
</VirtualHost>
APACHE

      if [ -n "$SITE_DOMAIN" ]; then
        cat >> "$SITE" <<APACHE

<VirtualHost *:80>
    ServerName $SITE_DOMAIN
    ServerAlias www.$SITE_DOMAIN
    DocumentRoot $SITE_ROOT

    <Directory $SITE_ROOT>
        Options -Indexes +FollowSymLinks
        AllowOverride All
        Require all granted
    </Directory>
</VirtualHost>
APACHE
      fi

      if [ -d /etc/apache2/sites-available ]; then a2ensite safechat >/dev/null 2>&1 || true; fi
      systemctl reload apache2 2>/dev/null || systemctl reload httpd 2>/dev/null || true

      get_cert apache "$DOMAIN" ${SITE_DOMAIN:+"$SITE_DOMAIN" "www.$SITE_DOMAIN"}
      ;;

    caddy|none)
      if [ "$WEB" = "none" ] && ! command -v caddy >/dev/null 2>&1; then
        say "Installing Caddy"
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

      mkdir -p /etc/caddy /etc/caddy/sites
      touch /etc/caddy/Caddyfile

      # Caddy already running means other sites are in that file. These two go
      # in their own file and the main one only gets an import line, so nothing
      # already there is rewritten and a later run of this script is a no-op.
      printf '%s {\n    encode gzip\n    reverse_proxy 127.0.0.1:%s\n}\n' "$DOMAIN" "$PORT" \
        > /etc/caddy/sites/safechat.caddyfile

      if [ -n "$SITE_DOMAIN" ]; then
        # Caddy fetches a certificate for every name in the block, so a www that
        # nobody pointed anywhere would only produce failures in its log.
        NAMES="$SITE_DOMAIN"
        if getent hosts "www.$SITE_DOMAIN" >/dev/null 2>&1; then
          NAMES="$SITE_DOMAIN, www.$SITE_DOMAIN"
        fi

        printf '\n%s {\n    encode gzip\n    root * %s\n    file_server\n    try_files {path} {path}/ /index.html\n}\n' \
          "$NAMES" "$SITE_ROOT" >> /etc/caddy/sites/safechat.caddyfile
        chown -R caddy:caddy "$SITE_ROOT" 2>/dev/null || true
      fi

      if ! grep -q 'import /etc/caddy/sites/\*' /etc/caddy/Caddyfile; then
        printf '\nimport /etc/caddy/sites/*.caddyfile\n' >> /etc/caddy/Caddyfile
      fi

      caddy validate --config /etc/caddy/Caddyfile >/dev/null 2>&1 \
        || echo "  ! Caddy did not like the config. Check:  caddy validate --config /etc/caddy/Caddyfile"
      systemctl enable caddy >/dev/null 2>&1 || true
      systemctl reload caddy 2>/dev/null || systemctl restart caddy
      ;;

    other)
      echo "  ! Something is on 443 that is not nginx, Apache or Caddy, so nothing"
      echo "    was changed. Add these by hand — one proxy and one static folder:"
      echo
      echo "        $DOMAIN  ->  http://127.0.0.1:$PORT"
      [ -n "$SITE_DOMAIN" ] && echo "        $SITE_DOMAIN      ->  $SITE_ROOT"
      echo
      URL="http://$IP:$PORT"
      ;;
  esac
fi

# ------------------------------------------------------------- 8. report

say "Result"
systemctl is-active cw-orders || true
echo "health: $(curl -s --max-time 5 "http://127.0.0.1:$PORT/api/health" || echo 'no answer')"
echo
echo "  Order form:   $URL"
echo "  Admin panel:  $URL/admin.html"
if [ -n "$SITE_DOMAIN" ] && [ -n "$DOMAIN" ]; then
echo "  Landing page: https://$SITE_DOMAIN  (files in $SITE_ROOT)"
echo "                copy the real site over the placeholder when it exists;"
echo "                this script never touches that folder again."
fi
echo "  Updates:      automatic, within two minutes of a push"
echo "                watch them with:  journalctl -u cw-update -f"
echo "  First password:"
cat "$DATA_DIR/FIRST-RUN-PASSWORD.txt" 2>/dev/null | sed 's/^/    /' || echo "    see $DATA_DIR/FIRST-RUN-PASSWORD.txt"
echo
if [ -z "$DOMAIN" ]; then
  echo "  No domain given, so this is plain HTTP. Fine for testing."
  echo "  Change the password once HTTPS is on, since it crosses the network in the clear."
fi
