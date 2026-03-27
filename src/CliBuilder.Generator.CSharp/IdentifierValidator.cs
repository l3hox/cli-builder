using System.Text.RegularExpressions;
using CliBuilder.Core.Models;

namespace CliBuilder.Generator.CSharp;

public static partial class IdentifierValidator
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while"
    };

    private static readonly HashSet<string> ContextualKeywords = new(StringComparer.Ordinal)
    {
        "var", "dynamic", "async", "await", "value", "get", "set",
        "add", "remove", "global", "partial", "where", "when", "yield", "nameof",
        // C# 9-11 keywords (net8.0 target)
        "nint", "nuint", "record", "init",
        // C# 11
        "required", "scoped", "file"
    };

    private static readonly HashSet<string> BoilerplateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "JsonFormatter", "TableFormatter", "AuthHandler", "Program"
    };

    private static readonly Regex ValidIdentifier = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    public static bool IsKeyword(string name)
    {
        var lower = name.ToLowerInvariant();
        return CSharpKeywords.Contains(lower) || ContextualKeywords.Contains(lower);
    }

    public static (string CSharpName, string CliFlag, Diagnostic? Diagnostic) SanitizeParameter(string name)
    {
        var kebab = PascalToKebab(name);
        var lower = name.ToLowerInvariant();

        if (CSharpKeywords.Contains(lower) || ContextualKeywords.Contains(lower))
        {
            return ($"@{name}", $"{kebab}-value",
                new Diagnostic(DiagnosticSeverity.Info, "CB004",
                    $"Parameter '{name}' is a C# keyword — renamed to '@{name}' (code) / '--{kebab}-value' (CLI)"));
        }

        if (BoilerplateNames.Contains(name))
        {
            return ($"@{name}", $"{kebab}-value",
                new Diagnostic(DiagnosticSeverity.Info, "CB004",
                    $"Parameter '{name}' collides with generated class name — renamed to '@{name}' (code) / '--{kebab}-value' (CLI)"));
        }

        if (!ValidIdentifier.IsMatch(name))
        {
            var safe = SanitizeToIdentifier(name);
            return (safe, PascalToKebab(safe),
                new Diagnostic(DiagnosticSeverity.Info, "CB204",
                    $"Identifier '{name}' sanitized to '{safe}'"));
        }

        return (name, kebab, null);
    }

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsPathSafe(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Contains('/') || name.Contains('\\')) return false;
        if (name.Contains("..")) return false;
        if (name.Contains('\0')) return false;
        if (name == ".") return false;
        if (name.Length > 200) return false;

        // Windows reserved device names (with or without extension)
        var baseName = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        if (WindowsReservedNames.Contains(baseName)) return false;

        return true;
    }

    public static string PascalToKebab(string pascal)
    {
        if (string.IsNullOrEmpty(pascal)) return pascal;

        var result = new System.Text.StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                bool prevUpper = char.IsUpper(pascal[i - 1]);
                bool nextLower = i + 1 < pascal.Length && char.IsLower(pascal[i + 1]);
                bool prevLower = char.IsLower(pascal[i - 1]);

                // Insert hyphen when:
                // - transitioning from lowercase to uppercase (e.g., "get" → "Get")
                // - at the end of an acronym before a new word (e.g., "API" → "Key")
                if (prevLower || (prevUpper && nextLower))
                    result.Append('-');
            }
            result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }

    public static string KebabToPascal(string kebab)
    {
        if (string.IsNullOrEmpty(kebab)) return kebab;

        return string.Concat(
            kebab.Split('-', StringSplitOptions.RemoveEmptyEntries)
                 .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string SanitizeToIdentifier(string name)
    {
        var result = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (result.Length == 0)
            {
                if (char.IsLetter(c) || c == '_')
                    result.Append(c);
                else
                    result.Append('_');
            }
            else
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    result.Append(c);
            }
        }
        return result.Length > 0 ? result.ToString() : "_invalid";
    }
}
