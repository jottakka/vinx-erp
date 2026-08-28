using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
public partial class RecordApprovalResourceKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Model-only migration: o SQL bruto em 20260828091000_AddApprovalResourceKey
        // adiciona a coluna e troca o índice de fronteira viva; esta migração
        // registra apenas o mapeamento da entidade no snapshot do modelo.
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
