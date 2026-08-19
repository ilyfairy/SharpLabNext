#!/bin/sh
set -eu

if [ "$#" -ne 3 ]; then
  echo "usage: fetch-verified-reference-package URL SHA512 OUTPUT" >&2
  exit 64
fi

url="$1"
expected_sha512="$2"
output="$3"
temporary="${output}.tmp.$$"

verify() {
  printf '%s  %s\n' "$expected_sha512" "$1" | sha512sum --check --strict >/dev/null 2>&1
}

if [ -s "$output" ] && verify "$output"; then
  exit 0
fi

rm -f "$output" "$temporary"
trap 'rm -f "$temporary"' EXIT HUP INT TERM
curl --fail --location --retry 5 --retry-all-errors \
  --retry-delay 1 \
  --output "$temporary" \
  "$url"
verify "$temporary"
mv "$temporary" "$output"
trap - EXIT HUP INT TERM
