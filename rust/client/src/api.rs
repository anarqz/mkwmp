//! Seat API client (docs/contracts/SEAT_API.md).

use serde::Deserialize;
use serde_json::json;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct View {
    pub boot_id: String,
    pub session_id: String,
    pub name: String,
    #[serde(default)]
    pub session_key: Option<String>,
    pub state: String,
    pub position: Option<usize>,
    pub seats_total: usize,
    pub seats_free: usize,
    pub next_poll_ms: u64,
    pub ticket: Option<Ticket>,
}

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Ticket {
    pub token: String,
    pub expires_at: u64,
    pub lobby: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub name: String,
    pub seats_total: usize,
    pub seated: usize,
    pub queued: usize,
    pub lobby_endpoint: Option<String>,
}

/// One friend and what they're doing.
#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
pub struct Friend {
    pub name: String,
    /// "playing" (in the game, seated), "online" (launcher running) or "offline".
    pub state: String,
}

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
pub struct Request {
    pub name: String,
    pub since: u64,
}

/// GET /seat/v1/friends.
#[derive(Debug, Clone, Default, Deserialize, PartialEq, Eq)]
pub struct Friends {
    pub cursor: u64,
    pub friends: Vec<Friend>,
    pub incoming: Vec<Request>,
    pub outgoing: Vec<Request>,
}

#[derive(Debug)]
pub enum ApiError {
    /// The session is gone (expired, or the server restarted): rejoin.
    Gone,
    /// The server refused for a reason the player must fix (name taken...).
    Refused(String),
    /// REGISTER: the name belongs to someone already.
    Taken,
    /// Network or server trouble: retry.
    Transient(String),
    /// The launcher's presence is gone (server restart, new PIN): sign in again.
    SignedOut,
    /// Too many tries: wait this many seconds.
    Limited(u64),
}

impl std::fmt::Display for ApiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ApiError::Gone => f.write_str("session expired"),
            ApiError::Taken => f.write_str("that gametag is already registered"),
            ApiError::Refused(m) | ApiError::Transient(m) => f.write_str(m),
            ApiError::SignedOut => f.write_str("signed out"),
            ApiError::Limited(secs) => write!(f, "too many tries; wait {}", wait_text(*secs)),
        }
    }
}

fn wait_text(secs: u64) -> String {
    if secs < 90 {
        format!("{} s", secs.max(1))
    } else {
        format!("{} min", secs.div_ceil(60))
    }
}

pub struct Api {
    http: reqwest::Client,
    base: String,
}

impl Api {
    pub fn new(base: &str) -> Self {
        let http = reqwest::Client::builder()
            .timeout(std::time::Duration::from_secs(10))
            .user_agent(concat!("mkwmp-launcher/", env!("CARGO_PKG_VERSION")))
            .build()
            .expect("http client");
        Self { http, base: base.to_string() }
    }

    async fn read(res: Result<reqwest::Response, reqwest::Error>) -> Result<View, ApiError> {
        let res = res.map_err(|e| ApiError::Transient(format!("cannot reach the server: {e}")))?;
        let status = res.status();
        if status.is_success() {
            return res.json().await.map_err(|e| ApiError::Transient(format!("bad answer: {e}")));
        }
        let body: serde_json::Value = res.json().await.unwrap_or_default();
        let code = body["error"]["code"].as_str().unwrap_or("");
        // Only a lost session means "rejoin"; a missing profile is the player's to fix.
        if status == reqwest::StatusCode::NOT_FOUND && (code == "SESSION_NOT_FOUND" || code.is_empty()) {
            return Err(ApiError::Gone);
        }
        let msg = match code {
            "PIN_WRONG" => "wrong PIN for this gametag".to_string(),
            "PROFILE_NOT_FOUND" => "this gametag isn't registered: press REGISTER".to_string(),
            _ => body["error"]["message"].as_str().unwrap_or("server error").to_string(),
        };
        if status.is_client_error() {
            Err(ApiError::Refused(msg))
        } else {
            Err(ApiError::Transient(msg))
        }
    }

    /// The server's public status.
    pub async fn status(&self) -> Result<Status, ApiError> {
        let url = format!("{}/seat/v1/status", self.base);
        let res = self.http.get(url).send().await.map_err(|e| ApiError::Transient(format!("cannot reach the server: {e}")))?;
        if !res.status().is_success() {
            return Err(ApiError::Transient(format!("server answered {}", res.status())));
        }
        res.json().await.map_err(|e| ApiError::Transient(format!("not an mkwmp server: {e}")))
    }

    /// Where the game connects: the server's lobby.
    pub async fn lobby_endpoint(&self) -> Result<String, ApiError> {
        self.status()
            .await?
            .lobby_endpoint
            .ok_or_else(|| ApiError::Transient("the server is too old (no lobbyEndpoint)".into()))
    }

    pub async fn join(&self, name: &str, pin: &str) -> Result<View, ApiError> {
        let url = format!("{}/seat/v1/sessions", self.base);
        Self::read(self.http.post(url).json(&json!({"name": name, "pin": pin})).send().await).await
    }

    /// Is `name` registered (or reserved)?
    pub async fn profile(&self, name: &str) -> Result<(bool, bool), ApiError> {
        let url = format!("{}/seat/v1/profiles/{name}", self.base);
        let res = self.http.get(url).send().await.map_err(|e| ApiError::Transient(format!("cannot reach the server: {e}")))?;
        let v: serde_json::Value = res.json().await.map_err(|e| ApiError::Transient(e.to_string()))?;
        Ok((v["registered"].as_bool().unwrap_or(false), v["reserved"].as_bool().unwrap_or(false)))
    }

    /// Delete the profile `name` (needs its PIN). The name becomes free.
    pub async fn delete_profile(&self, name: &str, pin: &str) -> Result<(), ApiError> {
        let url = format!("{}/seat/v1/profiles/{name}", self.base);
        let res = self
            .http
            .delete(url)
            .json(&json!({ "pin": pin }))
            .send()
            .await
            .map_err(|e| ApiError::Transient(format!("cannot reach the server: {e}")))?;
        if res.status().is_success() {
            return Ok(());
        }
        let v: serde_json::Value = res.json().await.unwrap_or_default();
        Err(ApiError::Refused(match v["error"]["code"].as_str() {
            Some("PIN_WRONG") => "wrong PIN for this gametag".into(),
            Some("PROFILE_NOT_FOUND") => "this gametag isn't registered".into(),
            _ => v["error"]["message"].as_str().unwrap_or("could not delete the profile").to_string(),
        }))
    }

    /// Register `name`; returns its PIN.
    pub async fn register(&self, name: &str) -> Result<String, ApiError> {
        let url = format!("{}/seat/v1/profiles", self.base);
        let res = self
            .http
            .post(url)
            .json(&json!({ "name": name }))
            .send()
            .await
            .map_err(|e| ApiError::Transient(format!("cannot reach the server: {e}")))?;
        let ok = res.status().is_success();
        let v: serde_json::Value = res.json().await.unwrap_or_default();
        match (ok, v["pin"].as_str(), v["error"]["code"].as_str()) {
            (true, Some(pin), _) => Ok(pin.to_string()),
            (_, _, Some("NAME_REGISTERED")) => Err(ApiError::Taken),
            _ => Err(ApiError::Refused(v["error"]["message"].as_str().unwrap_or("could not register").to_string())),
        }
    }

    pub async fn poll(&self, sid: &str, key: &str) -> Result<View, ApiError> {
        let url = format!("{}/seat/v1/sessions/{sid}", self.base);
        Self::read(self.http.get(url).bearer_auth(key).send().await).await
    }

    // ---- launcher presence and friends (no seat) -------------------------

    /// A friends-API answer: JSON on success, else a friendly error.
    async fn friendly(res: Result<reqwest::Response, reqwest::Error>) -> Result<serde_json::Value, ApiError> {
        let res = res.map_err(|e| ApiError::Transient(format!("cannot reach the server: {e}")))?;
        let status = res.status();
        if status == reqwest::StatusCode::TOO_MANY_REQUESTS {
            let secs = res
                .headers()
                .get(reqwest::header::RETRY_AFTER)
                .and_then(|v| v.to_str().ok()?.parse().ok())
                .unwrap_or(60);
            return Err(ApiError::Limited(secs));
        }
        if status == reqwest::StatusCode::NO_CONTENT {
            return Ok(serde_json::Value::Null);
        }
        let v: serde_json::Value = res.json().await.unwrap_or_default();
        if status.is_success() {
            return Ok(v);
        }
        let code = v["error"]["code"].as_str().unwrap_or("");
        let msg = v["error"]["message"].as_str().unwrap_or("server error").to_string();
        match code {
            "PRESENCE_NOT_FOUND" => Err(ApiError::SignedOut),
            "PIN_WRONG" => Err(ApiError::Refused("wrong PIN for this gametag".into())),
            "PROFILE_NOT_FOUND" => Err(ApiError::Refused("this gametag isn't registered".into())),
            _ if status.is_client_error() => Err(ApiError::Refused(msg)),
            _ => Err(ApiError::Transient(msg)),
        }
    }

    /// Sign the launcher in (no seat); returns the presence token.
    pub async fn presence(&self, name: &str, pin: &str) -> Result<String, ApiError> {
        let url = format!("{}/seat/v1/presence", self.base);
        let v = Self::friendly(self.http.post(url).json(&json!({"name": name, "pin": pin})).send().await).await?;
        v["token"].as_str().map(str::to_string).ok_or_else(|| ApiError::Transient("bad answer".into()))
    }

    /// The launcher quits: show offline at once.
    pub async fn presence_end(&self, token: &str) {
        let url = format!("{}/seat/v1/presence", self.base);
        let _ = self.http.delete(url).bearer_auth(token).timeout(std::time::Duration::from_secs(3)).send().await;
    }

    /// Friends and requests. With `since` (the last cursor) the server
    /// waits up to 25 s for a change.
    pub async fn friends(&self, token: &str, since: Option<u64>) -> Result<Friends, ApiError> {
        let mut url = format!("{}/seat/v1/friends", self.base);
        if let Some(c) = since {
            url.push_str(&format!("?since={c}"));
        }
        let req = self.http.get(url).bearer_auth(token).timeout(std::time::Duration::from_secs(40));
        let v = Self::friendly(req.send().await).await?;
        serde_json::from_value(v).map_err(|e| ApiError::Transient(format!("bad answer: {e}")))
    }

    /// Ask `name` to be friends; "pending" or "accepted" (they had asked).
    pub async fn friend_request(&self, token: &str, name: &str) -> Result<(String, String), ApiError> {
        let url = format!("{}/seat/v1/friends", self.base);
        let v = Self::friendly(self.http.post(url).bearer_auth(token).json(&json!({ "name": name })).send().await).await?;
        Ok((v["name"].as_str().unwrap_or(name).to_string(), v["state"].as_str().unwrap_or("pending").to_string()))
    }

    pub async fn friend_accept(&self, token: &str, name: &str) -> Result<(), ApiError> {
        let url = format!("{}/seat/v1/friends/{name}/accept", self.base);
        Self::friendly(self.http.post(url).bearer_auth(token).send().await).await.map(|_| ())
    }

    /// Reject, cancel or unfriend.
    pub async fn friend_remove(&self, token: &str, name: &str) -> Result<(), ApiError> {
        let url = format!("{}/seat/v1/friends/{name}", self.base);
        Self::friendly(self.http.delete(url).bearer_auth(token).send().await).await.map(|_| ())
    }

    pub async fn leave(&self, sid: &str, key: &str) {
        let url = format!("{}/seat/v1/sessions/{sid}", self.base);
        let _ = self.http.delete(url).bearer_auth(key).send().await;
    }
}
