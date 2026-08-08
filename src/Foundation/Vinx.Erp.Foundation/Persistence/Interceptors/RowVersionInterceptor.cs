using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Vinx.Erp.Foundation.Persistence.Interceptors;

public sealed class RowVersionInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        AdvanceRowVersions(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AdvanceRowVersions(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void AdvanceRowVersions(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries()
                     .Where(entry => entry.State == EntityState.Modified && entry.Metadata.FindProperty("RowVersion") is not null))
        {
            AdvanceRowVersion(entry);
        }
    }

    private static void AdvanceRowVersion(EntityEntry entry)
    {
        var rowVersion = entry.Property("RowVersion");
        var originalValue = rowVersion.OriginalValue as long?;
        rowVersion.CurrentValue = checked((originalValue ?? 0L) + 1L);
    }
}
