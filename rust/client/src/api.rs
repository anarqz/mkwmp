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
    #[serde(default)]
    pub friends: Vec<Friend>,
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

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
pub struct Friend {
    pub name: String,
    pub online: bool,
    pub mutual: bool,
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
}

impl std::fmt::Display for ApiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ApiError::Gone => f.write_str("session expired"),
            ApiError::Taken => f.write_str("that name is already registered"),
            ApiError::Refused(m) | ApiError::Transient(m) => f.write_str(m),
        }
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
            "PIN_WRONG" => "wrong PIN for this name".to_string(),
            "PROFILE_NOT_FOUND" => "this name isn't registered: press REGISTER".to_string(),
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

    pub async fn join(&self, name: &str, pin: &str, friends: &[String]) -> Result<View, ApiError> {
        let url = format!("{}/seat/v1/sessions", self.base);
        Self::read(self.http.post(url).json(&json!({"name": name, "pin": pin, "friends": friends})).send().await).await
    }

    /// Is `name` registered (or reserved)?
    pub async fn profile(&self, name: &str) -> Result<(bool, bool), ApiError> {
        let url = format!("{}/seat/v1/profiles/{name}", self.base);
        let res = self.http.get(url).send().await.map_err(|e| ApiError::Transient(format!("cannot reach the server: {e}")))?;
        let v: serde_json::Value = res.json().await.map_err(|e| ApiError::Transient(e.to_string()))?;
        Ok((v["registered"].as_bool().unwrap_or(false), v["reserved"].as_bool().unwrap_or(false)))
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

    pub async fn set_friends(&self, sid: &str, key: &str, friends: &[String]) -> Result<View, ApiError> {
        let url = format!("{}/seat/v1/sessions/{sid}/friends", self.base);
        Self::read(self.http.put(url).bearer_auth(key).json(&json!({"friends": friends})).send().await).await
    }

    pub async fn leave(&self, sid: &str, key: &str) {
        let url = format!("{}/seat/v1/sessions/{sid}", self.base);
        let _ = self.http.delete(url).bearer_auth(key).send().await;
    }
}
