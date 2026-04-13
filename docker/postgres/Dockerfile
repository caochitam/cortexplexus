FROM apache/age:release_PG17_1.6.0
# Pinned to AGE 1.6 on Postgres 17. Do NOT use `apache/age:latest`: upstream
# rolls it to newer PG majors without warning (rolled to PG18 with binaries
# using CPU instructions not present on older Xeon/Core hosts — see issue #1
# follow-up, SIGILL on Kaby Lake i3-7100 with no AVX-512). Bumping the major
# version should be a deliberate, tested upgrade, not silent drift from :latest.

# Install pgvector extension on top of AGE image
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        build-essential \
        postgresql-server-dev-17 \
        git \
        ca-certificates && \
    git clone https://github.com/pgvector/pgvector.git /tmp/pgvector && \
    cd /tmp/pgvector && \
    make && \
    make install && \
    cd / && \
    rm -rf /tmp/pgvector && \
    apt-get purge -y build-essential postgresql-server-dev-17 git && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*
