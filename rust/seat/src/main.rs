//! Seat server binary: load config, serve the Seat service.

use tonic::transport::Server;

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let path = std::env::args().nth(1).unwrap_or_else(|| "config/server.toml".into());
    let cfg = config::ServerConfig::load(&path).expect("load server config");
    let addr = cfg.seats.bind.parse()?;
    println!("seat server '{}': {} seats on {}", cfg.server.name, cfg.seats.capacity, addr);
    Server::builder()
        .add_service(seat::service(cfg.seats.capacity))
        .serve(addr)
        .await?;
    Ok(())
}
