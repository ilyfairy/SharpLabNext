#!/bin/sh
set -eu

fail() {
    printf 'Digest-pinned image validation failed: %s\n' "$1" >&2
    exit 1
}

allow_bare_image_id=false
allow_local_image_tag=false
while test "$#" -gt 0; do
    case "$1" in
        --allow-bare-image-id)
            test "$#" -ge 3 || fail '--allow-bare-image-id requires true or false plus NAME VALUE pairs'
            allow_bare_image_id=$2
            shift 2
            test "$allow_bare_image_id" = true || test "$allow_bare_image_id" = false \
                || fail '--allow-bare-image-id must be true or false'
            ;;
        --allow-local-image-tag)
            test "$#" -ge 3 || fail '--allow-local-image-tag requires true or false plus NAME VALUE pairs'
            allow_local_image_tag=$2
            shift 2
            test "$allow_local_image_tag" = true || test "$allow_local_image_tag" = false \
                || fail '--allow-local-image-tag must be true or false'
            ;;
        *) break ;;
    esac
done

test "$#" -gt 0 || fail 'at least one NAME VALUE pair is required'
test $(( $# % 2 )) -eq 0 || fail 'arguments must be NAME VALUE pairs'

while test "$#" -gt 0; do
    name=$1
    value=$2
    shift 2

    test -n "$name" || fail 'an image input name is empty'
    case "$value" in
        *[[:space:]]*)
            fail "${name} contains whitespace"
            ;;
    esac

    if test "$allow_bare_image_id" = true && test "$name" = WINE_IDENTITY; then
        case "$value" in
            sha256:[0-9a-f]*)
                digest=${value#sha256:}
                test "${#digest}" -eq 64 \
                    || fail "${name} bare image ID must contain exactly 64 hexadecimal characters"
                case "$digest" in
                    *[!0-9a-f]*) fail "${name} bare image ID must use lowercase hexadecimal" ;;
                esac
                continue
                ;;
        esac
    fi

    if test "$allow_local_image_tag" = true && test "$name" = WINE_IMAGE; then
        tag=${value##*:}
        repository=${value%:*}
        if test -n "$repository" && test -n "$tag" && test "$value" = "${repository}:${tag}" \
            && test "${#tag}" -le 128 \
            && printf '%s' "$repository" | grep -Eq '^[a-z0-9][a-z0-9._/-]*$' \
            && printf '%s' "$tag" | grep -Eq '^[a-z0-9][a-z0-9._-]*$'; then
            continue
        fi
        fail "${name} local reference must use a safe explicit tag"
    fi

    repository=${value%@sha256:*}
    digest=${value##*@sha256:}
    test -n "$repository" \
        && test "$value" = "${repository}@sha256:${digest}" \
        || fail "${name} must use repository@sha256:<64 lowercase hex>"
    case "$repository" in
        *@*) fail "${name} contains more than one digest separator" ;;
    esac
    test "${#digest}" -eq 64 \
        || fail "${name} SHA-256 digest must contain exactly 64 characters"
    case "$digest" in
        *[!0-9a-f]*) fail "${name} SHA-256 digest must use lowercase hexadecimal" ;;
    esac
done
