//! Diagnostics formatter — colored stderr output grouped by severity.

use cli_builder_core::models::{Diagnostic, DiagnosticSeverity};

/// Print diagnostics to stderr with optional ANSI color.
pub fn print_diagnostics(diagnostics: &[Diagnostic]) {
    if diagnostics.is_empty() {
        return;
    }

    let use_color = atty_stderr();

    for d in diagnostics {
        let severity_str = match d.severity {
            DiagnosticSeverity::Error => {
                if use_color {
                    "\x1b[31m[ERROR]\x1b[0m  "
                } else {
                    "[ERROR]   "
                }
            }
            DiagnosticSeverity::Warning => {
                if use_color {
                    "\x1b[33m[WARNING]\x1b[0m"
                } else {
                    "[WARNING] "
                }
            }
            DiagnosticSeverity::Info => {
                if use_color {
                    "\x1b[90m[INFO]\x1b[0m   "
                } else {
                    "[INFO]    "
                }
            }
        };
        eprintln!("{} {}: {}", severity_str, d.code, d.message);
    }
}

/// Check if stderr is a TTY (for color support).
fn atty_stderr() -> bool {
    // Simple heuristic: check if TERM is set and NO_COLOR is not set
    std::env::var("NO_COLOR").is_err() && std::env::var("TERM").is_ok()
}
