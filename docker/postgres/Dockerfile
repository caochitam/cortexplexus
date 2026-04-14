FROM apache/age:release_PG17_1.6.0
# Pinned to AGE 1.6 on Postgres 17. Do NOT use `apache/age:latest`: upstream
# rolls it to newer PG majors without warning (rolled to PG18 with binaries
# using CPU instructions not present on older Xeon/Core hosts — see issue #1
# follow-up, SIGILL on Kaby Lake i3-7100 with no AVX-512). Bumping the major
# version should be a deliberate, tested upgrade, not silent drift from :latest.

# Install pgvector. Pin the tag (no silent upstream drift) and constrain the
# compiler to x86-64-v2 so the binary does not emit AVX-512 or BMI2 instructions
# that are missing on older hosts (observed SIGILL on Intel Core i3-7100 / Kaby
# Lake when the image was built on a CI runner with a modern CPU — gcc's auto-
# vectorizer plus pgvector's SIMD paths produced code the runtime host could
# not execute). `x86-64-v2` covers anything from ~2008 onward (SSE4.2), which
# is the baseline we support. Upgrade deliberately if you need newer SIMD.
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        build-essential \
        postgresql-server-dev-17 \
        git \
        ca-certificates && \
    git clone --branch v0.8.0 --depth 1 https://github.com/pgvector/pgvector.git /tmp/pgvector && \
    cd /tmp/pgvector && \
    make OPTFLAGS="-O2 -march=x86-64-v2" && \
    make install && \
    cd / && \
    rm -rf /tmp/pgvector && \
    apt-get purge -y build-essential postgresql-server-dev-17 git && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*
