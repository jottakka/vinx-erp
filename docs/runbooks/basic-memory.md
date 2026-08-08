# Basic Memory

The `vinx-erp-docs` Basic Memory project indexes only `docs/`. Its SQLite
index and configuration under `~/.basic-memory` are disposable local state,
not a source of record.

Durable architectural and operational decisions belong in Git-tracked
Markdown and require a Git commit. Rebuild the local index after documentation
changes rather than treating the index as an archive.

## Commands

```bash
basic-memory status
basic-memory reindex
basic-memory doctor
```
