//! gRPC message framing: 1 byte compressed flag + 4 bytes big-endian length.
//! Compression is never negotiated, so frames are always uncompressed.

/// Largest client message accepted (a guard, far above anything the game sends).
pub const MAX_MESSAGE: usize = 4 << 20;

pub fn frame(msg: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(5 + msg.len());
    out.push(0);
    out.extend_from_slice(&(msg.len() as u32).to_be_bytes());
    out.extend_from_slice(msg);
    out
}

/// Reassembles messages from DATA chunks that split or join frames anywhere.
#[derive(Default)]
pub struct Deframer {
    buf: Vec<u8>,
    /// Compressed messages seen (and dropped): nothing negotiates compression.
    pub compressed: usize,
}

#[derive(Debug, PartialEq, Eq)]
pub struct TooLarge(pub usize);

impl Deframer {
    pub fn push(&mut self, chunk: &[u8]) -> Result<Vec<Vec<u8>>, TooLarge> {
        self.buf.extend_from_slice(chunk);
        let mut out = vec![];
        let mut pos = 0;
        while self.buf.len() - pos >= 5 {
            let len = u32::from_be_bytes(self.buf[pos + 1..pos + 5].try_into().unwrap()) as usize;
            if len > MAX_MESSAGE {
                return Err(TooLarge(len));
            }
            if self.buf.len() - pos - 5 < len {
                break;
            }
            if self.buf[pos] == 0 {
                out.push(self.buf[pos + 5..pos + 5 + len].to_vec());
            } else {
                self.compressed += 1;
            }
            pos += 5 + len;
        }
        self.buf.drain(..pos);
        Ok(out)
    }

    /// Bytes of an incomplete frame still waiting.
    pub fn pending(&self) -> usize {
        self.buf.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames_split_and_joined() {
        let mut d = Deframer::default();
        let mut wire = frame(b"hello");
        wire.extend(frame(b""));
        wire.extend(frame(&[7u8; 300]));
        let (a, b) = wire.split_at(3);
        assert!(d.push(a).unwrap().is_empty());
        let (b1, b2) = b.split_at(12);
        assert_eq!(d.push(b1).unwrap(), vec![b"hello".to_vec(), vec![]]);
        assert_eq!(d.push(b2).unwrap(), vec![vec![7u8; 300]]);
        assert_eq!(d.pending(), 0);
    }

    #[test]
    fn compressed_messages_are_dropped() {
        let mut d = Deframer::default();
        let mut wire = frame(b"zz");
        wire[0] = 1;
        wire.extend(frame(b"ok"));
        assert_eq!(d.push(&wire).unwrap(), vec![b"ok".to_vec()]);
        assert_eq!(d.compressed, 1);
    }

    #[test]
    fn oversized_frame_is_refused() {
        let mut d = Deframer::default();
        let mut wire = vec![0];
        wire.extend(((MAX_MESSAGE + 1) as u32).to_be_bytes());
        assert_eq!(d.push(&wire), Err(TooLarge(MAX_MESSAGE + 1)));
    }
}
