//! TLS for the game's connections: the PEM pair the game patch trusts,
//! ALPN "h2" (a client offering no ALPN still gets HTTP/2, like the Python).

use std::path::Path;
use std::sync::Arc;

use rustls_pki_types::pem::PemObject;
use rustls_pki_types::{CertificateDer, PrivateKeyDer};
use tokio_rustls::TlsAcceptor;

pub fn acceptor(cert: &Path, key: &Path) -> std::io::Result<TlsAcceptor> {
    let bad = |what: &str, p: &Path, e: &dyn std::fmt::Display| {
        std::io::Error::new(std::io::ErrorKind::InvalidData, format!("{what} {}: {e}", p.display()))
    };
    let certs: Vec<CertificateDer<'static>> = CertificateDer::pem_file_iter(cert)
        .map_err(|e| bad("certificate", cert, &e))?
        .collect::<Result<_, _>>()
        .map_err(|e| bad("certificate", cert, &e))?;
    let key = PrivateKeyDer::from_pem_file(key).map_err(|e| bad("key", key, &e))?;
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let mut cfg = rustls::ServerConfig::builder_with_provider(provider)
        .with_protocol_versions(&[&rustls::version::TLS13, &rustls::version::TLS12])
        .map_err(std::io::Error::other)?
        .with_no_client_auth()
        .with_single_cert(certs, key)
        .map_err(std::io::Error::other)?;
    cfg.alpn_protocols = vec![b"h2".to_vec()];
    Ok(TlsAcceptor::from(Arc::new(cfg)))
}
