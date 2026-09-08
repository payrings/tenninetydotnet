# 10/90 tenninety - Tester role image (offline build/test guest and restricted Restore).
#
# Contract (see docs/TESTER-SANDBOX.md):
#   - contains /bin/bash, the .NET 10 SDK, and dotnet reachable at the EXACT path
#     /usr/bin/dotnet (the trusted Restore command invokes /usr/bin/dotnet);
#   - numeric non-root USER (UID build argument), no ENTRYPOINT: the sandbox runtime appends the fixed
#     `sleep infinity` waiting command and `--user <uid>` itself;
#   - no credentials, keys, host paths or host configuration are baked in (no NuGet.config,
#     no feed credentials - the Restore phase generates its own trusted config).
#
# Offline dependency behavior (docs/TESTER-SANDBOX.md):
#   - the Tester always runs OFFLINE (network: none). Dependencies come from the accepted
#     restricted Restore phase, which installs into /workspace/.tenninety/restore-packages
#     and is exposed to the Tester build/test commands through NUGET_PACKAGES; or from
#     pre-baked/vendored package caches baked into a DERIVED image the operator maintains.
#   - with neither, a build/test command that needs packages fails (fail closed).
#   - after an accepted Restore, run build/test with --no-restore (e.g.
#     "dotnet build --no-restore") so no implicit restore is attempted.
#   - git is included because generated repositories keep a disposable .git.
#
# Pinning (NOT bit-for-bit reproducibility): the base image is pinned by digest, but the
# live apt repositories mean image content is NOT guaranteed identical across builds.
# Rebuild deliberately, verify with the build script, and record the resulting local image
# ID (sha256:...) in .tenninety/config.json (sandbox.roles.tester.image).
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510

# Pin the exact dotnet location the framework invokes. The official SDK image already
# exposes /usr/bin/dotnet; create it only if a future base image stops doing so.
RUN set -eux; \
    command -v dotnet; \
    [ -x /usr/bin/dotnet ] || ln -s "$(command -v dotnet)" /usr/bin/dotnet; \
    test -x /bin/bash; \
    test -x /usr/bin/dotnet; \
    dotnet --list-sdks

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
