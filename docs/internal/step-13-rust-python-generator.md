# Step 13: Python CLI Generator in Rust

**Prerequisite:** Steps 1-12 complete. Python adapter produces SdkMetadata JSON. No Python generator exists yet. ADR-017 decides all generators in Rust with shared ModelMapper + Tera templates.
**Output:** `cli-builder-gen-python` Rust binary that reads SdkMetadata JSON and emits a `click`-based Python CLI project. End-to-end pipeline: Python SDK → Python adapter → SdkMetadata JSON → Rust generator → Python CLI.

---

## Problem

The Python adapter (Step 12) extracts SdkMetadata from Python SDKs but there's no generator to produce a Python CLI from it. The C# generator can't help — it emits C# code that calls .NET SDKs. We need a Python CLI generator, and per ADR-017, all generators are built in Rust with shared core + Tera templates.

---

## Design

### Rust workspace structure

```
cli-builder-rust/
  Cargo.toml                    # Workspace root
  crates/
    cli-builder-core/           # Shared core (ModelMapper, ParameterFlattener, models)
      Cargo.toml
      src/
        lib.rs
        models.rs               # SdkMetadata structs (serde)
        model_mapper.rs         # SdkMetadata → GeneratorModel
        parameter_flattener.rs  # Flatten options into CLI flags
        identifier_validator.rs # Language-specific keyword checking
    cli-builder-gen-python/     # Python CLI generator
      Cargo.toml
      src/
        main.rs                 # CLI entry point (reads JSON stdin/file)
        lib.rs
        python_mapper.rs        # Python-specific type mapping (str→str, int→int)
        python_keywords.rs      # Python keyword list
      templates/
        project/                # Tera templates for Python CLI
          pyproject.toml.tera
          __main__.py.tera
          cli.py.tera           # click-based CLI entry point
          commands/
            resource.py.tera    # Per-resource command group
          output/
            json_formatter.py.tera
            table_formatter.py.tera
          auth/
            handler.py.tera
```

### Dependencies (Cargo.toml)

```toml
[workspace.dependencies]
serde = { version = "1", features = ["derive"] }
serde_json = "1"
tera = "1"
clap = { version = "4", features = ["derive"] }
```

### Shared core (cli-builder-core)

Port from .NET, targeting language-agnostic logic only. **Council-reviewed naming rules:**

- **No C#-specific field names in core.** `CSharpType` → `cli_type: String`, `ConversionExpression` → absent from core (populated by each generator's mapper). `OptionsClassName` → `options_type_name`.
- **`SanitizeString` split.** Core does structural validation only (identifier safety, null guards, length limits). Template-engine escaping (Scriban `{{`/Tera `{{`) belongs in each generator, NOT in core.
- **`MakeValueTypesNullable` is generator-side.** Core carries `requires_sentinel_nullability: bool` flag. Python generator ignores it. Future C# generator uses it to emit `?` suffixes.

| .NET file | Rust equivalent | Lines (est.) |
|-----------|----------------|-------------|
| `SdkMetadata.cs` + all models | `models.rs` | ~200 (serde structs) |
| `ModelMapper.cs` (language-agnostic parts) | `model_mapper.rs` | ~300 |
| `ParameterFlattener.cs` (without nullable logic) | `parameter_flattener.rs` | ~120 |
| `IdentifierValidator.cs` (framework) | `identifier_validator.rs` | ~50 |
| **Total shared core** | | **~670** |

### Python generator (cli-builder-gen-python)

| Component | File | Lines (est.) |
|-----------|------|-------------|
| CLI entry point | `main.rs` | ~50 |
| Python type mapping | `python_mapper.rs` | ~100 |
| Python keywords | `python_keywords.rs` | ~30 |
| Tera templates (7 files) | `templates/` | ~500 |
| **Total generator** | | **~680** |

### Generated Python CLI structure

```
stripe-cli/
  pyproject.toml              # References stripe SDK as dependency
  src/
    stripe_cli/
      __init__.py
      __main__.py             # python -m stripe_cli
      cli.py                  # click group + commands
      commands/
        customer.py           # Per-resource: @cli.group() + @group.command()
        payment_intent.py
        ...
      output/
        json_formatter.py     # JSON output formatter
        table_formatter.py    # Table output formatter
      auth/
        handler.py            # API key resolution (env var, --api-key flag)
```

### Generated CLI patterns (click-based)

```python
# commands/customer.py
import json
import click

@click.group()
def customer():
    """Customer operations."""
    pass

@customer.command()
@click.option("--id", required=True, help="Customer ID")
@click.option("--json", "use_json", is_flag=True, help="Output as JSON")
@click.pass_context
def get(ctx, id: str, use_json: bool):
    """Get a customer by ID."""
    client = ctx.obj["client"]
    result = client.customers.retrieve(id)
    if use_json:
        click.echo(json.dumps(result.to_dict() if hasattr(result, 'to_dict') else result, indent=2))
    else:
        # table format
        ...
```

Note: SDK result objects may not be plain dicts. Templates must handle `to_dict()` or similar serialization methods.

### Type mapping (Python SDK → Python CLI)

| SdkMetadata TypeKind | Python CLI type | click type | Conversion |
|---------------------|-----------------|------------|------------|
| Primitive (str) | `str` | `click.STRING` | identity |
| Primitive (int) | `int` | `click.INT` | identity |
| Primitive (float) | `float` | `click.FLOAT` | identity |
| Primitive (bool) | `bool` | `click.BOOL` (flag) | identity |
| Enum | `str` | `click.Choice(values)` | identity (SDK accepts strings) |
| Array/Generic/Dictionary | `str` (JSON) | `click.STRING` | `json.loads()` |
| Class (options) | constructed | N/A (flattened) | per-field |

---

## Implementation Order

### Phase 1: Rust workspace + shared core models

1. Create `cli-builder-rust/` workspace at repo root
2. `cli-builder-core` crate with `models.rs` — serde structs mirroring SdkMetadata
3. JSON deserialization test: parse the .NET TestSdk fixture (`tests/fixtures/testsdk-metadata.json`)
4. JSON deserialization test: parse the Python adapter output

### Phase 2: Shared ModelMapper + ParameterFlattener

1. Port `ModelMapper` language-agnostic parts to Rust
2. Port `ParameterFlattener` — flatten logic, threshold, `--json-input` detection
3. Port `IdentifierValidator` — framework with pluggable keyword lists
4. Tests against TestSdk fixture → GeneratorModel output

### Phase 3: Python generator templates

1. Create Tera templates for click-based Python CLI
2. `pyproject.toml.tera` — project metadata, SDK dependency
3. `cli.py.tera` — click group, global options (--json, --api-key)
4. `resource.py.tera` — per-resource command group with operations
5. `json_formatter.py.tera`, `table_formatter.py.tera`
6. `handler.py.tera` — auth handler (env var, CLI flag)
7. **Python syntax validation**: after rendering each `.py` template, run `python3 -c "import ast; ast.parse(open('file.py').read())"` to catch indentation errors, missing colons, unclosed brackets

### Phase 4: CLI entry point + template rendering

1. `main.rs` — clap CLI: `cli-builder-gen-python --input metadata.json --output ./stripe-cli`
2. Read SdkMetadata JSON → deserialize → ModelMapper → Tera render → write files
3. **Structural assertions** (not golden files): verify file existence, presence of `import click`, `@click.group()`, `@click.command()` decorators, correct file count
4. Serde deserialization test: parse BOTH .NET fixture and Python adapter output, assert `resources.len() > 0`

### Phase 5: End-to-end validation + golden files

1. Python adapter → SdkMetadata JSON → Rust generator → Python CLI project
2. **Golden file tests**: commit generated output snapshots for TestSdk, assert byte-for-byte stability (use `insta` crate)
3. **Python syntax validation**: `ast.parse` on every generated `.py` file
4. Install generated CLI (`pip install -e ./generated-cli`)
5. Run generated CLI against TestSdk: `testsdk-cli customer get --id cust_123 --json`

### Phase 6: Documentation

1. Update AGENTS.md, FUTURE.md, spec
2. Update design-notes.md with Python generator patterns

---

## Key files

| File | Purpose |
|------|---------|
| `cli-builder-rust/Cargo.toml` | Workspace definition |
| `cli-builder-rust/crates/cli-builder-core/src/models.rs` | SdkMetadata serde structs |
| `cli-builder-rust/crates/cli-builder-core/src/model_mapper.rs` | Shared model mapping |
| `cli-builder-rust/crates/cli-builder-core/src/parameter_flattener.rs` | Shared parameter flattening |
| `cli-builder-rust/crates/cli-builder-gen-python/src/main.rs` | Generator CLI |
| `cli-builder-rust/crates/cli-builder-gen-python/templates/` | Tera templates |

---

## Verification

```bash
# Build Rust workspace
cd cli-builder-rust && cargo build

# Run tests (fixture deserialization, model mapping, template rendering)
cargo test

# Generate Python CLI from TestSdk
python -m cli_builder_adapter --package test_sdk --module test_sdk.services --json \
  | cargo run -p cli-builder-gen-python -- --output /tmp/testsdk-py-cli

# Verify generated structure
ls /tmp/testsdk-py-cli/

# Install and run generated CLI
cd /tmp/testsdk-py-cli && pip install -e .
python -m testsdk_cli customer get --id cust_123 --json
```

---

## Risk

**Medium.** First Rust code in the project. The shared core port is mechanical (C# records → Rust structs, C# methods → Rust functions). The main risks:

- **Tera vs Scriban**: template syntax differs. Tera uses `{{ variable }}` and `{% for %}` (Jinja2-like). Scriban uses `{{ variable }}` and `{{ for }}`. The migration is mostly cosmetic but error-prone in details.
- **click template authoring**: generating valid click code requires understanding click's decorator patterns, context passing, and option types. Test with simple cases first.
- **serde deserialization**: the SdkMetadata JSON uses camelCase. serde's `#[serde(rename_all = "camelCase")]` handles this, but enum variants need explicit `#[serde(rename = "...")]`.
- **Fixture compatibility**: the Rust deserializer must parse the exact JSON produced by both .NET and Python adapters. Any field mismatch = deserialization error.

---

## What this does NOT solve

- C# generator stays in .NET (ported in Step 14)
- Rust orchestrator (Step 15) — for now, pipe JSON manually or use shell scripts
- Kotlin/Go/TypeScript generators — later, reuse shared core
- Python adapter hardening (Step 12b) — separate concern
