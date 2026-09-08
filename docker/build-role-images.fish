#!/usr/bin/env fish
# 10/90 tenninety - build the pinned Docker role images and print their EXACT IDs.
#
# Usage (fish):
#   ./docker/build-role-images.fish              # build all five role images
#   ./docker/build-role-images.fish aider tester # build only the named images
#
# Names: aider | opencode | pi | reviewer | tester
#
# What this script guarantees:
#   - every Dockerfile pins its base image by digest and its top-level tool by exact version.
#     This is NOT bit-for-bit reproducibility: the live apt repositories and the unlocked
#     transitive npm/Python dependency closure mean image content is not guaranteed identical
#     across builds. Rebuild deliberately and re-record the resulting local image ID;
#   - the images are built with the INVOKING HOST USER's numeric UID/GID, because the
#     sandbox runtime bind-mounts the attempt workspace owned by that user; pass
#     TENNINETY_IMAGE_UID / TENNINETY_IMAGE_GID to override;
#   - each built image is verified against the sandbox contract BEFORE its ID is printed:
#     numeric non-root USER, NO ENTRYPOINT, (for coder images) the tool at the exact
#     expected path /usr/local/bin/<tool>, (for the tester) the .NET SDK at the exact path
#     /usr/bin/dotnet, and no common credential/host-configuration file present;
#   - the exact local image ID (sha256:<64 hex>) is printed per image - copy it into
#     .tenninety/config.json (sandbox.roles.coder.image / roles.reviewer.image /
#     roles.tester.image). Digest-pinned registry references are equally valid; a local
#     image ID pins the bytes you just built and validated.
#
# Nothing here is automatic: tenninety never builds or pulls role images at runtime.

set -l all aider opencode pi reviewer tester

function role_image_name
    switch $argv[1]
        case aider opencode pi
            echo "tenninety-coder-$argv[1]"
        case '*'
            echo "tenninety-$argv[1]"
    end
end

function role_context
    switch $argv[1]
        case aider
            echo docker/coder-aider
        case opencode
            echo docker/coder-opencode
        case pi
            echo docker/coder-pi
        case reviewer
            echo docker/reviewer
        case tester
            echo docker/tester
    end
end

function role_tool_path
    switch $argv[1]
        case aider
            echo /usr/local/bin/aider
        case opencode
            echo /usr/local/bin/opencode
        case pi
            echo /usr/local/bin/pi
        case tester
            echo /usr/bin/dotnet
        case '*'
            echo ""
    end
end

set -l requested $argv
if test (count $requested) -eq 0
    set requested $all
end

for role in $requested
    if not contains -- $role $all
        echo "unknown role '$role' (known: "(string join ', ' $all)")" >&2
        exit 2
    end
end

for role in $requested
    set -l image (role_image_name $role)
    set -l context (role_context $role)
    set -l tool (role_tool_path $role)

    set -l build_uid (set -q TENNINETY_IMAGE_UID; and echo $TENNINETY_IMAGE_UID; or echo (id -u))
    set -l build_gid (set -q TENNINETY_IMAGE_GID; and echo $TENNINETY_IMAGE_GID; or echo (id -g))

    echo "==> building $image from $context (digest-pinned base; versions pinned in the Dockerfile; uid/gid $build_uid:$build_gid)"
    docker build --build-arg UID=$build_uid --build-arg GID=$build_gid -t $image $context
    or begin
        echo "build failed for $image" >&2
        exit 1
    end

    # ---- verify the sandbox contract on the image we just built ------------------------
    set -l user (docker image inspect --format '{{.Config.User}}' $image)
    set -l entrypoint (docker image inspect --format '{{json .Config.Entrypoint}}' $image)
    set -l uid (string split ':' $user)[1]
    if not string match -qr '^[0-9]+(:[0-9]+)?$' -- $user
        echo "CONTRACT VIOLATION: $image User is '$user'; the sandbox requires a numeric non-root identity" >&2
        exit 1
    end
    if test "$uid" = 0
        echo "CONTRACT VIOLATION: $image runs as root (uid=0)" >&2
        exit 1
    end
    if test "$entrypoint" != null
        echo "CONTRACT VIOLATION: $image declares an ENTRYPOINT ($entrypoint); the fixed waiting command cannot be guaranteed" >&2
        exit 1
    end
    if test -n "$tool"
        docker run --rm --entrypoint /bin/sh $image -c "test -x $tool"
        or begin
            echo "CONTRACT VIOLATION: $image lacks the expected tool at $tool" >&2
            exit 1
        end
    end
    # No common credential or host-configuration file may exist in the image (best-effort
    # presence proof: the checked paths cover npm/pip/NuGet/Docker/SSH credentials and
    # configuration in /root and /home).
    docker run --rm --entrypoint /bin/sh $image -c \
        'for f in /root/.npmrc /root/.netrc /root/.ssh /root/.docker/config.json /root/.nuget/NuGet/NuGet.Config /root/.gitconfig /home/*/.npmrc /home/*/.netrc /home/*/.ssh /home/*/.gitconfig; do if test -e "$f"; then echo "$f"; exit 97; fi; done; exit 0'
    if test $status -ne 0
        echo "CONTRACT VIOLATION: $image contains a credential or host-configuration file (see the path above)" >&2
        exit 1
    end

    set -l id (docker image inspect --format '{{.Id}}' $image)
    echo "    image : $image"
    echo "    user  : $user   entrypoint: none"
    echo "    ID    : $id"
end

echo
echo "Next step: copy each printed 'sha256:...' ID into .tenninety/config.json:"
echo '  "sandbox": { "roles": { "coder":   { "image": "sha256:<coder id>" },'
echo '                            "reviewer": { "image": "sha256:<reviewer id>" },'
echo '                            "tester":   { "image": "sha256:<tester id>" } ... }'
echo "The coder image must match your \"coder_agent\" selection (aider, opencode or pi)."
