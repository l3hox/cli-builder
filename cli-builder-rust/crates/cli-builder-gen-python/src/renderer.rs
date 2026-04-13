//! Template rendering — loads Tera templates, builds context, writes output files.

use std::fs;
use std::path::Path;

use cli_builder_core::generator_model::GeneratorModel;
use tera::{Context, Tera};

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
fn escape_model(model: &mut GeneratorModel) {
    model.cli_description = tera_escape(&model.cli_description);
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

/// Generate a Python CLI project from a GeneratorModel.
pub fn generate(model: &GeneratorModel, output_dir: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let mut model = model.clone();
    escape_model(&mut model);

    let mut tera = Tera::default();
    tera.autoescape_on(vec![]); // Disable HTML auto-escaping

    // Register embedded templates
    tera.add_raw_template("pyproject.toml", include_str!("../templates/pyproject.toml.tera"))?;
    tera.add_raw_template("init.py", include_str!("../templates/init.py.tera"))?;
    tera.add_raw_template("main_module.py", include_str!("../templates/main_module.py.tera"))?;
    tera.add_raw_template("cli.py", include_str!("../templates/cli.py.tera"))?;
    tera.add_raw_template("resource.py", include_str!("../templates/resource.py.tera"))?;
    tera.add_raw_template("json_formatter.py", include_str!("../templates/json_formatter.py.tera"))?;
    tera.add_raw_template("table_formatter.py", include_str!("../templates/table_formatter.py.tera"))?;
    tera.add_raw_template("auth_handler.py", include_str!("../templates/auth_handler.py.tera"))?;

    let package_name = model.cli_name.replace('-', "_");

    // Build global context
    let mut context = Context::from_serialize(&model)?;
    context.insert("package_name", &package_name);
    context.insert("has_auth", &model.auth.is_some());

    // Create directory structure
    let src_dir = output_dir.join("src").join(&package_name);
    let commands_dir = src_dir.join("commands");
    let output_pkg_dir = src_dir.join("output");
    let auth_dir = src_dir.join("auth");
    fs::create_dir_all(&commands_dir)?;
    fs::create_dir_all(&output_pkg_dir)?;
    fs::create_dir_all(&auth_dir)?;

    // Render global files
    render_to(&tera, "pyproject.toml", &context, &output_dir.join("pyproject.toml"))?;
    render_to(&tera, "init.py", &context, &src_dir.join("__init__.py"))?;
    render_to(&tera, "main_module.py", &context, &src_dir.join("__main__.py"))?;
    render_to(&tera, "cli.py", &context, &src_dir.join("cli.py"))?;

    // Output formatters
    fs::write(output_pkg_dir.join("__init__.py"), "")?;
    render_to(&tera, "json_formatter.py", &context, &output_pkg_dir.join("json_formatter.py"))?;
    render_to(&tera, "table_formatter.py", &context, &output_pkg_dir.join("table_formatter.py"))?;

    // Auth handler
    if model.auth.is_some() {
        render_to(&tera, "init.py", &context, &auth_dir.join("__init__.py"))?;
        render_to(&tera, "auth_handler.py", &context, &auth_dir.join("handler.py"))?;
    }

    // Per-resource command files
    fs::write(commands_dir.join("__init__.py"), "")?;
    for resource in &model.resources {
        let mut res_ctx = context.clone();
        res_ctx.insert("resource", resource);
        let file_name = format!("{}.py", resource.name.replace('-', "_"));
        render_to(&tera, "resource.py", &res_ctx, &commands_dir.join(&file_name))?;
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
    fs::write(output_path, rendered)?;
    Ok(())
}
