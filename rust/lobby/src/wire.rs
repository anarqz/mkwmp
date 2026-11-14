//! Minimal protobuf wire format: what the lobby needs to read and write NPLN messages whose
//! schemas are only partly recovered (Gamesync values, documents) byte-for-byte like the
//! verified Python reference. Typed messages use `contracts` (prost) where the shape is known.

use std::fmt;

/// A decoding error (truncated or malformed input).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WireError(pub &'static str);

impl fmt::Display for WireError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "protobuf: {}", self.0)
    }
}
impl std::error::Error for WireError {}

pub type Result<T> = std::result::Result<T, WireError>;

/// One decoded field value.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Field<'a> {
    Varint(u64),
    Fixed64(u64),
    Bytes(&'a [u8]),
    Fixed32(u32),
}

impl<'a> Field<'a> {
    pub fn bytes(self) -> Option<&'a [u8]> {
        match self {
            Field::Bytes(b) => Some(b),
            _ => None,
        }
    }
    pub fn varint(self) -> Option<u64> {
        match self {
            Field::Varint(v) => Some(v),
            _ => None,
        }
    }
}

pub fn read_varint(buf: &[u8], pos: &mut usize) -> Result<u64> {
    let mut out = 0u64;
    for shift in (0..64).step_by(7) {
        let byte = *buf.get(*pos).ok_or(WireError("truncated varint"))?;
        *pos += 1;
        out |= u64::from(byte & 0x7f) << shift;
        if byte & 0x80 == 0 {
            return Ok(out);
        }
    }
    Err(WireError("varint too long"))
}

/// Iterate `(field number, value)` over a message, flat (no descent).
pub fn fields(buf: &[u8]) -> Fields<'_> {
    Fields { buf, pos: 0 }
}

pub struct Fields<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> Iterator for Fields<'a> {
    type Item = Result<(u32, Field<'a>)>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.pos >= self.buf.len() {
            return None;
        }
        Some(self.read())
    }
}

impl<'a> Fields<'a> {
    fn read(&mut self) -> Result<(u32, Field<'a>)> {
        let tag = read_varint(self.buf, &mut self.pos)?;
        let number = u32::try_from(tag >> 3).map_err(|_| WireError("field number"))?;
        if number == 0 {
            return Err(WireError("field 0"));
        }
        let value = match tag & 7 {
            0 => Field::Varint(read_varint(self.buf, &mut self.pos)?),
            1 => Field::Fixed64(u64::from_le_bytes(self.take(8)?.try_into().unwrap())),
            2 => {
                let len = usize::try_from(read_varint(self.buf, &mut self.pos)?).map_err(|_| WireError("length"))?;
                Field::Bytes(self.take(len)?)
            }
            5 => Field::Fixed32(u32::from_le_bytes(self.take(4)?.try_into().unwrap())),
            _ => return Err(WireError("unsupported wire type")),
        };
        Ok((number, value))
    }

    fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        let end = self.pos.checked_add(n).ok_or(WireError("length"))?;
        let out = self.buf.get(self.pos..end).ok_or(WireError("truncated field"))?;
        self.pos = end;
        Ok(out)
    }
}

/// `(field number, raw encoded field incl. its tag)` over a message, flat.
pub fn raw_fields(buf: &[u8]) -> Result<Vec<(u32, &[u8])>> {
    let mut it = fields(buf);
    let mut out = Vec::new();
    let mut start = 0;
    while let Some(f) = it.next() {
        let (n, _) = f?;
        out.push((n, &buf[start..it.pos]));
        start = it.pos;
    }
    Ok(out)
}

/// First length-delimited field `number` (bytes / string / message).
pub fn bytes_field(buf: &[u8], number: u32) -> Result<Option<&[u8]>> {
    for f in fields(buf) {
        let (n, v) = f?;
        if n == number {
            if let Field::Bytes(b) = v {
                return Ok(Some(b));
            }
        }
    }
    Ok(None)
}

/// First string field `number` (lossy UTF-8).
pub fn str_field(buf: &[u8], number: u32) -> Result<Option<String>> {
    Ok(bytes_field(buf, number)?.map(|b| String::from_utf8_lossy(b).into_owned()))
}

/// Every length-delimited field `number`, in order.
pub fn repeated_bytes(buf: &[u8], number: u32) -> Result<Vec<&[u8]>> {
    let mut out = Vec::new();
    for f in fields(buf) {
        let (n, v) = f?;
        if n == number {
            if let Field::Bytes(b) = v {
                out.push(b);
            }
        }
    }
    Ok(out)
}

/// First varint field `number`.
pub fn varint_field(buf: &[u8], number: u32) -> Result<Option<u64>> {
    for f in fields(buf) {
        let (n, v) = f?;
        if n == number {
            if let Field::Varint(x) = v {
                return Ok(Some(x));
            }
        }
    }
    Ok(None)
}

/// Message builder.
#[derive(Default, Clone, Debug, PartialEq, Eq)]
pub struct Msg(pub Vec<u8>);

impl Msg {
    pub fn new() -> Self {
        Self::default()
    }

    fn varint_raw(&mut self, mut v: u64) {
        while v >= 0x80 {
            self.0.push((v as u8) | 0x80);
            v >>= 7;
        }
        self.0.push(v as u8);
    }

    fn tag(&mut self, number: u32, wire: u8) {
        self.varint_raw((u64::from(number) << 3) | u64::from(wire));
    }

    /// Length-delimited field (bytes, string, nested message).
    pub fn bytes(mut self, number: u32, value: impl AsRef<[u8]>) -> Self {
        let value = value.as_ref();
        self.tag(number, 2);
        self.varint_raw(value.len() as u64);
        self.0.extend_from_slice(value);
        self
    }

    pub fn msg(self, number: u32, value: &Msg) -> Self {
        self.bytes(number, &value.0)
    }

    pub fn varint(mut self, number: u32, value: u64) -> Self {
        self.tag(number, 0);
        self.varint_raw(value);
        self
    }

    pub fn fixed64(mut self, number: u32, value: u64) -> Self {
        self.tag(number, 1);
        self.0.extend_from_slice(&value.to_le_bytes());
        self
    }

    /// Append already-encoded fields verbatim.
    pub fn raw(mut self, encoded: &[u8]) -> Self {
        self.0.extend_from_slice(encoded);
        self
    }

    pub fn into_bytes(self) -> Vec<u8> {
        self.0
    }
}

/// `google.protobuf.Timestamp` / `Duration` with whole seconds: `{1: seconds}`.
pub fn seconds(secs: u64) -> Msg {
    Msg::new().varint(1, secs)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_fields() {
        let m = Msg::new().bytes(1, "tenants/current").varint(2, 300).bytes(3, b"\x01\x02");
        let got: Vec<_> = fields(&m.0).collect::<Result<_>>().unwrap();
        assert_eq!(got[0], (1, Field::Bytes(b"tenants/current")));
        assert_eq!(got[1], (2, Field::Varint(300)));
        assert_eq!(str_field(&m.0, 1).unwrap().as_deref(), Some("tenants/current"));
        assert_eq!(varint_field(&m.0, 2).unwrap(), Some(300));
        assert_eq!(bytes_field(&m.0, 9).unwrap(), None);
    }

    #[test]
    fn matches_python_encoding() {
        // protobuf_bytes(1, "ab") + b"\x10" + encode_varint(2) in the reference server.
        let m = Msg::new().bytes(1, "ab").varint(2, 2);
        assert_eq!(m.0, b"\x0a\x02ab\x10\x02");
    }

    #[test]
    fn truncated_is_an_error() {
        assert!(fields(b"\x0a\x05ab").next().unwrap().is_err());
    }
}
