namespace LifeScript;

/// <summary>A position in the source. Lines and columns are 1-based.</summary>
public readonly record struct SourceSpan(int Line, int Column, int Length)
{
    public static readonly SourceSpan None = new(0, 0, 0);

    public override string ToString() => Line == 0 ? "?" : $"{Line}:{Column}";
}

public enum Severity
{
    Error,
    Warning,
}

public sealed record Diagnostic(Severity Severity, string Message, SourceSpan Span)
{
    public override string ToString() =>
        $"{Span}: {(Severity == Severity.Error ? "error" : "warning")}: {Message}";
}

/// <summary>Thrown when a script fails to parse or compile. Carries every diagnostic found.</summary>
public sealed class LifeScriptException(IReadOnlyList<Diagnostic> diagnostics)
    : Exception(string.Join(Environment.NewLine, diagnostics))
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

    public LifeScriptException(string message, SourceSpan span)
        : this([new Diagnostic(Severity.Error, message, span)]) { }
}

/// <summary>Thrown while a simulation is running (e.g. a for loop that never ends).</summary>
public sealed class LifeScriptRuntimeException(string message, SourceSpan span)
    : Exception($"{span}: {message}")
{
    public SourceSpan Span { get; } = span;
}
