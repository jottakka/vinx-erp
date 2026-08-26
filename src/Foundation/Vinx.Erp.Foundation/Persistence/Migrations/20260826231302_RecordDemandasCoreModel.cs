using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordDemandasCoreModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Model-only migration: as tabelas do schema demandas são criadas pelo SQL
            // bruto em 20260826160000_AddDemandasCore (capabilities, RLS e grants
            // incluídos); esta migração apenas registra o mapeamento das entidades no
            // snapshot do modelo.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
