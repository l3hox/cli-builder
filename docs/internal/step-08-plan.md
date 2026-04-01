# Step 8: Multi-Arg Constructor Support

**Prerequisite:** Step 7 complete. Generated CLIs make real SDK calls. OpenAI CLI compiles (169 ops, 35 wired, 134 echo). Live-validated against OpenAI API (get-models). 315 tests, 82.7% coverage.
**Output:** Multi-arg constructor support unblocks 35 chat/embedding/image/audio/moderation operations. The generated OpenAI CLI can make real chat completions.

---

## Problem Statement

Of 169 OpenAI operations, only 35 are wired with real SDK calls. The largest blocker:

| Blocker | Ops blocked | Root cause |
|---------|-------------|------------|
| Multi-arg constructors | 35 | Sub-clients need `(string model, ApiKeyCredential cred)` — template passes 1 arg |

Step 8 focuses exclusively on this. `--json-input` deserialization (the second blocker affecting 78 operations) is deferred — see "Deferred" section for rationale.

---

## Council Review Findings

A Developer Council (SoftwareDeveloper, QaTester) reviewed the initial plan and identified:

**P0 — Constructor selection logic must be reversed.** The current adapter prefers the fewest-param constructor. For multi-arg support, we need: among constructors WITH an auth param, prefer the one with the MOST user-facing config params. `ChatClient(string model, ApiKeyCredential cred)` must win over `ChatClient(ApiKeyCredential cred)`.

**P0 — `--json-input` deserialization deferred.** `ChatMessage` is abstract — `JsonSerializer.Deserialize<List<ChatMessage>>()` fails at runtime. The OpenAI SDK uses a discriminated union pattern (factory methods), not constructors. Solving this requires custom `JsonConverter<T>` or SDK-specific serialization hooks. This is a separate design problem — defer to step 9.

**P1 — `--model` must be per-resource (on `Build()` signature), not per-command.** All operations under `chat` share the same `ChatClient`, so `--model` is a resource-level option. The `Build()` method signature must change. `Program.sbn` must pass it.

**P1 — Remove old `ConstructorAuthTypeName`/`ConstructorAuthTypeNamespace` fields.** Replace with `ConstructorParams`. No coexistence.

**P2 — Optional constructor params need template handling.** `ctor(string model, ApiKeyCredential cred, string? endpoint = null)` — template must omit `endpoint` from the call when null.

---

## Phase 8A: Multi-Arg Constructor Support

**Goal:** Sub-clients like `ChatClient(string model, ApiKeyCredential cred)` can be constructed. Unblocks 35 operations.

### Design

The OpenAI SDK pattern: sub-clients have constructors with a `string model` parameter (the model name like "gpt-4o") plus an auth credential. The `model` parameter is not auth — it's a configuration value that should be a resource-level CLI option.

**Constructor selection rule (reversed from step 7):**
1. Find all public constructors that contain at least one auth param (ApiKeyCredential > Credential > apiKey string).
2. Among those, prefer the one with the MOST non-auth, non-infrastructure, non-optional params. This picks `ChatClient(string model, ApiKeyCredential cred)` over `ChatClient(ApiKeyCredential cred)`.
3. Stable tiebreaker on parameter names.
4. If no constructor has an auth param → `CanConstruct = false` (unchanged).

**`--model` as a resource-level option:**
- Non-auth constructor params (like `model`) become resource-level options, passed via the `Build()` method signature (alongside `jsonOption` and `apiKeyOption`).
- `Program.sbn` creates the option and passes it to each resource's `Build()`.
- All operations under a resource share the same constructor config (same `--model` value).

### Core model changes

**`src/CliBuilder.Core/Models/Resource.cs`** — replace single auth fields with constructor param list:

```csharp
public record Resource(
    string Name, string? Description,
    IReadOnlyList<Operation> Operations,
    string? SourceClassName = null,
    string? SourceNamespace = null,
    IReadOnlyList<ConstructorParam>? ConstructorParams = null  // REPLACES ConstructorAuthTypeName/Namespace
);

public record ConstructorParam(
    string Name,           // "model", "apiKey", "credential"
    string TypeName,       // "string", "ApiKeyCredential"
    string? TypeNamespace, // "System.ClientModel"
    bool IsAuth,           // true for credential params (detected by IsApiKeyCredential/IsCredential/IsApiKey)
    bool IsRequired        // false for optional params with defaults
);
```

The old `ConstructorAuthTypeName` and `ConstructorAuthTypeNamespace` fields are **removed**.

### Adapter changes

**`src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs`**:

Replace `ExtractConstructorAuthType` with `ExtractConstructorParams`:

```csharp
private IReadOnlyList<ConstructorParam>? ExtractConstructorParams(Type type)
{
    // Find constructors that have at least one auth param
    var candidates = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
        .Where(ctor => ctor.GetParameters().Any(p =>
            IsApiKeyCredentialParameter(p) || IsCredentialParameter(p) || IsApiKeyParameter(p)))
        .ToList();

    if (candidates.Count == 0) return null;

    // Among candidates, prefer the one with the MOST user-facing params
    // (picks ChatClient(string model, ApiKeyCredential cred) over ChatClient(ApiKeyCredential cred))
    var best = candidates
        .OrderByDescending(c => c.GetParameters().Count(p =>
            !InfrastructureParamTypes.Contains(p.ParameterType.FullName ?? "")))
        .ThenBy(c => string.Join(",", c.GetParameters().Select(p => p.Name)))
        .First();

    var result = new List<ConstructorParam>();
    foreach (var param in best.GetParameters())
    {
        if (InfrastructureParamTypes.Contains(param.ParameterType.FullName ?? ""))
            continue;

        bool isAuth = IsApiKeyCredentialParameter(param) || IsCredentialParameter(param) || IsApiKeyParameter(param);
        result.Add(new ConstructorParam(
            param.Name ?? "unknown",
            isAuth && param.ParameterType.FullName == "System.String" ? "string" : param.ParameterType.Name,
            isAuth && param.ParameterType.FullName == "System.String" ? null : param.ParameterType.Namespace,
            isAuth,
            param.HasDefaultValue));
    }
    return result;
}
```

The resource construction loop changes:
```csharp
var ctorParams = ExtractConstructorParams(type);
resources.Add(new Resource(noun, null, operations,
    SourceClassName: type.Name, SourceNamespace: type.Namespace,
    ConstructorParams: ctorParams));
```

### Generator model changes

**`src/CliBuilder.Generator.CSharp/GeneratorModel.cs`**:

```csharp
public record ResourceModel(
    string Name, string ClassName, string? Description,
    IReadOnlyList<OperationModel> Operations,
    string? SourceClassName = null,
    string? SourceNamespace = null,
    string? ConstructorExpression = null,      // "modelValue, new ApiKeyCredential(credential)"
    IReadOnlyList<string>? RequiredNamespaces = null,
    bool CanConstruct = false,
    IReadOnlyList<ConstructorConfigParam>? ConstructorConfigParams = null  // non-auth params → CLI flags
);

public record ConstructorConfigParam(
    string CliFlag,        // "model"
    string VarName,        // "modelValue" (pre-computed)
    string CSharpType,     // "string"
    bool IsRequired        // true
);
```

`ConstructorAuthExpression` is replaced by `ConstructorExpression` — which now includes all args:
- Single-auth: `"credential"` or `"new ApiKeyCredential(credential)"`
- Multi-arg: `"modelValue, new ApiKeyCredential(credential)"`
- Multi-arg with optional: `"modelValue, new ApiKeyCredential(credential)"` (optional params omitted from call)

### ModelMapper changes

**`src/CliBuilder.Generator.CSharp/ModelMapper.cs`**:

Build `ConstructorExpression` from `ConstructorParams`:
```csharp
private static (string? Expression, IReadOnlyList<ConstructorConfigParam> ConfigParams, bool CanConstruct)
    BuildConstructorInfo(Resource resource)
{
    if (resource.ConstructorParams is null || resource.ConstructorParams.Count == 0)
        return (null, Array.Empty<ConstructorConfigParam>(), false);

    var configParams = new List<ConstructorConfigParam>();
    var argParts = new List<string>();

    foreach (var p in resource.ConstructorParams)
    {
        if (p.IsAuth)
        {
            argParts.Add(p.TypeName == "string" ? "credential" : $"new {p.TypeName}(credential)");
        }
        else if (p.IsRequired)
        {
            var (_, cliFlag, _) = IdentifierValidator.SanitizeParameter(p.Name);
            var varName = KebabToCamelCase(cliFlag) + "Value";
            configParams.Add(new ConstructorConfigParam(cliFlag, varName, MapPrimitiveForConfig(p.TypeName), true));
            argParts.Add(varName);
        }
        // Optional non-auth params: omitted from constructor call for v1
    }

    return (string.Join(", ", argParts), configParams, true);
}
```

### Template changes

**`ResourceCommands.sbn`** — resource-level constructor config params:

```scriban
    public static Command Build(Option<bool> jsonOption
        {{- if has_auth }}, Option<string?> apiKeyOption{{ end }}
        {{- for cp in resource.constructor_config_params }}, Option<{{ cp.csharp_type }}> {{ cp.var_name }}Option{{ end }})
    {
```

Inside each handler, read the config params:
```scriban
{{~ for cp in resource.constructor_config_params }}
                    var {{ cp.var_name }} = ctx.ParseResult.GetValueForOption({{ cp.var_name }}Option);
{{~ end }}
```

Client construction (unchanged syntax, but expression now includes config args):
```scriban
                    var client = new {{ resource.source_class_name }}({{ resource.constructor_expression }});
```

**`Program.sbn`** — create and pass config options:

```scriban
{{~ for resource in resources }}
{{~ for cp in resource.constructor_config_params }}
        var {{ cp.var_name }}Option = new Option<{{ cp.csharp_type }}>("--{{ cp.cli_flag }}")
        { IsRequired = {{ if cp.is_required }}true{{ else }}false{{ end }} };
        root.AddGlobalOption({{ cp.var_name }}Option);
{{~ end }}
        root.AddCommand({{ resource.class_name }}Commands.Build(jsonOption
            {{- if auth }}, apiKeyOption{{ end }}
            {{- for cp in resource.constructor_config_params }}, {{ cp.var_name }}Option{{ end }}));
{{~ end }}
```

**Note:** `--model` is a global option because different resources might share it. If two resources have the same config param name (`model`), the option is created once and shared.

### What this unblocks

All 35 "no auth constructor" operations gain real SDK calls:
- `chat complete-chat` (still needs `--json-input` for `messages` param — but constructor works)
- `embedding generate-embeddings` (needs `--json-input` for `inputs` param)
- `image generate-images` (has only options class params — fully wired!)
- `audio generate-speech`, `audio transcribe-audio`
- `moderation classify-text-inputs`

**Important:** Some of these operations ALSO have complex direct params (like `IEnumerable<ChatMessage>`). Multi-arg constructors unblock the CLIENT construction, but `CanWireSdkCall` may still be `false` for operations with unconvertible params. The net effect:
- Operations with only options-class params → fully wired
- Operations with complex direct params → still echo (until `--json-input` in step 9)

### Tests

```
// Adapter
ExtractConstructorParams_MultiArg_ReturnsAllParams
ExtractConstructorParams_SingleArg_BackwardCompatible
ExtractConstructorParams_NoAuthCtor_ReturnsNull
ExtractConstructorParams_PrefersRichestWithAuth

// ModelMapper
BuildConstructorInfo_MultiArg_Expression
BuildConstructorInfo_SingleArg_BackwardCompatible
BuildConstructorInfo_ConfigParams_FromNonAuthParams
CanConstruct_WithParams_ReturnsTrue
CanConstruct_NullParams_ReturnsFalse

// Generator
Generate_WithConfigParam_HasModelOption
Generate_WithConfigParam_PassesMultipleArgs
Generate_SingleArgCtor_UnchangedOutput

// Integration
GenerateOpenAi_Compiles (must still pass)
MissingRequiredConfigParam_ExitsNonZero (--model omitted)
```

---

## Phase 8B: Validation + Cleanup

### Live API validation

After 8A, operations with only options-class params can be called:
```bash
export OPENAI_APIKEY=sk-...
# Image generation (options class only — fully wired after 8A)
openai-cli image generate-images --model dall-e-3 --prompt "a cat" --json

# Moderation (no model needed for some overloads)
openai-cli moderation classify-text-inputs --model omni-moderation-latest --json-input '...'
```

For `chat complete-chat`, the constructor works but `messages` is `IEnumerable<ChatMessage>` (complex direct param) → still echo until step 9.

### Documentation

- Update README with new demo showing `--model` flag
- Update AGENTS.md
- Update design-notes.md with multi-arg constructor rule, constructor config param policy
- Update FUTURE.md: move `--json-input` to step 9 candidates

### Coverage

Run `./scripts/coverage.sh` — maintain >= 80%.

---

## Implementation Order

```
8A.1: Core model — ConstructorParam record, remove old auth fields from Resource
8A.2: Adapter — ExtractConstructorParams (replaces ExtractConstructorAuthType)
8A.3: Generator model — ConstructorExpression, ConstructorConfigParam
8A.4: ModelMapper — BuildConstructorInfo, update MapResource
8A.5: Template — ResourceCommands.sbn (config param options, multi-arg call)
8A.6: Template — Program.sbn (create + pass config options)
8A.7: Fixture regen + golden regen + all tests green
8A.8: New tests for multi-arg constructors

8B: Live validation + docs + coverage
```

---

## Files to Modify

| Phase | File | Change |
|-------|------|--------|
| 8A | `src/CliBuilder.Core/Models/Resource.cs` | Add `ConstructorParam`, `ConstructorParams`; REMOVE `ConstructorAuthTypeName/Namespace` |
| 8A | `src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs` | `ExtractConstructorParams` replaces `ExtractConstructorAuthType` |
| 8A | `src/CliBuilder.Generator.CSharp/GeneratorModel.cs` | `ConstructorExpression`, `ConstructorConfigParam`; REMOVE `ConstructorAuthExpression` |
| 8A | `src/CliBuilder.Generator.CSharp/ModelMapper.cs` | `BuildConstructorInfo`, update `MapResource` |
| 8A | `src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn` | Config param options in Build(), read in handler |
| 8A | `src/CliBuilder.Generator.CSharp/Templates/Program.sbn` | Create config options, pass to Build() |
| 8A | Tests + fixtures + golden files | Regen all |
| 8B | Docs: AGENTS.md, README, design-notes, FUTURE.md | Update |

---

## Risk Assessment

**8A (multi-arg constructors):** Medium risk. Reverses constructor preference direction (fewest → most params). Must verify TestSdk still works — its constructors are single-arg, so they have exactly one auth param and zero config params. The `ConstructorExpression` for single-arg should be identical to the old `ConstructorAuthExpression`. The `Build()` signature change in ResourceCommands.sbn affects all generated CLIs.

**Backward compatibility:** Single-arg constructors produce `ConstructorParams = [ConstructorParam("apiKey", "string", null, IsAuth: true, IsRequired: true)]`. The expression becomes `"credential"` — same as before. Zero config params. `Build()` signature unchanged for these resources.

---

## Deferred to Step 9

**`--json-input` deserialization** — deferred per council recommendation. Root cause: `ChatMessage` is abstract and uses a discriminated union pattern (factory methods like `ChatMessage.CreateUserMessage()`). `System.Text.Json` cannot deserialize abstract types without custom converters. Design options for step 9:

1. **SDK-specific serialization:** Use the SDK's own `BinaryData.FromString(json)` pattern — many OpenAI types support construction from raw JSON via `BinaryData`.
2. **Custom JsonConverter registration:** Generate converters for known abstract hierarchies.
3. **Pass-through:** Accept JSON string as the CLI param and pass it directly to the SDK method as `BinaryData` — the SDK handles deserialization internally.

Option 3 is the most promising — the OpenAI SDK's protocol methods accept `BinaryContent` which can be constructed from a JSON string. This bypasses the abstract type problem entirely.

**Factory return operations** (21 ops) — `OpenAIClient.GetChatClient()` etc. Users call sub-clients directly; factory methods are SDK internals.
