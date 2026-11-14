//! Gamesync documents: the client's writes (`WriteDocumentsRequest`) and the in-memory store
//! each lobby keeps of them. Shapes in docs/RE-MAP.md section 1 (all verified):
//!
//! ```text
//! WriteDocumentsRequest { 1: repeated Write, 2: repeated DefermentOp }
//! Write (oneof)  1: Update    { 1: Document{1 name, 2 fields}, 2: FieldMask{1: repeated path},
//!                              3: Precondition{1: bool exists} }
//!                3: Transform { 1: name, 2: repeated FieldTransform }   (only __pus upcsid)
//!                4: Delete    { 1: name }
//! DefermentOp    { 1: { 1: Deferment{ 1: "userSessions/current/deferments/<path>",
//!                                     2: repeated Write } } }
//! ```

use std::collections::BTreeMap;

use crate::value::{Document, Fields};
use crate::wire::{self, Result};

/// One client write.
#[derive(Clone, Debug, PartialEq)]
pub enum Write {
    Update {
        name: String,
        fields: Fields,
        /// FieldMask paths (backticks stripped). `None` = replace the document.
        mask: Option<Vec<String>>,
        /// `Some(false)` = create (must not exist), `Some(true)` = must exist.
        exists: Option<bool>,
    },
    /// Server-side field transforms; the lobby answers each with the user's number.
    Transform { name: String, count: usize },
    Delete { name: String },
    /// A write kind we don't model; acknowledged and otherwise ignored.
    Other,
}

impl Write {
    pub fn name(&self) -> &str {
        match self {
            Write::Update { name, .. } | Write::Transform { name, .. } | Write::Delete { name } => name,
            Write::Other => "",
        }
    }

    pub fn decode(buf: &[u8]) -> Result<Self> {
        if let Some(update) = wire::bytes_field(buf, 1)? {
            let doc = wire::bytes_field(update, 1)?.unwrap_or_default();
            let (name, fields) = Document::decode_written(doc)?;
            let mask = match wire::bytes_field(update, 2)? {
                Some(m) => Some(
                    wire::repeated_bytes(m, 1)?
                        .into_iter()
                        .map(|p| String::from_utf8_lossy(p).trim_matches('`').to_owned())
                        .collect(),
                ),
                None => None,
            };
            let exists = match wire::bytes_field(update, 3)? {
                Some(p) => Some(wire::varint_field(p, 1)?.unwrap_or(0) != 0),
                None => None,
            };
            return Ok(Write::Update { name, fields, mask, exists });
        }
        if let Some(t) = wire::bytes_field(buf, 3)? {
            let name = wire::str_field(t, 1)?.unwrap_or_default();
            let count = wire::repeated_bytes(t, 2)?.len();
            return Ok(Write::Transform { name, count });
        }
        if let Some(d) = wire::bytes_field(buf, 4)? {
            return Ok(Write::Delete { name: wire::str_field(d, 1)?.unwrap_or_default() });
        }
        Ok(Write::Other)
    }
}

/// "Do these writes when my user session ends" (in practice: delete my documents).
#[derive(Clone, Debug, PartialEq)]
pub struct Deferment {
    /// `userSessions/current/deferments/<path>`; re-registering a name replaces it.
    pub name: String,
    pub writes: Vec<Write>,
}

/// A parsed `WriteDocumentsRequest`.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct WriteRequest {
    pub writes: Vec<Write>,
    pub deferments: Vec<Deferment>,
}

impl WriteRequest {
    pub fn decode(buf: &[u8]) -> Result<Self> {
        let mut out = WriteRequest::default();
        for w in wire::repeated_bytes(buf, 1)? {
            out.writes.push(Write::decode(w)?);
        }
        for op in wire::repeated_bytes(buf, 2)? {
            let Some(inner) = wire::bytes_field(op, 1)? else { continue };
            let Some(def) = wire::bytes_field(inner, 1)? else { continue };
            let name = wire::str_field(def, 1)?.unwrap_or_default();
            let writes = wire::repeated_bytes(def, 2)?.into_iter().map(Write::decode).collect::<Result<_>>()?;
            out.deferments.push(Deferment { name, writes });
        }
        Ok(out)
    }
}

/// How a stored document changed (Gamesync Change kinds).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ChangeKind {
    Added = 1,
    Modified = 2,
    Removed = 3,
}

#[derive(Clone, Debug)]
struct Entry {
    doc: Document,
    /// User id of the last writer (documents of a leaving player are cleaned up).
    writer: Option<String>,
}

/// One lobby's documents, by full name (`docs/...`).
#[derive(Default, Debug)]
pub struct Store {
    docs: BTreeMap<String, Entry>,
}

/// `collection` ends with "/"; only direct children match.
pub fn in_collection(name: &str, collection: &str) -> bool {
    name.strip_prefix(collection).is_some_and(|rest| !rest.is_empty() && !rest.contains('/'))
}

/// Whether a listen target (a document name, or a collection ending in "/") covers `name`.
pub fn covers(target: &str, name: &str) -> bool {
    target == name || (target.ends_with('/') && in_collection(name, target))
}

impl Store {
    pub fn get(&self, name: &str) -> Option<&Document> {
        self.docs.get(name).map(|e| &e.doc)
    }

    pub fn len(&self) -> usize {
        self.docs.len()
    }

    pub fn is_empty(&self) -> bool {
        self.docs.is_empty()
    }

    /// Documents a target covers, by name.
    pub fn matching(&self, target: &str) -> Vec<&Document> {
        if target.ends_with('/') {
            self.docs.range(target.to_owned()..).take_while(|(n, _)| n.starts_with(target))
                .filter(|(n, _)| in_collection(n, target)).map(|(_, e)| &e.doc).collect()
        } else {
            self.get(target).into_iter().collect()
        }
    }

    /// Ids (last path component) of the documents directly in `collection`.
    pub fn ids_in(&self, collection: &str) -> Vec<&str> {
        self.matching(collection).into_iter().map(|d| crate::users::tail(&d.name)).collect()
    }

    /// Apply an update. Game writes are merge writes (FUN_0049c9d8): the mask lists the fields
    /// the write sets; a listed field that is absent from `fields` is removed. No mask or `*`
    /// replaces the document. A create (`exists: Some(false)`) of an existing document is a
    /// no-op. Returns the stored document and its change kind, or None when nothing changed.
    pub fn update(
        &mut self,
        name: &str,
        fields: Fields,
        mask: Option<&[String]>,
        exists: Option<bool>,
        writer: Option<&str>,
        now: u64,
    ) -> Option<(Document, ChangeKind)> {
        let existing = self.docs.get(name);
        match (exists, existing) {
            (Some(false), Some(_)) | (Some(true), None) => return None,
            _ => {}
        }
        let (fields, create_time) = match (existing, mask) {
            (Some(old), Some(mask)) if !mask.iter().any(|p| p == "*") => {
                let mut merged = old.doc.fields.clone();
                for key in mask {
                    merged.remove(key);
                }
                for (k, v) in fields.0 {
                    merged.set(&k, v);
                }
                (merged, old.doc.create_time)
            }
            (Some(old), _) => (fields, old.doc.create_time),
            (None, _) => (fields, now),
        };
        let kind = if existing.is_some() { ChangeKind::Modified } else { ChangeKind::Added };
        let doc = Document { name: name.to_owned(), fields, create_time, update_time: now };
        self.docs.insert(name.to_owned(), Entry { doc: doc.clone(), writer: writer.map(str::to_owned) });
        Some((doc, kind))
    }

    /// Server-authored document (replace).
    pub fn put(&mut self, doc: Document) -> ChangeKind {
        let kind = if self.docs.contains_key(&doc.name) { ChangeKind::Modified } else { ChangeKind::Added };
        self.docs.insert(doc.name.clone(), Entry { doc, writer: None });
        kind
    }

    pub fn delete(&mut self, name: &str) -> Option<Document> {
        self.docs.remove(name).map(|e| e.doc)
    }

    /// Names of the documents last written by `user`.
    pub fn written_by(&self, user: &str) -> Vec<String> {
        self.docs.iter().filter(|(_, e)| e.writer.as_deref() == Some(user)).map(|(n, _)| n.clone()).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::value::Value;
    use crate::wire::Msg;

    fn f(pairs: &[(&str, i64)]) -> Fields {
        pairs.iter().fold(Fields::new(), |f, (k, v)| f.with(k, Value::Int(*v)))
    }

    #[test]
    fn merge_writes_follow_the_mask() {
        let mut s = Store::default();
        let m = |p: &[&str]| p.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        let n = "docs/PlayerStatic/1";
        assert_eq!(s.update(n, f(&[("p2p_sid", 7)]), Some(&m(&["p2p_sid"])), None, None, 1).unwrap().1, ChangeKind::Added);
        s.update(n, f(&[("vehicle", 3)]), Some(&m(&["vehicle", "driver"])), None, None, 2);
        let d = s.get(n).unwrap();
        assert_eq!(d.fields.get("p2p_sid"), Some(&Value::Int(7)));
        assert_eq!(d.fields.get("vehicle"), Some(&Value::Int(3)));
        assert_eq!((d.create_time, d.update_time), (1, 2));
        // masked but absent = removed
        s.update(n, Fields::new(), Some(&m(&["vehicle"])), None, None, 3);
        assert!(!s.get(n).unwrap().fields.contains("vehicle"));
        // no mask = replace
        s.update(n, f(&[("x", 1)]), None, None, None, 4);
        assert_eq!(s.get(n).unwrap().fields.0.len(), 1);
    }

    #[test]
    fn create_does_not_overwrite() {
        let mut s = Store::default();
        let n = "docs/@@RefereeRequest/1/Request/k";
        assert!(s.update(n, f(&[("a", 1)]), None, Some(false), None, 1).is_some());
        assert!(s.update(n, f(&[("a", 2)]), None, Some(false), None, 2).is_none());
        assert_eq!(s.get(n).unwrap().fields.get("a"), Some(&Value::Int(1)));
    }

    #[test]
    fn collections_are_direct_children() {
        let mut s = Store::default();
        for n in ["docs/__pgn/All/__pus/a", "docs/__pgn/All/__pus/b", "docs/__pgn/All/__stu/a"] {
            s.put(Document::new(n, Fields::new(), 0));
        }
        assert_eq!(s.ids_in("docs/__pgn/All/__pus/"), vec!["a", "b"]);
        assert!(covers("docs/__pgn/All/__pus/", "docs/__pgn/All/__pus/a"));
        assert!(!covers("docs/__pgn/All/", "docs/__pgn/All/__pus/a"));
    }

    #[test]
    fn decodes_update_delete_transform_and_deferment() {
        let doc = Msg::new().bytes(1, "docs/PlayerStatic/2").msg(2, &f(&[("greeting_id", 4)]).encode());
        let update = Msg::new().msg(1, &doc).msg(2, &Msg::new().bytes(1, "`greeting_id`"));
        let delete = Msg::new().msg(4, &Msg::new().bytes(1, "docs/__pgn/All/__pus/u"));
        let transform = Msg::new().msg(3, &Msg::new().bytes(1, "docs/__pgn/All/__pus/u").bytes(2, "x"));
        let deferment = Msg::new().msg(1, &Msg::new().msg(1, &Msg::new()
            .bytes(1, "userSessions/current/deferments/PlayerStatic/2").msg(2, &Msg::new().msg(4, &Msg::new().bytes(1, "docs/PlayerStatic/2")))));
        let req = Msg::new().msg(1, &Msg::new().msg(1, &update)).msg(1, &delete).msg(1, &transform).msg(2, &deferment);
        let r = WriteRequest::decode(&req.0).unwrap();
        assert_eq!(r.writes[0], Write::Update {
            name: "docs/PlayerStatic/2".into(), fields: f(&[("greeting_id", 4)]),
            mask: Some(vec!["greeting_id".into()]), exists: None });
        assert_eq!(r.writes[1], Write::Delete { name: "docs/__pgn/All/__pus/u".into() });
        assert_eq!(r.writes[2], Write::Transform { name: "docs/__pgn/All/__pus/u".into(), count: 1 });
        assert_eq!(r.deferments[0].writes, vec![Write::Delete { name: "docs/PlayerStatic/2".into() }]);
    }
}
