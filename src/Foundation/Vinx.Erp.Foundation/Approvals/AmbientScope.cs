using Microsoft.EntityFrameworkCore.Storage;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Approvals;

/// <summary>
/// Escopo de unidade de trabalho dos serviços de aprovação. Abre transação
/// própria quando o contexto está solto (modo standalone/legado); com
/// transação ambiente já aberta pelo chamador (orquestração módulo+etapa)
/// apenas participa — commit e rollback ficam a cargo do dono.
/// </summary>
public interface IUnitOfWorkScope : IAsyncDisposable
{
    IDbContextTransaction? Transaction { get; }

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

public static class AmbientScope
{
    public static async Task<IUnitOfWorkScope> BeginAsync(
        FoundationDbContext database,
        CancellationToken cancellationToken
    ) =>
        database.Database.CurrentTransaction is null
            ? await OwnedScope.BeginAsync(database, cancellationToken)
            : new AmbientParticipation();

    private sealed class OwnedScope : IUnitOfWorkScope
    {
        private OwnedScope(IDbContextTransaction transaction) => Transaction = transaction;

        public static async Task<OwnedScope> BeginAsync(
            FoundationDbContext database,
            CancellationToken cancellationToken
        ) =>
            new(await database.Database.BeginTransactionAsync(cancellationToken));

        public IDbContextTransaction? Transaction { get; }

        public async Task CommitAsync(CancellationToken cancellationToken) =>
            await (Transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask);

        public async Task RollbackAsync(CancellationToken cancellationToken) =>
            await (Transaction?.RollbackAsync(cancellationToken) ?? Task.CompletedTask);

        public async ValueTask DisposeAsync()
        {
            if (Transaction is not null)
            {
                await Transaction.DisposeAsync();
            }
        }
    }

    private sealed class AmbientParticipation : IUnitOfWorkScope
    {
        public IDbContextTransaction? Transaction => null;

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
