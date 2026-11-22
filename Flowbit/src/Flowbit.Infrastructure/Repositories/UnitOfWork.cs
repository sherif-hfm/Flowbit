using Microsoft.EntityFrameworkCore.Storage;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;

namespace Flowbit.Infrastructure.Repositories;

public sealed class UnitOfWork(AppDbContext dbContext) : IUnitOfWork
{
    private EfWorkflowTransaction? _open;

    public async Task<IWorkflowTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        _open = new EfWorkflowTransaction(transaction, dbContext, this);
        return _open;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);

    public void DiscardChanges() =>
        dbContext.ChangeTracker.Clear();

    public void AfterCommit(Action callback)
    {
        if (_open is null)
        {
            callback();
            return;
        }

        _open.AfterCommit.Add(callback);
    }

    private sealed class EfWorkflowTransaction(
        IDbContextTransaction transaction,
        AppDbContext dbContext,
        UnitOfWork owner) : IWorkflowTransaction
    {
        private bool _committed;

        public List<Action> AfterCommit { get; } = [];

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            _committed = true;
            Close();
            foreach (var callback in AfterCommit)
            {
                callback();
            }

            AfterCommit.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            Close();
            await transaction.DisposeAsync();
            if (!_committed)
            {
                // A rolled-back transaction's observations describe work that never existed.
                AfterCommit.Clear();
                // EF keeps entity mutations after a database rollback. Leaving
                // them tracked lets a later job-only SaveChanges accidentally
                // re-persist workflow state that was intentionally rolled back.
                dbContext.ChangeTracker.Clear();
            }
        }

        private void Close()
        {
            if (ReferenceEquals(owner._open, this))
            {
                owner._open = null;
            }
        }
    }
}
