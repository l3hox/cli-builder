# Step 12: Python Adapter — Standalone Subprocess

**Prerequisite:** Steps 1-11 complete. SdkMetadata is language-neutral. `cli-builder inspect --json` serves as the .NET adapter's subprocess interface. 397 tests, 0 failures.
**Output:** `cli-builder-adapter-python` is a standalone Python package that extracts `SdkMetadata` JSON from installed Python SDK packages. Validated against a purpose-built TestSdk and `stripe-python`.

---

## Problem

cli-builder only supports .NET SDKs. The architecture (ADR-016) calls for subprocess-based adapters in native languages. A Python adapter proves the architecture works and enables CLI generation for Python SDKs.

---

## Design

### Package structure

```
cli-builder-adapter-python/
  pyproject.toml              # Package metadata, entry point
  src/
    cli_builder_adapter/
      __init__.py
      __main__.py             # Entry point: python -m cli_builder_adapter
      cli.py                  # CLI argument parsing (argparse)
      extractor.py            # Core: inspect Python package → SdkMetadata
      type_mapper.py          # Map Python types → TypeRef
      auth_detector.py        # Detect auth patterns (api_key params, env vars)
      models.py               # SdkMetadata dataclasses (mirror Core models)
      json_output.py          # Serialize to SdkMetadata JSON (camelCase)
  tests/
    test_sdk/                 # Purpose-built Python TestSdk
      __init__.py
      services.py             # CustomerClient, OrderClient, MessageClient
      models.py               # Customer, Order, Message (dataclass)
      auth.py                 # ApiKeyCredential
    test_extractor.py
    test_type_mapper.py
    test_auth_detector.py
    test_integration.py       # Full extraction → JSON round-trip
```

### Invocation

```bash
# Extract metadata from installed package
python -m cli_builder_adapter --package stripe --json

# Extract from a specific module path
python -m cli_builder_adapter --module stripe --json

# Output: SdkMetadata JSON to stdout, diagnostics to stderr
# Exit: 0 (success), 1 (errors), 2 (environment failure)
```

### Extraction strategy

Python SDK analysis uses the `inspect` module + type annotations:

1. **Discovery**: import the package, find classes matching service patterns (ending in `Client`, `Service`, `Api`)
2. **Method extraction**: `inspect.getmembers(cls, predicate=inspect.isfunction)` for public methods
3. **Parameter extraction**: `inspect.signature(method)` for parameter names, types, defaults
4. **Type mapping**: `typing.get_type_hints(method)` for annotated types → TypeRef
5. **Auth detection**: look for `api_key` parameters, credential types, environment variable patterns

### Type mapping (Python → TypeRef)

| Python type | TypeKind | TypeRef.Name |
|-------------|----------|--------------|
| `str` | Primitive | "str" |
| `int` | Primitive | "int" |
| `float` | Primitive | "float" |
| `bool` | Primitive | "bool" |
| `None` | Primitive | "None" |
| `list[T]` | Generic | "list" (GenericArguments: [T]) |
| `dict[K, V]` | Dictionary | "dict" (GenericArguments: [K, V]) |
| `Optional[T]` | (inner kind) | IsNullable=true |
| `Enum` subclass | Enum | enum name (EnumValues from members) |
| `@dataclass` / Pydantic model | Class | class name (Properties from fields) |
| `Any` / untyped | Other | "object" |

### Auth detection patterns

| Pattern | AuthType | Example |
|---------|----------|---------|
| `api_key: str` parameter | ApiKey | `stripe.Customer.list(api_key="sk_...")` |
| `OPENAI_API_KEY` env var | ApiKey | `openai.api_key = os.environ["OPENAI_API_KEY"]` |
| Module-level attribute | StaticAuth | `stripe.api_key = "sk_..."` |

### SdkMetadata JSON output

Must match the exact schema produced by the .NET adapter:
- camelCase property names
- Enum values as camelCase strings
- Indented JSON
- Same field names: `artifactPath`, `sourceModule`, `module`, `staticAuth`, etc.

---

## Implementation Order

### Phase 1: Project scaffold + models

1. Create `cli-builder-adapter-python/` directory at repo root
2. `pyproject.toml` with entry point, dependencies (none for core — stdlib only)
3. `models.py` — Python dataclasses mirroring `SdkMetadata`, `Resource`, `Operation`, `Parameter`, `TypeRef`, `AuthPattern`, `Diagnostic`, `StaticAuthConfig`
4. `json_output.py` — serialize dataclasses to camelCase JSON matching `SdkMetadataJson.Options`
5. Tests: JSON round-trip matches .NET fixture format

### Phase 2: Python TestSdk

Create `tests/test_sdk/` with:
- `CustomerClient(api_key: str)` with `get(id: str) -> Customer`, `list(limit: int = 10) -> list[Customer]`, `create(options: CreateCustomerOptions) -> Customer`
- `OrderClient(api_key: str)` with `get(id: str) -> Order`, `create(items: list[str], options: Optional[CreateOrderOptions] = None) -> Order`
- `MessageClient(api_key: str)` with `send(messages: list[Message], options: Optional[SendMessageOptions] = None) -> Order`, `batch(ids: list[str]) -> Order`
- `Message` as abstract base with `UserMessage`, `SystemMessage` subclasses
- `Customer`, `Order` as dataclasses with typed fields
- `CreateCustomerOptions`, `CreateOrderOptions`, `SendMessageOptions` as dataclasses

This mirrors the .NET TestSdk structure for direct comparison.

### Phase 3: Core extractor

1. `extractor.py` — discover service classes, extract operations, parameters, return types
2. `type_mapper.py` — map `typing` annotations to `TypeRef`
3. `auth_detector.py` — detect `api_key` patterns, module-level auth
4. Tests for each module against TestSdk

### Phase 4: CLI entry point

1. `cli.py` — argparse-based CLI: `--package`, `--module`, `--json`
2. `__main__.py` — `python -m cli_builder_adapter` entry point
3. Exit codes: 0/1/2 matching the adapter invocation contract
4. Diagnostics to stderr

### Phase 5: TestSdk validation

1. Extract TestSdk → compare JSON output structure with .NET TestSdk fixture
2. Verify resource names, operation names, parameter types match expectations
3. Verify auth detection works

### Phase 6: Stripe validation

1. `pip install stripe` → extract metadata
2. Compare resource count, operation count with .NET Stripe fixture
3. Verify auth detection (module-level `stripe.api_key`)
4. Verify options classes (Pydantic models with typed fields)

### Phase 7: Wire into cli-builder CLI

1. Add `--adapter python` flag to `cli-builder generate`
2. When `--adapter python`: shell out to `python -m cli_builder_adapter --package <name> --json`
3. Read JSON from stdout, deserialize to `SdkMetadata`, pass to C# generator
4. Integration test: Python SDK → SdkMetadata JSON → C# CLI generation

### Phase 8: Documentation

1. Update `AGENTS.md`, `FUTURE.md`, spec
2. Add Python adapter usage to README
3. Update `design-notes.md` with Python-specific extraction rules

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
| Nullable | `string?` | `Optional[str]` |

---

## Key files

| File | Purpose |
|------|---------|
| `cli-builder-adapter-python/pyproject.toml` | Package definition |
| `cli-builder-adapter-python/src/cli_builder_adapter/extractor.py` | Core extraction logic |
| `cli-builder-adapter-python/src/cli_builder_adapter/type_mapper.py` | Python type → TypeRef |
| `cli-builder-adapter-python/src/cli_builder_adapter/models.py` | SdkMetadata dataclasses |
| `cli-builder-adapter-python/src/cli_builder_adapter/json_output.py` | JSON serialization |
| `cli-builder-adapter-python/tests/test_sdk/` | Purpose-built Python SDK |
| `src/CliBuilder/Program.cs` | Add `--adapter python` flag |

---

## Verification

```bash
# Python adapter standalone
cd cli-builder-adapter-python
pip install -e .
python -m cli_builder_adapter --module tests.test_sdk --json | python -m json.tool

# Validate against Stripe
pip install stripe
python -m cli_builder_adapter --package stripe --json > /tmp/stripe-py.json
# Compare resource/operation counts with .NET fixture

# Wire through cli-builder
dotnet run --project src/CliBuilder -- generate \
  --adapter python --package stripe --output /tmp/stripe-py-cli

# Full test suite
cd cli-builder-adapter-python && pytest
cd .. && dotnet test  # .NET tests still pass
```

---

## Risk

**Medium.** Python's type system is more dynamic than .NET's — untyped SDKs, `*args`/`**kwargs`, monkey-patching, dynamic attribute access. The adapter must degrade gracefully for untyped code (emit `TypeKind.Other` with name "object").

Key risks:
- **Untyped parameters**: many older Python SDKs lack type annotations. The adapter produces `TypeKind.Other` for these — the generated CLI treats them as `--json-input` only.
- **Pydantic vs dataclass**: Stripe uses Pydantic models, not dataclasses. The extractor needs to handle both field extraction patterns.
- **Module-level auth**: `stripe.api_key = "..."` doesn't map cleanly to `StaticAuthConfig` — needs the Style discriminator deferred from Step 11 council, or a pragmatic workaround.
- **Async methods**: Python's `async def` methods need detection similar to .NET's `*Async` suffix stripping.

---

## What this does NOT solve

- Python CLI generator (generating `click`-based Python CLIs) — that's a future generator, not this step
- Rust orchestrator — stays .NET for now
- Multi-adapter orchestration in a single `cli-builder` invocation — deferred
