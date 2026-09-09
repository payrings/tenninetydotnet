# 10/90 tenninety - Coder role image: OpenCode (selected with "coder_agent": "opencode").
#
# Contract (see docs/TESTER-SANDBOX.md):
#   - numeric non-root USER (UID build argument), no ENTRYPOINT: the sandbox runtime appends the fixed
#     `sleep infinity` waiting command and `--user <uid>` itself;
#   - the supported tool is present at the EXACT path /usr/local/bin/opencode, with Git
#     available for repository-aware operation;
#   - no credentials, keys, host paths or host configuration are baked in;
#   - /workspace is the working directory (the only writable bind at runtime).
#
# Pinning (NOT bit-for-bit reproducibility): the base image is pinned by digest and the
# tool version by an exact pin, but the tool's transitive dependencies are unlocked and the
# base repositories are live, so image content is NOT guaranteed identical across builds.
# Rebuild deliberately, verify with the build script, and record the resulting local image
# ID (sha256:...) in .tenninety/config.json (sandbox.roles.coder.image).
FROM node:22-slim@sha256:83f487e0a63425e5b4d146fb5e5be574bcbe1b7b843d3ebafdd95eaf7767a7e5

ARG OPENCODE_VERSION=1.18.29

RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/* \
    && npm install --global "opencode-ai@${OPENCODE_VERSION}" \
    && npm cache clean --force \
    && test -x /usr/local/bin/opencode \
    && git --version >/dev/null

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

# Clear the base image's ENTRYPOINT/CMD: the sandbox runtime supplies the fixed
# `sleep infinity` waiting command itself and the preflight refuses any ENTRYPOINT.
ENTRYPOINT []
CMD []

USER ${UID}
WORKDIR /workspace
