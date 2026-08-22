using AwsBilling.Database;

namespace AwsBilling.Collection;

/// <summary>Запускает единый защищённый от параллельности цикл коллекции.</summary>
public interface IBillingCollectionRunner
{
    Task<Report?> RunAsync(CancellationToken cancellationToken = default);
}
