using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI.Providers.DeepSeek;

namespace Vinx.Erp.AI.UnitTests.Providers.DeepSeek;

/// <summary>
/// Logger de captura para provar redaction: acumula nível, mensagem formatada,
/// escopos e mensagem de exceção sem reproduzir conteúdo sensível.
/// </summary>
internal sealed class RecordingLogger : ILogger<DeepSeekChatClient>
{
    private readonly Lock _lock = new();
    private readonly StringBuilder _text = new();

    internal string Text
    {
        get
        {
            lock (_lock)
            {
                return _text.ToString();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        Append("scope", state.ToString());
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _text.Append(logLevel).Append(' ').Append(formatter(state, exception));
            if (exception is not null)
            {
                _text.Append(" ex=").Append(exception.Message);
            }
            _text.AppendLine();
        }
    }

    private void Append(string kind, string? value)
    {
        lock (_lock)
        {
            _text.Append(kind).Append(' ').AppendLine(value);
        }
    }
}
