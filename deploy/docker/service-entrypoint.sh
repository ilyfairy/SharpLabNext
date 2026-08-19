#!/bin/sh
set -eu

case "${SHARPLABNEXT_ASSEMBLY:-}" in
    *.dll) ;;
    *)
        echo "SHARPLABNEXT_ASSEMBLY must name a published .dll." >&2
        exit 64
        ;;
esac

exec dotnet "/app/${SHARPLABNEXT_ASSEMBLY}" "$@"
