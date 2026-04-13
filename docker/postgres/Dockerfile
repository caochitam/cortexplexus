FROM apache/age:PG17_latest
# Pinned to Postgres 17. Do NOT use `apache/age:latest`: upstream rolls it to
# newer PG majors without warning (last rolled to PG18 which shipped binaries
# using CPU instructions not present on older Xeon/Core hosts — see issue #1
# follow-up). Bumping major version is a deliberate, tested upgrade.

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
