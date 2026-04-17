//! Test-only helpers shared across crates in the workspace.
//!
//! Gated behind the `test-support` feature so the helpers don't ship in
//! normal builds. Downstream crates enable it in `[dev-dependencies]`:
//!
//! ```toml
//! [dev-dependencies]
//! cli-builder-core = { path = "../core", features = ["test-support"] }
//! ```

use std::path::PathBuf;

/// Repo root resolved from the **current test package's** `CARGO_MANIFEST_DIR`.
///
/// Cargo sets `CARGO_MANIFEST_DIR` to the crate whose tests are running, so
/// from any `crates/<crate>/` this resolves via `../..` to the repo root.
/// A `.git` sentinel check guards against path breakage.
pub fn workspace_root() -> PathBuf {
    let manifest_dir = std::env::var("CARGO_MANIFEST_DIR")
        .expect("CARGO_MANIFEST_DIR not set — helper must be called from a cargo test context");
    let root = PathBuf::from(&manifest_dir)
        .join("../..")
        .canonicalize()
        .unwrap_or_else(|e| panic!("Failed to canonicalize repo root from {manifest_dir}: {e}"));
    let git = root.join(".git");
    assert!(
        git.exists(),
        "Repo root resolution broke — expected .git at {}, but none was found \
         (CARGO_MANIFEST_DIR = {manifest_dir})",
        git.display()
    );
    root
}

/// Shared JSON metadata fixtures directory (language-agnostic).
pub fn fixtures_dir() -> PathBuf {
    workspace_root().join("tests/fixtures")
}
