//! Template rendering — loads Tera templates, registers custom filters, writes output files.

use std::collections::HashMap;
use std::fs;
use std::path::Path;

use cli_builder_core::identifier_validator::kebab_to_camel_case;
use tera::{Context, Tera, Value};

use crate::csharp_model::CSharpGeneratorModel;

/// Escape strings that may contain Tera template syntax.
/// Generator-side escaping per ADR-017 council decision.
pub(crate) fn tera_escape(value: &str) -> String {
    value
        .replace("{{", "{ {")
        .replace("}}", "} }")
        .replace("{%", "{ %")
        .replace("%}", "% }")
}

/// Escape user-provided strings in the model before template rendering.
fn escape_model(model: &mut CSharpGeneratorModel) {
    model.cli_description = tera_escape(&model.cli_description);
    model.static_auth_setup = model.static_auth_setup.as_ref().map(|s| tera_escape(s));
    if let Some(ref mut auth) = model.auth {
        auth.env_var = tera_escape(&auth.env_var);
        auth.parameter_name = tera_escape(&auth.parameter_name);
    }
    for resource in &mut model.resources {
        resource.description = resource.description.as_ref().map(|d| tera_escape(d));
        for op in &mut resource.operations {
            op.description = op.description.as_ref().map(|d| tera_escape(d));
            for p in &mut op.parameters {
                p.description = p.description.as_ref().map(|d| tera_escape(d));
            }
        }
    }
}

/// Generate a C# CLI project from a CSharpGeneratorModel.
pub fn generate(
    model: &CSharpGeneratorModel,
    output_dir: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut model = model.clone();
    escape_model(&mut model);

    let mut tera = Tera::default();
    tera.autoescape_on(vec![]); // Disable HTML auto-escaping

    // Register embedded templates
    tera.add_raw_template("csproj", include_str!("../templates/csproj.tera"))?;
    tera.add_raw_template("program", include_str!("../templates/program.tera"))?;
    tera.add_raw_template("resource_commands", include_str!("../templates/resource_commands.tera"))?;
    tera.add_raw_template("json_formatter", include_str!("../templates/json_formatter.tera"))?;
    tera.add_raw_template("table_formatter", include_str!("../templates/table_formatter.tera"))?;
    tera.add_raw_template("auth_handler", include_str!("../templates/auth_handler.tera"))?;

    // Register custom filters
    tera.register_filter("escape_csharp", escape_csharp_filter);
    tera.register_filter("to_var_name", to_var_name_filter);
    tera.register_filter("apply_conversion", apply_conversion_filter);

    let has_auth = model.auth.is_some();

    // Build global context
    let mut context = Context::from_serialize(&model)?;
    context.insert("has_auth", &has_auth);

    // Create project directory
    let project_dir = output_dir.join(&model.cli_name);
    fs::create_dir_all(&project_dir)?;

    // Render global files
    render_to(
        &tera,
        "csproj",
        &context,
        &project_dir.join(format!("{}.csproj", model.cli_name)),
    )?;
    render_to(&tera, "program", &context, &project_dir.join("Program.cs"))?;

    // Per-resource command files
    if !model.resources.is_empty() {
        let commands_dir = project_dir.join("Commands");
        fs::create_dir_all(&commands_dir)?;

        for resource in &model.resources {
            let mut res_ctx = Context::new();
            res_ctx.insert("root_namespace", &model.root_namespace);
            res_ctx.insert("resource", resource);
            res_ctx.insert("has_auth", &has_auth);
            res_ctx.insert("static_auth_setup", &model.static_auth_setup);

            let file_name = format!("{}Commands.cs", resource.class_name);
            render_to(&tera, "resource_commands", &res_ctx, &commands_dir.join(&file_name))?;
        }
    }

    // Output formatters
    let output_pkg_dir = project_dir.join("Output");
    fs::create_dir_all(&output_pkg_dir)?;

    let mut output_ctx = Context::new();
    output_ctx.insert("root_namespace", &model.root_namespace);
    render_to(
        &tera,
        "json_formatter",
        &output_ctx,
        &output_pkg_dir.join("JsonFormatter.cs"),
    )?;
    render_to(
        &tera,
        "table_formatter",
        &output_ctx,
        &output_pkg_dir.join("TableFormatter.cs"),
    )?;

    // Auth handler
    if let Some(ref auth) = model.auth {
        let auth_dir = project_dir.join("Auth");
        fs::create_dir_all(&auth_dir)?;

        let mut auth_ctx = Context::new();
        auth_ctx.insert("root_namespace", &model.root_namespace);
        auth_ctx.insert("cli_name", &model.cli_name);
        auth_ctx.insert("env_var", &auth.env_var);
        auth_ctx.insert("parameter_name", &auth.parameter_name);
        render_to(&tera, "auth_handler", &auth_ctx, &auth_dir.join("AuthHandler.cs"))?;
    }

    Ok(())
}

fn render_to(
    tera: &Tera,
    template: &str,
    context: &Context,
    output_path: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    let rendered = tera.render(template, context)?;
    // LF line endings, no trailing \r
    let content = rendered.replace("\r\n", "\n");
    fs::write(output_path, content)?;
    Ok(())
}

// ---- Custom Tera filters ----

/// `escape_csharp` — convert to C# verbatim string literal: `@"..."` with doubled quotes.
/// Null/empty → `null`.
fn escape_csharp_filter(
    value: &Value,
    _args: &HashMap<String, Value>,
) -> tera::Result<Value> {
    match value {
        Value::Null => Ok(Value::String("null".to_string())),
        Value::String(s) if s.is_empty() => Ok(Value::String("null".to_string())),
        Value::String(s) => {
            let escaped = s.replace('"', "\"\"");
            Ok(Value::String(format!("@\"{}\"", escaped)))
        }
        _ => Ok(Value::String("null".to_string())),
    }
}

/// `to_var_name` — kebab-case to camelCase: `credit-limit` → `creditLimit`.
fn to_var_name_filter(
    value: &Value,
    _args: &HashMap<String, Value>,
) -> tera::Result<Value> {
    let s = value.as_str().unwrap_or("");
    Ok(Value::String(kebab_to_camel_case(s)))
}

/// `apply_conversion` — substitute `{0}` in conversion expression with `{varName}Value`.
/// If `expr` arg is null/absent, returns `{varName}Value` (identity — no conversion).
fn apply_conversion_filter(
    value: &Value,
    args: &HashMap<String, Value>,
) -> tera::Result<Value> {
    let var_name = value.as_str().unwrap_or("_param");
    let value_expr = format!("{}Value", var_name);

    let expr = args.get("expr").and_then(|v| v.as_str());
    match expr {
        Some(e) if !e.is_empty() => Ok(Value::String(e.replace("{0}", &value_expr))),
        _ => Ok(Value::String(value_expr)),
    }
}
