//! TCP → (TLS) → HTTP/2 → gRPC calls into a [`Backend`].
//!
//! What the game relies on (verified against server/mkwmp_server.py):
//! - Response HEADERS go out as soon as the request HEADERS arrive, before
//!   any DATA: subscriptions stay open for minutes with no data.
//! - No idle timeouts on streams; h2 answers the game's PINGs itself.
//! - Received DATA is acknowledged at once (stream and connection windows).
//! - Outgoing DATA past the peer's window is queued by h2 and resumes on
//!   WINDOW_UPDATE; h2 also splits it to the peer's max frame size.
//! - RST_STREAM from the client → `reset`; every TCP close (clean, error,
//!   TLS failure after the handshake) → `connection_closed`.

use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;

use bytes::Bytes;
use h2::server::SendResponse;
use h2::RecvStream;
use http::{HeaderMap, HeaderValue, Request, Response};
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc;
use tokio_rustls::TlsAcceptor;

use crate::grpc::{self, Deframer};
use crate::limits::{self, Bucket, Connections};
use crate::log::line;
use crate::{Backend, Call, ConnId, Out, StreamId};

/// How long a new connection may take to send its first byte.
const SNIFF_TIMEOUT: Duration = Duration::from_secs(10);

pub async fn accept_loop<B: Backend>(
    listener: TcpListener,
    tls: Option<TlsAcceptor>,
    backend: Arc<B>,
    ids: Arc<AtomicU64>,
    conns: Arc<Connections>,
) -> std::io::Result<()> {
    let mut refusals = Bucket::new(1.0, 5.0); // log at most ~1 refusal a second
    loop {
        let (tcp, peer) = match listener.accept().await {
            Ok(x) => x,
            Err(e) => {
                // EMFILE and friends: back off instead of spinning.
                line(format!("accept failed: {e}"));
                tokio::time::sleep(Duration::from_millis(100)).await;
                continue;
            }
        };
        let guard = match conns.admit(peer.ip()) {
            Ok(g) => g,
            Err(why) => {
                if refusals.take(1.0) {
                    line(format!("refused {peer}: {why}"));
                }
                continue; // dropping the stream closes it
            }
        };
        let _ = tcp.set_nodelay(true);
        // A crashed game never sends FIN: keepalive closes its connection
        // (and so frees its seat) within about a minute.
        let ka = socket2::TcpKeepalive::new().with_time(Duration::from_secs(30)).with_interval(Duration::from_secs(10));
        #[cfg(any(target_os = "linux", target_os = "macos", windows))]
        let ka = ka.with_retries(3);
        let _ = socket2::SockRef::from(&tcp).set_tcp_keepalive(&ka);
        let conn = ids.fetch_add(1, Ordering::Relaxed);
        let (tls, backend) = (tls.clone(), backend.clone());
        tokio::spawn(async move {
            connection(tcp, peer, conn, tls, backend).await;
            drop(guard);
        });
    }
}

/// One TCP connection: sniff TLS vs h2c, then serve HTTP/2 on it.
async fn connection<B: Backend>(tcp: TcpStream, peer: SocketAddr, conn: ConnId, tls: Option<TlsAcceptor>, backend: Arc<B>) {
    let mut first = [0u8; 1];
    let is_tls = match tokio::time::timeout(SNIFF_TIMEOUT, tcp.peek(&mut first)).await {
        Ok(Ok(1)) => first[0] == 0x16,
        _ => return, // closed or silent before any byte: nothing was opened
    };
    let port = tcp.local_addr().map(|a| a.port()).unwrap_or(0);
    match (is_tls, tls) {
        (true, Some(acceptor)) => match tokio::time::timeout(limits::HANDSHAKE_TIMEOUT, acceptor.accept(tcp)).await {
            Ok(Ok(stream)) => {
                let alpn = stream.get_ref().1.alpn_protocol().map(|a| String::from_utf8_lossy(a).into_owned());
                line(format!("conn {conn} from {peer} on :{port} (TLS, ALPN {})", alpn.as_deref().unwrap_or("none")));
                serve_h2(stream, peer, conn, backend).await;
            }
            Ok(Err(e)) => line(format!("conn {conn} from {peer} on :{port}: TLS handshake failed: {e}")),
            Err(_) => line(format!("conn {conn} from {peer} on :{port}: TLS handshake timed out")),
        },
        (true, None) => line(format!("conn {conn} from {peer} on :{port}: TLS without a certificate configured")),
        (false, _) => {
            line(format!("conn {conn} from {peer} on :{port} (h2c)"));
            serve_h2(tcp, peer, conn, backend).await;
        }
    }
}

async fn serve_h2<T, B>(io: T, peer: SocketAddr, conn: ConnId, backend: Arc<B>)
where
    T: AsyncRead + AsyncWrite + Unpin,
    B: Backend,
{
    let mut builder = h2::server::Builder::new();
    builder
        .initial_window_size(1 << 20)
        .initial_connection_window_size(4 << 20)
        .max_concurrent_streams(limits::MAX_STREAMS)
        .max_header_list_size(limits::MAX_HEADER_LIST)
        .max_send_buffer_size(4 << 20);
    let mut h2 = match tokio::time::timeout(limits::HANDSHAKE_TIMEOUT, builder.handshake::<_, Bytes>(io)).await {
        Ok(Ok(c)) => c,
        Ok(Err(e)) => {
            line(format!("conn {conn}: HTTP/2 handshake failed: {e}"));
            backend.connection_closed(conn);
            return;
        }
        Err(_) => {
            line(format!("conn {conn}: no HTTP/2 preface in time"));
            backend.connection_closed(conn);
            return;
        }
    };
    let mut streams = tokio::task::JoinSet::new();
    let mut last_activity = tokio::time::Instant::now();
    loop {
        // A connection with no open stream for a while is closed (the game
        // keeps its subscriptions open, so it is never idle while playing).
        let idle_at = last_activity + limits::IDLE_CONNECTION;
        tokio::select! {
            next = h2.accept() => match next {
                Some(Ok((req, respond))) => {
                    last_activity = tokio::time::Instant::now();
                    streams.spawn(stream(req, respond, peer, conn, backend.clone()));
                }
                Some(Err(e)) => {
                    if !e.is_io() || e.get_io().is_some_and(|io| io.kind() != std::io::ErrorKind::UnexpectedEof) {
                        line(format!("conn {conn}: {e}"));
                    }
                    break;
                }
                None => break,
            },
            // Reap finished stream tasks so the set doesn't grow.
            Some(_) = streams.join_next(), if !streams.is_empty() => {
                last_activity = tokio::time::Instant::now();
            }
            _ = tokio::time::sleep_until(idle_at), if streams.is_empty() => {
                line(format!("conn {conn}: idle, closing"));
                break;
            }
        }
    }
    // The connection is gone: its streams can't send or receive anymore.
    streams.abort_all();
    line(format!("conn {conn} closed"));
    backend.connection_closed(conn);
}

fn bearer(h: &HeaderMap) -> Option<&str> {
    let v = h.get(http::header::AUTHORIZATION)?.to_str().ok()?;
    Some(v.strip_prefix("Bearer ").or_else(|| v.strip_prefix("bearer ")).unwrap_or(v))
}

async fn stream<B: Backend>(req: Request<RecvStream>, mut respond: SendResponse<Bytes>, peer: SocketAddr, conn: ConnId, backend: Arc<B>) {
    let sid: StreamId = u64::from(u32::from(respond.stream_id()));
    let path = req.uri().path().to_string();
    let authority = req.uri().authority().map(|a| a.to_string()).unwrap_or_default();
    let token = bearer(req.headers()).map(str::to_string);
    let (tx, mut rx) = mpsc::unbounded_channel();

    let mut head = Response::new(());
    let h = head.headers_mut();
    h.insert(http::header::CONTENT_TYPE, HeaderValue::from_static("application/grpc"));
    h.insert("grpc-encoding", HeaderValue::from_static("identity"));
    h.insert("grpc-accept-encoding", HeaderValue::from_static("identity"));
    let mut send = match respond.send_response(head, false) {
        Ok(s) => s,
        Err(e) => {
            line(format!("conn {conn} stream {sid} {path}: {e}"));
            return;
        }
    };
    backend.open(
        &Call { conn, stream: sid, path: &path, authority: &authority, bearer: token.as_deref(), peer },
        tx,
    );

    let mut body = req.into_body();
    let mut deframer = Deframer::default();
    let (mut recv_open, mut out_open) = (true, true);
    let (mut msgs_in, mut bytes_in, mut msgs_out, mut bytes_out) = (0usize, 0usize, 0usize, 0usize);
    let summary = |status: &str, mi, bi, mo, bo| {
        line(format!("conn {conn} stream {sid} {path}: in {mi} msg/{bi} B, out {mo} msg/{bo} B, {status}"));
    };
    loop {
        tokio::select! {
            chunk = body.data(), if recv_open => match chunk {
                Some(Ok(data)) => {
                    let _ = body.flow_control().release_capacity(data.len());
                    match deframer.push(&data) {
                        Ok(msgs) => {
                            for m in msgs {
                                msgs_in += 1;
                                bytes_in += m.len();
                                backend.message(conn, sid, &m);
                            }
                            if deframer.compressed > 0 {
                                line(format!("conn {conn} stream {sid} {path}: dropped {} compressed message(s)", deframer.compressed));
                                deframer.compressed = 0;
                            }
                        }
                        Err(grpc::TooLarge(n)) => {
                            summary(&format!("refused a {n} B message"), msgs_in, bytes_in, msgs_out, bytes_out);
                            send.send_reset(h2::Reason::FRAME_SIZE_ERROR);
                            backend.reset(conn, sid);
                            return;
                        }
                    }
                }
                Some(Err(e)) => {
                    summary(&format!("reset by client ({e})"), msgs_in, bytes_in, msgs_out, bytes_out);
                    backend.reset(conn, sid);
                    return;
                }
                None => {
                    recv_open = false;
                    backend.half_closed(conn, sid);
                }
            },
            out = rx.recv(), if out_open => match out {
                Some(Out::Message(m)) => {
                    msgs_out += 1;
                    bytes_out += m.len();
                    if send.send_data(Bytes::from(grpc::frame(&m)), false).is_err() {
                        summary("send failed", msgs_in, bytes_in, msgs_out, bytes_out);
                        backend.reset(conn, sid);
                        return;
                    }
                }
                Some(Out::End { grpc_status, message }) => {
                    let mut t = HeaderMap::new();
                    t.insert("grpc-status", HeaderValue::from(grpc_status));
                    if let Some(m) = message.as_deref().and_then(|m| HeaderValue::from_str(&encode_message(m)).ok()) {
                        t.insert("grpc-message", m);
                    }
                    let _ = send.send_trailers(t);
                    summary(&format!("grpc-status {grpc_status}"), msgs_in, bytes_in, msgs_out, bytes_out);
                    // Keep draining nothing: the call is over for both sides.
                    return;
                }
                // The backend dropped the call without ending it: keep the
                // stream open (never close what the backend hasn't ended)
                // until the client resets it or leaves.
                None => out_open = false,
            },
            reset = std::future::poll_fn(|cx| send.poll_reset(cx)) => {
                let why = match reset { Ok(r) => format!("{r:?}"), Err(e) => e.to_string() };
                summary(&format!("reset by client ({why})"), msgs_in, bytes_in, msgs_out, bytes_out);
                backend.reset(conn, sid);
                return;
            }
        }
    }
}

/// grpc-message is percent-encoded (outside printable ASCII and '%').
fn encode_message(m: &str) -> String {
    let mut s = String::with_capacity(m.len());
    for b in m.bytes() {
        if (0x20..0x7f).contains(&b) && b != b'%' {
            s.push(b as char);
        } else {
            s.push_str(&format!("%{b:02X}"));
        }
    }
    s
}

#[cfg(test)]
mod tests {
    #[test]
    fn grpc_message_is_percent_encoded() {
        assert_eq!(super::encode_message("bad 100% ñ"), "bad 100%25 %C3%B1");
    }
}
