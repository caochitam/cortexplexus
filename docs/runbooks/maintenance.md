# Runbook: Disk & Docker maintenance

Over time a CortexPlexus host accumulates Docker junk: stale images from
`docker compose pull` updates, dangling build cache, orphan volumes from
smoke tests, and PostgreSQL WAL growth. On modest VMs (20-30 GB disk)
this can fill the filesystem in weeks and crash Postgres with
`FATAL: could not write lock file "postmaster.pid": No space left on device`.

This runbook covers prevention (weekly prune) and emergency recovery.

---

## Why disk fills up

| Source | Typical size | Growth trigger |
|---|---|---|
| Superseded images (`<none>:<none>`) | 100 MB - 1 GB each | Every `docker compose pull` / `docker build` |
| Build cache layers | 1 - 10 GB | Every `docker compose build` or CI publish |
| Orphan volumes | 50 MB - 5 GB | `docker compose down -v` on test stacks you forgot about |
| PostgreSQL WAL | 100 MB - 2 GB | Normal churn; reclaimed at checkpoint |
| App container `appdata` volume | grows with file-hash cache | Re-indexing large repos |

A healthy CortexPlexus host sits around 2-4 GB disk. Anything above
8 GB with 2 containers running means junk to clean.

---

## 1. Quick health check

```bash
# Overall disk
df -h /

# Docker-specific breakdown
docker system df

# Per-volume sizes (CortexPlexus volumes)
docker volume ls --filter name=cortexplexus --format '{{.Name}}' \
  | xargs -I{} sh -c 'echo -n "{}: "; docker run --rm -v {}:/v alpine du -sh /v | cut -f1'
```

**Red flags:**
- `df -h /` shows `Avail` under 2 GB → act now.
- `docker system df` shows `RECLAIMABLE` > 50% → prune due.
- Individual volume > 10 GB → investigate before pruning (don't nuke `cortexplexus_pgdata`, that's your indexed data).

---

## 2. Weekly prune (safe, non-destructive to running data)

```bash
# Remove stopped containers, dangling images, build cache, unused networks.
# Does NOT touch volumes attached to running containers.
docker system prune -f

# Include unused images (those not tagged as :latest / :main / etc.)
docker image prune -af
```

Run this weekly. Typical recovery: 500 MB - 2 GB. Zero risk to running
services.

### Automate via cron (optional)

```bash
sudo tee /etc/cron.weekly/docker-prune > /dev/null <<'EOF'
#!/bin/bash
# Weekly Docker housekeeping for CortexPlexus host
docker system prune -f >> /var/log/docker-prune.log 2>&1
docker image prune -af >> /var/log/docker-prune.log 2>&1
df -h / >> /var/log/docker-prune.log 2>&1
echo "---" >> /var/log/docker-prune.log
EOF
sudo chmod +x /etc/cron.weekly/docker-prune
```

Logs go to `/var/log/docker-prune.log`. Read it after the first week to
confirm it's running.

---

## 3. Emergency recovery (disk full, Postgres crashed)

Symptoms:
- `docker compose up` fails with `dependency postgres failed to start`
- `docker logs cortexplexus-postgres-1` shows `No space left on device`
- `df -h /` reports 100% used

### Step 3a — Free space aggressively

```bash
# Stop the stack first (keeps volumes intact — your data is safe)
cd /opt/cortexplexus && docker compose down

# Nuclear prune: removes all unused containers, networks, images,
# AND build cache AND volumes not attached to a container.
# Since the stack is stopped, the cortexplexus_pgdata and _appdata
# volumes are detached — the `--volumes` flag WILL try to delete them
# if nothing references them. Only safe because stopping compose keeps
# the volume metadata intact but marks them unreferenced.
# Prefer the safer form below if you have any doubt.
docker system prune -af --volumes
```

**Safer alternative** (keeps all CortexPlexus volumes no matter what):

```bash
docker system prune -af                         # everything except volumes
docker volume ls --filter dangling=true -q \
  | grep -v cortexplexus \
  | xargs -r docker volume rm                   # dangling volumes except ours
```

### Step 3b — Restart stack

```bash
cd /opt/cortexplexus && docker compose up -d
sleep 10
docker ps --filter name=cortexplexus
curl -s -o /dev/null -w "mcp=%{http_code}\n" http://localhost:8080/mcp   # expect 400 or 405
```

### Step 3c — Confirm data survived

From any MCP client:

```
ListRepositories()
```

Each repo should still list its previous `Last indexed` timestamp and
`Health: OK` line. If they show `EMPTY` / `UNKNOWN`, the `pgdata`
volume was wiped — re-index from your local code via `ActivateAgent`.

---

## 4. Rotating PostgreSQL WAL (rare)

Normally Postgres checkpoints every 5 minutes and truncates WAL
automatically. If WAL grows unbounded (check
`docker exec cortexplexus-postgres-1 du -sh /var/lib/postgresql/pg_wal`),
something is holding a replication slot or long-running transaction:

```bash
docker exec cortexplexus-postgres-1 psql -U postgres -d cortexplexus \
  -c "SELECT slot_name, active, restart_lsn FROM pg_replication_slots;"

docker exec cortexplexus-postgres-1 psql -U postgres -d cortexplexus \
  -c "SELECT pid, state, xact_start FROM pg_stat_activity WHERE state <> 'idle' ORDER BY xact_start;"
```

If a replication slot is inactive or a transaction is hours old, drop
the slot / terminate the backend. This is very unusual for CortexPlexus
(no replication, short transactions).

---

## 5. Rebuild image locally (skip GHCR, offline)

If you need a fresh app image without waiting for CI:

```bash
cd /opt/cortexplexus
docker compose build cortexplexus     # builds from src/CortexPlexus.App/Dockerfile
docker compose up -d
```

Note: this overwrites the `ghcr.io/dt-tuan/cortexplexus:main` tag
locally. Next `docker compose pull` will overwrite it back with the
registry version. This is by design — local builds are ad-hoc.

---

## See also

- [`deployment.md`](deployment.md) — initial deploy + connect
- [`development-setup.md`](development-setup.md) — native dev on the host
