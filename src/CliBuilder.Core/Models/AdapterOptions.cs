namespace CliBuilder.Core.Models;

public record AdapterOptions(
    string AssemblyPath,
    string? ConfigPath = null,
    string? XmlDocPath = null
);
