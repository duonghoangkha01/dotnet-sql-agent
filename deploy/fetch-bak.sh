#!/bin/sh
# Downloads the AdventureWorks2022 backup into /bak and verifies its SHA-256.
# Runs in the bak-fetch container (curlimages/curl, busybox sh).
#
# Trust note: Microsoft does not publish a checksum for this release asset (GitHub's digest field is
# empty for it), so the hash below was recorded from the first download on 2026-09-26. It detects a
# changed or corrupted file afterwards; it is not proof of origin.
set -eu

URL="https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorks2022.bak"
SHA256="d17567adb1521f972e1dc183a7216cea869c4580b5d75632425bcadbaf82ce5e"
FILE="/bak/AdventureWorks2022.bak"

if [ -f "$FILE" ] && echo "$SHA256  $FILE" | sha256sum -c - >/dev/null 2>&1; then
  echo "AdventureWorks2022.bak already present and verified."
  exit 0
fi

echo "Downloading AdventureWorks2022.bak (about 200 MB)..."
curl -fL --retry 3 --retry-delay 5 -o "$FILE.part" "$URL"

if ! echo "$SHA256  $FILE.part" | sha256sum -c - ; then
  rm -f "$FILE.part"
  echo "Checksum mismatch: the download changed or is corrupted. Refusing to use it." >&2
  exit 1
fi

mv "$FILE.part" "$FILE"
chmod 644 "$FILE"
echo "AdventureWorks2022.bak downloaded and verified."
