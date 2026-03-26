namespace CliBuilder.Core.Models;

public record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message
);

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}
