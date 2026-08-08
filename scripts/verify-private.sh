#!/usr/bin/env bash
set -euo pipefail

if [[ "${VINX_QUALITY_VERIFIER:-}" != "1" ]]; then
  printf '%s\n' 'Private quality execution requires VINX_QUALITY_VERIFIER=1.' >&2
  exit 64
fi

project='quality/private/Vinx.Erp.PrivateQualityTests/Vinx.Erp.PrivateQualityTests.csproj'
suite_name='private-quality-suite'
raw_output="$(mktemp)"
trap 'rm -f "$raw_output"' EXIT

correlation_id='not-provided'
if [[ "${VINX_QUALITY_CORRELATION_ID:-}" =~ ^[A-Za-z0-9._:-]{1,128}$ ]]; then
  correlation_id="$VINX_QUALITY_CORRELATION_ID"
fi

if dotnet test "$project" --no-restore --logger 'console;verbosity=minimal' >"$raw_output" 2>&1; then
  printf 'private-quality outcome=passed suite=%s correlation_id=%s\n' "$suite_name" "$correlation_id"
  exit 0
fi

printf 'private-quality outcome=failed suite=%s correlation_id=%s\n' "$suite_name" "$correlation_id" >&2
printf '%s\n' 'Private evidence is redacted; the verifier must report only an actionable public-contract summary.' >&2
exit 1
