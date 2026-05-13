#!/usr/bin/env bash
# Manual E2E test for the Python CLI pipeline against a real SDK.
#
# Does what the placeholder test at crates/gen-python/tests/e2e.rs can't:
#   adapter → generator → venv → pip install → console-script → live API call
#
# Usage:
#   scripts/manual-test-python-sdk.sh                   # stripe, skip live calls
#   SDK_NAME=stripe scripts/manual-test-python-sdk.sh   # same
#   STRIPE_API_KEY=sk_test_... scripts/manual-test-python-sdk.sh   # includes live calls
#
# Environment:
#   SDK_NAME         Package to test against (default: stripe)
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
SDK_VERSION="${SDK_VERSION:-}"
CLI_NAME="${CLI_NAME:-${SDK_NAME}-cli}"
WORK_DIR="${WORK_DIR:-/tmp/cli-builder-manual-test}"
SKIP_LIVE="${SKIP_LIVE:-0}"

# Infer the API key env var from SDK name if not set explicitly.
if [[ -z "${API_KEY_VAR:-}" ]]; then
    case "$SDK_NAME" in
        stripe)  API_KEY_VAR="STRIPE_API_KEY" ;;
        openai)  API_KEY_VAR="OPENAI_API_KEY" ;;
        *)       API_KEY_VAR="$(echo "$SDK_NAME" | tr '[:lower:]' '[:upper:]')_API_KEY" ;;
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

heading "Phase 3 — inspect: extract metadata from $SDK_NAME"

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
info "running: cli-builder inspect --adapter python --package $SDK_NAME --json"
export CLI_BUILDER_PYTHON_ADAPTER="$SETUP_PY"
if "$CLI_BIN" inspect --adapter python --package "$SDK_NAME" --json > "$METADATA_JSON" 2> "$WORK_DIR/inspect.stderr"; then
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

info "running: cli-builder generate --adapter python --package $SDK_NAME --generator python --output $OUT_DIR"
if "$CLI_BIN" generate --adapter python --package "$SDK_NAME" --generator python --output "$OUT_DIR" --cli-name "$CLI_NAME" 2> "$WORK_DIR/generate.stderr"; then
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
elif [[ "$SDK_NAME" != "stripe" ]]; then
    info "live API test is only wired for stripe — SDK=$SDK_NAME, skipping"
    record "live-api" "SKIP" "unsupported SDK for live test"
else
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
