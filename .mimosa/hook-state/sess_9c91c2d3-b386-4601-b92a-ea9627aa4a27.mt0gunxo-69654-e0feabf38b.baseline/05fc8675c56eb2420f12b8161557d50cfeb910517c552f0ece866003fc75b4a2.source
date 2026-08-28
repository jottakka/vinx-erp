#!/usr/bin/env bash
set -euo pipefail

readonly container_name="vinx-foundation-ef-negative-guards-$$"
readonly admin_password="$(openssl rand -hex 32)"
temporary_directory=""

cleanup() {
  if [[ -n "$temporary_directory" ]]; then
    rm -rf "$temporary_directory"
  fi

  docker rm --force "$container_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

temporary_directory="$(mktemp -d)"
readonly drift_project="$temporary_directory/drift"
readonly broken_project="$temporary_directory/broken"

write_project() {
  local project_directory="$1"
  mkdir -p "$project_directory"

  cat > "$project_directory/Guard.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
  </ItemGroup>
</Project>
EOF

  cat > "$project_directory/DriftContext.cs" <<'EOF'
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public sealed class DriftEntity
{
    public Guid Id { get; set; }
}

public sealed class DriftContext(DbContextOptions<DriftContext> options) : DbContext(options)
{
    public DbSet<DriftEntity> Entities => Set<DriftEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DriftEntity>(entity =>
        {
            entity.ToTable("drift_entities");
            entity.HasKey(item => item.Id);
        });
    }
}

public sealed class DriftContextFactory : IDesignTimeDbContextFactory<DriftContext>
{
    public DriftContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("EF_GUARD_CONNECTION")
            ?? throw new InvalidOperationException("EF_GUARD_CONNECTION must be set.");
        var options = new DbContextOptionsBuilder<DriftContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new DriftContext(options);
    }
}
EOF

  dotnet restore "$project_directory/Guard.csproj" --nologo >/dev/null
}

echo "Starting disposable EF guard PostgreSQL database."
docker run --detach --rm --name "$container_name" \
  --publish 127.0.0.1::5432 \
  --env POSTGRES_PASSWORD="$admin_password" \
  postgres:17.9-alpine >/dev/null

for _ in {1..60}; do
  if docker exec "$container_name" pg_isready --quiet --username postgres --dbname postgres; then
    break
  fi

  sleep 1
done

if ! docker exec "$container_name" pg_isready --quiet --username postgres --dbname postgres; then
  echo "Disposable EF guard PostgreSQL database did not become ready." >&2
  exit 1
fi

host_port="$(docker port "$container_name" 5432/tcp | awk -F: 'NR == 1 { print $NF }')"
if [[ -z "$host_port" ]]; then
  echo "Disposable EF guard PostgreSQL database did not expose a port." >&2
  exit 1
fi

guard_connection="Host=127.0.0.1;Port=$host_port;Database=postgres;Username=postgres;Password=$admin_password"

write_project "$drift_project"
EF_GUARD_CONNECTION="$guard_connection" \
  dotnet ef migrations add Initial --project "$drift_project" --startup-project "$drift_project" --output-dir Migrations >/dev/null

perl -0pi -e 's/public Guid Id \{ get; set; \}/public Guid Id { get; set; }\n    public string Name { get; set; } = string.Empty;/' \
  "$drift_project/DriftContext.cs"
perl -0pi -e 's/entity\.HasKey\(item => item\.Id\);/entity.HasKey(item => item.Id);\n            entity.Property(item => item.Name).HasMaxLength(200);/' \
  "$drift_project/DriftContext.cs"

set +e
drift_output="$(EF_GUARD_CONNECTION="$guard_connection" dotnet ef migrations has-pending-model-changes --project "$drift_project" --startup-project "$drift_project" 2>&1)"
drift_exit_code=$?
set -e

if [[ $drift_exit_code -eq 0 ]]; then
  echo "The controlled EF model-drift guard unexpectedly exited successfully." >&2
  exit 1
fi

drift_output_lowercase="$(printf '%s' "$drift_output" | tr '[:upper:]' '[:lower:]')"
if [[ "$drift_output_lowercase" != *"pending changes"* \
  && "$drift_output_lowercase" != *"changes have been made to the model since the last migration"* ]]; then
  echo "The controlled EF model-drift guard did not report model drift." >&2
  exit 1
fi

write_project "$broken_project"
EF_GUARD_CONNECTION="$guard_connection" \
  dotnet ef migrations add Initial --project "$broken_project" --startup-project "$broken_project" --output-dir Migrations >/dev/null
EF_GUARD_CONNECTION="$guard_connection" \
  dotnet ef database update --project "$broken_project" --startup-project "$broken_project" >/dev/null
EF_GUARD_CONNECTION="$guard_connection" \
  dotnet ef migrations add BrokenMigration --project "$broken_project" --startup-project "$broken_project" --output-dir Migrations >/dev/null

broken_migration="$(find "$broken_project/Migrations" -name '*_BrokenMigration.cs' -print -quit)"
if [[ -z "$broken_migration" ]]; then
  echo "The controlled BrokenMigration was not generated." >&2
  exit 1
fi

perl -0pi -e 's/(protected override void Up\(MigrationBuilder migrationBuilder\)\s*\{)/$1\n            migrationBuilder.Sql("SELECT 1 \/ 0");/' \
  "$broken_migration"
if ! grep --quiet --fixed-strings 'migrationBuilder.Sql("SELECT 1 / 0")' "$broken_migration"; then
  echo "The controlled BrokenMigration did not contain its deliberate failure." >&2
  exit 1
fi

set +e
broken_output="$(EF_GUARD_CONNECTION="$guard_connection" dotnet ef database update --project "$broken_project" --startup-project "$broken_project" 2>&1)"
broken_exit_code=$?
set -e

if [[ $broken_exit_code -eq 0 ]]; then
  echo "The controlled BrokenMigration unexpectedly exited successfully." >&2
  exit 1
fi

if [[ "$broken_output" != *"BrokenMigration"* ]]; then
  printf '%s\n' "$broken_output" >&2
  echo "The controlled BrokenMigration did not fail with its migration identifier." >&2
  exit 1
fi

broken_recorded="$(docker exec --env PGPASSWORD="$admin_password" "$container_name" \
  psql --quiet --tuples-only --no-align --username postgres --dbname postgres \
    --command "SELECT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%_BrokenMigration');")"
if [[ "$broken_recorded" != "f" ]]; then
  echo "The failed BrokenMigration was recorded in EF migration history." >&2
  exit 1
fi

echo "Controlled EF model drift and failed migration guards passed."
