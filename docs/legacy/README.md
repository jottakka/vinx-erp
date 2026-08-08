# Legacy Source Inspection

The legacy source is inspected from a separate, read-only checkout. The source
URL and immutable commit must be configured in `reference.md` before running
these commands.

From the parent directory of this repository, set the configured values and
choose a sibling directory for the checkout:

```bash
cd "$(dirname "$PWD")"
LEGACY_URL="<configured-source-url>"
LEGACY_COMMIT="<configured-immutable-commit>"
LEGACY_DIR="legacy-reference"
git clone "$LEGACY_URL" "$LEGACY_DIR"
cd "$LEGACY_DIR"
git checkout --detach "$LEGACY_COMMIT"
chmod -R a-w .
```

Inspect the detached checkout in place. Do not add it as a subdirectory or
submodule of this repository, and do not modify the legacy files during
inspection. Remove or recreate the sibling checkout when the inspection is
finished according to local retention policy.

The committed reference metadata records only the source URL, immutable
commit, inspection date, checkout-location guidance, and status. It contains
no local checkout path that exposes user-specific secrets.
