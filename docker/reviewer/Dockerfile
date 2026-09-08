# 10/90 tenninety - Reviewer role image (offline exploration guest).
#
# The Reviewer model call stays host-side; repository exploration happens through
# /bin/sh -lc <command> actions inside this OFFLINE guest (network: none at runtime).
# The image ships the complete bounded exploration toolbox the reviewer gate's shell
# actions rely on: bash, sh, GNU coreutils (ls/cat/wc/head/tail/sort/uniq/...), grep,
# sed, gawk, find, diff, git (the disposable workspace carries a disposable .git),
# jq and ripgrep. Anything outside this toolbox simply fails inside the guest - which
# is fail-closed, never a security concern.
#
# Contract (see docs/TESTER-SANDBOX.md):
#   - numeric non-root USER (UID build argument), no ENTRYPOINT: the sandbox runtime appends the fixed
#     `sleep infinity` waiting command and `--user <uid>` itself;
#   - no credentials, keys, host paths or host configuration are baked in.
#
# Pinning (NOT bit-for-bit reproducibility): the base image is pinned by digest, but the
# live apt repositories mean image content is NOT guaranteed identical across builds.
# Rebuild deliberately, verify with the build script, and record the resulting local image
# ID (sha256:...) in .tenninety/config.json (sandbox.roles.reviewer.image).
FROM debian:bookworm-slim@sha256:88200866dfff7ea7f5cbcb6ec7c8a701889efe6fe859fe64d6990e4b07ea4171

# Exploration toolbox actually invoked by the reviewer gate's shell actions.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash coreutils grep sed gawk findutils diffutils \
        git jq ripgrep ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && test -x /bin/sh && test -x /bin/bash && test -x /usr/bin/git \
    && test -x /usr/bin/jq && test -x /usr/bin/rg

# Numeric non-root identity, parameterized at build time: the sandbox runtime mounts the
# attempt workspace owned by the HOST user that runs tenninety, so build the image with
#   --build-arg UID=<host uid> --build-arg GID=<host gid>
# (docker/build-role-images.fish does this automatically). HOME points at /home/tenninety
# WITHOUT creating it: the runtime provides that path as a bounded tmpfs, and a
# pre-existing directory would inherit its restrictive mode into the mount. No
# credentials, keys or host configuration are baked in.
ARG UID=10001
ARG GID=10001
RUN set -eux; \
    getent group "$GID" >/dev/null 2>&1 || groupadd --gid "$GID" tenninety; \
    useradd --non-unique --uid "$UID" --gid "$GID" \
        --home-dir /home/tenninety --no-create-home --shell /usr/sbin/nologin tenninety; \
    mkdir -p /workspace /home/tenninety; \
    chown "$UID:$GID" /workspace; \
    chmod 0777 /home/tenninety

# The runtime mounts a bounded tmpfs at /home/tenninety; the 0777 directory mode above is
# what that tmpfs inherits, so every numeric identity can use it. Pin HOME so processes
# never resolve a conflicting passwd entry (e.g. a base-image user with the same UID).
ENV HOME=/home/tenninety

USER ${UID}
WORKDIR /workspace
