# Putting the orders service on a server

One command stands it up, and after that a push is the whole deployment: the
server checks the repo every two minutes and rebuilds itself when the commit
moves. No uploading, no publishing by hand, no touching the server again.

## The short way (Linux)

On the server, as a user who can sudo:

```bash
export GH_TOKEN=github_pat_...          # needs Contents: read on the repo

curl -sSL -H "Authorization: token $GH_TOKEN" -H "Accept: application/vnd.github.raw" \
  https://api.github.com/repos/Moohsenam/LMWatch/contents/server/bootstrap.sh -o bootstrap.sh

sudo -E bash bootstrap.sh
```

It installs git, the .NET 8 SDK, clones the repo, builds, writes the systemd
service, installs the two-minute auto-update timer, and prints the admin
password it generated on the first run.

It sets up two names, not one:

| Name | What answers |
| --- | --- |
| **app.safechat.ir** | the service: order form, key lookup, the API the desktop app calls, `/admin.html` |
| **safechat.ir** | the landing page, served as plain files out of `/var/www/safechat` |

The root is deliberately not the service. It is a folder you copy a site into,
and until there is one it holds a placeholder that links to the order page.
Copying the real site over it is the whole deployment; the script writes that
file once and never touches the folder again.

Both names need an A record pointing at the server before you run it, or the
certificates cannot be issued. The script checks and says which one is missing
rather than letting certbot find out.

What it takes from the environment:

```bash
export DOMAIN=orders.example.com        # a different name for the service
export DOMAIN=                          # none at all: plain HTTP on the port
export SITE_DOMAIN=                     # skip the landing page entirely
export SITE_ROOT=/var/www/safechat      # where the landing page's files live
export CW_PORT=5080                     # the port the service itself listens on
export CERT_EMAIL=you@example.com       # expiry warnings from the certificate authority
```

### A server that already has sites on 443

It keeps them. The script looks at what is answering on 443 and adds its two
sites to it instead of replacing anything:

| Already there | What happens |
| --- | --- |
| nginx | both vhosts in one file, `sites-available/safechat` (or `conf.d/safechat.conf`), then `certbot --nginx` |
| Apache | both vhosts in `sites-available/safechat.conf` (or `conf.d/`), then `certbot --apache` |
| Caddy | `/etc/caddy/sites/safechat.caddyfile`, reached by one `import` line added to the existing Caddyfile |
| nothing | Caddy is installed and takes 443 |
| something else | nothing is touched; it prints the proxy rule and the folder to wire up by hand |

Your other two domains are untouched in every one of those rows. Nothing
already in the config is rewritten, and certbot only ever writes the blocks for
the names you gave it.

### Shipping a new version of the app

The server hosts the installer and tells every copy of the app what the current
build is. From the machine with the source on it:

```powershell
.\tools\release.ps1 -Version 1.1.0 -Notes "Faster startup"
```

That publishes, compiles the installer, attaches it to a `v1.1.0` release on
GitHub, uploads it here and makes it current. The panel's **نسخه‌ها** tab lists
every build with its size, hash and download count, and can switch one off or
delete it. `/download` is always whatever is current, which is the link the
landing page should use.

### Taking builds from GitHub instead

The same tab can watch a repository and bring in whatever is tagged there, so
`git tag` is the whole release and nothing has to be uploaded by hand. Fill in
the repository and a token with read access to it, and every five minutes the
server takes any release whose tag is newer than the one it already has, copies
the `.exe` attached to it, and starts serving that.

**The repository can stay private, and that is the point of doing it here.** The
token lives on this server and never leaves it: the app is never told where the
code is, customers download from `/download` like always, and the panel shows
the token as dots once it is stored. A token that shipped inside the app could
be read straight back out of it by anyone holding a copy, which is why the app
does not do this part itself.

The tag has to look like `v1.2.3`, and the installer has to be attached to the
release as a `.exe`. Anything else attached to it is left alone.

Three endpoints do the work:

| | |
| --- | --- |
| `GET /api/app/latest` | what the app asks: version, notes, URL, SHA-256 |
| `GET /download` | the current installer |
| `PUT /api/admin/releases/{version}` | the upload, admin only |

The installers live in `releases/` inside the data folder, so they are part of
the same backup as everything else. They are tens of megabytes each, so delete
the ones nobody is on any more.

### Putting the landing page up

`/var/www/safechat` is a plain folder of files. When the site exists, copy it in:

```bash
scp -r ./landing/* root@your-server:/var/www/safechat/
```

No restart, no reload, no config. The placeholder is `index.html`, so copying
your own `index.html` over it is what replaces it. The certificate is already
there from the first bootstrap run.

### Changing the port

One file, `/etc/cw-orders.conf`:

```
CW_DATA=/var/lib/cw-orders
CW_PORT=5080
CW_URLS=http://127.0.0.1:5080
```

```bash
sudo nano /etc/cw-orders.conf
sudo systemctl restart cw-orders
```

The service, the health check and the update timer all read that file, so
nothing else needs editing. The web server in front still points at the old
port, though — change `127.0.0.1:5080` in its config too, or just re-run
`bootstrap.sh` with `CW_PORT` set and it rewrites both.

**Later updates need nothing.** Push, wait two minutes. To watch it happen:

```bash
journalctl -u cw-update -f
```

To force one immediately:

```bash
sudo systemctl start cw-update
```

A build that fails leaves the running version alone, and a build that starts
but does not answer `/api/health` is rolled back within ten seconds.

Everything below is the long way, and the Docker route.

---

Two routes. Pick one.

- **Docker** — least to install, works on any machine that runs Docker,
  including Windows Server. Start at [Route A](#route-a--docker).
- **Plain Linux** — no Docker, runs as a normal systemd service. Slightly more
  setup, a little less overhead. Start at [Route B](#route-b--plain-linux).

Either way the data lives outside the application folder, so a rebuild can
never take orders or settings with it.

---

## First: let the server read the repo

The repo is private, so the server needs its own read-only key. On the server:

```bash
ssh-keygen -t ed25519 -C "claude-watch server" -f ~/.ssh/cw_deploy -N ""
cat ~/.ssh/cw_deploy.pub
```

Copy that public key into GitHub under the repo's
**Settings → Deploy keys → Add deploy key**. Leave *Allow write access*
unticked — the server only ever reads.

Then tell ssh to use it:

```bash
cat >> ~/.ssh/config <<'EOF'
Host github.com
  IdentityFile ~/.ssh/cw_deploy
  IdentitiesOnly yes
EOF
```

And clone:

```bash
sudo mkdir -p /srv && sudo chown "$USER" /srv
git clone git@github.com:YOURNAME/claude-watch.git /srv/claude-watch
cd /srv/claude-watch
```

---

## Route A — Docker

```bash
cd /srv/claude-watch/server
nano Caddyfile          # put your own domain in place of orders.example.com
docker compose up -d --build
```

That is the whole install. Caddy fetches an HTTPS certificate on its own and
renews it. Point your domain's DNS at this machine first, or the certificate
will not be issued.

To update by hand:

```bash
cd /srv/claude-watch && git pull && docker compose -f server/docker-compose.yml up -d --build
```

To update automatically, see [Hands-off updates](#hands-off-updates) and use
this line as the `ExecStart`:

```
/bin/sh -c 'cd /srv/claude-watch && git pull --ff-only && docker compose -f server/docker-compose.yml up -d --build'
```

**No domain yet?** Delete the `caddy` block from `docker-compose.yml`, add
`ports: ["5080:5080"]` to the `orders` block, and reach it at
`http://your-server-ip:5080`. Do that only for testing: without HTTPS the
admin password crosses the network in the clear.

---

## Route B — plain Linux

### 1. The .NET SDK

The server builds what it pulls, so it needs the SDK, not just the runtime.

```bash
sudo apt update
sudo apt install -y dotnet-sdk-8.0 caddy curl
```

If your distribution does not carry it:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | sudo bash -s -- --channel 8.0 --install-dir /usr/share/dotnet
sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
```

### 2. A user for it to run as

```bash
sudo useradd --system --home /opt/cw-orders --shell /usr/sbin/nologin cworders
sudo mkdir -p /opt/cw-orders /var/lib/cw-orders
sudo chown -R cworders:cworders /opt/cw-orders /var/lib/cw-orders
```

### 3. The service

```bash
sudo cp /srv/claude-watch/server/cw-orders.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable cw-orders
```

### 4. First deploy

```bash
cd /srv/claude-watch
./server/deploy.sh --force
```

`deploy.sh` builds into a staging folder, swaps it in, checks
`/api/health`, and **puts the old version back if the new one does not
answer**. A bad push takes the site down for about five seconds, not until you
notice.

### 5. HTTPS

If nothing is on 443 yet, `/etc/caddy/sites/safechat.caddyfile`:

```
app.safechat.ir {
    encode gzip
    reverse_proxy 127.0.0.1:5080
}

safechat.ir {
    encode gzip
    root * /var/www/safechat
    file_server
    try_files {path} {path}/ /index.html
}
```

and one line in `/etc/caddy/Caddyfile` so it is read:

```
import /etc/caddy/sites/*.caddyfile
```

```bash
sudo systemctl reload caddy
```

If nginx is already serving other domains, add one file instead,
`/etc/nginx/sites-available/safechat`:

```nginx
server {
    listen 80;
    server_name app.safechat.ir;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}

server {
    listen 80;
    server_name safechat.ir www.safechat.ir;

    root /var/www/safechat;
    index index.html;

    location / {
        try_files $uri $uri/ /index.html;
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/safechat /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d app.safechat.ir -d safechat.ir
```

Certbot adds the 443 blocks to that one file and leaves your other sites alone.

The service itself listens on `127.0.0.1` only, so nothing reaches it except
through the web server in front.

---

## Hands-off updates

This is the part that matters. A timer checks the repo every two minutes; when
nothing has changed it is one `git fetch` and nothing else.

```bash
sudo useradd --system --home /srv/claude-watch --shell /bin/bash deploy
sudo chown -R deploy:deploy /srv/claude-watch
sudo cp /srv/claude-watch/server/cw-deploy.{service,timer} /etc/systemd/system/
```

The deploy user needs to restart the service, and nothing else:

```bash
echo 'deploy ALL=(root) NOPASSWD: /bin/systemctl start cw-orders, /bin/systemctl stop cw-orders, /bin/chown -R cworders\:cworders /opt/cw-orders' \
  | sudo tee /etc/sudoers.d/cw-deploy
sudo chmod 440 /etc/sudoers.d/cw-deploy
```

Move the deploy key to that user, then:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now cw-deploy.timer
```

Watch it work:

```bash
systemctl list-timers cw-deploy.timer
journalctl -u cw-deploy -f
```

From here on, a push reaches the server on its own.

---

## First sign-in

The password is generated on the very first start and printed once.

```bash
# Route A
docker compose -f /srv/claude-watch/server/docker-compose.yml exec orders cat /data/FIRST-RUN-PASSWORD.txt

# Route B
sudo cat /var/lib/cw-orders/FIRST-RUN-PASSWORD.txt
```

Sign in at `https://your-domain/admin.html`, change the password, then delete
that file.

---

## Backups

Everything is one folder of JSON. The service writes a dated copy into
`backups/` by itself, so a backup is a copy of the data folder:

```bash
# Route A
docker run --rm -v server_orders-data:/data -v "$PWD":/out alpine \
  tar czf /out/cw-backup-$(date +%F).tgz -C /data .

# Route B
sudo tar czf ~/cw-backup-$(date +%F).tgz -C /var/lib/cw-orders .
```

Restoring is putting the files back and restarting.

---

## Checking it works

```bash
curl https://your-domain/api/health
curl https://your-domain/api/pricing
BASE=https://your-domain PASSWORD=your-admin-password ./server/test-api.sh
```

The last one runs all 84 API checks against the live server. It creates a test
order, so run it before you have real ones, or delete the test order after.
