using CliBuilder.Core.Models;

namespace CliBuilder.Generator.CSharp;

public static class ParameterFlattener
{
    public record FlattenResult(
        IReadOnlyList<FlatParameter> Parameters,
        bool NeedsJsonInput,
        IReadOnlyList<Diagnostic> Diagnostics);

    public static FlattenResult Flatten(IReadOnlyList<Parameter> parameters, int threshold = 10)
    {
        var flatParams = new List<FlatParameter>();
        var needsJsonInput = false;
        var diagnostics = new List<Diagnostic>();

        foreach (var param in parameters)
        {
            if (param.Type.Kind == TypeKind.Class && param.Type.Properties != null)
            {
                FlattenOptionsClass(param.Type.Properties, threshold,
                    flatParams, ref needsJsonInput, diagnostics);
            }
            else
            {
                // Primitive / enum param — always flat
                flatParams.Add(MapParameter(param, diagnostics));
            }
        }

        // Deduplicate — multiple options classes on the same operation may share
        // property names (e.g., Stream.Position on two different Stream params).
        // Keep the first occurrence, emit CB303 for dropped required params.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<FlatParameter>();
        foreach (var fp in flatParams)
        {
            if (seen.Add(fp.CliFlag))
            {
                deduped.Add(fp);
            }
            else if (fp.IsRequired)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB303",
                    $"Required parameter '--{fp.CliFlag}' duplicated across options classes — " +
                    "only the first occurrence is used."));
            }
        }

        return new FlattenResult(deduped, needsJsonInput, diagnostics);
    }

    private static void FlattenOptionsClass(
        IReadOnlyList<Parameter> properties,
        int threshold,
        List<FlatParameter> flatParams,
        ref bool needsJsonInput,
        List<Diagnostic> diagnostics)
    {
        var scalarProps = properties
            .Where(p => IsScalar(p.Type))
            .OrderBy(p => !p.Required)   // required first
            .ThenBy(p => p.Name)         // alphabetical
            .ToList();

        var hasNested = properties.Any(p => !IsScalar(p.Type));

        if (hasNested)
        {
            // Nested objects present → always add --json-input,
            // but still flatten ALL scalar props (no threshold truncation)
            needsJsonInput = true;
            flatParams.AddRange(scalarProps.Select(p => MapParameter(p, diagnostics)));
        }
        else if (scalarProps.Count > threshold)
        {
            // Too many scalars → flatten first {threshold}, add --json-input
            needsJsonInput = true;
            flatParams.AddRange(scalarProps.Take(threshold).Select(p => MapParameter(p, diagnostics)));

            // Emit CB301 for required props beyond threshold
            foreach (var hidden in scalarProps.Skip(threshold).Where(p => p.Required))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB301",
                    $"Required parameter '{hidden.Name}' is only accessible " +
                    "via --json-input due to flatten threshold."));
            }
        }
        else
        {
            // All scalar, within threshold → flatten all
            flatParams.AddRange(scalarProps.Select(p => MapParameter(p, diagnostics)));
        }
    }

    private static FlatParameter MapParameter(Parameter param, List<Diagnostic> diagnostics)
    {
        var (csharpName, cliFlag, diag) = IdentifierValidator.SanitizeParameter(param.Name);
        if (diag != null) diagnostics.Add(diag);

        return new FlatParameter(
            CliFlag: cliFlag,
            PropertyName: csharpName,
            CSharpType: ModelMapper.MapTypeName(param.Type, forCliParam: true),
            IsRequired: param.Required,
            DefaultValueLiteral: ModelMapper.SanitizeDefaultValue(param.DefaultValue, param.Type, diagnostics),
            Description: ModelMapper.SanitizeString(param.Description),
            EnumValues: param.Type.EnumValues);
    }

    private static bool IsScalar(TypeRef type) =>
        type.Kind is TypeKind.Primitive or TypeKind.Enum;
}
