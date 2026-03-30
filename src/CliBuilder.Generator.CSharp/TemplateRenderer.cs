using System.Reflection;
using System.Text;
using Scriban;
using Scriban.Runtime;

namespace CliBuilder.Generator.CSharp;

public class TemplateRenderer
{
    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;

    public TemplateRenderer()
    {
        _assembly = typeof(TemplateRenderer).Assembly;
        _resourcePrefix = "CliBuilder.Generator.CSharp.Templates.";
    }

    /// <summary>
    /// Render a template to a file. Returns the full path of the written file.
    /// All output uses LF line endings and UTF-8 without BOM.
    /// </summary>
    public string RenderToFile(string templateName, string outputDir, string fileName, object model)
    {
        var rendered = Render(templateName, model);
        var outputPath = Path.Combine(outputDir, fileName);

        // Ensure parent directory exists
        var dir = Path.GetDirectoryName(outputPath);
        if (dir != null) Directory.CreateDirectory(dir);

        // LF enforcement + UTF-8 no BOM
        var content = rendered.Replace("\r\n", "\n");
        File.WriteAllText(outputPath, content, new UTF8Encoding(false));

        return outputPath;
    }

    /// <summary>
    /// Render a template to a string.
    /// </summary>
    public string Render(string templateName, object model)
    {
        var templateText = LoadTemplate(templateName);
        return RenderInline(templateText, model);
    }

    /// <summary>
    /// Render an inline template string with the standard context (custom functions, renamer).
    /// </summary>
    internal string RenderInline(string templateText, object model)
    {
        var template = Template.Parse(templateText);

        if (template.HasErrors)
        {
            var errors = string.Join("\n", template.Messages.Select(m => m.ToString()));
            throw new InvalidOperationException($"Template has errors:\n{errors}");
        }

        var context = CreateContext(model);
        return template.Render(context);
    }

    private TemplateContext CreateContext(object model)
    {
        var context = new TemplateContext
        {
            // Use LF for newlines in template output
            NewLine = "\n"
        };

        // Push model data
        var scriptObject = new ScriptObject();
        scriptObject.Import(model, renamer: MemberRenamer);
        context.PushGlobal(scriptObject);

        // Register custom functions
        var functions = new ScriptObject();
        functions.Import("escape_csharp", new Func<string?, string>(EscapeCSharp));
        functions.Import("to_var_name", new Func<string?, string>(ToVarName));
        context.PushGlobal(functions);

        return context;
    }

    private string LoadTemplate(string name)
    {
        var resourceName = _resourcePrefix + name;
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            var available = string.Join(", ", _assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"Template '{name}' not found as embedded resource '{resourceName}'. " +
                $"Available: {available}");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Scriban member renamer: PascalCase → snake_case.
    /// CliName → cli_name, SdkVersion → sdk_version.
    /// </summary>
    private static string MemberRenamer(MemberInfo member)
    {
        var name = member.Name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                bool prevUpper = char.IsUpper(name[i - 1]);
                bool nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (!prevUpper || nextLower)
                    sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // -----------------------------------------------------------
    // Custom Scriban functions
    // -----------------------------------------------------------

    /// <summary>
    /// Defense-in-depth: convert a string to a C# verbatim string literal.
    /// Primary sanitization happens in ModelMapper; this is the template-layer safety net.
    /// </summary>
    private static string EscapeCSharp(string? value)
    {
        if (value is null) return "null";
        // C# verbatim string: @"..." with doubled quotes
        var escaped = value.Replace("\"", "\"\"");
        return $"@\"{escaped}\"";
    }

    private static string ToVarName(string? value) =>
        IdentifierValidator.KebabToCamelCase(value ?? "");
}
