//! End-to-end: a real seat server, two gRPC clients, capacity 1. The second
//! client queues; when the first disconnects it is promoted and granted.

use std::time::Duration;

use contracts::mkwmp::seat::v1::seat_client::SeatClient;
use contracts::mkwmp::seat::v1::{JoinRequest, State};
use tokio_stream::StreamExt;
use tonic::transport::Server;

#[tokio::test]
async fn second_client_queues_then_is_promoted() {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let incoming = tokio_stream::wrappers::TcpListenerStream::new(listener);
    tokio::spawn(async move {
        Server::builder()
            .add_service(seat::service(1))
            .serve_with_incoming(incoming)
            .await
            .unwrap();
    });
    tokio::time::sleep(Duration::from_millis(100)).await;
    let url = format!("http://{addr}");

    let mut a = SeatClient::connect(url.clone()).await.unwrap();
    let mut a_updates =
        a.join_queue(JoinRequest { username: "ana".into() }).await.unwrap().into_inner();
    assert_eq!(a_updates.next().await.unwrap().unwrap().state, State::Granted as i32);

    let mut b = SeatClient::connect(url).await.unwrap();
    let mut b_updates =
        b.join_queue(JoinRequest { username: "bia".into() }).await.unwrap().into_inner();
    let q = b_updates.next().await.unwrap().unwrap();
    assert_eq!(q.state, State::Queued as i32);
    assert_eq!(q.position, 1);

    drop(a_updates); // ana disconnects -> seat frees
    let g = tokio::time::timeout(Duration::from_secs(2), b_updates.next())
        .await
        .expect("promotion within 2s")
        .unwrap()
        .unwrap();
    assert_eq!(g.state, State::Granted as i32);
    assert!(!g.ticket.is_empty());
}
