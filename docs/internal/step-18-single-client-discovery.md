# Step 18: Single-client SDK shape discovery in the Python adapter

**Prerequisite:** v0.2.1 shipped (Step 17 PEP 692 resolution). 119 Python adapter tests, 15-job CI green. Stripe `customer list --limit 1` validated end-to-end.

**Output:** Python adapter discovers and extracts resources from SDKs that follow the **single-client model** — one main entry class with many methods naming verb-noun-grouped operations (PyGithub, Notion, Linear, Slack, Anthropic, Twilio, etc.). Generated CLI for `PyGithub`: `github-cli repo get owner/name`, `github-cli search repositories "query"`, `github-cli user get login`, etc.

**Status:** Plan v2 — council-reviewed 2026-05-13, 3 rounds, full convergence. See "Council verdict" section below.

---

## Problem

Probed `PyGithub` end-to-end via `scripts/manual-test-python-sdk.sh` (with `--package github` workaround for the PyPI-name-vs-import-name mismatch). Adapter extracted **zero resources**. The CLI generator faithfully produced an empty noun-verb tree.

Root cause: `python/src/cli_builder_adapter/extractor.py:_discover_services` looks only at top-level classes whose names match `*Service`, `*Client`, `*Api`. PyGithub has none — its public surface is a single `Github` class with 40 methods (`get_repo`, `search_repositories`, `get_user`, `create_gist`, …) plus exception types and value objects. This is the **single-client model**, used by most modern Python SDKs:

| SDK | Entry class | Discovery today |
|---|---|---|
| `stripe` (legacy) | `stripe.Customer`, `stripe.Account`, … (class-method services) | ✅ discovered (Step 17) |
| `stripe` (modern) | `StripeClient.v1.customers` (nested service) | ❌ deferred |
| `openai` | `OpenAI()` → `.chat.completions` | ❌ partial |
| `anthropic` | `Anthropic()` → `.messages` | ❌ same shape as OpenAI |
| `PyGithub` | `Github()` (single class with verb_noun methods) | ❌ this step |
| `notion-client` | `Client()` (similar) | ❌ same shape |
| `slack_sdk.WebClient` | `WebClient()` (similar) | ❌ same shape |

PyGithub is the validation target because its structure is canonical, its API is free-tier, and its method-naming follows clean verb_noun patterns.

## Investigation findings (probed 2026-05-13)

| Question | Answer |
|---|---|
| Does PyGithub have any `*Client`/`*Service`/`*Api`-named class? | No — only `Github`, `GithubIntegration`, `GithubRetry`, value objects, and exception types. |
| How many methods on the `Github` class? | 40 public methods. |
| What verb prefixes appear? | `get_*` (28), `search_*` (6), `create_*` (1 — `create_from_raw_data`), `render_*` (1), `close`/`dump`/`load`/`withLazy` (no `_`). |
| Are method signatures cleanly extractable? | Yes for most. `get_repo(full_name_or_id: int \| str, lazy: Opt[bool] = NotSet) -> Repository` — one positional param + optional. `search_repositories(query: str, sort: Opt[str] = NotSet, order: Opt[str] = NotSet, **qualifiers: Any)` — `**qualifiers` is unannotated (silent-skip per Step 17). |
| Are there utility methods we'd want to filter out? | Yes — `close`, `dump`, `load`, `withLazy` (no verb_noun), `create_from_raw_data` (takes a `type[T]` first arg). |
| How does Github's constructor expose auth? | Multiple paths: `login_or_token: str \| None`, `password: str \| None`, `jwt: str \| None`, `app_auth: AppAuthentication \| None`, `auth: github.Auth.Auth \| None` (recommended modern path). Current `auth_detector.py` heuristic ("name contains 'token'/'key'/'secret'") would match `login_or_token`. |
| What's the PyPI package name vs Python module name? | `PyGithub` (PyPI) imports as `github`. Script needs separate `--package` and pip-install names. |
| Does PyGithub use PEP 692 anywhere? | No — explicit positional/keyword args throughout. Step 17 work doesn't transfer here. |

---

## Council verdict (2026-05-13, 3 rounds, full convergence)

Specialists: SoftwareDeveloper, QaTester, SystemArchitect. Two architectural calls reversed mid-debate (CB610 severity, `_naming.py` module placement); one structural concern that the plan missed entirely (discovery-mode provenance in SdkMetadata).

| Decision | Rationale | Where documented |
|---|---|---|
| **Detection mode: multi-service first → single-client fallback → `--entry-class` override** | Existing extraction stays the default; new path activates only when multi-service finds nothing or user pins the entry. Backwards-compatible by construction. | ADR-023 (PR 3) |
| **`_naming.py` new module** for `VERB_WHITELIST` (frozenset), `DESCRIPTIVE_NOUN_PREFIXES` (tuple), and `parse_verb_noun(method_name) -> tuple[str, str] \| None` helper | Verb whitelist + descriptive-noun filter are *naming policy*, not string-conversion mechanics (which is `_utils.py`'s domain). Mixing them blurs module identity. Step 19+ sub-resource walking will need the same constants — import from `_naming` cleanly. | design-notes.md (PR 3); new module lands in PR 1 |
| **`_extract_single_client_resources` does NOT reuse `_extract_operations`** | `_method_to_verb` (extractor.py:417) conflates verb+noun into one operation name (`get_repo` → `get-repo`). Single-client model needs split: verb `get`, resource `repo`. Shared `_extract_params` stays (preserves Step 17 Unpack path). | PR 1 implementation |
| **Shared `_collect_candidate_classes(module) -> list[type]` helper** extracted from `_discover_services` | Both discovery functions need the same module-walking + import-map handling. Noun derivation (`*Service` → resource name) stays in caller — shared collection, divergent classification. | PR 1 implementation |
| **`discovery_mode: Literal["multi_service", "single_client"]` field added to `SdkMetadata`** | Without provenance, downstream consumers (orchestrator, generator, test harness, future language server) can't tell which discovery path produced the metadata. CB611 is transient; a stable field is the assertion anchor. Default `"multi_service"` for round-trip compat with existing Stripe JSON. | PR 1 (`models.py` field + JSON schema regen) — load-bearing for consumers |
| **JSON schema regeneration lands in PR 1**, not PR 3 | `docs/sdk-metadata-schema.json` has `additionalProperties: false` on `SdkMetadata`. Any adapter emitting `discovery_mode` before the schema accepts it fails validation. Field + schema ship together. | PR 1 |
| **Verb whitelist** (`get`/`list`/`create`/`update`/`delete`/`search`/`find`/`retrieve`) | Whitelist over denylist — silent surface reduction is recoverable only if user can see what was skipped. `CB610` (warning) on every skip, with reason string. New SDKs using `fetch_`/`read_` get visible signal, not silent drops. | design-notes.md (PR 3); `_naming.py` (PR 1) |
| **Method-name parsing rule** | `verb_noun_qualifier` — split on first `_`. Skip if no `_`. Skip if `noun` starts with `from_`/`to_`/`with_`/`for_` (descriptive, not resource). Skip if any param annotation is `type[T]` (factory method, not user operation). Each skip emits CB610 with reason identifying which rule fired. | `parse_verb_noun()` in `_naming.py`; PR 1 |
| **Resource naming** | Noun string after first `_`, lowercase, kebab-case for multi-segment. `get_repo` → `repo`. `search_repositories` → `repositories`. `get_pull_request` → `pull-request`. Singular/plural NOT normalized. | design-notes.md (PR 3) |
| **Verb canonicalization NOT done** | `retrieve` stays `retrieve`, `find_user` is a separate operation from `get_user` under the same resource. Faithful > opinionated for pre-1.0. | design-notes.md (PR 3) |
| **CB610 severity = WARNING** (not Info) | Step 17 set the precedent: CB607/CB608 (param-loss diagnostics) are Warnings. CB610 covers method-loss — same class of partial-failure event. Asymmetry between Step-17 codes and Step-18's was the most-debated point of the council. Final: WARNING. CB611 (mode auto-engaged) stays INFO — that's an observation, not a discard signal. CB609 (entry-class resolution ambiguous) is WARNING. | design-notes.md diagnostic table (PR 3) |
| **Sub-resource discovery deferred** to Step 19+ | PyGithub `Github.get_repo()` returns `Repository` with its own methods. Recursive walking is a separate architectural concern; consistent with ADR-022's nested-TypedDict → `--json-input` fallback pattern. Generated CLI gets a comment noting "sub-resources detected but not expanded". | ADR-023 consequences + README v0.2.2 "Known Limitations" |
| **Generator-side sub-resource note** (detection, not new model field) | When `discovery_mode == "single_client"` and any operation's return type is non-primitive/non-list-of-primitive, the generator emits a comment in the `cli.py` header. NO new field on `Resource` model — premature concept-fixing. | PR 3 (generator change in `crates/gen-python`) |
| **README v0.2.2 "Known Limitations" section** names sub-resource gap; cross-references `--entry-class` flag as escape hatch | The sub-resource deferral is acceptable architecturally but must be communicated. Hidden gap is what made the Step 17 PyGithub probe surprise us. | PR 3 |
| **`--entry-class` CLI flag** (not config file `cli-builder.toml`) | Plan-level decision. ADR-014's config-file design is parked. Watch-line: third adapter-config flag triggers ADR-014 implementation — recorded in **ADR-023 consequences** for durability (survives step-plan archival). | ADR-023 (PR 3) |
| **PyGithub validation in PR 2** — manual-test script run + auth-detector sanity. **Notion live validation deferred** to a future session (user request 2026-05-13). | Notion would prove heuristic generality but requires a separate API setup the user wants to handle later. PR 2 ships PyGithub-only; the heuristic is still gated by `CB609` ambiguity warnings + `--entry-class` override for any single-SDK over-fit. | PR 2 / FUTURE.md |
| **Auth detector unit test** (PR 2) = pure synthetic-ctor unit test, NO live PyGithub import in CI | Test asserts auth_detector picks `login_or_token` from a synthetic ctor that mirrors PyGithub's shape. Live PyGithub install only in the developer-local manual-test script. | PR 2 |
| **Reason-string + severity assertions** required on the two highest-signal CB610 tests | `test_skipped_methods_emit_cb610_descriptive_noun` and `test_skipped_methods_emit_cb610_type_param` — both filters most likely to silently expand/misbehave under refactor. Other CB610 tests can assert code+severity only. | PR 1 test set |
| **`test_multi_service_path_unchanged` splits into TWO tests** | Positive resource count + diagnostic-absence (CB611/CB609 NOT emitted). Backwards-compat insurance against the fallback model accidentally engaging on existing fixtures. | PR 1 test set |
| **`@classmethod` and async-method coverage** = MUST in PR 1 fixture | Real SDKs (Slack, async HTTP clients) use both. Threshold check on method count must not silently undercount `async def` or `@classmethod` methods. | PR 1 fixture + test |
| **`--entry-class` invalid sub-cases** = 3 MUST tests | (i) class absent from module → CB609; (ii) class present but method count below threshold → CB609 with reason "below method threshold"; (iii) class present but multiple ≥-threshold candidates exist → CB609 with reason "ambiguous". | PR 1 test set |
| **Final PR 1 test count: 13** (was 7 in v1 plan) | The growth tracks council additions — not optionalities, all MUSTs. | PR 1 |
| **DECLINE**: extending `_method_to_verb` to be configurable, recursive sub-resource flattening tests in this step, snapshot-pinned PyGithub version | Each declined for clear architectural reason — see consequences in ADR-023. | ADR-023 alternatives |

---

## Implementation plan

### PR 1 — Detection + naming module + SdkMetadata field + synthetic fixture (13 tests)

**Goal:** Land the single-client discovery path end-to-end against a synthetic fixture, with the SdkMetadata contract change and naming-policy module both included.

**1a. Diagnostic codes** (do first)

Confirm `CB609` / `CB610` / `CB611` are next-free in CB6xx. Reserve in extractor diagnostic-emission sites. Update `docs/design-notes.md` diagnostic-code table in PR 3 (functional emission lands in PR 1).

**1b. `SdkMetadata.discovery_mode` field**

`python/src/cli_builder_adapter/models.py` — add `discovery_mode: Literal["multi_service", "single_client"] = "multi_service"` to `SdkMetadata` dataclass. Update `docs/sdk-metadata-schema.json` `SdkMetadata` definition: add to `properties`, list in `required` (since `additionalProperties: false`). Backwards-compat: existing Stripe-derived JSON consumers see no change when reading; new emissions always include the field.

**1c. New `_naming.py` module** (`python/src/cli_builder_adapter/_naming.py`)

```python
"""Naming policy for single-client SDK discovery (Step 18 / ADR-023).

Defines the verb vocabulary the adapter recognizes as CLI-worthy and the
prefix patterns that mark a method's "noun" as descriptive (not a resource).
Imported by extractor.py and any future sub-resource walker (Step 19+).
"""
from __future__ import annotations

VERB_WHITELIST: frozenset[str] = frozenset({
    "get", "list", "create", "update", "delete",
    "search", "find", "retrieve",
})

DESCRIPTIVE_NOUN_PREFIXES: tuple[str, ...] = ("from_", "to_", "with_", "for_")

ENTRY_CLASS_NAME_PATTERN = (
    # Names matching these are candidate entry classes for single-client mode.
    # Matched as: equals package name (capitalized), or ends in Client/Api,
    # or is literally "Client" / "Api".
    # Pattern is documented in ADR-023.
)

MIN_ENTRY_CLASS_METHODS = 10


def parse_verb_noun(method_name: str) -> tuple[str, str] | None:
    """Parse a method name into (verb, noun) per single-client conventions.

    Returns None if the method should be skipped (caller emits CB610 with reason).

    Rules:
    1. Skip methods without `_` (no verb_noun split possible).
    2. Skip methods whose verb is not in VERB_WHITELIST.
    3. Skip methods whose noun starts with any DESCRIPTIVE_NOUN_PREFIXES entry.
    """
    if "_" not in method_name:
        return None  # rule 1
    verb, _, noun = method_name.partition("_")
    if verb not in VERB_WHITELIST:
        return None  # rule 2
    if any(noun.startswith(p) for p in DESCRIPTIVE_NOUN_PREFIXES):
        return None  # rule 3
    return verb, noun


def skip_reason(method_name: str) -> str:
    """Human-readable reason why a method was skipped — used in CB610 messages.

    Mirrors parse_verb_noun's filter order so reasons stay stable across refactors.
    Caller is responsible for the type[T] filter (rule 4) reason string separately.
    """
    if "_" not in method_name:
        return "no underscore — cannot split into verb_noun"
    verb, _, noun = method_name.partition("_")
    if verb not in VERB_WHITELIST:
        return f"verb '{verb}' not in whitelist"
    if any(noun.startswith(p) for p in DESCRIPTIVE_NOUN_PREFIXES):
        return f"descriptive noun prefix (starts with from_/to_/with_/for_)"
    return ""  # method should NOT be skipped
```

**1d. Shared `_collect_candidate_classes(module) -> list[type]` helper**

Extract from `_discover_services` in `extractor.py`. Walks `dir(module)` plus lazy-load registries (Stripe-style `_import_map`). Returns `list[type]` of all top-level classes. Both `_discover_services` (for `*Service`-suffix filter) and `_discover_single_client` (for entry-class heuristic) call this.

**1e. New `_discover_single_client(module, candidate_classes, explicit_name=None) -> type | None`**

If `explicit_name` provided: look up by name in candidates. If not found OR found-but-method-count < `MIN_ENTRY_CLASS_METHODS`, emit `CB609` with reason and return `None`.

Otherwise auto-detect: filter candidates by name pattern (matches package-capitalized name, or ends in `Client`/`Api`) AND method count ≥ `MIN_ENTRY_CLASS_METHODS`. If exactly one match, return it. If zero or multiple, emit `CB609` with appropriate reason and return `None`.

Method counting: include `@classmethod`, `async def`, plain `def`. Use `inspect.getmembers(cls, inspect.isfunction) + inspect.getmembers(cls, inspect.ismethod)` then `getattr_static` to detect classmethods (they don't show up as functions via plain `isfunction`).

**1f. New `_extract_single_client_resources(entry_cls, diagnostics) -> list[Resource]`**

Walk public methods of `entry_cls`. For each method:
1. `parse_verb_noun(name)` — if `None`, emit `CB610` WARNING with reason from `skip_reason(name)` and continue.
2. Check first non-self param's annotation. If it's `type[T]` or `Type[T]`, emit `CB610` WARNING with reason "first param is `type[T]` (factory method)" and continue.
3. Group method into resource by `noun`. Apply existing kebab-case normalization (`pull_request` → `pull-request`).
4. Extract method's params via shared `_extract_params` (preserves Step 17 Unpack[TypedDict] resolution).
5. Each method becomes an `Operation` with `verb` as the operation name (NOT `_method_to_verb`-style verb-noun-flattened name).

Returns `list[Resource]` ready to attach to `SdkMetadata`.

**1g. Wire fallback in `extract()`**

After multi-service discovery, if `service_classes` is empty AND single-client mode wasn't explicitly disabled, call `_discover_single_client(module, candidates)`. If it returns a class, extract resources from it and set `metadata.discovery_mode = "single_client"`. Emit `CB611` INFO with the chosen class name. If `--entry-class` was provided, force single-client mode regardless of multi-service results.

Inline code-comment references ADR-023 so future readers see the design rationale.

**1h. `--entry-class` CLI flag** (Python adapter side + Rust orchestrator side)

- Python adapter `cli.py`: accept `--entry-class <ClassName>` argument, pass to `extract()` via `AdapterOptions` or equivalent.
- Rust orchestrator `crates/cli/src/main.rs` + `crates/cli/src/adapter.rs`: add `--entry-class` flag to `inspect` / `generate` commands; thread through to Python adapter subprocess as `--entry-class <value>`.
- Invalid class name handling: catches in `_discover_single_client` (returns None + CB609). No crash.

**1i. Synthetic fixture** (`python/tests/test_sdk/single_client_sdk/`)

```
single_client_sdk/
├── __init__.py             # exports GithubClient + AmbiguousClient (for CB609 multi-match test)
├── _client.py              # GithubClient with verb_noun methods + classmethod + async method
├── _ambiguous.py           # Two ≥10-method clients matching heuristic (for ambiguity CB609 test)
└── _types.py               # Repo, User return types (no methods worth surfacing)
```

`GithubClient` methods (mirrors PyGithub shape):
- `get_repo(name: str) -> Repo`
- `list_repos(user: str) -> list[Repo]`
- `create_repo(name: str, private: bool = False) -> Repo`
- `delete_repo(name: str) -> None`
- `update_repo(name: str, description: str = None) -> Repo`
- `get_user(login: str) -> User`
- `get_users(org: str) -> list[User]`
- `search_repositories(query: str, sort: str = "stars") -> list[Repo]`
- `get_pull_request(repo: str, number: int) -> dict`  *(tests kebab-case multi-segment noun)*
- `@classmethod` `find_user(cls, login: str) -> User`  *(tests classmethod handling)*
- `async def list_organizations(self) -> list[dict]`  *(tests async method handling)*
- `close() -> None`  → should skip (no `_`)
- `withLazy() -> "GithubClient"`  → should skip (no `_`)
- `create_from_raw_data(klass: type, data: dict) -> object`  → should skip (descriptive noun + `type[T]` param)
- `render_markdown(text: str) -> str`  → should skip (verb `render` not in whitelist)

`AmbiguousClient`-shaped second class with ≥10 methods matching the entry-class name pattern — used only in the multi-match CB609 test.

**1j. PR 1 test set** (13 tests in `python/tests/test_extractor_single_client.py`)

| # | Test | Asserts |
|---|---|---|
| 1 | `test_single_client_mode_extracts_resources` | 5 resources from `GithubClient` fixture (`repo`, `repos`, `user`, `users`, `repositories`, `pull-request`, plus async/classmethod added ones — actually count from fixture). Each has expected operation verbs. `metadata.discovery_mode == "single_client"`. |
| 2 | `test_skipped_methods_emit_cb610_no_underscore` | `close`, `withLazy` each emit CB610 WARNING with reason "no underscore". |
| 3 | `test_skipped_methods_emit_cb610_descriptive_noun` | `create_from_raw_data` emits CB610 WARNING; assert reason string contains "descriptive" (high-signal — descriptive-noun filter most likely to misbehave). |
| 4 | `test_skipped_methods_emit_cb610_type_param` | `create_from_raw_data` *also* hits the `type[T]` filter (second filter — only one CB610 per method emitted, with reason from whichever rule fired first per implementation order). Refine: synthetic method `register_class(klass: type) -> None` (verb `register` not in whitelist, but type[T] filter is second — orchestrate fixture so this one method tests `type[T]` filter cleanly). Assert reason string contains "type[T]". |
| 5 | `test_verb_whitelist_filters_unknown` | `render_markdown` → CB610 WARNING with reason "verb 'render' not in whitelist". |
| 6 | `test_single_client_mode_auto_engaged_emits_cb611` | Fixture has no `*Service`/`*Client`/`*Api`-suffix classes (except GithubClient itself); adapter falls through to single-client. CB611 INFO emitted with chosen class name. `metadata.discovery_mode == "single_client"`. |
| 7 | `test_explicit_entry_class_forces_single_client` | Fixture includes BOTH a `*Service`-suffix class and `GithubClient`; with `--entry-class GithubClient`, only single-client extraction happens. Assert resources come from GithubClient exclusively (no resources from the multi-service class). |
| 8 | `test_entry_class_missing_emits_cb609` | `--entry-class NonExistent` → CB609 WARNING with reason "class not found". Zero resources extracted. No crash. |
| 9 | `test_entry_class_under_threshold_emits_cb609` | `--entry-class SmallClient` (fixture has class with < 10 methods) → CB609 WARNING with reason "below method threshold". |
| 10 | `test_entry_class_ambiguous_emits_cb609` | Auto-detect (no `--entry-class`) on fixture with TWO ≥10-method candidates matching heuristic → CB609 WARNING with reason "ambiguous". |
| 11 | `test_classmethod_and_async_extracted_correctly` | `find_user` (@classmethod) and `list_organizations` (async def) both appear as operations on their respective resources. Method-count threshold includes both. |
| 12 | `test_multi_service_path_positive_resource_count` | Existing TestSdk fixture (`CustomerClient`, `OrderClient`, `MessageClient`) → 3 resources extracted. `metadata.discovery_mode == "multi_service"`. |
| 13 | `test_multi_service_path_no_single_client_diagnostics` | Same TestSdk fixture; assert NO `CB611` and NO `CB609` emitted. Proves single-client fallback didn't accidentally engage. |

**1k. PR 1 pass criteria**

- 13 new tests green
- 119 existing Python tests still green (no regression in multi-service path)
- JSON schema validates against existing fixture metadata files
- `make ci` green locally
- PR description includes a synthetic-fixture run showing extracted resources + diagnostic output

### PR 2 — PyGithub live validation + script `PYTHON_MODULE` decoupling + auth detector unit test

**2a. Manual-test script gains `PYTHON_MODULE` env var**

`scripts/manual-test-python-sdk.sh` — decouple PyPI install name from Python import name. `SDK_NAME=PyGithub PYTHON_MODULE=github CLI_NAME=github-cli scripts/manual-test-python-sdk.sh` works end-to-end. When `PYTHON_MODULE` is unset, defaults to `$SDK_NAME` (backward compat for Stripe).

**2b. Auth detector unit test**

New test `test_pygithub_auth_detector_finds_login_or_token` in `python/tests/test_auth_detector.py`. Pure synthetic-ctor unit test (NOT requiring `pip install PyGithub` in CI). Constructs an `inspect.Signature` mimicking PyGithub's ctor:
```python
login_or_token: str | None = None,
password: str | None = None,
jwt: str | None = None,
...
```
Asserts the detector picks `login_or_token` as the auth parameter. If detector fails: broaden the existing pattern list in `auth_detector.py` (e.g., add `login_or_*` pattern).

**2c. PyGithub end-to-end smoke (developer-local, NOT CI)**

`SDK_NAME=PyGithub PYTHON_MODULE=github CLI_NAME=github-cli GITHUB_TOKEN=ghp_... scripts/manual-test-python-sdk.sh` — all 9 phases pass + a new Phase 7c "github flag-presence regression gate" that asserts canonical flag names appear in `github-cli repo get --help` and `github-cli user get --help`.

**2d. PR 2 pass criteria**

- 1 new pytest test green (auth detector)
- Manual-test script run for PyGithub captured in PR description (with at least `--help` output for `github-cli repo get`, `github-cli user get`, `github-cli search repositories`)
- `make ci` green

### PR 3 — ADR-023 + design-notes + CHANGELOG v0.2.2 + README "Known Limitations" + generator-side sub-resource note

**3a. ADR-023** — `Single-client SDK shape discovery via verb-noun method grouping and SdkMetadata.discovery_mode provenance`. Full Nygard format (matches ADR-022 depth). Sections:
- Status: Accepted (council-reviewed)
- Context: PyGithub probe data, single-client model prevalence
- Decision: multi-service first → single-client fallback → `--entry-class` override; `_naming.py` module; `discovery_mode` field on SdkMetadata; CB609/610/611 in CB6xx; sub-resource discovery deferred
- Rationale: each consensus item with reasoning
- 7-row alternatives table (including: mandatory `--entry-class`, verb denylist, `_method_to_verb` reuse, recursive sub-resource walking)
- Consequences: backwards-compatible by construction; CLI surface area trade-off (PyGithub exposes ~30 operations across ~10 resources); **3rd adapter-config flag triggers ADR-014 (`cli-builder.toml`) implementation** — durable watch-line
- Verification: synthetic fixture (PR 1) + PyGithub live (PR 2)

**3b. design-notes.md additions**

- New "Python adapter — single-client discovery mode" subsection covering: detection heuristic, `_naming.py` module contract, verb whitelist rationale, descriptive-noun filter, `type[T]` filter, singular/plural NOT normalized, verb NOT canonicalized.
- CB6xx diagnostic table: `CB609` (warning), `CB610` (warning), `CB611` (info).
- Multi-word-noun parsing (`get_pull_request` → `pull-request`) explicitly documented.

**3c. Generator-side sub-resource note** (`crates/gen-python`)

When `metadata.discovery_mode == "single_client"` AND any operation's return type is non-primitive/non-list-of-primitive, the generator emits a comment in the generated `cli.py` header:
```python
# NOTE: This CLI was generated from a single-client SDK. Some operations return
# complex objects (e.g., Repository, Issue) whose own methods are not surfaced
# as nested CLI commands. Use --json output to inspect returned objects, then
# operate on them via the SDK directly. See ADR-023 for details.
```

**3d. CHANGELOG.md** — v0.2.2 entry referencing ADR-023.

**3e. README.md "Known Limitations" section**

Names the sub-resource gap explicitly with PyGithub as concrete example. Cross-references `--entry-class` flag as the escape hatch when auto-detection picks wrong. Cross-references ADR-023.

**3f. FUTURE.md** — single-client SDK support moves from "Next up" to "Completed under v0.2.2". Sub-resource discovery added as "Later" item (becomes Step 19+ candidate). Notion live validation explicitly listed as deferred.

**3g. Memory update.**

---

## Architecture documentation surfaces (CONTRIBUTING.md hierarchy)

| Decision | Document | Edited in |
|---|---|---|
| Detection mode + `_naming.py` module + sub-resource deferral + CB6xx codes + ADR-014 flag-accumulation watch-line | `docs/ADR.md` ADR-023 | PR 3 |
| `_naming.py` contract + verb whitelist rationale + descriptive-noun filter + `type[T]` filter + multi-word-noun parsing rule | `docs/design-notes.md` | PR 3 |
| Diagnostic codes `CB609`/`CB610`/`CB611` | `docs/design-notes.md` diagnostic-code table | PR 3 (emission lands PR 1, table update PR 3) |
| `SdkMetadata.discovery_mode` field | `python/src/cli_builder_adapter/models.py` + `docs/sdk-metadata-schema.json` | PR 1 |
| Sub-resource gap | `README.md` "Known Limitations" + generated CLI header comment | PR 3 |
| `--entry-class` CLI flag | (orchestrator changes; not architectural) | PR 1 |
| PyPI-vs-import name script decoupling | `scripts/manual-test-python-sdk.sh` inline comment | PR 2 |
| v0.2.2 release notes | `CHANGELOG.md` | PR 3 |

---

## Key files

| File | Change | PR |
|---|---|---|
| `python/src/cli_builder_adapter/_naming.py` | **New module**: `VERB_WHITELIST`, `DESCRIPTIVE_NOUN_PREFIXES`, `MIN_ENTRY_CLASS_METHODS`, `parse_verb_noun()`, `skip_reason()` | PR 1 |
| `python/src/cli_builder_adapter/models.py` | Add `discovery_mode: Literal["multi_service", "single_client"] = "multi_service"` to `SdkMetadata` | PR 1 |
| `docs/sdk-metadata-schema.json` | Add `discoveryMode` property to `SdkMetadata` definition (camelCase), include in `required` | PR 1 |
| `python/src/cli_builder_adapter/extractor.py` | `_collect_candidate_classes`, `_discover_single_client`, `_extract_single_client_resources`, fallback wiring in `extract()` | PR 1 |
| `python/src/cli_builder_adapter/cli.py` | Accept `--entry-class` argument | PR 1 |
| `crates/cli/src/main.rs` + `crates/cli/src/adapter.rs` | Wire `--entry-class` flag from orchestrator into adapter subprocess args | PR 1 |
| `python/tests/test_extractor_single_client.py` | 13 new tests | PR 1 |
| `python/tests/test_sdk/single_client_sdk/` | New synthetic fixture | PR 1 |
| `python/tests/test_auth_detector.py` | 1 new test (`test_pygithub_auth_detector_finds_login_or_token`) | PR 2 |
| `scripts/manual-test-python-sdk.sh` | Add `PYTHON_MODULE` env var + Phase 7c flag-presence regression gate for PyGithub | PR 2 |
| `docs/ADR.md` | ADR-023 (Nygard format) | PR 3 |
| `docs/design-notes.md` | Single-client subsection + CB6xx table update | PR 3 |
| `crates/gen-python/src/...` | Generator emits sub-resource note comment when `discovery_mode == "single_client"` and complex return types detected | PR 3 |
| `CHANGELOG.md` | v0.2.2 entry | PR 3 |
| `README.md` | "Known Limitations" section + Validated-SDKs row | PR 3 |
| `AGENTS.md`, `FUTURE.md`, `docs/cli-builder-spec.md` ADR table | Version label + ADR index updates | PR 3 |

---

## Verification

```bash
# PR 1
cd python && pytest -v test_extractor_single_client.py    # 13 new tests
cd python && pytest                                       # 119 + 13 = 132 tests green
# JSON schema validation:
python3 -c "import json, jsonschema; jsonschema.Draft202012Validator(json.load(open('docs/sdk-metadata-schema.json'))).check_schema()"

# PR 2
cd python && pytest                                       # 132 + 1 = 133 tests
SDK_NAME=PyGithub PYTHON_MODULE=github CLI_NAME=github-cli \
  scripts/manual-test-python-sdk.sh                       # all phases pass
GITHUB_TOKEN=ghp_... SDK_NAME=PyGithub PYTHON_MODULE=github CLI_NAME=github-cli \
  scripts/manual-test-python-sdk.sh                       # live API validated

# PR 3
make ci                                                   # 15-job matrix green
```

---

## Risks (revised post-council)

| Risk | Mitigation |
|---|---|
| `discovery_mode` field breaks existing fixture JSON files | Default value `"multi_service"`; existing JSON deserializes without change. JSON schema requires the field on new emissions but tooling produces it automatically. |
| Heuristic picks wrong entry class on edge-case SDKs (e.g., `GithubIntegration` over `Github`) | Method-count threshold (10) + name-pattern combo + `--entry-class` override. `CB609` warning on ambiguity. |
| Single-client fallback hides multi-service regression | Two-test gate (`test_multi_service_path_positive_resource_count` + `test_multi_service_path_no_single_client_diagnostics`) ensures the fallback never engages on existing fixtures. |
| Verb whitelist misses legitimate operations (`fetch_X`, `read_X`) | Conservative is correct — silent surface reduction is the failure class. CB610 WARNING on every skip with reason; user feedback drives whitelist expansion in future steps. |
| Singular/plural confusion creates two resources where one was intended (`repo` and `repos`) | Documented as known cost in ADR-023. Better than over-normalizing. SDK author chose names; we reproduce faithfully. |
| Sub-resource gap surprises end users | README "Known Limitations" + generated CLI header comment + ADR-023 + `--json-input` fallback. Hidden gap is what surprised us; explicit gap is acceptable. |
| `--entry-class` flag accumulation | ADR-023 consequences note triggers ADR-014 (`cli-builder.toml`) implementation when a third adapter-config flag is added. |
| @classmethod / async-method method-count miscounting causes wrong entry-class auto-detection | `_collect_candidate_classes` enumerates via `inspect.getmembers` + `getattr_static` to surface classmethods correctly. Test 11 (`test_classmethod_and_async_extracted_correctly`) is the gate. |

---

## Out of scope (explicit)

- **Notion live validation** — deferred (user request 2026-05-13). PR 2 ships PyGithub-only. Notion synthetic fixture stub also out of scope.
- **Sub-resource discovery via return-type walking** — Step 19+ candidate. README "Known Limitations" + generated CLI comment communicate the gap.
- **Verb canonicalization** (`retrieve` → `get`). Adapter reflects SDK author's verbs.
- **Singularization** (`gists` → `gist`). Same reasoning.
- **Multi-entry-class single-client SDKs** — either CB609 fires or `--entry-class` explicit. Run cli-builder twice if both clients are wanted.
- **Service-pattern discovery for modern Stripe** (`StripeClient.v1.customers.list`) — different shape (nested service tree). Independent step.
- **OpenAI/Anthropic `NotGiven`/`Omit` sentinel handling.** Independent concern.
- **Configuration file (`cli-builder.toml`).** ADR-014 covered the design but never built it. `--entry-class` stays a CLI flag for now. Watch-line in ADR-023.
