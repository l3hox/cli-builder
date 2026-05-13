# Step 17: PEP 692 `Unpack[TypedDict]` resolution in the Python adapter

**Prerequisite:** v0.2.0 shipped — Python adapter at 109 pytest tests, Python generator at 36 tests, 15-job CI green. Manual-test script (`scripts/manual-test-python-sdk.sh`) runs end-to-end through generate → install → invoke.

**Output:** Generated Python CLIs expose `--limit`, `--email`, `--starting-after`, … on Stripe (and structurally similar PEP 692 SDKs) instead of zero flags. Adapter learns to walk `Unpack[TypedDict]` kwargs into flat parameters.

**Status:** Plan v2 — council-reviewed 2026-05-13, 3 rounds, full convergence. See "Council verdict" section below.

---

## Problem

`scripts/manual-test-python-sdk.sh` against `stripe-python 15.x` reaches `customer list --limit 1` and fails with `Error: No such option: --limit`. Investigation:

- `stripe.Customer.list` signature is `(**params: Unpack[CustomerListParams]) -> ListObject[Customer]` — PEP 692 with PEP 655 `Required`/`NotRequired`.
- `python/src/cli_builder_adapter/extractor.py:324-329` skips `VAR_KEYWORD` unconditionally: every `**kwargs` parameter is dropped before annotation inspection.
- Result: **313 of 922 Stripe operations (34%) have zero extracted parameters**, including every CRUD method that matters (`list`, `create`, `retrieve`, `update`, `delete`). Generated CLI is functionally empty for those.

The `CHANGELOG.md` / `README.md` "Stripe 15.x validated" claim is metadata-extraction-only; end-to-end the CLI is unusable on the most prominent Python SDK in the world.

## Investigation findings (probed 2026-05-13)

| Question | Answer |
|---|---|
| Where does Stripe define `CustomerListParams`? | `stripe.params._customer_list_params.CustomerListParams` — runtime-importable Python class, not `.pyi`. |
| Is it visible from `stripe._customer.__dict__`? | **No.** Imports live inside an `if TYPE_CHECKING:` block — not executed at runtime. |
| Does `typing.get_type_hints(method)` resolve it? | **No** — `name 'CustomerListParams' is not defined`. |
| Does `inspect.get_annotations(method, eval_str=True, globals=module.__dict__)` resolve it? | **No** — same root cause. Returns `Unpack[ForwardRef('CustomerListParams')]` with the ref still unresolved. |
| Can we AST-parse the module and discover the import target? | **Yes** — AST walk of `if TYPE_CHECKING:` blocks finds 41 imports in `_customer.py` including `from stripe.params._customer_list_params import CustomerListParams`. `importlib.import_module` then resolves cleanly. |
| Does the TypedDict expose required-vs-optional cleanly? | **Yes** — `__required_keys__` / `__optional_keys__` frozensets (PEP 589 metaclass aggregates inherited fields automatically). |
| Does OpenAI Python SDK use the same shape? | **No** — OpenAI 2.x uses explicit `def create(*, messages, model, …)` keyword-only params. Already extractable. Sentinel handling (`NotGiven` / `Omit`) is a separate concern, out of scope. |
| Does Stripe's newer `StripeClient.v1.customers.list` use the same shape? | **No** — service pattern uses `params: Optional[X] = None`. Different surface; adapter currently discovers the legacy class-method surface. Service-pattern discovery is out of scope. |
| Is `crates/core` `ParameterFlattener` C#-specific or generic? | **Generic.** Operates on `&[Parameter]` with `&dyn LanguageProfile`. Python already inherits it via `model_mapper::build` at `crates/gen-python/src/main.rs:44`. The threshold at `crates/core/src/model_mapper.rs:287` already handles the 30-flag Stripe explosion through `--json-input` fallback. **No Python-specific flattening work needed in this step.** |

**Key implication**: solvable without `.pyi` parsing for Stripe. Strategy: AST-walk the defining module for `TYPE_CHECKING` imports, resolve the `Unpack[X]` ForwardRef against that table, read `__required_keys__` / `__optional_keys__` + `__annotations__` from the resolved TypedDict.

---

## Council verdict (2026-05-13, 3 rounds, full convergence)

Specialists: SoftwareDeveloper, QaTester, SystemArchitect. Each round forced explicit cross-referencing. Two architectural calls reversed mid-debate (CB1xx → CB6xx; flattening retracted).

| Decision | Rationale | Where documented |
|---|---|---|
| **Strategy: AST-walk top-level `if TYPE_CHECKING:` `ast.ImportFrom` nodes; `.pyi` parsing is named as a future-fallback path but not implemented** | Stripe TypedDicts ship in real `.py`, not stubs. Future SDKs may differ. Closing the design space prematurely would force an ADR amendment. | ADR-022 (added in PR 3) |
| **3 PRs, not 2** | Bisectability across structural change (PR 1) vs field-resolution detail (PR 2) vs docs (PR 3). Dev's R1 "collapse" position was incompatible with the no-CI-key constraint. | This file, sections below |
| **No `sk_test_` key in CI** | Secrets in CI for a public portfolio repo is a non-starter. Live Stripe is a developer pre-merge gate. | This file + PR 3 manual-test script |
| **Parameter flattening is out of scope (already-handled)** | `ParameterFlattener` is generic via `LanguageProfile`; Python inherits it through `model_mapper::build`. Threshold at `model_mapper.rs:287` already triggers `--json-input` fallback past ~10 flags. Stripe `CustomerCreateParams` will not explode the CLI surface. | Council retracted CRITICAL flag in R2 after Architect investigated; no plan action |
| **`map_type` stays pure — no resolution-context parameter** | `map_type` is a pure type-mapper. Threading `globals` through it conflates type mapping with import resolution. Resolve all ForwardRefs in `_try_resolve_unpack_kwargs` upstream; pass concrete types into `map_type`. Architectural boundary worth locking. | design-notes.md (added in PR 3): "Python adapter — `map_type` purity contract" |
| **Diagnostic codes are `CB6xx`, not `CB1xx`** | Python adapter already emits `CB600`–`CB605` (extractor.py + stub_parser.py:73). `CB1xx` is the C# adapter range (design-notes.md:179-213). Next free triplet: `CB606`, `CB607`, `CB608`. | design-notes.md update + assignment in PR 1 |
| **`CB606` = D-UnpackResolved (info); `CB607` = D-UnpackUnresolved (warning); `CB608` = D-UnpackFieldUnresolved (warning, PR 2)** | Silent fallback is what caused the original bug. Warning level for unresolvable cases is non-negotiable. Codes must be reserved before PR 1 merges. (`CB604` is already taken by `stub_parser.py:73` for malformed-stub diagnostics — third code is `CB608`, skipping the gap.) | design-notes.md updates in PR 1 + PR 2 |
| **`typing_extensions` is a hard dependency, not optional** | Python 3.10 in our CI matrix requires `typing_extensions.Unpack`; `typing.Unpack` only ships from 3.11. `get_origin(Unpack[X])` behavior differs across versions; `typing_extensions.get_origin` normalizes. Leaving it optional means 3.10 CI can pass locally and fail on a clean install. | `python/pyproject.toml` `[project.dependencies]` in PR 1 |
| **`inspect.unwrap()` is applied at the operation-walking boundary, not inside the new helper** | Decorated methods (`@classmethod` etc.) must be unwrapped once at the entry point where signature inspection begins (~`extractor.py:270`), not threaded through every downstream function. Defensive unwrapping at the boundary, not at every call site. | PR 1 implementation; noted in design-notes.md update |
| **MRO walk is not coded manually** | PEP 589 metaclass aggregates `__required_keys__` / `__optional_keys__` across inheritance at class creation time. Iterating those frozensets is the source-of-truth path. MRO walk is only a fallback for name→annotation lookup. Inheritance **test** still required — silent regression is the failure vector. | PR 1 implementation + test |
| **Helper monolithic in PR 1, refactored in PR 2** | Shape isn't proven until field resolution lands. Premature 3-piece decomposition adds review surface without correctness gain. | PR 1 ships `_try_resolve_unpack_kwargs` as one function; PR 2 splits into (annotation inspector / TYPE_CHECKING table builder / class resolver) |
| **`functools.lru_cache` on `_collect_type_checking_imports`, keyed by module source path** | Stripe's 313 affected ops hammer the same TYPE_CHECKING blocks repeatedly. Self-documenting, thread-safe, testable via `cache_clear()`. Module-level mutable dict was the wrong answer. | PR 1 implementation |
| **VAR_KEYWORD branch reads `param.annotation` directly, NOT `hints[pname]`** | `hints` is the output of `typing.get_type_hints()` which is exactly what fails on Stripe ForwardRefs. The `_extract_params` function at line 314 currently does `hints.get(pname, param.annotation)`; the new VAR_KEYWORD branch must bypass `hints` entirely. | PR 1 implementation; called out explicitly in plan PR-1a step list below |
| **Service-pattern discovery, nested-TypedDict recursion, OpenAI `NotGiven` sentinels: out of scope** | Each is a separable concern. Nested TypedDicts fall back to `TypeKind.Other` and reach generated CLIs via `--json-input` (Step 9 path). | "Out of scope" section + ADR-022 |
| **PR 2 gate: synthetic-fixture tests in CI + local-developer Stripe sanity check** | CI cannot require a Stripe key. Developer must verify locally before pushing — gate documented in PR description, not enforced by CI. | This file + PR 2 checklist below |
| **PR 3 manual-test `--help` flag-count assertion is a required pass condition** | `grep`-based check for presence of `--limit`, `--email`, `--starting-after` in `customer list --help` output. Without it, "optional" means never-checked. | PR 3 manual-test script edit |
| **DECLINE: extending `map_type` signature with `globals=None` keyword arg** | Dev's R1 compromise. Architect held the line. Pure function stays pure. | design-notes.md purity contract |
| **DECLINE: pinning a Stripe version + snapshotting `--help` output** | Brittle to Stripe release cadence; synthetic fixture is the testable contract. Live validation is a manual smoke. | QA call; not a plan action |
| **DECLINE: nested-TypedDict recursive flattening tests in this step** | Tests for code that isn't implemented create confusion. The fallback to `TypeKind.Other` is testable; recursion is not. | QA call; out of scope |

---

## Implementation plan

### PR 1 — Detection, resolution skeleton, synthetic fixture

**Goal:** Lift `**kwargs: Unpack[X]` into structured parameters using a synthetic TypedDict; prove the AST-walk + `importlib` mechanic; reserve diagnostic codes; lock dependencies.

**1a. Diagnostic-code reservation** (do first, before any other code in PR 1)

Confirmed taken (across `extractor.py` + `stub_parser.py:73`): `CB600`, `CB601`, `CB602`, `CB603`, `CB604`, `CB605`. Free triplet to claim: `CB606`, `CB607`, `CB608`. Add to the diagnostic-code table:

- `CB606` — `D-UnpackResolved` — INFO — Successfully resolved `Unpack[TypedDict]` for a method's `**kwargs`.
- `CB607` — `D-UnpackUnresolved` — WARNING — `Unpack[ForwardRef(X)]` could not be resolved; falling back to zero-param skip.
- `CB608` — `D-UnpackFieldUnresolved` — WARNING — A TypedDict field's annotation couldn't be resolved; emitted as `TypeKind.Other`. *(Reserved here; emission lands in PR 2.)*

If any of those slots is already used in the file, pick the next-free `CB6xx` triplet and document in the design-notes update.

**1b. Hard dependency on `typing_extensions`**

`python/pyproject.toml` — move `typing_extensions` (if present) or add it to `[project.dependencies]`:

```toml
[project]
dependencies = [
  "typing_extensions>=4.6",
]
```

Standardize the adapter on `from typing_extensions import Unpack, get_origin, get_args` (these normalize behavior across 3.10/3.11/3.12). The existing `typing.X` imports stay for non-Unpack call sites.

**1c. `inspect.unwrap()` at operation-walking boundary**

Locate where `_extract_params` is called from (currently `_extract_operations` around `extractor.py:270`). Apply `method = inspect.unwrap(method)` once at that boundary, before `inspect.signature(method)` is read. Single change, defensive against `@classmethod` / `@functools.wraps`.

**1d. New helper `_try_resolve_unpack_kwargs`** (monolithic in PR 1)

In `python/src/cli_builder_adapter/extractor.py`:

```python
def _try_resolve_unpack_kwargs(
    method,
    param: inspect.Parameter,
    diagnostics: list[Diagnostic],
) -> list[Parameter] | None:
    """
    For a VAR_KEYWORD parameter annotated as `Unpack[TypedDict]`, resolve
    the TypedDict and return one Parameter per field. Returns None if the
    annotation is not Unpack-shaped (caller continues with the existing
    skip behavior).

    Read `param.annotation` directly — NOT `hints.get(pname)`. The hints
    dict is the output of `typing.get_type_hints()` which is exactly what
    fails on these ForwardRefs.
    """
    ann = param.annotation
    if ann is inspect.Parameter.empty:
        return None
    if get_origin(ann) is not Unpack:
        return None
    target = get_args(ann)[0]
    td_cls = _resolve_unpack_target(method, target, diagnostics)
    if td_cls is None:
        return None  # diagnostic already emitted
    return _walk_typed_dict(td_cls, diagnostics)
```

The two private helpers:

- `_resolve_unpack_target(method, target, diagnostics)` — if `target` is a class already, return it. If it's a `ForwardRef`, look up the name in `_collect_type_checking_imports(method)`. Use `importlib.import_module` + `getattr`. On failure emit `CB607` and return `None`.
- `_collect_type_checking_imports(module_source_path)` — AST-parse the module file once, walk top-level `if TYPE_CHECKING:` blocks, return a `dict[str, str]` mapping each `ast.ImportFrom` name to its source module. **Decorated with `@functools.lru_cache(maxsize=None)`** keyed by `module_source_path`. Ignore non-`ImportFrom` nodes (no `Import`, no nested ifs, no star imports — those become diagnostic emissions in PR 2).
- `_walk_typed_dict(td_cls, diagnostics)` — iterate `td_cls.__required_keys__ | td_cls.__optional_keys__`. Look up each name in `td_cls.__annotations__` (walk MRO with `getattr(klass, '__annotations__', {})` only as fallback for names not in the direct dict). Strip `NotRequired[X]` / `Required[X]` wrappers via `get_origin`/`get_args` recognition. Call `map_type` on the concrete annotation. Set `required = name in td_cls.__required_keys__`. Emit `CB606` info diagnostic on success.

**1e. Wire VAR_KEYWORD branch in `_extract_params`**

At `extractor.py:325-329`, replace:

```python
if param.kind in (
    inspect.Parameter.VAR_POSITIONAL,
    inspect.Parameter.VAR_KEYWORD,
):
    continue
```

with:

```python
if param.kind == inspect.Parameter.VAR_POSITIONAL:
    continue
if param.kind == inspect.Parameter.VAR_KEYWORD:
    unpacked = _try_resolve_unpack_kwargs(method, param, diagnostics)
    if unpacked is not None:
        params.extend(unpacked)
    continue
```

Pass `method` into `_extract_params` (currently the function receives only `sig` and `hints` — add the `method` parameter to the signature; update the single internal caller).

**1f. Synthetic fixture** (`python/tests/test_sdk/unpack_sdk/`)

```
unpack_sdk/
├── __init__.py                 # exports CustomerService, ChildParamsService
├── _service.py                 # methods with Unpack[TypedDict] via TYPE_CHECKING import
└── params/
    └── _customer_params.py     # BaseListParams; CustomerListParams; CustomerCreateParams; NestedAddressParams
```

Fixture coverage:
- `CustomerListParams` — `total=False`, fields covering `str`, `int`, `bool`, `Literal["a","b"]`, `List[str]`.
- `CustomerCreateParams` — `total=True` with mixed `Required[X]` and `NotRequired[X]` (the exact Stripe shape).
- `ChildListParams(BaseListParams)` — inheritance test (parent provides `limit`, child provides `email`).
- `NestedAddressParams` — referenced from `CustomerCreateParams` for the PR 2 nested-fallback test.

**1g. PR 1 test set** (`python/tests/test_extractor_unpack.py`, new file)

Locked test list (six required tests):

| Test | What it asserts |
|---|---|
| `test_plain_kwargs_without_unpack_still_skipped` | A method with bare `**kwargs` (no annotation) yields zero params and no `CB606`/`CB607` diagnostic. Backward-compat for every non-Stripe SDK. |
| `test_unpack_typed_dict_resolves_total_false_fields` | `CustomerListParams` (`total=False`) emits one param per key, all `required=False`, types mapped correctly. |
| `test_unpack_required_and_notrequired_classification` | `CustomerCreateParams` (`total=True` with mixed `Required`/`NotRequired`) → required fields have `required=True`, NotRequired have `required=False`. `__required_keys__` / `__optional_keys__` are the source of truth. |
| `test_unpack_inheritance_aggregates_parent_fields` | `ChildListParams(BaseListParams)` emits parent fields + child fields. (Frozensets aggregate via metaclass; test catches regression if iteration switches to `__annotations__` directly.) |
| `test_unpack_unresolvable_forwardref_emits_diagnostic` | Synthetic method whose `Unpack[ForwardRef(X)]` target is not in any TYPE_CHECKING block → returns zero params AND emits `CB607`. Both assertions required (silent param-loss is the original bug class). |
| `test_unpack_cross_version_typing_vs_typing_extensions` | Parametrized over `typing.Unpack` and `typing_extensions.Unpack`. Asserts identical param output. Marked `skipif` when `typing.Unpack` is absent (3.10). |

Existing 109 pytest tests must remain green.

**1h. Manual smoke** (developer-local, not CI)

Re-run `scripts/manual-test-python-sdk.sh` (no API key needed for this check). Expect `customer list --help` and `customer create --help` to print at least one flag each. Document in the PR description. Not a CI gate.

**1i. PR 1 pass criteria**

- 6 new tests green
- 109 existing pytest tests green
- `make ci` green
- Diagnostic codes `CB608`/`CB606`/`CB607` reserved in design-notes.md (added in this PR, even though `CB608` is only emitted in PR 2)
- `typing_extensions` declared as hard dep in `python/pyproject.toml`
- PR description includes a copy of the local `stripe-cli customer list --help` output proving at least one flag emitted

---

### PR 2 — Field-level ForwardRef resolution + helper refactor

**Goal:** Resolve Stripe TypedDict field annotations like `NotRequired[ForwardRef('str|None')]` to concrete types. Refactor `_try_resolve_unpack_kwargs` into three named pieces now that the shape is proven.

**2a. Field-level ForwardRef resolution in `_walk_typed_dict`**

Use `inspect.get_annotations(td_cls, eval_str=True, globals=sys.modules[td_cls.__module__].__dict__)` to evaluate string ForwardRefs against the TypedDict's defining module. Wrap each field's resolution in its own try/except — a single bad field annotation must not abort the entire TypedDict walk. On per-field failure, emit `CB608` (warning) and emit the parameter as `TypeKind.Other` (so the user can still pass it via `--json-input`).

**2b. Helper split (refactor only — same behavior)**

Decompose `_try_resolve_unpack_kwargs` into:

1. `_inspect_unpack_annotation(annotation) -> ForwardRef | type | None` — pure annotation inspection.
2. `_collect_type_checking_imports(module_source_path) -> dict[str, str]` — already exists from PR 1, now standalone.
3. `_resolve_class(name, import_table) -> type | None` — `importlib.import_module` + `getattr`.

Pure refactor — gate on byte-for-byte test output (no diagnostic changes, no new params).

**2c. New tests in `test_extractor_unpack.py`**

- `test_typed_dict_field_forwardref_resolves_to_str` — `NotRequired[ForwardRef('str')]` → `TypeKind.String`, `required=False`.
- `test_typed_dict_field_union_with_none_renders_optional` — `NotRequired[str | None]` (the exact Stripe shape) → optional string, not `TypeKind.Other`.
- `test_typed_dict_nested_typed_dict_falls_back_to_other_with_diagnostic` — `NotRequired[NestedAddressParams]` → `TypeKind.Other` + `CB608` diagnostic. Both assertions required.

**2d. Developer-local Stripe sanity check** (NOT a CI gate)

Before opening PR 2 for review:

```bash
scripts/manual-test-python-sdk.sh   # no API key needed; check Phase 7 output
```

Expect `customer list --help` to print `--limit`, `--email`, `--starting-after`, `--ending-before`. Paste output into PR description. CI runs only the synthetic suite.

**2e. PR 2 pass criteria**

- 3 new tests green (total 109 + 6 + 3 = 118)
- Pure refactor commits have zero behavioral diff (no diagnostic emission changes, no param shape changes)
- `make ci` green
- PR description shows local Stripe `customer list --help` output

---

### PR 3 — Live validation, docs, ADR-022

**Goal:** Update the documentation hierarchy per `CONTRIBUTING.md`, validate live against Stripe, surface the change to users honestly.

**3a. Manual-test script `--help` flag-count assertion** (`scripts/manual-test-python-sdk.sh`)

Add to Phase 7 — required pass condition:

```bash
REQUIRED_FLAGS=("--limit" "--email" "--starting-after")
for flag in "${REQUIRED_FLAGS[@]}"; do
    if ! grep -q -- "$flag" "$WORK_DIR/help-customer-list.out"; then
        fail "expected $flag in customer list --help; not found"
        NOUN_FAILURES=$((NOUN_FAILURES + 1))
    fi
done
```

Flag-name presence (not line count) — survives cosmetic reformatting.

**3b. Live Stripe validation**

Run `STRIPE_API_KEY=sk_test_... scripts/manual-test-python-sdk.sh`. Phase 8 (`customer list --limit 1 --json`) must pass. Capture full output for the PR.

**3c. ADR-022** — add to `docs/ADR.md`

Full Nygard format (matching ADR-016 through 021 in the file). Title: **"PEP 692 `Unpack[TypedDict]` resolution via AST walk of `TYPE_CHECKING` imports"**.

Sections:
- **Status:** Accepted (2026-05-XX, council-reviewed)
- **Context:** PEP 692 is now the dominant Python SDK kwargs idiom (Stripe, increasingly others). `typing.get_type_hints()` fails because TYPE_CHECKING imports are not loaded at runtime. Adapter must extract structured parameters or generate empty CLIs.
- **Decision:** Primary strategy — AST-walk the defining module's top-level `if TYPE_CHECKING:` `ImportFrom` nodes, build a name→module table, `importlib.import_module` on demand. Fall back to `__required_keys__`/`__optional_keys__` for required/optional classification. **Documented fallback path** — `.pyi` stub parsing — not implemented in this step but reserved as the extension point if a future SDK ships TypedDicts stub-only. Service-pattern discovery (`StripeClient.v1.customers`), nested TypedDict recursive flattening, and OpenAI sentinel handling (`NotGiven`/`Omit`) are explicitly out of scope for this ADR.
- **Consequences:** Adapter gains AST-parsing of SDK source files (new dependency on the readability of `__file__` paths). Cache via `functools.lru_cache` keyed on module source path to avoid re-parsing. Generated CLIs grow flat-flag surface area — already moderated by the existing `ParameterFlattener` threshold in `crates/core/src/model_mapper.rs:287` which routes overflows to `--json-input`. `typing_extensions` becomes a hard runtime dependency for cross-version normalization.
- **Alternatives considered:** (a) brute-force package walk for the name — O(n) modules, collision-prone; (b) Stripe-specific namespace convention — couples adapter to one SDK; (c) `.pyi` stub parsing primary — unnecessary for current SDKs, added I/O cost; (d) require SDKs to opt into a metadata-friendly shape — won't fly.

**3d. design-notes.md additions** — `docs/design-notes.md`

Add three subsections under the existing "Python generator — click conventions and sanitization" section, OR create a new "Python adapter — PEP 692 resolution" section if the existing one is generator-only:

1. **`map_type` purity contract** — One sentence rule: `map_type` accepts only fully resolved type objects. ForwardRefs MUST be resolved by the caller before invocation. The TypedDict field walker (`_walk_typed_dict`) resolves ForwardRefs against the TypedDict's defining module's `__dict__` and passes concrete types in. Extending `map_type` to accept a resolution context is explicitly declined — see ADR-022 alternatives.
2. **`if TYPE_CHECKING:` AST walk** — Scope is top-level `ast.ImportFrom` nodes inside `if TYPE_CHECKING:` blocks. `ast.Import`, star imports, conditional nesting, and runtime `if TYPE_CHECKING:` evaluation are out of scope and emit `CB607` if encountered. `functools.lru_cache` on `_collect_type_checking_imports` keyed by module source path.
3. **TypedDict source-of-truth for required/optional** — Iterate `__required_keys__ | __optional_keys__`. PEP 589 metaclass aggregates inheritance automatically. Walk `__mro__` for `__annotations__` only as a name→type fallback when the field isn't in the direct class dict.

Append to the diagnostic-code table:

| Code | Severity | Source | Meaning |
|---|---|---|---|
| `CB608` | warning | python adapter | TypedDict field ForwardRef unresolvable; emitted as `TypeKind.Other` |
| `CB606` | info | python adapter | `Unpack[TypedDict]` successfully resolved via TYPE_CHECKING walk |
| `CB607` | warning | python adapter | `Unpack[ForwardRef(X)]` could not be resolved; param dropped |

**3e. CHANGELOG.md** — v0.2.1 entry

```markdown
## v0.2.1 — 2026-05-XX

### Features
- **PEP 692 `Unpack[TypedDict]` resolution** in the Python adapter (ADR-022). Methods with `**kwargs: Unpack[X]` now extract one structured parameter per TypedDict field. Stripe `customer list --limit`, `customer create --email`, … now work end-to-end. Resolution strategy: AST walk of `if TYPE_CHECKING:` blocks + `importlib.import_module`. Fallback to `--json-input` for nested TypedDicts and unresolvable ForwardRefs.

### Bug fixes
- Generated Python CLIs for SDKs using PEP 692 (Stripe-style `**params: Unpack[X]`) previously rendered zero flags on every CRUD method — 313 of 922 Stripe operations were unusable. Now extracted as flat flags.

### Stats
- Python adapter: 109 → 118 tests (+9)
- Stripe 15.x end-to-end validated: `customer list --limit 1 --json` returns a real Stripe list object.

### Dependencies
- `typing_extensions >= 4.6` is now a hard runtime dependency of the Python adapter (was optional).
```

**3f. README.md / AGENTS.md** — "Validated SDKs" honesty

Replace "Stripe 15.x validated (105 resources extracted)" with the specific scope:

> **Stripe 15.x** — 105 resources, PEP 692 `Unpack[TypedDict]` resolution (ADR-022). End-to-end validated via `customer list --limit 1 --json` and `customer create --email`. Nested params (e.g., `address`, `payment_method_data`) fall back to `--json-input`.

**3g. FUTURE.md** — move "PEP 692 support" from Later → Completed under v0.2.1.

**3h. Memory update** — `~/.claude/projects/-home-jlehotsky-prog-cli-builder/memory/project_status.md`. Drop the "metadata extraction only" gap line about stripe-python; replace with the validated-end-to-end statement.

**3i. PR 3 pass criteria**

- `make ci` green
- Live Stripe `customer list --limit 1 --json` succeeds (output pasted in PR description)
- ADR-022 written and reviewed
- design-notes.md updated with the three subsections + diagnostic-code table
- CHANGELOG.md v0.2.1 entry
- README, AGENTS, FUTURE updated
- Memory updated

---

## Architecture documentation surfaces (CONTRIBUTING.md hierarchy)

Per `CONTRIBUTING.md`, "every piece of information should exist in exactly one place at the right granularity." Mapping for this step:

| Decision | Document | Edited in |
|---|---|---|
| AST-walk strategy + `.pyi` fallback reserved | `docs/ADR.md` ADR-022 | PR 3 |
| `map_type` purity contract | `docs/design-notes.md` new subsection | PR 3 |
| TYPE_CHECKING walk scope rules (top-level `ImportFrom` only, star-import diagnostic, lru_cache) | `docs/design-notes.md` new subsection | PR 3 |
| TypedDict source-of-truth for required/optional | `docs/design-notes.md` new subsection | PR 3 |
| Diagnostic codes `CB608`/`CB606`/`CB607` | `docs/design-notes.md` diagnostic-code table | PR 1 (`CB606`/`CB607` actively emit; `CB608` reserved); PR 2 (`CB608` actively emits) |
| `typing_extensions` as hard dep | `python/pyproject.toml` + mentioned in ADR-022 consequences | PR 1 |
| v0.2.1 release notes | `CHANGELOG.md` | PR 3 |
| Validated-SDKs honesty | `README.md`, `AGENTS.md`, `docs/FUTURE.md` | PR 3 |
| Step plan (this file) | `docs/internal/step-17-pep692-unpack.md` | PR 3 archives as "shipped" |
| Council verdict | Embedded in this file (above) | already in this file |

---

## Key files

| File | Change | PR |
|---|---|---|
| `python/src/cli_builder_adapter/extractor.py` | Wire VAR_KEYWORD branch, add `_try_resolve_unpack_kwargs`, `_collect_type_checking_imports`, `_resolve_unpack_target`, `_walk_typed_dict`; apply `inspect.unwrap()` at `_extract_operations` boundary; pass `method` through `_extract_params` signature | PR 1 |
| `python/src/cli_builder_adapter/models.py` (or wherever the diagnostic-code constants live) | Reserve `CB608`/`CB606`/`CB607` | PR 1 |
| `python/pyproject.toml` | `typing_extensions >= 4.6` in `[project.dependencies]` | PR 1 |
| `python/tests/test_extractor_unpack.py` | New, 6 tests | PR 1 |
| `python/tests/test_sdk/unpack_sdk/` | New synthetic fixture | PR 1 |
| `python/src/cli_builder_adapter/extractor.py` | Refactor monolithic helper into 3-piece split; add field-level ForwardRef resolution | PR 2 |
| `python/tests/test_extractor_unpack.py` | +3 tests | PR 2 |
| `scripts/manual-test-python-sdk.sh` | Add required `--help` flag-presence assertion | PR 3 |
| `docs/ADR.md` | ADR-022 | PR 3 |
| `docs/design-notes.md` | Python adapter PEP 692 subsections + diagnostic-code table additions | PR 3 |
| `CHANGELOG.md` | v0.2.1 entry | PR 3 |
| `README.md`, `AGENTS.md`, `docs/FUTURE.md` | Honest "validated SDKs" wording | PR 3 |

---

## Verification

```bash
# PR 1
cd python && pytest -v test_extractor_unpack.py    # 6 new tests
cd python && pytest                                # 109 + 6 = 115 tests

# PR 2
cd python && pytest                                # 115 + 3 = 118 tests
scripts/manual-test-python-sdk.sh                  # local; expect flags on customer list/create

# PR 3
make ci                                            # all 15 CI jobs green
STRIPE_API_KEY=sk_test_... scripts/manual-test-python-sdk.sh    # live Stripe pass
```

---

## Risks

| Risk | Mitigation |
|---|---|
| AST-walk fragile on SDKs with non-standard `TYPE_CHECKING` shapes (re-exports, conditional nesting, star imports) | Scope strictly to top-level `ast.ImportFrom`. Anything else → `CB607` + zero-param fallback (existing behavior preserved). Test covers star-import fallback emission. |
| TypedDict module import is slow on big SDKs | `functools.lru_cache` on `_collect_type_checking_imports` keyed by module source path. Stripe's 313 resources × ~5 ops resolve through ~41 unique TypeChecking blocks per resource — cached, sub-second total. |
| `inspect.get_annotations(td, eval_str=True)` blows up on recursive TypedDicts | Per-field try/except in PR 2 walker. Single bad field → `CB608` + `TypeKind.Other`, rest of fields still resolved. |
| Python 3.10 vs 3.11+ `Unpack` origin diverges | Hard dep on `typing_extensions >= 4.6`. Standardize on `typing_extensions.get_origin`. Parametrized cross-version test in CI matrix. |
| Plain unannotated `**kwargs` regresses to zero params (silent breakage) | `test_plain_kwargs_without_unpack_still_skipped` covers it. `_try_resolve_unpack_kwargs` returns `None` for non-Unpack annotations → existing skip behavior preserved. |
| Diagnostic-code collision with future C# adapter codes | All new codes in `CB6xx` (Python adapter namespace). C# adapter is `CB1xx` per design-notes.md. No cross-talk. |
| `map_type` extension creeps back in via PR 2 refactor | design-notes.md purity contract is the lock; PR 2 review must reject any `globals` / `resolution_context` parameter on `map_type`. |
| 30+ flag CLI surface area on Stripe `customer create` | Already handled by `ParameterFlattener` threshold at `crates/core/src/model_mapper.rs:287`. Tested generically; Python inherits via `LanguageProfile`. |

---

## Out of scope (explicit)

- **Service-pattern discovery** — `StripeClient.v1.customers.list(params: Optional[X])` surface uses `params: Optional[TypedDict] = None` (single positional), not `**kwargs: Unpack[X]`. Different extraction surface. Adapter currently picks the legacy class-method surface; switching is a separate step if needed.
- **Nested TypedDict recursive flattening** — Nested params (Stripe `address`, `payment_method_data`) emit as `TypeKind.Other` and reach the user via `--json-input`. Recursive flattening would mirror C# behavior but creates a consistency hazard until both languages have aligned thresholds.
- **OpenAI `NotGiven` / `Omit` sentinel defaults** — Orthogonal concern. OpenAI uses keyword-only positionals; sentinels surface as default-value parsing complications, not param-discovery failures.
- **`.pyi` stub parsing** — Documented in ADR-022 as the named fallback if a future SDK ships TypedDicts stub-only. Not implemented in this step.
- **Live-Stripe CI gate** — CI cannot require an `sk_test_` key. Developer-local pre-merge check only.
- **Auto-generated full-suite Python E2E in CI** — remains the deferred Step 13b nightly item.
- **Snapshot tests pinned to a Stripe version** — too brittle; synthetic fixtures are the testable contract.
