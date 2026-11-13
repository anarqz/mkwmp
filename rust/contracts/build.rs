// Compiles ../../contracts/*.proto into prost types + tonic services.
// Also the contract validator: a malformed .proto fails the build.
use std::{fs, path::PathBuf};

fn main() {
    // dunce::canonicalize avoids the \\?\ prefix that breaks protoc on Windows.
    let root = dunce::canonicalize(
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../contracts"),
    )
    .expect("contracts dir");
    // Self-contained: use the vendored protoc so no system install is needed.
    if std::env::var_os("PROTOC").is_none() {
        if let Ok(path) = protoc_bin_vendored::protoc_bin_path() {
            std::env::set_var("PROTOC", path);
        }
    }
    let mut protos = Vec::new();
    collect(&root, &mut protos);
    for p in &protos {
        println!("cargo:rerun-if-changed={}", p.display());
    }
    tonic_build::configure()
        .build_client(false)
        .compile_protos(&protos, &[root])
        .expect("compile protos");
}

fn collect(dir: &std::path::Path, out: &mut Vec<PathBuf>) {
    for entry in fs::read_dir(dir).unwrap().flatten() {
        let path = entry.path();
        if path.is_dir() {
            collect(&path, out);
        } else if path.extension().is_some_and(|e| e == "proto") {
            out.push(path);
        }
    }
}
