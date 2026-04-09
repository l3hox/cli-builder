using CliBuilder.Core.Models;

namespace CliBuilder;

public static class DiagnosticsFormatter
{
    public static void Print(IReadOnlyList<Diagnostic> diagnostics, TextWriter? writer = null)
    {
        if (diagnostics.Count == 0)
            return;

        writer ??= Console.Error;
        var useColor = writer == Console.Error && !Console.IsErrorRedirected;

        var grouped = diagnostics
            .OrderBy(d => d.Severity switch
            {
                DiagnosticSeverity.Error => 0,
                DiagnosticSeverity.Warning => 1,
                _ => 2
            })
            .ThenBy(d => d.Code);

        foreach (var d in grouped)
        {
            var label = d.Severity switch
            {
                DiagnosticSeverity.Error => "ERROR",
                DiagnosticSeverity.Warning => "WARN ",
                _ => "INFO "
            };

            if (useColor)
            {
                var color = d.Severity switch
                {
                    DiagnosticSeverity.Error => "\x1b[31m",   // red
                    DiagnosticSeverity.Warning => "\x1b[33m", // yellow
                    _ => "\x1b[90m"                            // dim
                };
                writer.WriteLine($"{color}[{label}] {d.Code,-6} {d.Message}\x1b[0m");
            }
            else
            {
                writer.WriteLine($"[{label}] {d.Code,-6} {d.Message}");
            }
        }
    }
}
