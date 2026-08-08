# Legacy Source Inspection

The legacy source is inspected from a separate, read-only checkout. The source
URL and immutable commit must be configured in `reference.md` before running
these commands.

Create a unique checkout directory under the system temporary directory. This
location is outside both this repository and its worktrees:

```bash
LEGACY_URL="<configured-source-url-without-credentials>"
LEGACY_COMMIT="<configured-full-commit-sha>"
LEGACY_TMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/vinx-legacy.XXXXXX")"
LEGACY_DIR="$LEGACY_TMP_ROOT/checkout"
git clone "$LEGACY_URL" "$LEGACY_DIR"
git -C "$LEGACY_DIR" checkout --detach "$LEGACY_COMMIT"
chmod -R a-w "$LEGACY_DIR"
```

Inspect the detached checkout in place. Do not add it as a subdirectory or
submodule of this repository, and do not modify the legacy files during
inspection. Remove or recreate the temporary checkout when the inspection is
finished according to local retention policy. Use an existing credential helper
or SSH agent for access; never put credentials in `LEGACY_URL`, committed
metadata, or documentation.

Every inventory entry must use exactly one decision: `preserve`, `adapt`,
`discard`, or `hypothesis`.

The committed reference metadata records only the source URL, immutable
commit, inspection date, checkout-location guidance, and status. It contains
no local checkout path that exposes user-specific secrets.
