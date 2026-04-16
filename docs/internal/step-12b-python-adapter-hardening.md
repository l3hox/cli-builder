# Step 12b: Python Adapter Hardening + Real SDK Validation

**Prerequisite:** Step 12 MVP complete. Python adapter extracts SdkMetadata from TestSdk. Step 13 proves the Rust generator consumes the JSON correctly.
**Output:** Production-quality Python adapter with pytest suite, JSON schema contract, Stripe validation, and `.pyi` stub support.

---

## Problem

The Python adapter MVP (~745 lines) works against the TestSdk fixture but has no pytest test suite, no real SDK validation, limited auth detection (API key only), and extracts metadata via runtime import (ADR-013 compliance gap). Step 12b hardens it for real-world use.

---

## Current State

| Component | Status | Gap |
|-----------|--------|-----|
| `extractor.py` (286 lines) | Functional | No `.pyi` stub fallback, no PEP 563 handling |
| `type_mapper.py` (147 lines) | Functional | No unit tests, untested edge cases |
| `auth_detector.py` (58 lines) | API key only | No module-level auth (`stripe.api_key`), no bearer/OAuth |
| `json_output.py` (65 lines) | Tested (16 tests) | Only test file that exists |
| `cli.py` (54 lines) | Functional | No error path tests |
| `test_sdk/` fixtures | Complete | Only fixture — no real SDK tests |

**Diagnostic codes in use:** CB202, CB600, CB601, CB602, CB603

---

## Implementation Order

### Phase 1: pytest test suite (no code changes)

Write tests against the existing adapter — pin current behavior before changing anything.

**Coverage target:** 85% line coverage for Phases 1-3 via `pytest-cov --fail-under=85`.

**Note on baseline tests:** Some Phase 1 tests pin current behavior (e.g., "API key only" auth) that Phases 3-4 will intentionally change. Mark these with `# BASELINE: expect to update in Phase N` comments so they're not treated as regressions when they fail.

**1a. `tests/test_type_mapper.py` (~140 lines)**

Unit tests for `type_mapper.map_type()`:

| Test case | Input | Expected TypeKind | Notes |
|-----------|-------|-------------------|-------|
| `str` | `str` | Primitive, name="str" | |
| `int` | `int` | Primitive, name="int" | |
| `float` | `float` | Primitive, name="float" | |
| `bool` | `bool` | Primitive, name="bool" | |
| `bytes` | `bytes` | Primitive, name="bytes" | |
| `None` | `type(None)` | Primitive, name="None" | |
| `datetime` | `datetime.datetime` | Primitive, name="datetime" | |
| `Optional[str]` | `typing.Optional[str]` | Primitive, is_nullable=True | |
| `str \| None` (PEP 604) | `str \| None` | Primitive, is_nullable=True | |
| `list[str]` | `list[str]` | Array, element_type="str" | |
| `dict[str, int]` | `dict[str, int]` | Dictionary, 2 generic args | |
| `Enum subclass` | `CustomerStatus` | Enum, enum_values=["Active",...] | |
| `Literal["a","b"]` | `typing.Literal["a","b"]` | Enum, enum_values=["a","b"] | |
| `dataclass` | `Customer` | Class, properties populated | |
| `ABC subclass` | `Message` | Class, is_abstract=True | |
| `AsyncIterator[T]` | `collections.abc.AsyncIterator[Customer]` | Generic, is_streaming | |
| `tuple` | `tuple` | Other | |
| `typing.Any` | `typing.Any` | Primitive, name="object" | |
| `Union[str,int]` (non-optional) | `typing.Union[str,int]` | Other, name="Union" | |
| `unknown class` | `SomeNewType` | Class, module set | |
| `Optional[list[Customer]]` | nested container + nullable | Array, is_nullable=True, element_type=Class | Council fix: tests recursive type resolution |

**1b. `tests/test_auth_detector.py` (~60 lines)**

| Test case | Input | Expected |
|-----------|-------|----------|
| `api_key: str` param | `CustomerClient.__init__(self, api_key: str)` | AuthType.API_KEY, env_var derived |
| `token: str` param | `__init__(self, token: str)` | AuthType.API_KEY |
| `secret_key: str` param | `__init__(self, secret_key: str)` | AuthType.API_KEY |
| No auth param | `__init__(self, base_url: str)` | No auth detected |
| Non-string auth param | `__init__(self, api_key: int)` | No auth detected (string only) |
| Env var derivation | `stripe.services.CustomerClient` with `api_key` | `STRIPE_API_KEY` |
| Multiple auth candidates | `__init__(self, api_key: str, token: str)` | First by parameter order (iteration order of `inspect.signature().parameters`) |

**1c. `tests/test_extractor.py` (~170 lines)**

| Test case | Scope |
|-----------|-------|
| TestSdk discovers 3 services (Customer, Order, Message) | `_discover_services` |
| Skips imported classes (re-exports) | `_discover_services` |
| Extracts get/create/list/delete operations from CustomerClient | `_extract_operations` |
| Skips private methods (`_helper`) | `_extract_operations` |
| Skips dunder methods (`__repr__`) | `_extract_operations` |
| Handles parameterless constructor | `_has_parameterless_init` |
| Extracts constructor params with auth flag | `_extract_constructor_params` |
| Parameter required/optional detection | `_extract_params` |
| Streaming detection (AsyncIterator return) | `_extract_operations` |
| Method name to kebab-case (`get_customer` → `get-customer`) | naming |
| `@classmethod` method extraction | `_extract_operations` | Council fix: pin expected behavior for Phase 4 |
| PEP 563 forward reference module | `_extract_params` | Council fix: module with `from __future__ import annotations` — assert CB603 emitted and extraction proceeds with partial type info |

**1d. `tests/test_error_paths.py` (~80 lines)**

| Test case | Trigger | Expected |
|-----------|---------|----------|
| Package not found | `--package nonexistent` | Exit 2, CB600 diagnostic |
| Module not found | `--module nonexistent.mod` | Exit 2, CB600 |
| Method with no type hints | `def foo(self, x):` | CB603 diagnostic, params extracted with fallback |
| Signature inspection failure | `unittest.mock.patch("inspect.signature", side_effect=ValueError)` | CB602 diagnostic, method skipped |
| Empty module (no services) | Module with no `*Client` classes | Exit 0, empty resources, no errors |
| Package with version | `module.__version__` present | Version captured in metadata |
| Package without version | No `__version__` attribute | `"0.0.0"` fallback |

**1e. `tests/test_integration.py` (~100 lines)**

Full pipeline tests:
- TestSdk extraction → JSON → deserialize → assert structure
- Round-trip: extract → serialize → parse JSON → validate field names are camelCase
- Schema version present (`"1"`)
- Cross-adapter compatibility: Python JSON output matches .NET fixture structure (same top-level keys, same resource/operation shape)
- CLI exit codes: success (0), error (1), environment failure (2)

**Test infrastructure:**
- Add `pytest>=7.0`, `pytest-cov`, and `jsonschema` to `[project.optional-dependencies]` test group in `pyproject.toml`
- Add `[tool.pytest.ini_options]` with `testpaths = ["tests"]`

### Phase 2: JSON schema contract

**File:** `docs/sdk-metadata-schema.json`

Create a JSON Schema (draft 2020-12) that formally defines the SdkMetadata envelope. Must include constrained enum values for `TypeRef.kind` (`"primitive"`, `"enum"`, `"class"`, `"generic"`, `"array"`, `"dictionary"`, `"other"`) and `AuthType` (`"apiKey"`, `"bearerToken"`, `"oAuth"`, `"custom"`) — without enum constraints the contract test won't catch serialization regressions.

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["schemaVersion", "metadata", "diagnostics"],
  "properties": {
    "schemaVersion": { "const": "1" },
    "metadata": { "$ref": "#/$defs/SdkMetadata" },
    "diagnostics": { "type": "array", "items": { "$ref": "#/$defs/Diagnostic" } }
  }
}
```

Derive from existing models in:
- `python/src/cli_builder_adapter/models.py`
- `src/CliBuilder.Core/Models/SdkMetadata.cs`
- `crates/core/src/models.rs`

Add schema validation to `test_integration.py` using `pathlib.Path` for reliable path resolution:
```python
from pathlib import Path
import jsonschema
schema_path = Path(__file__).parent.parent.parent / "docs" / "sdk-metadata-schema.json"
schema = json.loads(schema_path.read_text())
jsonschema.validate(adapter_output, schema)
```

### Phase 3: Module-level auth detection

Extend `auth_detector.py` to detect module-level auth patterns:

**Pattern:** `stripe.api_key = "sk_..."` (Stripe-style static configuration)

Detection logic:
1. After service discovery, scan module for known auth attribute names: `api_key`, `api_secret`, `secret_key`
2. Check if the attribute is a string-typed module-level variable
3. If found, produce `StaticAuthConfig` with `style: MODULE_ATTRIBUTE`

**Call site:** Add `detect_module_auth(module, diagnostics)` call in `extractor.extract()` after line 69 (after `detect_constructor_auth`). Deduplication: if constructor-level auth already found for the same parameter name, skip module-level. Module-level auth produces `static_auth` on SdkMetadata, not additional entries in `auth_patterns`.

**New auth patterns:**
| Pattern | Auth type | Target field | Example |
|---------|-----------|-------------|---------|
| `module.api_key` attribute | Static module auth | `metadata.static_auth` | `stripe.api_key` |
| `__init__(self, api_key: str)` | Constructor param (existing) | `metadata.auth_patterns` | `CustomerClient(api_key=...)` |

**StaticAuthConfig.Style usage:**
- `STATIC_PROPERTY` — .NET style (`StripeConfiguration.ApiKey`)
- `MODULE_ATTRIBUTE` — Python style (`stripe.api_key`)

The `Style` discriminator already exists in `models.py` (`AuthSetupStyle` enum). Wire it into detection.

### Phase 4: Stripe validation

Test the adapter against `stripe-python` (v5+):

**Install:** `pip install stripe>=5.0`

**`@classmethod` extraction (required code change):**

Stripe v5+ uses class methods on resource objects (`stripe.Customer.create()`), not instance methods on `*Client` classes. The current `_extract_operations` in `extractor.py:146` uses `inspect.isfunction`, which misses `@classmethod` methods.

Required changes to `extractor.py`:
1. Extend `_discover_services` to also detect classes that are subclasses of a base resource type (e.g., `stripe.api_resources.abstract.APIResource`) or that have `@classmethod` methods matching CRUD patterns
2. In `_extract_operations`, use `inspect.getmembers(cls, predicate=inspect.ismethod)` for class methods in addition to `inspect.isfunction` for regular methods. Alternatively, iterate `cls.__dict__` and check `isinstance(v, classmethod)`.

**Expected challenges:**
| Issue | Stripe specifics | Handling |
|-------|-----------------|----------|
| Service discovery | `stripe.Customer`, `stripe.PaymentIntent` — not `*Client` classes | Extend discovery to detect resource-like classes with `@classmethod` CRUD methods |
| Module-level auth | `stripe.api_key = "..."` | Phase 3 module-level auth detection |
| StripeObject attributes | Dynamic attributes via `__getattr__` | TypeRef fallback to Class with no properties |
| Nested resources | `stripe.Customer.create()` → class methods | `inspect.ismethod` or `cls.__dict__` classmethod detection |
| Large API surface | 50+ resources | Pin known-stable resource name subset |

**Test file:** `tests/test_stripe.py` (~80 lines)
- Skip if `stripe` not installed (`pytest.importorskip`)
- Assert known-stable resource names are a subset of discovered resources: `{"Customer", "PaymentIntent", "Charge"} <= discovered_names`
- Assert `api_key` auth detected (module-level via Phase 3)
- Assert `Customer` resource has operations (mark `Customer.create` assertion as `pytest.mark.xfail(strict=True, reason="@classmethod extraction not yet implemented")` until the extraction code change lands — convert to normal assert after)
- Assert operations have parameters

### Phase 5: `.pyi` stub support (ADR-013 compliance)

Add fallback extraction from `.pyi` type stubs via `ast.parse`:

**Why:** ADR-013 says "package artifacts only — never raw source code." Runtime import executes code. `.pyi` stubs are artifacts that describe types without execution.

**Fallback chain (clarified):**
1. **NEW:** Try `.pyi` stubs first (if available in package) — `ast.parse` on stub files
2. **CURRENT BEHAVIOR:** Fall back to controlled import + `typing.get_type_hints()` with CB601 warning
3. **Note:** `get_type_hints()` is unreliable under PEP 563 (`from __future__ import annotations`) — it may raise `NameError` or return unresolved strings when referenced types are not in scope. The fallback must catch `NameError` and `TypeError` from this call, emit CB603 diagnostic, and proceed with `inspect.signature` annotations as strings.

**Implementation:**
- New file: `src/cli_builder_adapter/stub_parser.py` (~150 lines)
- Parse `.pyi` files with `ast.parse`
- Extract class definitions, method signatures, type annotations from AST
- Convert AST type annotations to TypeRef

**Scope:** This is the most complex phase. For 12b, implement the stub parsing infrastructure and use it when stubs are available. The fallback to controlled import remains for packages without stubs.

---

## Key files

| File | Change |
|------|--------|
| `tests/test_type_mapper.py` | New — ~140 lines |
| `tests/test_auth_detector.py` | New — ~60 lines |
| `tests/test_extractor.py` | New — ~170 lines |
| `tests/test_error_paths.py` | New — ~80 lines |
| `tests/test_integration.py` | New — ~100 lines |
| `tests/test_stripe.py` | New — ~80 lines |
| `tests/test_stub_parser.py` | New — ~80 lines (ast.parse round-trips, malformed stub handling, annotation→TypeRef) |
| `docs/sdk-metadata-schema.json` | New — JSON Schema with constrained enum values for TypeRef.kind and AuthType |
| `src/cli_builder_adapter/auth_detector.py` | Extend — module-level auth, call site in extract() |
| `src/cli_builder_adapter/stub_parser.py` | New — `.pyi` AST parsing |
| `src/cli_builder_adapter/extractor.py` | Modify — stub fallback chain, @classmethod extraction |
| `pyproject.toml` | Add pytest, pytest-cov, jsonschema deps |

---

## Verification

```bash
# Phase 1: test suite
cd python
pip install -e ".[test]"
pytest -v --cov=cli_builder_adapter --cov-fail-under=85

# Phase 2: schema validation
pytest tests/test_integration.py -k "schema"

# Phase 3: module-level auth
pytest tests/test_auth_detector.py -k "module_level"

# Phase 4: Stripe (requires stripe package)
pip install stripe>=5.0
pytest tests/test_stripe.py -v

# Phase 5: stub parsing
pytest tests/test_stub_parser.py -v
pytest tests/test_extractor.py -k "stub"
```

---

## Risk

**Low-Medium.** Phases 1-2 are mechanical (tests + schema, no behavior changes). Phase 3 is targeted (one new auth pattern). Phase 4 depends on Stripe's API surface — may surface unexpected patterns requiring `@classmethod` extraction changes. Phase 5 is the most complex but scoped to infrastructure + fallback, not replacing the primary extraction path.

**Stripe-specific risk:** Stripe v5+ uses class methods on resource objects (`stripe.Customer.create()`), not instance methods on client classes. Phase 4 includes a concrete code change plan for `_extract_operations` to handle `@classmethod` via `inspect.ismethod` or `cls.__dict__` inspection.

**PEP 563 risk:** `typing.get_type_hints()` is unreliable under `from __future__ import annotations`. Phase 5 fallback chain accounts for this by catching `NameError`/`TypeError` and falling through to string annotations with CB603 diagnostic.

---

## What this does NOT solve

- Wiring `--adapter python` into `cli-builder generate` (deferred to Step 15 Rust orchestrator — the .NET CLI won't be the long-term entry point)
- Bearer token / OAuth auth detection (future — no real SDK uses these in a way we can detect from signatures alone)
- OpenAPI adapter (separate concern, lower unique value)
