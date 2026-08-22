using Microsoft.Extensions.Logging;

namespace AwsBilling.Tests;

/// <summary>
/// Логгер-записчик: хранит отформаттированные сообщения для проверок
/// текстов логов (ветки «ошибка — только в лог, хост живёт»).
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _sync = new();
    private readonly List<string> _messages = new();

    /// <summary>Сообщения в порядке записи (снимок).</summary>
    public IReadOnlyList<string> Messages
    {
        get { lock (_sync) return _messages.ToList(); }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => NoOpScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
            _messages.Add(formatter(state, exception));
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}
