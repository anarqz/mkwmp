//! Username rules, shared by the player's own name and the friend list:
//! 3-16 chars of [A-Za-z0-9_-]. Names compare case-insensitively.

pub const MIN_LEN: usize = 3;
pub const MAX_LEN: usize = 16;
pub const MAX_FRIENDS: usize = 8;

#[derive(Debug, PartialEq, Eq)]
pub enum NameError {
    Invalid(String),
    TooManyFriends,
}

impl std::fmt::Display for NameError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            NameError::Invalid(n) => write!(
                f,
                "invalid gamertag {n:?}: use {MIN_LEN}-{MAX_LEN} letters, digits, '-' or '_'"
            ),
            NameError::TooManyFriends => write!(f, "at most {MAX_FRIENDS} friends"),
        }
    }
}

pub fn is_valid(name: &str) -> bool {
    (MIN_LEN..=MAX_LEN).contains(&name.len())
        && name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
}

pub fn validate(name: &str) -> Result<(), NameError> {
    if is_valid(name) {
        Ok(())
    } else {
        Err(NameError::Invalid(name.chars().take(32).collect()))
    }
}

/// The case-insensitive key a name is compared by.
pub fn key(name: &str) -> String {
    name.to_ascii_lowercase()
}

/// Validate a friend list: every entry valid, at most 8 after dropping
/// duplicates (case-insensitive) and the player's own name. Keeps first-seen
/// spelling and order.
pub fn friends(own: &str, list: &[String]) -> Result<Vec<String>, NameError> {
    let own = key(own);
    let mut out: Vec<String> = Vec::new();
    for f in list {
        validate(f)?;
        let k = key(f);
        if k != own && !out.iter().any(|o| key(o) == k) {
            out.push(f.clone());
        }
    }
    if out.len() > MAX_FRIENDS {
        return Err(NameError::TooManyFriends);
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rules() {
        for ok in ["abc", "Player_1", "a-b_c", "ABCDEFGHIJKLMNOP"] {
            assert!(is_valid(ok), "{ok}");
        }
        for bad in ["ab", "ABCDEFGHIJKLMNOPQ", "with space", "ação", "a.b", "", "x\"y"] {
            assert!(!is_valid(bad), "{bad}");
        }
    }

    #[test]
    fn friend_list_dedupes_and_drops_self() {
        let l: Vec<String> = ["Bia", "bia", "Ana", "Cid"].map(String::from).into();
        assert_eq!(friends("ana", &l).unwrap(), vec!["Bia", "Cid"]);
    }

    #[test]
    fn friend_list_limits() {
        let nine: Vec<String> = (0..9).map(|i| format!("friend{i}")).collect();
        assert_eq!(friends("me_", &nine), Err(NameError::TooManyFriends));
        assert!(friends("me_", &nine[..8]).is_ok());
        assert!(matches!(friends("me_", &["x".into()]), Err(NameError::Invalid(_))));
    }
}
