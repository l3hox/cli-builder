//! Tests for the Python CLI generator.

use std::path::PathBuf;

use cli_builder_core::generator_model::{FlatParameter, LanguageProfile};
use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::*;
use cli_builder_core::test_support;

use crate::python_keywords;
use crate::python_mapper::PythonProfile;
use crate::renderer;

fn testsdk_fixture_path() -> PathBuf {
    test_support::fixtures_dir().join("testsdk-metadata.json")
}

fn generate_testsdk(output_dir: &std::path::Path) {
    let json = std::fs::read_to_string(testsdk_fixture_path()).unwrap();
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();
    let profile = PythonProfile;
    let (model, _) = model_mapper::build(
        &envelope.metadata,
        &MapperOptions {
            cli_name: Some("testsdk-cli".into()),
        },
        &profile,
    );
    renderer::generate(&model, output_dir).unwrap();
}

// ================================================================
// Unit tests: tera_escape
// ================================================================

#[test]
fn tera_escape_breaks_double_braces() {
    assert_eq!(renderer::tera_escape("{{ var }}"), "{ { var } }");
}

#[test]
fn tera_escape_breaks_block_tags() {
    assert_eq!(renderer::tera_escape("{% if x %}"), "{ % if x % }");
}

#[test]
fn tera_escape_preserves_normal_text() {
    assert_eq!(renderer::tera_escape("hello world"), "hello world");
}

#[test]
fn tera_escape_handles_mixed_content() {
    let input = "Use {{ env 'SECRET' }} and {% raw %} here";
    let escaped = renderer::tera_escape(input);
    assert!(!escaped.contains("{{"));
    assert!(!escaped.contains("{%"));
}

// ================================================================
// Unit tests: PythonProfile type mapping
// ================================================================

fn tr(kind: TypeKind, name: &str) -> TypeRef {
    TypeRef {
        kind,
        name: name.to_string(),
        is_nullable: false,
        is_abstract: false,
        is_extensible_enum: false,
        generic_arguments: None,
        enum_values: None,
        properties: None,
        element_type: None,
        module: None,
    }
}

#[test]
fn python_map_primitive_types() {
    let p = PythonProfile;
    assert_eq!(p.map_primitive_type("str"), "str");
    assert_eq!(p.map_primitive_type("string"), "str");
    assert_eq!(p.map_primitive_type("String"), "str");
    assert_eq!(p.map_primitive_type("int"), "int");
    assert_eq!(p.map_primitive_type("Int32"), "int");
    assert_eq!(p.map_primitive_type("long"), "int");
    assert_eq!(p.map_primitive_type("float"), "float");
    assert_eq!(p.map_primitive_type("double"), "float");
    assert_eq!(p.map_primitive_type("Double"), "float");
    assert_eq!(p.map_primitive_type("decimal"), "float");
    assert_eq!(p.map_primitive_type("bool"), "bool");
    assert_eq!(p.map_primitive_type("Boolean"), "bool");
    assert_eq!(p.map_primitive_type("void"), "None");
    assert_eq!(p.map_primitive_type("TimeSpan"), "str");
    assert_eq!(p.map_primitive_type("UnknownType"), "str"); // catch-all
}

#[test]
fn python_map_cli_type_for_cli_param() {
    let p = PythonProfile;
    assert_eq!(p.map_cli_type(&tr(TypeKind::Primitive, "int"), true), "int");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Enum, "Status"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Class, "Options"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Array, "int[]"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Dictionary, "Dict"), true), "str");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Generic, "List"), true), "str");
}

#[test]
fn python_map_cli_type_for_return() {
    let p = PythonProfile;
    assert_eq!(p.map_cli_type(&tr(TypeKind::Class, "Customer"), false), "Customer");
    assert_eq!(p.map_cli_type(&tr(TypeKind::Generic, "List"), false), "List");
}

#[test]
fn python_is_binary_type() {
    let p = PythonProfile;
    assert!(p.is_binary_type("bytes"));
    assert!(p.is_binary_type("BinaryContent")); // .NET compat
    assert!(p.is_binary_type("Stream"));
    assert!(!p.is_binary_type("Customer"));
}

#[test]
fn python_is_unwirable_return_type() {
    let p = PythonProfile;
    assert!(p.is_unwirable_return_type("AsyncIterator"));
    assert!(p.is_unwirable_return_type("ChatClient"));
    assert!(p.is_unwirable_return_type("T")); // single-char generic
    assert!(!p.is_unwirable_return_type("Customer"));
}

// ================================================================
// Unit tests: Python keywords
// ================================================================

#[test]
fn python_keywords_detected() {
    assert!(python_keywords::is_keyword("class"));
    assert!(python_keywords::is_keyword("def"));
    assert!(python_keywords::is_keyword("return"));
    assert!(python_keywords::is_keyword("yield"));
    // Builtins
    assert!(python_keywords::is_keyword("id"));
    assert!(python_keywords::is_keyword("list"));
    assert!(python_keywords::is_keyword("type"));
    assert!(python_keywords::is_keyword("input"));
    // Not keywords
    assert!(!python_keywords::is_keyword("customer"));
    assert!(!python_keywords::is_keyword("foobar"));
}

#[test]
fn python_boilerplate_names_detected() {
    assert!(python_keywords::is_boilerplate_name("json"));
    assert!(python_keywords::is_boilerplate_name("click"));
    assert!(python_keywords::is_boilerplate_name("ctx"));
    assert!(!python_keywords::is_boilerplate_name("customer"));
}

// ================================================================
// Structural assertions (Phase 3 — no golden files)
// ================================================================

#[test]
fn generates_expected_file_count() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let py_files: Vec<_> = walkdir::WalkDir::new(dir.path())
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.path().extension().map_or(false, |ext| ext == "py"))
        .collect();

    // Exact expected count:
    // __init__.py + __main__.py + cli.py = 3
    // commands/__init__.py + 7 resource files = 8
    // output/__init__.py + json_formatter.py + table_formatter.py = 3
    // auth/__init__.py + handler.py = 2
    // Total = 16
    assert_eq!(
        py_files.len(),
        16,
        "Expected exactly 16 .py files, got {}",
        py_files.len()
    );
}

#[test]
fn generates_pyproject_toml() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let toml = std::fs::read_to_string(dir.path().join("pyproject.toml")).unwrap();
    assert!(toml.contains("testsdk-cli"));
    assert!(toml.contains("click>=8.0"));
    assert!(toml.contains("[project.scripts]"));
}

#[test]
fn cli_py_has_click_patterns() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let cli_py = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/cli.py"),
    )
    .unwrap();
    assert!(cli_py.contains("import click"));
    assert!(cli_py.contains("@click.group()"));
    assert!(cli_py.contains("def cli("));
    assert!(cli_py.contains("def main():"));
    assert!(cli_py.contains("cli.add_command(customer)"));
    assert!(cli_py.contains("--api-key"));
}

#[test]
fn resource_files_have_click_commands() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let customer_py = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/commands/customer.py"),
    )
    .unwrap();
    assert!(customer_py.contains("import click"));
    assert!(customer_py.contains("@click.group()"));
    assert!(customer_py.contains("def customer("));
    assert!(customer_py.contains(".command(name="));
    assert!(customer_py.contains("def get("));
    assert!(customer_py.contains("def create("));
    assert!(customer_py.contains("_get_client"));
}

#[test]
fn auth_handler_references_env_var() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let handler = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/auth/handler.py"),
    )
    .unwrap();
    assert!(handler.contains("def resolve_credential("));
    assert!(handler.contains("os.environ.get("));
    assert!(handler.contains("TESTSDK_APIKEY"));
}

#[test]
fn all_generated_python_files_pass_ast_parse() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    // Collect all .py files
    let py_files: Vec<_> = walkdir::WalkDir::new(dir.path())
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.path().extension().map_or(false, |ext| ext == "py"))
        .map(|e| e.path().to_path_buf())
        .collect();

    assert!(!py_files.is_empty(), "No .py files found");

    // Run ast.parse on each file — pass path via sys.argv to avoid
    // backslash-as-unicode-escape issues on Windows temp paths.
    for path in &py_files {
        let python = if cfg!(windows) { "python" } else { "python3" };
        let output = std::process::Command::new(python)
            .args([
                "-c",
                "import ast, sys; ast.parse(open(sys.argv[1]).read())",
                &path.to_string_lossy(),
            ])
            .output()
            .expect("Failed to run python");

        assert!(
            output.status.success(),
            "ast.parse failed for {}: {}",
            path.display(),
            String::from_utf8_lossy(&output.stderr)
        );
    }
}

#[test]
fn keyword_param_id_gets_renamed_flag() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let customer_py = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/commands/customer.py"),
    )
    .unwrap();

    // "id" is a Python builtin — should be renamed to --id-value
    assert!(customer_py.contains("--id-value"));
}

// ================================================================
// Council fix: adversarial env_var escaping
// ================================================================

#[test]
fn adversarial_env_var_is_escaped_in_output() {
    use cli_builder_core::generator_model::*;

    // Build a minimal model with a malicious env_var
    let model = GeneratorModel {
        cli_name: "test-cli".into(),
        sdk_name: "TestSdk".into(),
        sdk_version: "1.0.0".into(),
        sdk_package_name: "testsdk".into(),
        root_namespace: "TestCli".into(),
        cli_description: "test".into(),
        resources: vec![],
        auth: Some(AuthModel {
            auth_type: "ApiKey".into(),
            env_var: "FOO{{ malicious }}BAR".into(),
            parameter_name: "apiKey".into(),
        }),
        static_auth_setup: None,
    };

    let dir = tempfile::tempdir().unwrap();
    renderer::generate(&model, dir.path()).unwrap();

    let handler = std::fs::read_to_string(
        dir.path().join("src/test_cli/auth/handler.py"),
    )
    .unwrap();

    // The {{ }} must be broken by tera_escape — should NOT appear verbatim
    assert!(!handler.contains("{{"), "Unescaped {{ in auth handler");
    assert!(!handler.contains("}}"), "Unescaped }} in auth handler");
    assert!(handler.contains("FOO"), "Env var content missing");

    let cli_py = std::fs::read_to_string(
        dir.path().join("src/test_cli/cli.py"),
    )
    .unwrap();
    assert!(!cli_py.contains("{{"), "Unescaped {{ in cli.py");
}

// ================================================================
// Council fix: echo-stub path for unwirable operations
// ================================================================

#[test]
fn unwirable_operation_generates_echo_stub() {
    use cli_builder_core::generator_model::*;

    // Synthetic model with one unwirable operation (can_wire_sdk_call = false)
    let model = GeneratorModel {
        cli_name: "stub-cli".into(),
        sdk_name: "StubSdk".into(),
        sdk_version: "1.0.0".into(),
        sdk_package_name: "stubsdk".into(),
        root_namespace: "StubCli".into(),
        cli_description: "test".into(),
        resources: vec![ResourceModel {
            name: "thing".into(),
            class_name: "Thing".into(),
            description: None,
            operations: vec![OperationModel {
                name: "upload".into(),
                method_name: "Upload".into(),
                description: Some("Upload binary data".into()),
                parameters: vec![],
                needs_json_input: false,
                return_type_name: "None".into(),
                is_streaming: false,
                source_method_name: None,
                options_type_name: None,
                method_params: vec![],
                can_wire_sdk_call: false, // echo stub
                has_json_direct_params: false,
                requires_sentinel_nullability: false,
            }],
            source_class_name: Some("ThingService".into()),
            source_module: Some("sdk.services".into()),
            can_construct: true,
            constructor_auth: None,
            constructor_config_params: vec![],
            required_modules: vec![],
        }],
        auth: None,
        static_auth_setup: None,
    };

    let dir = tempfile::tempdir().unwrap();
    renderer::generate(&model, dir.path()).unwrap();

    let thing_py = std::fs::read_to_string(
        dir.path().join("src/stub_cli/commands/thing.py"),
    )
    .unwrap();
    assert!(thing_py.contains("not yet wired to SDK"));
    assert!(thing_py.contains("@click.pass_context"));
    assert!(thing_py.contains(".command(name=\"upload\")"));

    // Must still be valid Python
    let python = if cfg!(windows) { "python" } else { "python3" };
    let stub_path = dir.path().join("src/stub_cli/commands/thing.py");
    let output = std::process::Command::new(python)
        .args([
            "-c",
            "import ast, sys; ast.parse(open(sys.argv[1]).read())",
            &stub_path.to_string_lossy(),
        ])
        .output()
        .expect("Failed to run python");
    assert!(output.status.success(), "Echo stub file failed ast.parse");
}

// ================================================================
// Phase 5: Golden file snapshots (insta)
// ================================================================

#[test]
fn golden_pyproject_toml() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());
    let content = std::fs::read_to_string(dir.path().join("pyproject.toml")).unwrap();
    insta::assert_snapshot!("pyproject_toml", content);
}

#[test]
fn golden_cli_py() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());
    let content = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/cli.py"),
    )
    .unwrap();
    insta::assert_snapshot!("cli_py", content);
}

#[test]
fn golden_customer_py() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());
    let content = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/commands/customer.py"),
    )
    .unwrap();
    insta::assert_snapshot!("customer_py", content);
}

#[test]
fn golden_auth_handler_py() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());
    let content = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/auth/handler.py"),
    )
    .unwrap();
    insta::assert_snapshot!("auth_handler_py", content);
}

#[test]
fn golden_json_formatter_py() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());
    let content = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/output/json_formatter.py"),
    )
    .unwrap();
    insta::assert_snapshot!("json_formatter_py", content);
}

// ================================================================
// Council fix: output/__init__.py is empty (not json_formatter copy)
// ================================================================

#[test]
fn output_init_py_is_empty() {
    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());

    let init = std::fs::read_to_string(
        dir.path().join("src/testsdk_cli/output/__init__.py"),
    )
    .unwrap();
    assert!(init.is_empty(), "output/__init__.py should be empty, got: {}", init);
}

// ================================================================
// Step 13b: optional-bool kwargs overwrite fix + coverage
// ================================================================

mod step_13b_bool_fix {
    use super::*;
    use cli_builder_core::generator_model::{
        GeneratorModel, OperationModel, ResourceModel,
    };

    /// Golden snapshot for the order resource — locks IsPriority and GiftWrap
    /// (both optional bools) against regression of the click-tri-state fix.
    #[test]
    fn golden_order_py() {
        let dir = tempfile::tempdir().unwrap();
        generate_testsdk(dir.path());
        let content = std::fs::read_to_string(
            dir.path().join("src/testsdk_cli/commands/order.py"),
        )
        .unwrap();
        insta::assert_snapshot!("order_py", content);
    }

    /// Class-level regression gate: no SDK-parameter rendering under
    /// `commands/` may contain an `is_flag=True` without a co-located
    /// `required=True`. Required bools are allowed; global CLI flags
    /// (e.g. `--json` in `cli.py`) live outside `commands/` and are excluded.
    ///
    /// Uses a count-based check (total `is_flag=True` occurrences vs.
    /// occurrences on lines with `required=True`) so a future formatter
    /// that splits the `@click.option(...)` call across multiple lines
    /// will still trip the assertion.
    #[test]
    fn no_generated_sdk_param_has_unguarded_is_flag() {
        let dir = tempfile::tempdir().unwrap();
        generate_testsdk(dir.path());

        let py_files: Vec<_> =
            walkdir::WalkDir::new(dir.path().join("src/testsdk_cli/commands"))
                .into_iter()
                .filter_map(|e| e.ok())
                .filter(|e| e.path().extension().map_or(false, |ext| ext == "py"))
                .map(|e| e.path().to_path_buf())
                .collect();

        assert!(!py_files.is_empty(), "No command .py files found under commands/");

        for path in &py_files {
            let content = std::fs::read_to_string(path).unwrap();
            let total = content.matches("is_flag=True").count();
            let guarded = content
                .lines()
                .filter(|l| l.contains("is_flag=True") && l.contains("required=True"))
                .count();
            assert_eq!(
                total, guarded,
                "{} has {} occurrences of `is_flag=True` but only {} on lines \
                 with `required=True` — optional bools must render as \
                 `type=click.BOOL, default=None`, not `is_flag=True, default=False`",
                path.display(),
                total,
                guarded
            );

            // Same-line contradictory pair: is_flag=True + type=click.BOOL
            // cannot both appear on a single `@click.option(...)` call.
            for (lineno, line) in content.lines().enumerate() {
                if line.contains("is_flag=True") && line.contains("type=click.BOOL") {
                    panic!(
                        "contradictory `is_flag=True` + `type=click.BOOL` at {}:{}:\n  {}",
                        path.display(),
                        lineno + 1,
                        line
                    );
                }
            }
        }
    }

    /// Synthetic model proof: an optional bool parameter renders as the
    /// tri-state `type=click.BOOL, default=None`, AND the kwargs block
    /// uses the `is not None` guard so the param is skipped when absent.
    #[test]
    fn optional_bool_renders_as_click_bool_tristate() {
        let content = render_single_param_resource(optional_bool("enabled", "Enabled"));

        assert!(
            content.contains(r#"@click.option("--enabled", type=click.BOOL, default=None)"#),
            "optional bool should render as tri-state; got:\n{}",
            content
        );
        assert!(
            !content.contains("is_flag=True"),
            "optional bool must not use is_flag=True; got:\n{}",
            content
        );
        // Kwargs guard: absent value must NOT be written to kwargs.
        assert!(
            content.contains("if enabled is not None:"),
            "kwargs guard missing — absent optional bool would clobber SDK default; got:\n{}",
            content
        );
        assert!(
            content.contains(r#"kwargs["Enabled"] = enabled"#),
            "kwargs assignment missing inside guard; got:\n{}",
            content
        );
    }

    /// Synthetic model proof: a required bool keeps `is_flag=True, default=False`
    /// with `required=True`, and MUST NOT also emit `type=click.BOOL` (the two
    /// forms are contradictory).
    #[test]
    fn required_bool_renders_as_is_flag_true() {
        let content = render_single_param_resource(FlatParameter {
            cli_flag: "dry-run".into(),
            property_name: "DryRun".into(),
            cli_type: "bool".into(),
            is_required: true,
            default_value: None,
            description: None,
            enum_values: None,
            sdk_type_name: None,
            sdk_type_kind: None,
            sdk_type_is_nullable: false,
            sdk_type_is_extensible_enum: false,
            source_options_class_name: None,
        });

        assert!(
            content.contains(
                r#"@click.option("--dry-run", required=True, is_flag=True, default=False)"#
            ),
            "required bool should render with is_flag=True; got:\n{}",
            content
        );
        assert!(
            !content.contains("type=click.BOOL"),
            "required bool must not also emit type=click.BOOL; got:\n{}",
            content
        );
    }

    /// Synthetic model proof: optional float renders with `type=float`.
    /// Float branch of the template is uncovered by any fixture snapshot.
    #[test]
    fn optional_float_renders_as_type_float() {
        let content = render_single_param_resource(FlatParameter {
            cli_flag: "ratio".into(),
            property_name: "Ratio".into(),
            cli_type: "float".into(),
            is_required: false,
            default_value: None,
            description: None,
            enum_values: None,
            sdk_type_name: None,
            sdk_type_kind: None,
            sdk_type_is_nullable: false,
            sdk_type_is_extensible_enum: false,
            source_options_class_name: None,
        });

        assert!(
            content.contains(r#"@click.option("--ratio", type=float)"#),
            "optional float should render with type=float; got:\n{}",
            content
        );
    }

    /// A param with `enum_values` AND a `cli_type` that also emits `type=...`
    /// (e.g. bool) must not produce two `type=` arguments on the same
    /// `@click.option(...)` call — that would be a Python TypeError at import.
    /// The enum branch takes precedence over the cli_type branch.
    #[test]
    fn bool_with_enum_values_does_not_emit_double_type_arg() {
        let content = render_single_param_resource(FlatParameter {
            cli_flag: "mode".into(),
            property_name: "Mode".into(),
            cli_type: "bool".into(),
            is_required: false,
            default_value: None,
            description: None,
            enum_values: Some(vec!["On".into(), "Off".into()]),
            sdk_type_name: None,
            sdk_type_kind: None,
            sdk_type_is_nullable: false,
            sdk_type_is_extensible_enum: false,
            source_options_class_name: None,
        });

        // Find the decorator line and assert it has exactly one `type=` arg.
        let decorator_line = content
            .lines()
            .find(|l| l.contains("--mode"))
            .expect("decorator line for --mode missing");
        let type_count = decorator_line.matches("type=").count();
        assert_eq!(
            type_count, 1,
            "bool + enum_values emitted {} `type=` args (should be exactly 1): {}",
            type_count, decorator_line
        );
        assert!(
            decorator_line.contains("type=click.Choice"),
            "enum_values should win, emitting type=click.Choice; got: {}",
            decorator_line
        );
        assert!(
            !decorator_line.contains("type=click.BOOL"),
            "bool cli_type must not leak through when enum_values is set; got: {}",
            decorator_line
        );
    }

    /// Runtime anchor: spawn `python -m testsdk_cli --help` against the generated
    /// CLI and snapshot the stdout. Catches click semantic drift (8→9) that pure
    /// string scans can miss. Uses PYTHONPATH — no pip install / venv needed.
    ///
    /// Skips gracefully when the ambient python interpreter is missing or has
    /// no click installed. PR3 (Step 13b) will add `setup-python` + click to
    /// the Rust CI job; until then this test is a no-op on clean runners.
    #[test]
    fn help_output_snapshot() {
        let python = if cfg!(windows) { "python" } else { "python3" };

        // Skip gracefully if python or click is unavailable — no hard panic.
        match std::process::Command::new(python)
            .args(["-c", "import click"])
            .output()
        {
            Err(_) => {
                eprintln!("help_output_snapshot: `{}` not in PATH — skipping", python);
                return;
            }
            Ok(out) if !out.status.success() => {
                eprintln!(
                    "help_output_snapshot: `{}` has no `click` module — skipping",
                    python
                );
                return;
            }
            _ => {}
        }

        let dir = tempfile::tempdir().unwrap();
        generate_testsdk(dir.path());
        let src_dir = dir.path().join("src");
        // PYTHONIOENCODING=utf-8 forces Python stdout/stderr to UTF-8 on all
        // platforms. Without it, Windows Python uses the console code page
        // (cp1252) and non-ASCII characters in the CLI description (e.g. the
        // em dash in `model_mapper.rs`) come back as mojibake.
        let output = std::process::Command::new(python)
            .env("PYTHONPATH", &src_dir)
            .env("PYTHONIOENCODING", "utf-8")
            .args(["-m", "testsdk_cli", "--help"])
            .output()
            .expect("Failed to invoke python despite probe success");

        assert!(
            output.status.success(),
            "python -m testsdk_cli --help failed (exit {:?}):\nstderr: {}",
            output.status.code(),
            String::from_utf8_lossy(&output.stderr)
        );

        let stdout = String::from_utf8_lossy(&output.stdout).to_string();
        insta::assert_snapshot!("help_output", stdout);
    }

    /// Regression anchor for the template refactor (PR2 + PR2b). Exercises
    /// every rendering branch in a single operation, in this order:
    ///
    ///   1. required str                     → required_clause set, type/help empty
    ///   2. optional int + description       → type_clause set, help_clause set
    ///   3. optional float                   → type_clause set
    ///   4. required bool                    → compound: required + is_flag
    ///   5. optional bool                    → type=click.BOOL, default=None
    ///   6. optional enum + description      → enum via join + help_clause
    ///
    /// Snapshotting the entire rendered file closes four gaps at once:
    ///   - help_clause branch (no fixture param has a description)
    ///   - required-str + optional-int transition (iteration-leakage gate)
    ///   - required-bool + optional-bool transition (compound-branch reset)
    ///   - enum combined with description (unwitnessed pairing)
    ///
    /// If a future refactor claims "byte-for-byte identical output", this
    /// snapshot is the proof.
    #[test]
    fn comprehensive_param_shapes_render_snapshot() {
        let params = vec![
            FlatParameter {
                cli_flag: "name".into(),
                property_name: "Name".into(),
                cli_type: "str".into(),
                is_required: true,
                default_value: None,
                description: None,
                enum_values: None,
                sdk_type_name: None,
                sdk_type_kind: None,
                sdk_type_is_nullable: false,
                sdk_type_is_extensible_enum: false,
                source_options_class_name: None,
            },
            FlatParameter {
                cli_flag: "limit".into(),
                property_name: "Limit".into(),
                cli_type: "int".into(),
                is_required: false,
                default_value: None,
                description: Some("Maximum number of items to return".into()),
                enum_values: None,
                sdk_type_name: None,
                sdk_type_kind: None,
                sdk_type_is_nullable: false,
                sdk_type_is_extensible_enum: false,
                source_options_class_name: None,
            },
            FlatParameter {
                cli_flag: "ratio".into(),
                property_name: "Ratio".into(),
                cli_type: "float".into(),
                is_required: false,
                default_value: None,
                description: None,
                enum_values: None,
                sdk_type_name: None,
                sdk_type_kind: None,
                sdk_type_is_nullable: false,
                sdk_type_is_extensible_enum: false,
                source_options_class_name: None,
            },
            FlatParameter {
                cli_flag: "dry-run".into(),
                property_name: "DryRun".into(),
                cli_type: "bool".into(),
                is_required: true,
                default_value: None,
                description: None,
                enum_values: None,
                sdk_type_name: None,
                sdk_type_kind: None,
                sdk_type_is_nullable: false,
                sdk_type_is_extensible_enum: false,
                source_options_class_name: None,
            },
            FlatParameter {
                cli_flag: "verbose".into(),
                property_name: "Verbose".into(),
                cli_type: "bool".into(),
                is_required: false,
                default_value: None,
                description: None,
                enum_values: None,
                sdk_type_name: None,
                sdk_type_kind: None,
                sdk_type_is_nullable: false,
                sdk_type_is_extensible_enum: false,
                source_options_class_name: None,
            },
            FlatParameter {
                cli_flag: "mode".into(),
                property_name: "Mode".into(),
                cli_type: "str".into(),
                is_required: false,
                default_value: None,
                description: Some("Execution mode".into()),
                enum_values: Some(vec!["Fast".into(), "Safe".into()]),
                sdk_type_name: None,
                sdk_type_kind: None,
                sdk_type_is_nullable: false,
                sdk_type_is_extensible_enum: false,
                source_options_class_name: None,
            },
        ];

        let content = render_resource_with_params(params);
        insta::assert_snapshot!("comprehensive_params", content);
    }

    /// Builder: optional bool parameter.
    fn optional_bool(cli_flag: &str, property_name: &str) -> FlatParameter {
        FlatParameter {
            cli_flag: cli_flag.into(),
            property_name: property_name.into(),
            cli_type: "bool".into(),
            is_required: false,
            default_value: None,
            description: None,
            enum_values: None,
            sdk_type_name: None,
            sdk_type_kind: None,
            sdk_type_is_nullable: false,
            sdk_type_is_extensible_enum: false,
            source_options_class_name: None,
        }
    }

    /// Helper: build a minimal GeneratorModel with one resource + one operation
    /// carrying a single parameter. Convenience wrapper around
    /// [`render_resource_with_params`].
    fn render_single_param_resource(param: FlatParameter) -> String {
        render_resource_with_params(vec![param])
    }

    /// Helper: build a minimal GeneratorModel with one resource + one operation
    /// carrying the given parameters, render it, and return the generated
    /// `commands/<resource>.py` content for assertion.
    ///
    /// Hardcoded context: `can_construct: true`, `can_wire_sdk_call: true`,
    /// no auth. Use the fixture-based golden snapshots for broader context
    /// coverage.
    fn render_resource_with_params(params: Vec<FlatParameter>) -> String {
        let model = GeneratorModel {
            cli_name: "probe-cli".into(),
            sdk_name: "ProbeSdk".into(),
            sdk_version: "1.0.0".into(),
            sdk_package_name: "probesdk".into(),
            root_namespace: "ProbeCli".into(),
            cli_description: "test".into(),
            resources: vec![ResourceModel {
                name: "probe".into(),
                class_name: "Probe".into(),
                description: None,
                operations: vec![OperationModel {
                    name: "run".into(),
                    method_name: "Run".into(),
                    description: None,
                    parameters: params,
                    needs_json_input: false,
                    return_type_name: "None".into(),
                    is_streaming: false,
                    source_method_name: Some("Run".into()),
                    options_type_name: None,
                    method_params: vec![],
                    can_wire_sdk_call: true,
                    has_json_direct_params: false,
                    requires_sentinel_nullability: false,
                }],
                source_class_name: Some("ProbeService".into()),
                source_module: Some("probesdk.services".into()),
                can_construct: true,
                constructor_auth: None,
                constructor_config_params: vec![],
                required_modules: vec![],
            }],
            auth: None,
            static_auth_setup: None,
        };

        let dir = tempfile::tempdir().unwrap();
        renderer::generate(&model, dir.path()).unwrap();
        std::fs::read_to_string(dir.path().join("src/probe_cli/commands/probe.py")).unwrap()
    }
}
