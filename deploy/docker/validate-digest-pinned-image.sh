#!/bin/sh
set -eu

fail() {
    printf 'Digest-pinned image validation failed: %s\n' "$1" >&2
    exit 1
}

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
