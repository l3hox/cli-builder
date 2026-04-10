# Step 12: Python Adapter — Standalone Subprocess

**Prerequisite:** Steps 1-11 complete. SdkMetadata is language-neutral. `cli-builder inspect --json` serves as the .NET adapter's subprocess interface. 397 tests, 0 failures.
**Output:** `cli-builder-adapter-python` is a standalone Python package that extracts `SdkMetadata` JSON from Python SDK packages using type annotations and `.pyi` stubs (no runtime import). Validated against a purpose-built TestSdk. Architecture proof: Python JSON → .NET deserialization round-trip.

**Requires Python 3.10+** (stable `get_type_hints`, `X | Y` union syntax, `match` statements).

---

## Problem

cli-builder only supports .NET SDKs. The architecture (ADR-016) calls for subprocess-based adapters in native languages. A Python adapter proves the architecture works and establishes the cross-adapter JSON contract.

---

## Prerequisites (before Phase 1)

### P1. Add `schemaVersion` to JSON envelope

Add a `schemaVersion` field to the serialized JSON output (in the serialization layer, NOT in the `SdkMetadata` record). The record stays version-agnostic; the JSON envelope carries the version.

```json
{
  "schemaVersion": "1",
  "metadata": { ... },
  "diagnostics": [ ... ]
}
```

Both .NET `inspect --json` and the Python adapter must emit this field. The orchestrator rejects incompatible versions.

### P2. Add JSON schema file

Create `docs/sdk-metadata-schema.json` — machine-readable definition of the `SdkMetadata` JSON contract. Both adapters validate against it. This is the cross-adapter regression guard.

### P3. StaticAuthConfig Style discriminator

Add `Style` field to `StaticAuthConfig`:
```csharp
public enum AuthSetupStyle { StaticProperty, ModuleAttribute }
```

.NET adapter sets `StaticProperty` (e.g., `Stripe.StripeConfiguration.ApiKey`). Python adapter sets `ModuleAttribute` (e.g., `stripe.api_key`). Each generator renders the expression for its target language.

**This blocks Phase 7 (wiring into cli-builder CLI).** Without it, Python-originated `StaticAuth` values produce wrong C# code.

---

## Design

### Package structure

```
cli-builder-adapter-python/
  pyproject.toml              # Package metadata, entry point, requires-python >= 3.10
  src/
    cli_builder_adapter/
      __init__.py
      __main__.py             # Entry point: python -m cli_builder_adapter
      cli.py                  # CLI argument parsing (argparse)
      extractor.py            # Core: analyze Python package → SdkMetadata
      type_mapper.py          # Map Python type annotations → TypeRef
      auth_detector.py        # Detect auth patterns (api_key params, env vars)
      models.py               # SdkMetadata dataclasses (mirror Core models)
      json_output.py          # Serialize to SdkMetadata JSON (camelCase + schemaVersion)
  tests/
    test_sdk/                 # Purpose-built Python TestSdk
      __init__.py
      services.py             # CustomerClient, OrderClient, MessageClient
      models.py               # Customer, Order, Message (dataclass)
      auth.py                 # ApiKeyCredential
    test_extractor.py         # Unit + error path tests
    test_type_mapper.py       # Type mapping tests
    test_auth_detector.py     # Auth detection tests
    test_json_output.py       # camelCase serialization, null handling, schema validation
    test_error_paths.py       # Import failure, untyped params, exit code 2
    test_integration.py       # Full extraction → JSON round-trip → schema validation
```

### Invocation

```bash
# Primary: extract from installed package (ADR-013 compliant)
python -m cli_builder_adapter --package stripe --json

# Sub-module targeting (optional)
python -m cli_builder_adapter --package stripe --module stripe.api_resources --json

# Output: SdkMetadata JSON to stdout (with schemaVersion), diagnostics to stderr
# Exit: 0 (success), 1 (errors), 2 (environment failure)
```

`--package` is the primary invocation (aligns with ADR-013: package artifacts). `--module` is for sub-module targeting within a package.

### Extraction strategy

**ADR-013 compliant: no runtime import of SDK code.** The adapter uses annotation-based extraction without executing the SDK's module-level code.

Extraction approach (in priority order):
1. **`.pyi` stub files**: If the package ships `.pyi` stubs (or `py.typed` marker with inline annotations), parse them with `ast.parse`. No `__init__.py` execution.
2. **Inline `__annotations__`**: For packages with `from __future__ import annotations` (PEP 563), annotations are strings that can be parsed without importing.
3. **Controlled import fallback**: If stubs are unavailable, import with a diagnostic warning (`CB601: Package imported at runtime — side effects may occur`). This is a documented ADR-013 exception, not a silent violation.

```
Fallback chain:
  .pyi stubs (ast.parse) → inline annotations (ast.parse) → controlled import (with diagnostic)
```

### Type mapping (Python → TypeRef)

| Python type | TypeKind | TypeRef details |
|-------------|----------|-----------------|
| `str` | Primitive | Name: "str" |
| `int` | Primitive | Name: "int" |
| `float` | Primitive | Name: "float" |
| `bool` | Primitive | Name: "bool" |
| `bytes` | Primitive | Name: "bytes" |
| `None` | Primitive | Name: "None" |
| `datetime` | Primitive | Name: "datetime" |
| `list[T]` | Array | ElementType: T |
| `dict[K, V]` | Dictionary | GenericArguments: [K, V] |
| `Optional[T]` / `T \| None` | (inner kind) | IsNullable=true |
| `Union[A, B]` (non-Optional) | Other | Name: "Union" |
| `Literal["a", "b"]` | Enum | EnumValues: ["a", "b"] |
| `Tuple[T, ...]` | Other | Name: "Tuple" |
| `Enum` subclass | Enum | EnumValues from members |
| `@dataclass` / typed class | Class | Properties from fields |
| `Any` / untyped | Other | Name: "object" |
| `AsyncIterator[T]` | Generic | IsStreaming=true |

### Auth detection patterns

| Pattern | AuthType | StaticAuth Style | Example |
|---------|----------|-----------------|---------|
| `api_key: str` param | ApiKey | — | `Client(api_key="sk_...")` |
| Module-level attribute | ApiKey | ModuleAttribute | `stripe.api_key = "sk_..."` |
| `OPENAI_API_KEY` env var | ApiKey | — | `os.environ["OPENAI_API_KEY"]` |

### SdkMetadata JSON output

Must match the exact schema produced by the .NET adapter + new `schemaVersion` envelope:
- camelCase property names (requires recursive key transformer in `json_output.py`)
- Enum values as camelCase strings
- Indented JSON
- `null` for absent optional fields (not omitted) — matches .NET `System.Text.Json` default
- `schemaVersion` field in envelope

Note: camelCase serialization from Python dataclasses requires a custom recursive transformer (no third-party deps). This is non-trivial but achievable with ~50 lines of stdlib code.

---

## Implementation Order

### Phase 1: Project scaffold + models + JSON schema

1. Create `cli-builder-adapter-python/` directory at repo root
2. `pyproject.toml` with entry point, `requires-python = ">=3.10"`
3. `models.py` — Python dataclasses mirroring all `SdkMetadata` types
4. `json_output.py` — recursive camelCase serializer (stdlib only)
5. Create `docs/sdk-metadata-schema.json` — formal JSON schema derived from .NET fixture
6. Tests: JSON round-trip validates against schema, null fields present (not absent)

### Phase 2: Python TestSdk

Create `tests/test_sdk/` with fully typed service classes:
- `CustomerClient(api_key: str)` with `get`, `list`, `create`
- `OrderClient(api_key: str)` with `get`, `create(items: list[str])`
- `MessageClient(api_key: str)` with `send(messages: list[Message])`, `batch(ids: list[str])`
- `Message` as `ABC` with `UserMessage`, `SystemMessage`
- Typed dataclass models: `Customer`, `Order`
- Options dataclasses: `CreateCustomerOptions`, `CreateOrderOptions`, `SendMessageOptions`
- `CustomerStatus` enum

### Phase 3: Core extractor

1. `extractor.py` — discover service classes, extract operations, parameters, return types
   - Use `ast.parse` on `.pyi` stubs or source files with `__annotations__`
   - Fallback: controlled import with `CB601` diagnostic
2. `type_mapper.py` — map `typing` annotations to `TypeRef`
   - Handle `get_type_hints` `NameError` → fall back to `inspect.signature` annotations
3. `auth_detector.py` — detect `api_key` patterns, module-level auth
4. Tests for each module against TestSdk
5. **Error path tests** (`test_error_paths.py`):
   - Package not installed → exit code 2
   - Module import fails → exit code 2 with diagnostic
   - C-extension method (no signature) → `CB602` warning, method skipped
   - Untyped parameters → `TypeKind.Other`, diagnostic
   - `*args`/`**kwargs` → single `TypeKind.Other` parameter with diagnostic

### Phase 4: CLI entry point

1. `cli.py` — argparse: `--package` (primary), `--module` (sub-targeting), `--json`
2. `__main__.py` — `python -m cli_builder_adapter` entry point
3. Exit codes: 0/1/2 matching the adapter invocation contract
4. Diagnostics to stderr (human-readable format)
5. `schemaVersion` in JSON envelope

### Phase 5: TestSdk validation (architecture proof)

1. Extract TestSdk → JSON output
2. Validate JSON against `docs/sdk-metadata-schema.json`
3. Structural field-by-field comparison with expected output (not string equality)
4. Verify: resource names, operation names, parameter types, auth detection
5. **Cross-adapter round-trip**: deserialize Python adapter JSON with .NET `SdkMetadataJson.Options` — verify no data loss

This is the architecture proof: Python code → SdkMetadata JSON → schema validates → .NET deserializes without error.

---

## Deferred to Step 12b/13

These are explicitly cut from MVP to keep Step 12 focused on the architecture proof:

### Phase 6 (deferred): Stripe validation
- Stripe v5+ uses `StripeObject` with dynamic attributes — needs dedicated handling
- Module-level auth (`stripe.api_key`) needs `StaticAuthConfig.Style` discriminator
- Scoped to service/auth detection only (not model field resolution)

### Phase 7 (deferred): Wire into cli-builder CLI
- **Blocked on** `StaticAuthConfig.Style` discriminator (prerequisite P3)
- Add `--adapter python` flag to `cli-builder generate`
- Shell out to `python3 -m cli_builder_adapter`, read JSON, pass to C# generator
- Python path resolution: use `python3` or configurable interpreter path
- **This is intentionally temporary scaffolding** — replaced by Rust orchestrator in v2.0

### Phase 8 (deferred): Documentation
- Collapsed into normal step completion docs

---

## TestSdk comparison table

| Concept | .NET TestSdk | Python TestSdk |
|---------|-------------|----------------|
| Service class | `CustomerService : class` | `class CustomerClient:` |
| Constructor auth | `CustomerService(string apiKey)` | `def __init__(self, api_key: str)` |
| Method | `Task<ClientResult<Customer>> GetAsync(string id)` | `def get(self, id: str) -> Customer` |
| Options class | `CreateCustomerOptions { Email, Name }` | `@dataclass class CreateCustomerOptions: email: str; name: str` |
| Enum | `CustomerStatus { Active, Inactive }` | `class CustomerStatus(Enum): ACTIVE = "active"` |
| Abstract type | `abstract class Message` | `class Message(ABC):` |
| Return wrapper | `ClientResult<T>` | direct return (no wrapper) |
| Streaming | `IAsyncEnumerable<T>` | `AsyncIterator[T]` |
| Nullable | `string?` | `Optional[str]` or `str \| None` |
| Literal enum | (extensible enum struct) | `Literal["a", "b"]` → TypeKind.Enum |

---

## Key files

| File | Purpose |
|------|---------|
| `docs/sdk-metadata-schema.json` | NEW: formal JSON schema for cross-adapter contract |
| `cli-builder-adapter-python/pyproject.toml` | Package definition (Python 3.10+) |
| `cli-builder-adapter-python/src/cli_builder_adapter/extractor.py` | Core extraction (ast.parse, no runtime import) |
| `cli-builder-adapter-python/src/cli_builder_adapter/type_mapper.py` | Python type → TypeRef |
| `cli-builder-adapter-python/src/cli_builder_adapter/models.py` | SdkMetadata dataclasses |
| `cli-builder-adapter-python/src/cli_builder_adapter/json_output.py` | camelCase JSON + schemaVersion |
| `cli-builder-adapter-python/tests/test_sdk/` | Purpose-built Python SDK |
| `cli-builder-adapter-python/tests/test_error_paths.py` | Error handling tests |
| `src/CliBuilder.Core/Json/SdkMetadataJson.cs` | Add schemaVersion to envelope |
| `src/CliBuilder.Core/Models/StaticAuthConfig.cs` | Add Style discriminator (prerequisite P3) |

---

## Test categories

| Category | File | What it covers |
|----------|------|----------------|
| Unit | `test_type_mapper.py` | All type mappings, including Literal, Union, Optional, untyped |
| Unit | `test_auth_detector.py` | api_key param, module-level auth, env var patterns |
| Unit | `test_json_output.py` | camelCase, null handling, schemaVersion, enum serialization |
| Error | `test_error_paths.py` | Import failure, C-extension, untyped params, *args, exit code 2 |
| Integration | `test_integration.py` | TestSdk → JSON → schema validation → .NET round-trip |
| Contract | `test_integration.py` | JSON validates against `sdk-metadata-schema.json` |

---

## Verification

```bash
# Python adapter standalone
cd cli-builder-adapter-python
pip install -e .
python -m cli_builder_adapter --package tests.test_sdk --json | python -m json.tool

# Validate against JSON schema
python -c "import json; print(json.load(open('/tmp/out.json'))['schemaVersion'])"

# Run Python tests
pytest

# Cross-adapter round-trip (.NET side)
# Deserialize Python adapter JSON with SdkMetadataJson.Options
dotnet test --filter "PythonAdapterRoundTrip"

# .NET tests still pass
dotnet test
```

---

## Risk

**Medium.** This is the first cross-language integration. The adapter must produce JSON that the .NET side deserializes correctly — any field naming mismatch is a silent bug.

Key risks:
- **camelCase serialization**: Python stdlib has no built-in camelCase JSON. Custom transformer must handle nested dataclasses, lists, enums, None → null correctly. ~50 lines but easy to get wrong.
- **`.pyi` stub availability**: not all Python SDKs ship stubs. Controlled import fallback must be robust.
- **`get_type_hints` NameError**: forward references in annotations cause failures. Must catch and fall back to `inspect.signature` annotations.
- **`*args`/`**kwargs`**: must not crash — emit TypeKind.Other with diagnostic.
- **Null vs absent in JSON**: .NET `System.Text.Json` defaults to serializing null fields. Python `json.dumps` omits `None` by default. Must explicitly include null fields.

---

## What this does NOT solve

- Python CLI generator (`click`-based Python CLIs) — future generator
- Rust orchestrator — stays .NET for now
- Stripe/OpenAI real-world validation — deferred to Step 12b/13
- `--adapter python` wiring in .NET CLI — deferred, blocked on StaticAuthConfig.Style
- Multi-adapter orchestration — deferred
