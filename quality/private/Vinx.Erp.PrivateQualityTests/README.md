# Private quality suite

This project is owned by the quality verifier. The first Foundation slice adds
verifier-authored abuse coverage for forged or missing company context and
cross-company leakage: a single Testcontainers Postgres 17 is migrated by
`FoundationDbContext` as `vinx_migrator`, prepared with the public role
bootstrap and Wolverine grant boundary, and attacked through `vinx_runtime` and
`vinx_worker` connections only. The `Vinx.Erp.Foundation` reference is a
build-time migration provider; all assertions are raw Npgsql/SQL authored by
the verifier. See `../../README.md` (custody notes) for the scenario list.

Run it only through `scripts/verify-private.sh` in the separate verifier
execution context (`dotnet test` on this csproj directly; it stays excluded
from `Vinx.Erp.sln` by custody policy). Builders must not add, inspect, or
execute private tests.
