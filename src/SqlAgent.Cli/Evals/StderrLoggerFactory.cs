using Microsoft.Extensions.Logging;

namespace SqlAgent.Cli.Evals;

/// <summary>Warnings and errors to standard error, so a failing model call during a run shows its cause. Avoids a logging package for the CLI.</summary>
internal sealed class StderrLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public ILogger<T> CreateLogger<T>() => new StderrLogger<T>();

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class StderrLogger<T>() : StderrLogger(typeof(T).Name), ILogger<T>;

    private class StderrLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Console.Error.WriteLine($"  {logLevel.ToString().ToLowerInvariant()}: {category}: {formatter(state, exception)}"
                                    + (exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})"));
        }
    }
}
