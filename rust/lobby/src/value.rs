//! Gamesync document model: `Value` (oneof, serializer main+0xf19724; full oneof in
//! contracts/nn/npln/gamesync/v1/values.proto), `Fields`
//! (repeated Entry{1 key, 2 Value}, main+0xf1b700) and `Document` (main+0x12ab0e8).
//! Encodings are byte-identical to the verified Python reference (`gs_*` helpers).
//! Value kinds we don't model are kept verbatim so client documents round-trip.

use crate::wire::{self, Field, Msg, Result};

#[derive(Clone, Debug, PartialEq)]
pub enum Value {
    /// field 2 (varint), e.g. `rating_enabled`.
    Bool(bool),
    /// field 3 (varint, two's complement for negatives, e.g. `dropout_rank` -1).
    Int(i64),
    /// field 5 (fixed64), e.g. `ratings` entries.
    Double(f64),
    /// field 6: `google.protobuf.Timestamp` (seconds).
    Timestamp(u64),
    /// field 7.
    Str(String),
    /// field 8, e.g. `__gs/f` `rs`.
    Bytes(Vec<u8>),
    /// field 9: `{1: repeated Value}`.
    List(Vec<Value>),
    /// field 10: `{1: repeated Entry}` (`ussid_map`, `option`, `ratings`).
    Map(Fields),
    /// Anything else, as the encoded Value message.
    Raw(Vec<u8>),
}

/// Ordered key/value entries (documents keep insertion order like the reference server).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Fields(pub Vec<(String, Value)>);

impl Fields {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn with(mut self, key: &str, value: Value) -> Self {
        self.set(key, value);
        self
    }

    pub fn get(&self, key: &str) -> Option<&Value> {
        self.0.iter().find(|(k, _)| k == key).map(|(_, v)| v)
    }

    /// Insert or replace, keeping the position of an existing key.
    pub fn set(&mut self, key: &str, value: Value) {
        match self.0.iter_mut().find(|(k, _)| k == key) {
            Some(slot) => slot.1 = value,
            None => self.0.push((key.to_owned(), value)),
        }
    }

    pub fn remove(&mut self, key: &str) {
        self.0.retain(|(k, _)| k != key);
    }

    pub fn contains(&self, key: &str) -> bool {
        self.get(key).is_some()
    }

    pub fn encode(&self) -> Msg {
        self.0.iter().fold(Msg::new(), |m, (k, v)| {
            m.msg(1, &Msg::new().bytes(1, k).msg(2, &v.encode()))
        })
    }

    pub fn decode(buf: &[u8]) -> Result<Self> {
        let mut out = Fields::new();
        for entry in wire::repeated_bytes(buf, 1)? {
            let key = wire::str_field(entry, 1)?.unwrap_or_default();
            let value = match wire::bytes_field(entry, 2)? {
                Some(v) => Value::decode(v)?,
                None => Value::Raw(Vec::new()),
            };
            out.0.push((key, value));
        }
        Ok(out)
    }
}

impl Value {
    pub fn str(s: impl Into<String>) -> Self {
        Value::Str(s.into())
    }

    pub fn as_int(&self) -> Option<i64> {
        match self {
            Value::Int(i) => Some(*i),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Value::Str(s) => Some(s),
            _ => None,
        }
    }

    /// Integers of a `List` value (other kinds skipped).
    pub fn int_list(&self) -> Vec<i64> {
        match self {
            Value::List(items) => items.iter().filter_map(Value::as_int).collect(),
            _ => Vec::new(),
        }
    }

    /// Entries of a `Map` value.
    pub fn entries(&self) -> Option<&Fields> {
        match self {
            Value::Map(f) => Some(f),
            _ => None,
        }
    }

    pub fn encode(&self) -> Msg {
        match self {
            Value::Bool(b) => Msg::new().varint(2, u64::from(*b)),
            Value::Int(i) => Msg::new().varint(3, *i as u64),
            Value::Double(d) => Msg::new().fixed64(5, d.to_bits()),
            Value::Timestamp(s) => Msg::new().msg(6, &wire::seconds(*s)),
            Value::Str(s) => Msg::new().bytes(7, s),
            Value::Bytes(b) => Msg::new().bytes(8, b),
            Value::List(items) => {
                let body = items.iter().fold(Msg::new(), |m, v| m.msg(1, &v.encode()));
                Msg::new().msg(9, &body)
            }
            Value::Map(f) => Msg::new().msg(10, &f.encode()),
            Value::Raw(raw) => Msg(raw.clone()),
        }
    }

    pub fn decode(buf: &[u8]) -> Result<Self> {
        let Some(first) = wire::fields(buf).next() else {
            return Ok(Value::Raw(Vec::new()));
        };
        let (number, field) = first?;
        Ok(match (number, field) {
            (2, Field::Varint(v)) => Value::Bool(v != 0),
            (3, Field::Varint(v)) => Value::Int(v as i64),
            (5, Field::Fixed64(v)) => Value::Double(f64::from_bits(v)),
            (6, Field::Bytes(b)) => Value::Timestamp(wire::varint_field(b, 1)?.unwrap_or(0)),
            (7, Field::Bytes(b)) => Value::Str(String::from_utf8_lossy(b).into_owned()),
            (8, Field::Bytes(b)) => Value::Bytes(b.to_vec()),
            (9, Field::Bytes(b)) => Value::List(
                wire::repeated_bytes(b, 1)?.into_iter().map(Value::decode).collect::<Result<_>>()?,
            ),
            (10, Field::Bytes(b)) => Value::Map(Fields::decode(b)?),
            _ => Value::Raw(buf.to_vec()),
        })
    }
}

/// A Gamesync document: `{1 name, 2 fields, 3 create_time, 4 update_time}`.
#[derive(Clone, Debug, PartialEq)]
pub struct Document {
    pub name: String,
    pub fields: Fields,
    pub create_time: u64,
    pub update_time: u64,
}

impl Document {
    pub fn new(name: impl Into<String>, fields: Fields, now: u64) -> Self {
        Self { name: name.into(), fields, create_time: now, update_time: now }
    }

    pub fn encode(&self) -> Msg {
        Msg::new()
            .bytes(1, &self.name)
            .msg(2, &self.fields.encode())
            .msg(3, &wire::seconds(self.create_time))
            .msg(4, &wire::seconds(self.update_time))
    }

    /// `{1 name, 2 fields}` as clients write them (times ignored).
    pub fn decode_written(buf: &[u8]) -> Result<(String, Fields)> {
        let name = wire::str_field(buf, 1)?.unwrap_or_default();
        let fields = match wire::bytes_field(buf, 2)? {
            Some(b) => Fields::decode(b)?,
            None => Fields::new(),
        };
        Ok((name, fields))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    #[test]
    fn decodes_captured_referee_request_values() {
        // From a real @@RefereeRequest (LAN run 2026-11-13), see server/test_protocol.py.
        assert_eq!(Value::decode(&hex("1805")).unwrap(), Value::Int(5));
        assert_eq!(Value::decode(&hex("1001")).unwrap(), Value::Bool(true));
        assert_eq!(Value::decode(&hex("3a06626174746c65")).unwrap(), Value::str("battle"));
        assert_eq!(Value::decode(&hex("4a040a021802")).unwrap(), Value::List(vec![Value::Int(2)]));
        let option = Value::decode(&hex(
            "52370a180a12746f74616c5f706c617965725f636f756e74120218020a1b0a0c64726f706f75745f72616e6b120b18ffffffffffffffffff01",
        ))
        .unwrap();
        let f = option.entries().unwrap();
        assert_eq!(f.get("total_player_count"), Some(&Value::Int(2)));
        assert_eq!(f.get("dropout_rank"), Some(&Value::Int(-1)));
    }

    #[test]
    fn encodings_roundtrip_and_match_reference() {
        for v in [
            Value::Int(-1),
            Value::Bool(false),
            Value::Double(3000.0),
            Value::str("Alex"),
            Value::Bytes(vec![1, 2, 3]),
            Value::List(vec![Value::Int(1), Value::Int(0)]),
            Value::Map(Fields::new().with("1", Value::Double(3000.0))),
        ] {
            assert_eq!(Value::decode(&v.encode().0).unwrap(), v);
        }
        // gs_list([gs_int(1), gs_int(0)]) in the reference server.
        assert_eq!(Value::List(vec![Value::Int(1), Value::Int(0)]).encode().0, hex("4a080a0218010a021800"));
    }

    #[test]
    fn fields_set_keeps_order() {
        let mut f = Fields::new().with("a", Value::Int(1)).with("b", Value::Int(2));
        f.set("a", Value::Int(3));
        assert_eq!(f.0[0], ("a".into(), Value::Int(3)));
        f.remove("a");
        assert!(!f.contains("a"));
    }
}
