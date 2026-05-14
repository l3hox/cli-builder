#!/usr/bin/env bash
# Manual E2E test for the Python CLI pipeline against a real SDK.
#
# Does what the placeholder test at crates/gen-python/tests/e2e.rs can't:
#   adapter → generator → venv → pip install → console-script → live API call
#
# Usage:
#   scripts/manual-test-python-sdk.sh                   # stripe, skip live calls
#   SDK_NAME=stripe scripts/manual-test-python-sdk.sh   # same
#   STRIPE_API_KEY=sk_test_... scripts/manual-test-python-sdk.sh   # stripe, live
#   SDK_NAME=PyGithub PYTHON_MODULE=github CLI_NAME=github-cli ENTRY_CLASS=Github \
#       scripts/manual-test-python-sdk.sh               # PyGithub (PyPI name ≠ import name)
#   GITHUB_TOKEN=ghp_... SDK_NAME=PyGithub PYTHON_MODULE=github CLI_NAME=github-cli ENTRY_CLASS=Github \
#       scripts/manual-test-python-sdk.sh               # PyGithub, live
#
# Environment:
#   SDK_NAME         PyPI package name (default: stripe). Used for `pip install`.
#   PYTHON_MODULE    Python import name (default: SDK_NAME). Used for the
#                    cli-builder --package argument. PyGithub installs as
#                    `PyGithub` but imports as `github` — decouple via this var.
#   ENTRY_CLASS      Force single-client discovery on this class (ADR-023).
#                    Required for SDKs where auto-detection is ambiguous (e.g.,
#                    PyGithub has Github + GithubIntegration + GithubRetry all
#                    matching the heuristic).
#   SDK_VERSION      Optional version constraint (default: unpinned)
#   CLI_NAME         Generated CLI name (default: {SDK_NAME}-cli)
#   API_KEY_VAR      Env var holding the API key (default: inferred from SDK_NAME)
#   WORK_DIR         Working directory (default: /tmp/cli-builder-manual-test)
#   SKIP_LIVE        Set to 1 to skip live API calls even if API key is set
#
# Output:
#   Step-by-step log to stdout. A summary table at the end showing pass/fail
#   per phase. Exits non-zero if any phase fails.

set -uo pipefail  # note: no -e — we want to continue past failures and report them all

# ---- Configuration ---------------------------------------------------------

REPO_ROOT="$(cd "$(dirname "$0")/.."; pwd)"
SDK_NAME="${SDK_NAME:-stripe}"
# Python module name (the `--package` argument to cli-builder). Defaults to
# SDK_NAME for backward compat with SDKs where install-name == import-name
# (Stripe). Override when they differ — e.g., SDK_NAME=PyGithub PYTHON_MODULE=github.
PYTHON_MODULE="${PYTHON_MODULE:-$SDK_NAME}"
# Forced single-client entry class (ADR-023). Empty = auto-detect via heuristic.
ENTRY_CLASS="${ENTRY_CLASS:-}"
SDK_VERSION="${SDK_VERSION:-}"
CLI_NAME="${CLI_NAME:-${SDK_NAME}-cli}"
WORK_DIR="${WORK_DIR:-/tmp/cli-builder-manual-test}"
SKIP_LIVE="${SKIP_LIVE:-0}"

# Infer the API key env var from SDK name if not set explicitly.
if [[ -z "${API_KEY_VAR:-}" ]]; then
    case "$SDK_NAME" in
        stripe)    API_KEY_VAR="STRIPE_API_KEY" ;;
        openai)    API_KEY_VAR="OPENAI_API_KEY" ;;
        PyGithub)  API_KEY_VAR="GITHUB_TOKEN" ;;
        *)         API_KEY_VAR="$(echo "$SDK_NAME" | tr '[:lower:]' '[:upper:]')_API_KEY" ;;
    esac
fi

# Phases — pass/fail tracked per phase, printed in summary at the end.
declare -A PHASE_STATUS
declare -a PHASE_ORDER

record() {
    local phase="$1" status="$2" note="${3:-}"
    PHASE_STATUS["$phase"]="$status|$note"
    PHASE_ORDER+=("$phase")
}

# ---- Helpers ---------------------------------------------------------------

heading() {
    echo ""
    echo "═══════════════════════════════════════════════════════════════════════════════"
    echo " $1"
    echo "═══════════════════════════════════════════════════════════════════════════════"
}

ok()   { echo "  ✓ $1"; }
fail() { echo "  ✗ $1" >&2; }
info() { echo "  · $1"; }

CLI_BIN="$REPO_ROOT/crates/target/release/cli-builder"
VENV="$WORK_DIR/venv"
OUT_DIR="$WORK_DIR/cli"
METADATA_JSON="$WORK_DIR/metadata.json"

# ---- Phase 1 — build cli-builder -------------------------------------------

heading "Phase 1 — build cli-builder (release)"

(cd "$REPO_ROOT/crates" && cargo build --release --package cli-builder) \
    && record "build" "PASS" \
    || { record "build" "FAIL" "cargo build failed — see output above"; fail "Cannot proceed without cli-builder binary."; exit 1; }

if [[ ! -x "$CLI_BIN" ]]; then
    record "build" "FAIL" "binary not at expected path: $CLI_BIN"
    exit 1
fi

ok "built $CLI_BIN"

# ---- Phase 2 — workspace setup ---------------------------------------------

heading "Phase 2 — workspace setup"

rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR"
ok "clean workspace at $WORK_DIR"

# Need a Python with the target SDK installed to extract metadata.
# Use a dedicated venv so we don't touch the system Python.
SETUP_VENV="$WORK_DIR/setup-venv"
if python3 -m venv "$SETUP_VENV" 2>/dev/null; then
    ok "setup venv created at $SETUP_VENV"
else
    record "build" "FAIL" "python3 -m venv failed — install python3-venv (apt install python3-venv on Debian/Ubuntu)"
    fail "Cannot proceed without venv support."
    exit 1
fi

SETUP_PIP="$SETUP_VENV/bin/pip"
SETUP_PY="$SETUP_VENV/bin/python"

if [[ -n "$SDK_VERSION" ]]; then
    PKG_SPEC="${SDK_NAME}==${SDK_VERSION}"
else
    PKG_SPEC="${SDK_NAME}"
fi

info "installing ${PKG_SPEC} into setup venv (for metadata extraction)"
if "$SETUP_PIP" install -q "$PKG_SPEC"; then
    ok "installed $PKG_SPEC"
    record "setup" "PASS"
else
    record "setup" "FAIL" "pip install $PKG_SPEC failed"
    exit 1
fi

# ---- Phase 3 — inspect (metadata extraction via adapter) -------------------

heading "Phase 3 — inspect: extract metadata from $SDK_NAME (module: $PYTHON_MODULE)"

# The Python adapter runs as `python -m cli_builder_adapter ...`. Our orchestrator
# invokes it as a subprocess — we just need Python in PATH with the adapter
# installed. Install the adapter from this repo into the setup venv.
info "installing cli-builder-adapter-python into setup venv"
if "$SETUP_PIP" install -q -e "$REPO_ROOT/python" 2>&1 | tail -3; then
    ok "adapter installed"
else
    record "setup" "FAIL" "adapter pip install failed"
    exit 1
fi

# Run inspect via the Rust orchestrator, pointing it at the setup venv's python
# (The orchestrator appends `-m cli_builder_adapter --package X --json` itself;
#  CLI_BUILDER_PYTHON_ADAPTER is just the python binary.)
INSPECT_ARGS=(inspect --adapter python --package "$PYTHON_MODULE" --json)
if [[ -n "$ENTRY_CLASS" ]]; then
    INSPECT_ARGS+=(--entry-class "$ENTRY_CLASS")
fi
info "running: cli-builder ${INSPECT_ARGS[*]}"
export CLI_BUILDER_PYTHON_ADAPTER="$SETUP_PY"
if "$CLI_BIN" "${INSPECT_ARGS[@]}" > "$METADATA_JSON" 2> "$WORK_DIR/inspect.stderr"; then
    RESOURCES=$("$SETUP_PY" -c "import json; d=json.load(open('$METADATA_JSON')); print(len(d.get('metadata',d).get('resources',[])))" 2>/dev/null || echo "?")
    ok "metadata written ($(wc -c < "$METADATA_JSON") bytes, $RESOURCES resources)"
    record "inspect" "PASS" "$RESOURCES resources"
else
    fail "inspect failed — stderr:"
    cat "$WORK_DIR/inspect.stderr" | sed 's/^/    /'
    record "inspect" "FAIL" "see $WORK_DIR/inspect.stderr"
fi

# ---- Phase 4 — generate (Python CLI from metadata) -------------------------

heading "Phase 4 — generate: Python CLI from metadata"

GENERATE_ARGS=(generate --adapter python --package "$PYTHON_MODULE" --generator python --output "$OUT_DIR" --cli-name "$CLI_NAME")
if [[ -n "$ENTRY_CLASS" ]]; then
    GENERATE_ARGS+=(--entry-class "$ENTRY_CLASS")
fi
info "running: cli-builder ${GENERATE_ARGS[*]}"
if "$CLI_BIN" "${GENERATE_ARGS[@]}" 2> "$WORK_DIR/generate.stderr"; then
    FILE_COUNT=$(find "$OUT_DIR" -name "*.py" -type f | wc -l)
    ok "generated project at $OUT_DIR ($FILE_COUNT .py files)"
    record "generate" "PASS" "$FILE_COUNT .py files"
else
    fail "generate failed — stderr:"
    cat "$WORK_DIR/generate.stderr" | sed 's/^/    /'
    record "generate" "FAIL" "see $WORK_DIR/generate.stderr"
    echo ""
    echo "Cannot proceed without generated CLI. Exiting."
    exit 1
fi

# ---- Phase 5 — syntax check (ast.parse every .py) --------------------------

heading "Phase 5 — syntax check: ast.parse every generated .py"

AST_FAILURES=0
while IFS= read -r py_file; do
    if ! "$SETUP_PY" -c "import ast, sys; ast.parse(open(sys.argv[1]).read())" "$py_file" 2> "$WORK_DIR/ast_error.tmp"; then
        fail "ast.parse failed: $py_file"
        cat "$WORK_DIR/ast_error.tmp" | sed 's/^/    /'
        AST_FAILURES=$((AST_FAILURES + 1))
    fi
done < <(find "$OUT_DIR" -name "*.py" -type f)

if [[ $AST_FAILURES -eq 0 ]]; then
    ok "all $FILE_COUNT .py files parse cleanly"
    record "syntax" "PASS"
else
    record "syntax" "FAIL" "$AST_FAILURES files failed ast.parse"
fi

# ---- Phase 6 — pip install into a fresh venv -------------------------------

heading "Phase 6 — pip install generated CLI into fresh venv"

python3 -m venv "$VENV"
PIP="$VENV/bin/pip"
PY="$VENV/bin/python"

info "installing $SDK_NAME into target venv"
if "$PIP" install -q "$PKG_SPEC" 2>&1 | tail -3; then
    ok "$SDK_NAME installed"
else
    fail "target venv $SDK_NAME install failed"
    record "install-sdk" "FAIL"
fi

info "pip install -e $OUT_DIR"
if "$PIP" install -e "$OUT_DIR" 2>&1 | tail -10; then
    ok "generated CLI installed"
    record "install-cli" "PASS"
else
    fail "pip install of generated CLI failed"
    record "install-cli" "FAIL" "see output above"
    echo ""
    echo "Cannot proceed without installed CLI. Exiting."
    exit 1
fi

# ---- Phase 7 — console script smoke tests (--help) -------------------------

heading "Phase 7 — --help smoke tests"

CLI_EXEC="$VENV/bin/$CLI_NAME"

if [[ ! -x "$CLI_EXEC" ]]; then
    fail "expected console script at $CLI_EXEC, not found"
    record "help-root" "FAIL" "[project.scripts] entry point missing"
else
    info "running: $CLI_NAME --help"
    if "$CLI_EXEC" --help > "$WORK_DIR/help-root.out" 2> "$WORK_DIR/help-root.err"; then
        ok "root --help works"
        head -15 "$WORK_DIR/help-root.out" | sed 's/^/    /'
        record "help-root" "PASS"
    else
        fail "$CLI_NAME --help failed (exit $?)"
        cat "$WORK_DIR/help-root.err" | sed 's/^/    /'
        record "help-root" "FAIL"
    fi
fi

# Try noun-level --help for a few resources. Pick whatever shows up in the
# root --help output — that's a stable way to probe discoverability.
if [[ -s "$WORK_DIR/help-root.out" ]]; then
    # Grab first 3 command names from the "Commands:" section of click output.
    COMMANDS=$(awk '/^Commands:/{flag=1; next} flag && /^[[:space:]]*[a-z]/{print $1}' "$WORK_DIR/help-root.out" | head -3)
    if [[ -n "$COMMANDS" ]]; then
        NOUN_FAILURES=0
        NOUN_TOTAL=0
        for cmd in $COMMANDS; do
            NOUN_TOTAL=$((NOUN_TOTAL + 1))
            info "running: $CLI_NAME $cmd --help"
            if "$CLI_EXEC" "$cmd" --help > "$WORK_DIR/help-$cmd.out" 2> "$WORK_DIR/help-$cmd.err"; then
                ok "$cmd --help works"
            else
                fail "$cmd --help failed — stderr:"
                cat "$WORK_DIR/help-$cmd.err" | sed 's/^/    /'
                NOUN_FAILURES=$((NOUN_FAILURES + 1))
            fi
        done
        if [[ $NOUN_FAILURES -eq 0 ]]; then
            record "help-nouns" "PASS" "$NOUN_TOTAL commands probed"
        else
            record "help-nouns" "FAIL" "$NOUN_FAILURES/$NOUN_TOTAL failed"
        fi
    else
        record "help-nouns" "SKIP" "no commands discovered from root --help"
    fi
fi

# ---- Phase 7b — flag-presence gate (Stripe-specific, ADR-022 regression guard) -------
#
# Pre-Step-17, stripe-cli `customer list --help` printed zero flags. Catching a
# silent regression of that bug is the whole point of this script. For Stripe,
# we assert specific flag names are present in customer list / customer create
# --help output. For other SDKs, this phase is SKIPped (we don't know the API).

if [[ "$SDK_NAME" == "stripe" ]]; then
    heading "Phase 7b — Stripe flag-presence regression gate (ADR-022)"
    EXPECTED_LIST_FLAGS=("--limit" "--email" "--starting_after" "--ending_before")
    EXPECTED_CREATE_FLAGS=("--email" "--name" "--description" "--phone")
    FLAG_FAILURES=0

    # Run customer list / create --help and capture
    for cmd_args in "customer list" "customer create"; do
        out_file="$WORK_DIR/flagcheck-$(echo "$cmd_args" | tr ' ' '-').out"
        if ! "$CLI_EXEC" $cmd_args --help > "$out_file" 2>&1; then
            fail "$CLI_NAME $cmd_args --help did not exit cleanly"
            FLAG_FAILURES=$((FLAG_FAILURES + 1))
            continue
        fi
    done

    for flag in "${EXPECTED_LIST_FLAGS[@]}"; do
        if ! grep -q -- "$flag" "$WORK_DIR/flagcheck-customer-list.out" 2>/dev/null; then
            fail "expected $flag in 'customer list --help'; not found (regression of ADR-022 / Step 17 bug)"
            FLAG_FAILURES=$((FLAG_FAILURES + 1))
        fi
    done
    for flag in "${EXPECTED_CREATE_FLAGS[@]}"; do
        if ! grep -q -- "$flag" "$WORK_DIR/flagcheck-customer-create.out" 2>/dev/null; then
            fail "expected $flag in 'customer create --help'; not found"
            FLAG_FAILURES=$((FLAG_FAILURES + 1))
        fi
    done

    if [[ $FLAG_FAILURES -eq 0 ]]; then
        ok "all expected Stripe customer flags present"
        record "flags-stripe" "PASS" "list+create expected flags found"
    else
        record "flags-stripe" "FAIL" "$FLAG_FAILURES flag(s) missing"
    fi
fi

# ---- Phase 7c — GitHub flag-presence regression gate (Step 18 / ADR-023) ----
#
# Single-client SDK validation. Pre-Step-18, cli-builder extracted zero
# resources from PyGithub (no *Service/*Client/*Api-suffix classes). After
# Step 18, the Github entry class is discovered via single-client mode and
# its verb_noun methods become CLI ops. Asserts a few canonical operations
# end up with --help that exposes their expected parameter flag.

if [[ "$SDK_NAME" == "PyGithub" ]]; then
    heading "Phase 7c — GitHub regression gate (ADR-023)"
    GH_FLAG_FAILURES=0

    # Step 18 / v0.2.2 surface for PyGithub:
    #   1. Resources are discovered (Github → repo/user/repositories/…)
    #   2. Operations are emitted with --json-input fallback (per-param flag
    #      emission for sentinel-Union types like `Opt[X]` is a known gap —
    #      deferred to a future step; see README "Known Limitations").
    #   3. Operations are WIRED to real SDK calls — NOT the
    #      "client construction not available" stub. This is the load-bearing
    #      regression check: if the can_construct gate breaks, every PyGithub
    #      op silently becomes a no-op stub. Catch it here.
    GITHUB_OPS=("repo get" "user get" "repositories search")

    for op in "${GITHUB_OPS[@]}"; do
        out_file="$WORK_DIR/flagcheck-github-$(echo "$op" | tr ' ' '-').out"
        if ! "$CLI_EXEC" $op --help > "$out_file" 2>&1; then
            fail "$CLI_NAME $op --help did not exit cleanly"
            GH_FLAG_FAILURES=$((GH_FLAG_FAILURES + 1))
            continue
        fi
        # 1: --json-input must appear (proves operation extraction → click code-gen)
        if ! grep -q -- "--json-input" "$out_file" 2>/dev/null; then
            fail "$op --help missing --json-input (operation extraction regression)"
            GH_FLAG_FAILURES=$((GH_FLAG_FAILURES + 1))
        fi
    done

    # Regression check #2: the GENERATED user.py file must NOT contain the
    # "client construction not available" stub string for the get operation.
    # If the can_construct gate misfires (e.g., ctor params not attached to
    # all single-client resources), every PyGithub op becomes a stub — silent
    # but catastrophic.
    USER_PY_FILE="$OUT_DIR/src/${CLI_NAME//-/_}/commands/user.py"
    if grep -q "client construction not available" "$USER_PY_FILE" 2>/dev/null; then
        fail "user.py contains 'client construction not available' stub — \
can_construct gate regression"
        GH_FLAG_FAILURES=$((GH_FLAG_FAILURES + 1))
    fi

    if [[ $GH_FLAG_FAILURES -eq 0 ]]; then
        ok "PyGithub ops wired to real SDK calls (no stubs); --json-input present"
        record "flags-github" "PASS" "${#GITHUB_OPS[@]} ops probed + stub check"
    else
        record "flags-github" "FAIL" "$GH_FLAG_FAILURES check(s) failed"
    fi
fi

# ---- Phase 8 — live API call (optional) ------------------------------------

heading "Phase 8 — live API call"

API_KEY_VALUE="${!API_KEY_VAR:-}"

if [[ "$SKIP_LIVE" == "1" ]]; then
    info "SKIP_LIVE=1 — skipping live API call"
    record "live-api" "SKIP" "SKIP_LIVE=1"
elif [[ -z "$API_KEY_VALUE" ]]; then
    info "\$$API_KEY_VAR not set — skipping live API call"
    info "(to run live calls, set: export $API_KEY_VAR=...)"
    record "live-api" "SKIP" "$API_KEY_VAR not set"
elif [[ "$SDK_NAME" == "stripe" ]]; then
    # Stripe: customer list --limit 1 is a safe read-only probe in test mode.
    info "running: $CLI_NAME customer list --limit 1 --json"
    export "$API_KEY_VAR=$API_KEY_VALUE"
    if "$CLI_EXEC" customer list --limit 1 --json > "$WORK_DIR/live.out" 2> "$WORK_DIR/live.err"; then
        ok "live call succeeded"
        head -20 "$WORK_DIR/live.out" | sed 's/^/    /'
        record "live-api" "PASS"
    else
        fail "live call failed (exit $?) — stderr:"
        cat "$WORK_DIR/live.err" | sed 's/^/    /'
        record "live-api" "FAIL" "see $WORK_DIR/live.err"
    fi
elif [[ "$SDK_NAME" == "PyGithub" ]]; then
    # PyGithub: `user get` with `login=octocat` is a public-profile read that
    # works with any token. Confirms:
    #   - Auth handler plumbs `--api-key` into `Github(login_or_token=...)`
    #   - Operation routes to a real SDK call (NOT a stub)
    #   - Result serializes through the json formatter
    # The `login` param flows through --json-input because PyGithub's
    # `Opt[str]` sentinel-Union type isn't yet resolved to a flat flag
    # (known limitation, see ADR-023 / Step 19+ candidate).
    info "running: $CLI_NAME --api-key=<redacted> --json user get --json-input '{\"login\": \"octocat\"}'"
    if "$CLI_EXEC" --api-key "$API_KEY_VALUE" --json user get --json-input '{"login": "octocat"}' \
            > "$WORK_DIR/live.out" 2> "$WORK_DIR/live.err"; then
        ok "live call succeeded"
        head -10 "$WORK_DIR/live.out" | sed 's/^/    /'
        record "live-api" "PASS"
    else
        fail "live call failed (exit $?) — stderr:"
        cat "$WORK_DIR/live.err" | sed 's/^/    /'
        record "live-api" "FAIL" "see $WORK_DIR/live.err"
    fi
else
    info "live API test is only wired for stripe and PyGithub — SDK=$SDK_NAME, skipping"
    record "live-api" "SKIP" "unsupported SDK for live test"
fi

# ---- Summary ---------------------------------------------------------------

heading "Summary"

echo ""
printf "  %-20s %-8s %s\n" "PHASE" "STATUS" "NOTE"
printf "  %-20s %-8s %s\n" "────────────────────" "────────" "──────────────────────────────"

FAILED=0
for phase in "${PHASE_ORDER[@]}"; do
    IFS='|' read -r status note <<< "${PHASE_STATUS[$phase]}"
    printf "  %-20s %-8s %s\n" "$phase" "$status" "$note"
    if [[ "$status" == "FAIL" ]]; then
        FAILED=$((FAILED + 1))
    fi
done

echo ""
echo "  Workspace: $WORK_DIR"
echo "  Generated CLI: $OUT_DIR"
echo "  Venv with installed CLI: $VENV"
echo ""
echo "  To poke around: source $VENV/bin/activate && $CLI_NAME --help"
echo ""

if [[ $FAILED -gt 0 ]]; then
    echo "  ✗ $FAILED phase(s) failed"
    exit 1
else
    echo "  ✓ all phases passed"
fi
