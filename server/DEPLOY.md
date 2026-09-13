# Putting the orders service on a server

One command stands it up, and after that a push is the whole deployment: the
server checks the repo every two minutes and rebuilds itself when the commit
moves. No uploading, no publishing by hand, no touching the server again.

## The short way (Linux)

On the server, as a user who can sudo:

```bash
export GH_TOKEN=github_pat_...          # needs Contents: read on the repo
export DOMAIN=orders.example.com        # leave this out for plain HTTP on :5080

curl -sSL -H "Authorization: token $GH_TOKEN" -H "Accept: application/vnd.github.raw" \
  https://api.github.com/repos/Moohsenam/LMWatch/contents/server/bootstrap.sh -o bootstrap.sh

sudo -E bash bootstrap.sh
```

It installs git, the .NET 8 SDK, clones the repo, builds, writes the systemd
service, sets up HTTPS with Caddy when DOMAIN is given, installs the
two-minute auto-update timer, and prints the admin password it generated on
the first run.

Point the domain's DNS at the server before running it, or the certificate
cannot be issued.

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

`/etc/caddy/Caddyfile`, with your own domain:

```
orders.example.com {
    encode gzip
    reverse_proxy 127.0.0.1:5080
}
```

```bash
sudo systemctl reload caddy
```

The service itself listens on `127.0.0.1` only, so nothing reaches it except
through Caddy.

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
