namespace AwsBilling.Collection;

/// <summary>Собирает один отчёт по указанным сервисам AWS.</summary>
public interface ICostExplorerCollector
{
    Task<CollectionResult> CollectAsync(
        IReadOnlyList<string> serviceNames,
        CancellationToken cancellationToken);
}
