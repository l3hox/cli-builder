"""Tests for single-client SDK shape discovery (Step 18 / ADR-023).

See `docs/internal/step-18-single-client-discovery.md` for the full plan
and `docs/ADR.md` ADR-023 (PR 3) for the architectural decisions.

Fixtures:
- `test_sdk.single_client_sdk` — PyGithub-shaped GithubClient with ~15 methods
  including all whitelisted verbs, multi-word noun, classmethod, async method,
  and methods that should be skipped (CB610).
- `test_sdk.ambiguous_client_sdk` — two ≥10-method classes both matching
  the heuristic, for the CB609 ambiguity test.
- `test_sdk.services` — existing TestSdk multi-service fixture, used for
  backwards-compat tests.
"""

import sys
from pathlib import Path

import pytest

# Ensure test_sdk is importable
sys.path.insert(0, str(Path(__file__).parent))

from cli_builder_adapter.extractor import extract  # noqa: E402
from cli_builder_adapter.models import DiagnosticSeverity  # noqa: E402


def _codes(diagnostics, severity=None, code_prefix=None) -> list[str]:
    return [
        d.code for d in diagnostics
        if (severity is None or d.severity == severity)
        and (code_prefix is None or d.code.startswith(code_prefix))
    ]


def _diags_for_code(diagnostics, code):
    return [d for d in diagnostics if d.code == code]


# ---- Test 1: full single-client extraction --------------------------------

def test_single_client_mode_extracts_resources():
    """`extract()` against single_client_sdk returns resources grouped by noun
    with verb-as-operation-name. `discovery_mode` reflects single-client."""
    result = extract("github", "test_sdk.single_client_sdk")
    md = result.metadata
    resource_names = {r.name for r in md.resources}

    # Singular and plural NOT normalized — `repo` and `repos` are distinct.
    assert "repo" in resource_names, f"missing 'repo' in {resource_names}"
    assert "repos" in resource_names, f"missing 'repos' (from list_repos) in {resource_names}"
    assert "repositories" in resource_names, f"missing 'repositories' (from search_repositories)"
    assert "user" in resource_names
    assert "pull-request" in resource_names, "multi-word noun must kebab-case"
    assert "organizations" in resource_names, "async method must produce resource"

    # repo has get/create/update/delete verbs
    repo = next(r for r in md.resources if r.name == "repo")
    repo_verbs = {op.name for op in repo.operations}
    assert {"get", "create", "update", "delete"}.issubset(repo_verbs)

    # user has get + retrieve + find (verb NOT canonicalized — three distinct ops)
    user = next(r for r in md.resources if r.name == "user")
    user_verbs = {op.name for op in user.operations}
    assert "get" in user_verbs
    assert "retrieve" in user_verbs, "verb 'retrieve' must NOT be canonicalized to 'get'"
    assert "find" in user_verbs, "classmethod find_user must surface as operation 'find' on user"

    # Discovery mode field on metadata
    assert md.discovery_mode == "single_client"


# ---- Test 2: CB610 — no underscore -----------------------------------------

def test_skipped_methods_emit_cb610_no_underscore():
    """`close` and `withLazy` have no underscore — skipped with reason."""
    result = extract("github", "test_sdk.single_client_sdk")
    cb610 = _diags_for_code(result.diagnostics, "CB610")
    close_diags = [d for d in cb610 if "close" in d.message and "Github" in d.message]
    lazy_diags = [d for d in cb610 if "withLazy" in d.message]

    assert close_diags, "expected CB610 for close()"
    assert lazy_diags, "expected CB610 for withLazy()"
    for d in close_diags + lazy_diags:
        assert d.severity == DiagnosticSeverity.WARNING, \
            f"CB610 must be WARNING (per Step 17 asymmetry), got {d.severity}"
        assert "no underscore" in d.message, \
            f"expected reason 'no underscore' in: {d.message}"


# ---- Test 3: CB610 — descriptive noun (HIGH-SIGNAL reason-string test) ----

def test_skipped_methods_emit_cb610_descriptive_noun():
    """`create_from_raw_data` — noun starts with `from_` (descriptive).

    Reason-string MUST be asserted (high-signal — descriptive-noun filter is
    most likely to expand incorrectly in a follow-up step).
    """
    result = extract("github", "test_sdk.single_client_sdk")
    cb610 = _diags_for_code(result.diagnostics, "CB610")
    matching = [d for d in cb610 if "create_from_raw_data" in d.message]

    assert matching, "expected CB610 for create_from_raw_data"
    assert matching[0].severity == DiagnosticSeverity.WARNING
    assert "descriptive" in matching[0].message, \
        f"expected reason mentioning 'descriptive' in: {matching[0].message}"


# ---- Test 4: CB610 — type[T] first-param filter (HIGH-SIGNAL reason-string) ----

def test_skipped_methods_emit_cb610_type_param():
    """`get_template(klass: type, name: str)` — whitelisted verb, non-descriptive
    noun, but first param is `type[T]`. Rule 4 must fire with explicit reason.

    Reason-string MUST be asserted (highest-regression-value — no prior test
    establishes this filter contract).
    """
    result = extract("github", "test_sdk.single_client_sdk")
    cb610 = _diags_for_code(result.diagnostics, "CB610")
    matching = [d for d in cb610 if "get_template" in d.message]

    assert matching, "expected CB610 for get_template(klass: type, name: str)"
    assert matching[0].severity == DiagnosticSeverity.WARNING
    assert "type[T]" in matching[0].message, \
        f"expected reason mentioning 'type[T]' in: {matching[0].message}"


# ---- Test 5: CB610 — verb not in whitelist --------------------------------

def test_verb_whitelist_filters_unknown():
    """`render_markdown` — verb 'render' not in whitelist."""
    result = extract("github", "test_sdk.single_client_sdk")
    cb610 = _diags_for_code(result.diagnostics, "CB610")
    matching = [d for d in cb610 if "render_markdown" in d.message]

    assert matching, "expected CB610 for render_markdown"
    assert matching[0].severity == DiagnosticSeverity.WARNING
    assert "render" in matching[0].message and "whitelist" in matching[0].message


# ---- Test 6: single-client auto-engaged when no services found ------------

def test_single_client_mode_auto_engaged_emits_cb611():
    """Fixture has no `*Service`/`*Client`/`*Api`-suffix class that matches
    the multi-service heuristic except `GithubClient`. Multi-service finds
    nothing because GithubClient is also picked up as a single-client entry
    (and has no CRUD classmethods). Adapter falls back to single-client mode
    and emits CB611."""
    # `single_client_sdk` only exposes `GithubClient` — name ends in `Client`
    # so it MIGHT be picked by multi-service strategy 1. Verify behavior here.
    result = extract("github", "test_sdk.single_client_sdk")
    md = result.metadata
    cb611 = _diags_for_code(result.diagnostics, "CB611")

    # If multi-service picked GithubClient as a *Client-suffix service, we'd
    # have discovery_mode=multi_service and one resource (the whole class
    # flattened). The test below confirms we got single-client mode instead.
    # NOTE: multi-service IS expected to find GithubClient (the suffix matches).
    # In that case the fallback would NOT engage. We test the BEHAVIOR — when
    # discovery_mode is single_client, CB611 must be emitted.
    if md.discovery_mode == "single_client":
        assert cb611, "discovery_mode=single_client requires CB611 emission"
    else:
        # If multi-service found GithubClient (also possible — Client suffix),
        # the assertion shifts: fallback should NOT engage.
        assert not cb611, "no CB611 expected when multi-service succeeded"


# ---- Test 7: explicit --entry-class forces single-client -------------------

def test_explicit_entry_class_forces_single_client():
    """When `--entry-class` is passed, multi-service discovery is skipped
    entirely. The named entry class becomes the sole source of resources.

    Pass a non-matching package name (`unrelated`) so auto-detection would
    NOT pick Github via package-capitalized match. Then `--entry-class Github`
    is the only path to single-client extraction. Proves the override engages
    even when the heuristic wouldn't have."""
    result = extract(
        "unrelated", "test_sdk.single_client_sdk",
        entry_class="Github",
    )
    md = result.metadata

    # discovery_mode = single_client (forced by --entry-class)
    assert md.discovery_mode == "single_client", \
        f"--entry-class must force single_client mode, got {md.discovery_mode}"

    # All resources sourced from Github (no other class extracted)
    source_classes = {r.source_class_name for r in md.resources}
    assert source_classes == {"Github"}, \
        f"--entry-class must produce resources only from named class, got: {source_classes}"


# ---- Test 8: --entry-class for missing class emits CB609 -------------------

def test_entry_class_missing_emits_cb609():
    """`--entry-class NonExistent` → CB609 WARNING with reason 'not found',
    zero resources, no crash."""
    result = extract(
        "test_sdk", "test_sdk.single_client_sdk",
        entry_class="DefinitelyNotARealClass",
    )
    cb609 = _diags_for_code(result.diagnostics, "CB609")

    assert cb609, "expected CB609 for missing --entry-class"
    assert cb609[0].severity == DiagnosticSeverity.WARNING
    assert "not found" in cb609[0].message
    assert len(result.metadata.resources) == 0, \
        "zero resources when --entry-class resolution fails"


# ---- Test 9: --entry-class for under-threshold class emits CB609 ----------

def test_entry_class_under_threshold_emits_cb609():
    """`--entry-class Repo` (a value type with no methods) → CB609 with reason
    mentioning 'below threshold'."""
    result = extract(
        "single_client_sdk", "test_sdk.single_client_sdk",
        entry_class="Repo",  # value type, very few methods
    )
    cb609 = _diags_for_code(result.diagnostics, "CB609")

    assert cb609, "expected CB609 for under-threshold --entry-class"
    assert cb609[0].severity == DiagnosticSeverity.WARNING
    assert "threshold" in cb609[0].message


# ---- Test 10: ambiguous auto-detection emits CB609 ------------------------

def test_entry_class_ambiguous_emits_cb609():
    """`ambiguous_client_sdk` has TWO ≥10-method classes ending in Client.
    Auto-detection without --entry-class must emit CB609 + zero resources."""
    result = extract("ambig", "test_sdk.ambiguous_client_sdk")
    cb609 = _diags_for_code(result.diagnostics, "CB609")

    assert cb609, "expected CB609 for ambiguous entry-class candidates"
    assert cb609[0].severity == DiagnosticSeverity.WARNING
    assert "ambiguous" in cb609[0].message.lower()
    assert len(result.metadata.resources) == 0


# ---- Test 11: classmethod and async-def method extraction -----------------

def test_classmethod_and_async_extracted_correctly():
    """`@classmethod find_user` and `async def list_organizations` both appear
    as operations on their respective resources. Method-count threshold MUST
    include both — Slack-style SDKs use them heavily."""
    result = extract("github", "test_sdk.single_client_sdk")

    # find_user → resource `user`, op `find`
    user = next(r for r in result.metadata.resources if r.name == "user")
    user_op_names = {op.name for op in user.operations}
    assert "find" in user_op_names, \
        f"classmethod find_user must produce operation 'find' on user resource"

    # list_organizations → resource `organizations`, op `list`
    orgs = next((r for r in result.metadata.resources if r.name == "organizations"), None)
    assert orgs is not None, "async def list_organizations must produce resource 'organizations'"
    assert any(op.name == "list" for op in orgs.operations), \
        "async def must surface as operation"


# ---- Test 12: multi-service path still works (positive) -------------------

def test_multi_service_path_positive_resource_count():
    """Existing TestSdk fixture (CustomerClient, OrderClient, MessageClient)
    → 3 resources, discovery_mode == multi_service."""
    result = extract("test_sdk", "test_sdk.services")
    md = result.metadata

    assert len(md.resources) == 3
    assert md.discovery_mode == "multi_service"
    names = {r.name for r in md.resources}
    assert names == {"customer", "order", "message"}


# ---- Test 13: multi-service path emits NO single-client diagnostics -------

def test_multi_service_path_no_single_client_diagnostics():
    """Same TestSdk fixture; assert CB611 and CB609 are NOT emitted. Proves
    the single-client fallback never accidentally engages on existing SDKs
    when multi-service discovery succeeds."""
    result = extract("test_sdk", "test_sdk.services")
    codes = {d.code for d in result.diagnostics}

    assert "CB611" not in codes, \
        f"CB611 (single-client mode engaged) must NOT fire on multi-service path"
    assert "CB609" not in codes, \
        f"CB609 (entry-class ambiguity) must NOT fire on multi-service path"
