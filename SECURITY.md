# Security Policy

cli-builder is a build-time tool — it reads SDK artifacts (compiled .NET assemblies, installed Python packages) and writes generated CLI source code. It does not run as a long-lived service and does not handle user secrets at runtime. Security considerations fall into three categories.

## Stated security properties

**1. No code execution during SDK analysis.**

The .NET adapter uses `MetadataLoadContext` only — assemblies are loaded for reflection but never executed. There is no `AssemblyLoadContext`, no `Invoke`, no dynamic method emission. A crafted SDK assembly cannot execute code during metadata extraction. See [ADR-003](docs/ADR.md#adr-003-metaloadcontext-only--no-code-execution-during-analysis).

The Python adapter uses `inspect` and `typing.get_type_hints()` on **already-imported** modules. It does import the target package (standard Python import semantics apply), but does not `exec()` arbitrary strings, does not call any SDK methods, and does not instantiate SDK classes. A malicious Python package with import-time side effects can affect the Python adapter — this is an inherent property of Python's import model, not a cli-builder bug. Running the Python adapter against untrusted packages is outside our threat model.

**2. Generated code is sanitized at template boundaries.**

Metadata strings (descriptions, identifiers) from SDKs flow into generated source code. Three sanitization surfaces protect against injection:

- **C# source code**: `SanitizeString` (Scriban/Tera syntax neutralization) + `escape_csharp` (verbatim string literals) + `IdentifierValidator` (keyword denylist, path safety)
- **XML (`.csproj`)**: `SanitizeXmlValue` — escapes `<`, `>`, `"`, `&`, `'` to prevent MSBuild injection during `dotnet build`
- **Python source code**: `py_str` Tera filter — escapes `\` and `"` in description strings

A crafted SDK with template-engine metacharacters (`{{`, `%}`) or XML special characters in its documentation cannot break the generated build or inject code. This is defense-in-depth: each layer neutralizes the injection vector relevant to its output format.

**3. Supply chain is pinned where it matters.**

- `click==8.*` pinned in `.github/workflows/ci.yml` — prevents a click 9.x release (or compromised upload) from invalidating tests or executing arbitrary code on CI runners.
- Dependabot covers github-actions, cargo, nuget, and pip (ADR-021). Security advisories surface independently of the update cadence.
- CI runners are GitHub-hosted ephemeral VMs — no persistent state between runs.

## Not-promises

cli-builder does **not**:

- Protect against SDK authors intentionally documenting parameters with misleading descriptions. The generated CLI faithfully reproduces what the SDK declares. Review the SDK before trusting its generated CLI.
- Validate generated CLI behavior against the SDK's actual API contract. The generator trusts reflection/inspect output. If a misleading type annotation produces a misleading CLI flag, cli-builder has no way to detect that.
- Handle secrets in the generated CLI. Generated CLIs resolve API keys via environment variables (`STRIPE_API_KEY`, `OPENAI_API_KEY`, etc.) or a `--api-key` flag. No secrets ship in the generated source.
- Sandbox the generated CLI. Once generated, it's normal Python or C# code that the user runs in their own environment.

## Reporting a vulnerability

If you believe you've found a vulnerability that affects the three properties above (code execution during analysis, injection via generated code, supply-chain compromise), please email jlehotsky@gmail.com with `[cli-builder security]` in the subject. Include a reproducer if possible.

Non-security bugs should be filed at <https://github.com/l3hox/cli-builder/issues>.

This is a portfolio project maintained by a single author. Response time for security reports is best-effort — typically within a week. No bug bounty.
