//! Seat server library: the seat manager and the internal Seat gRPC service.
//! The binary (`main.rs`) is a thin shell that loads config and serves this.

pub mod seats;

use std::pin::Pin;
use std::sync::Arc;

use contracts::mkwmp::seat::v1::seat_server::{Seat, SeatServer};
use contracts::mkwmp::seat::v1::{JoinRequest, QueueUpdate};
use seats::SeatManager;
use tokio_stream::wrappers::UnboundedReceiverStream;
use tokio_stream::{Stream, StreamExt};
use tonic::{Request, Response, Status};

pub struct SeatService {
    manager: Arc<SeatManager>,
}

impl SeatService {
    pub fn new(capacity: usize) -> Self {
        Self { manager: SeatManager::new(capacity) }
    }
}

type UpdateStream = Pin<Box<dyn Stream<Item = Result<QueueUpdate, Status>> + Send>>;

#[tonic::async_trait]
impl Seat for SeatService {
    type JoinQueueStream = UpdateStream;

    async fn join_queue(
        &self,
        request: Request<JoinRequest>,
    ) -> Result<Response<Self::JoinQueueStream>, Status> {
        let username = request.into_inner().username;
        if username.trim().is_empty() {
            return Err(Status::invalid_argument("username must not be empty"));
        }
        let (rx, membership) = self.manager.join(&username);
        // The membership rides in the stream closure, so it drops — releasing
        // the seat — exactly when the client disconnects.
        let stream = UnboundedReceiverStream::new(rx).map(move |u| {
            let _hold = &membership;
            Ok(u)
        });
        Ok(Response::new(Box::pin(stream)))
    }
}

/// Build the tonic service for a server with `capacity` seats.
pub fn service(capacity: usize) -> SeatServer<SeatService> {
    SeatServer::new(SeatService::new(capacity))
}
