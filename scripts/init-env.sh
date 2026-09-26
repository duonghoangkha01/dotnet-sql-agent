#!/usr/bin/env bash
# Creates .env from .env.example with a random secret in place of every __GENERATE__.
# Refuses to overwrite an existing .env unless --force is given.
set -euo pipefail

# Files created below (including the temp file) are readable by the owner only.
umask 077

cd "$(dirname "$0")/.."

if [ -f .env ] && [ "${1:-}" != "--force" ]; then
  echo ".env already exists. Use --force to regenerate (this rotates every secret)." >&2
  exit 1
fi

# Alphanumeric only, so values are safe in sqlcmd variables and shell without quoting.
# SQL Server requires upper + lower + digit, so retry until all three are present.
gen() {
  local length="$1" value
  while true; do
    # `head` closes the pipe early, so `tr` exits 141 (SIGPIPE); `|| true` keeps pipefail from aborting.
    value="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "$length" || true)"
    if [[ "$value" =~ [A-Z] && "$value" =~ [a-z] && "$value" =~ [0-9] ]]; then
      printf '%s' "$value"
      return
    fi
  done
}

: > .env.tmp
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"   # a CRLF checkout (Git for Windows autocrlf) must not hide the placeholder
  if [[ "$line" == *=__GENERATE__ ]]; then
    key="${line%%=*}"
    len=32
    [ "$key" = "JWT_SIGNING_KEY" ] && len=48
    printf '%s=%s\n' "$key" "$(gen "$len")" >> .env.tmp
  else
    printf '%s\n' "$line" >> .env.tmp
  fi
done < .env.example

# Never report success with a placeholder left behind: a leftover JWT key would be a public constant.
if grep -q '=__GENERATE__' .env.tmp; then
  rm -f .env.tmp
  echo "Some __GENERATE__ placeholders were not replaced. Check .env.example line endings." >&2
  exit 1
fi

mv .env.tmp .env
echo "Wrote .env with generated secrets (git-ignored)."
if [ "${1:-}" = "--force" ]; then
  echo "Note: SQL Server keeps the SA password it was first started with. If the sqlserver volume already"
  echo "exists, recreate it (docker compose down -v; this also deletes the restored database) so it takes the new one."
fi
