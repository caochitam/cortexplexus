FROM apache/age:latest

# Install pgvector extension on top of AGE image
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        build-essential \
        postgresql-server-dev-18 \
        git \
        ca-certificates && \
    git clone https://github.com/pgvector/pgvector.git /tmp/pgvector && \
    cd /tmp/pgvector && \
    make && \
    make install && \
    cd / && \
    rm -rf /tmp/pgvector && \
    apt-get purge -y build-essential postgresql-server-dev-18 git && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*
