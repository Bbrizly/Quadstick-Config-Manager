//! Native Google OAuth for the optional Drive feature.
//!
//! The refresh token stays in the OS credential store, access tokens exist only
//! in this process, and the WebView sees status booleans only. Authentication is
//! an installed-app Authorization Code flow with PKCE S256 and a loopback
//! callback opened in the system browser.

use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use qcm_core::error::{InternalError, OsDetail, QcmError};
use reqwest::Url;
use reqwest::blocking::Client;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::io::{Read, Write};
use std::net::{Ipv4Addr, SocketAddrV4, TcpListener, TcpStream};
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

const AUTH_ENDPOINT: &str = "https://accounts.google.com/o/oauth2/v2/auth";
const TOKEN_ENDPOINT: &str = "https://oauth2.googleapis.com/token";
const DRIVE_FILE_SCOPE: &str = "https://www.googleapis.com/auth/drive.file";
const SERVICE: &str = "QuadStick Config Manager";
const ACCOUNT: &str = "google-drive";
const CALLBACK_TIMEOUT: Duration = Duration::from_secs(120);
const HTTP_TIMEOUT: Duration = Duration::from_secs(20);
const MAX_CALLBACK_BYTES: usize = 16 * 1024;

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GoogleAuthStatusDto {
    pub supported: bool,
    pub configured: bool,
    pub connected: bool,
}

#[derive(Debug, Clone)]
struct AccessToken {
    value: String,
    expires: Instant,
}

#[derive(Debug, Deserialize)]
struct TokenReply {
    access_token: String,
    #[serde(default)]
    expires_in: Option<u64>,
    #[serde(default)]
    refresh_token: Option<String>,
}

#[derive(Debug, Deserialize)]
struct TokenErrorReply {
    #[serde(default)]
    error: String,
}

trait RefreshTokenStore: Send + Sync {
    fn load(&self) -> Result<Option<String>, QcmError>;
    fn save(&self, token: &str) -> Result<(), QcmError>;
    fn delete(&self) -> Result<(), QcmError>;
    fn supported(&self) -> bool;
}

#[derive(Debug, Default)]
struct PlatformTokenStore;

impl PlatformTokenStore {
    #[cfg(any(target_os = "macos", target_os = "windows"))]
    fn entry() -> Result<keyring::Entry, QcmError> {
        keyring::Entry::new(SERVICE, ACCOUNT).map_err(|error| auth_internal("open credential store", error))
    }
}

impl RefreshTokenStore for PlatformTokenStore {
    fn load(&self) -> Result<Option<String>, QcmError> {
        #[cfg(any(target_os = "macos", target_os = "windows"))]
        {
            match Self::entry()?.get_password() {
                Ok(token) => Ok(Some(token)),
                Err(keyring::Error::NoEntry) => Ok(None),
                Err(error) => Err(auth_internal("read Google refresh token", error)),
            }
        }
        #[cfg(not(any(target_os = "macos", target_os = "windows")))]
        {
            Ok(None)
        }
    }

    fn save(&self, token: &str) -> Result<(), QcmError> {
        #[cfg(any(target_os = "macos", target_os = "windows"))]
        {
            Self::entry()?
                .set_password(token)
                .map_err(|error| auth_internal("save Google refresh token", error))
        }
        #[cfg(not(any(target_os = "macos", target_os = "windows")))]
        {
            let _ = token;
            Err(auth_message("secure Google token storage is unavailable on this platform"))
        }
    }

    fn delete(&self) -> Result<(), QcmError> {
        #[cfg(any(target_os = "macos", target_os = "windows"))]
        {
            match Self::entry()?.delete_credential() {
                Ok(()) | Err(keyring::Error::NoEntry) => Ok(()),
                Err(error) => Err(auth_internal("delete Google refresh token", error)),
            }
        }
        #[cfg(not(any(target_os = "macos", target_os = "windows")))]
        {
            Ok(())
        }
    }

    fn supported(&self) -> bool {
        cfg!(any(target_os = "macos", target_os = "windows"))
    }
}

pub struct GoogleAuthService {
    http: Client,
    store: PlatformTokenStore,
    access: Mutex<Option<AccessToken>>,
}

impl GoogleAuthService {
    pub fn native() -> Result<Self, QcmError> {
        let http = Client::builder()
            .timeout(HTTP_TIMEOUT)
            .user_agent("QuadStickConfigManager")
            .build()
            .map_err(|error| auth_internal("build Google HTTP client", error))?;
        Ok(Self {
            http,
            store: PlatformTokenStore,
            access: Mutex::new(None),
        })
    }

    fn access(&self) -> MutexGuard<'_, Option<AccessToken>> {
        self.access.lock().unwrap_or_else(PoisonError::into_inner)
    }

    pub fn status(&self) -> Result<GoogleAuthStatusDto, QcmError> {
        let supported = self.store.supported();
        let configured = supported && client_id().is_some();
        let connected = configured && self.store.load()?.is_some();
        Ok(GoogleAuthStatusDto {
            supported,
            configured,
            connected,
        })
    }

    pub fn disconnect(&self) -> Result<GoogleAuthStatusDto, QcmError> {
        self.store.delete()?;
        *self.access() = None;
        self.status()
    }

    pub fn connect(&self) -> Result<GoogleAuthStatusDto, QcmError> {
        if !self.store.supported() {
            return Err(auth_message("Google Drive is not enabled on this platform"));
        }
        let client_id = client_id().ok_or_else(|| auth_message("Google Drive client is not configured"))?;
        let verifier = create_verifier()?;
        let challenge = challenge(&verifier);
        let state = create_verifier()?;
        let listener = TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0))
            .map_err(|error| auth_internal("bind Google sign-in callback", error))?;
        listener
            .set_nonblocking(true)
            .map_err(|error| auth_internal("configure Google sign-in callback", error))?;
        let port = listener
            .local_addr()
            .map_err(|error| auth_internal("read Google sign-in callback address", error))?
            .port();
        let redirect = format!("http://127.0.0.1:{port}/");
        let url = authorization_url(client_id, &redirect, &state, &challenge)?;
        open::that(url.as_str()).map_err(|error| auth_internal("open Google sign-in browser", error))?;
        let code = await_callback(&listener, &state, CALLBACK_TIMEOUT)?;
        let reply = self.exchange_code(&code, &verifier, &redirect)?;
        let Some(refresh) = reply.refresh_token.as_deref() else {
            return Err(auth_message("Google did not return a refresh token"));
        };
        self.store.save(refresh)?;
        self.cache_access(reply);
        self.status()
    }

    pub fn access_token(&self) -> Result<String, QcmError> {
        if let Some(cached) = self.access().as_ref()
            && cached.expires > Instant::now() + Duration::from_secs(60)
        {
            return Ok(cached.value.clone());
        }
        let client_id = client_id().ok_or_else(|| auth_message("Google Drive client is not configured"))?;
        let refresh = self
            .store
            .load()?
            .ok_or_else(|| auth_message("Not connected to Google"))?;
        let mut form = vec![
            ("client_id", client_id.to_owned()),
            ("refresh_token", refresh),
            ("grant_type", "refresh_token".to_owned()),
        ];
        if let Some(secret) = client_secret() {
            form.push(("client_secret", secret.to_owned()));
        }
        let response = self
            .http
            .post(TOKEN_ENDPOINT)
            .form(&form)
            .send()
            .map_err(|error| auth_internal("refresh Google access token", error))?;
        if !response.status().is_success() {
            let status = response.status();
            let body = response.text().unwrap_or_default();
            let code = serde_json::from_str::<TokenErrorReply>(&body)
                .map(|error| error.error)
                .unwrap_or_default();
            if code == "invalid_grant" {
                self.store.delete()?;
                *self.access() = None;
                return Err(auth_message("The Google connection was revoked or expired"));
            }
            return Err(auth_message(&format!("Google token endpoint returned {status}")));
        }
        let reply = response
            .json::<TokenReply>()
            .map_err(|error| auth_internal("parse Google access token", error))?;
        let value = reply.access_token.clone();
        self.cache_access(reply);
        Ok(value)
    }

    fn exchange_code(&self, code: &str, verifier: &str, redirect: &str) -> Result<TokenReply, QcmError> {
        let client_id = client_id().ok_or_else(|| auth_message("Google Drive client is not configured"))?;
        let mut form = vec![
            ("client_id", client_id.to_owned()),
            ("code", code.to_owned()),
            ("code_verifier", verifier.to_owned()),
            ("grant_type", "authorization_code".to_owned()),
            ("redirect_uri", redirect.to_owned()),
        ];
        if let Some(secret) = client_secret() {
            form.push(("client_secret", secret.to_owned()));
        }
        let response = self
            .http
            .post(TOKEN_ENDPOINT)
            .form(&form)
            .send()
            .map_err(|error| auth_internal("exchange Google authorization code", error))?;
        if !response.status().is_success() {
            return Err(auth_message(&format!(
                "Google token endpoint returned {}",
                response.status()
            )));
        }
        response
            .json::<TokenReply>()
            .map_err(|error| auth_internal("parse Google sign-in token", error))
    }

    fn cache_access(&self, reply: TokenReply) {
        let lifetime = reply.expires_in.unwrap_or(3600);
        *self.access() = Some(AccessToken {
            value: reply.access_token,
            expires: Instant::now() + Duration::from_secs(lifetime),
        });
    }
}

fn client_id() -> Option<&'static str> {
    option_env!("QCM_GOOGLE_CLIENT_ID")
        .filter(|id| !id.trim().is_empty() && !id.starts_with("REPLACE-ME"))
}

fn client_secret() -> Option<&'static str> {
    option_env!("QCM_GOOGLE_CLIENT_SECRET").filter(|secret| !secret.is_empty())
}

pub fn create_verifier() -> Result<String, QcmError> {
    let mut bytes = [0_u8; 32];
    getrandom::fill(&mut bytes).map_err(|error| auth_internal("generate PKCE verifier", error))?;
    Ok(URL_SAFE_NO_PAD.encode(bytes))
}

pub fn challenge(verifier: &str) -> String {
    URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()))
}

fn authorization_url(
    client_id: &str,
    redirect: &str,
    state: &str,
    challenge: &str,
) -> Result<Url, QcmError> {
    let mut url = Url::parse(AUTH_ENDPOINT).map_err(|error| auth_internal("build Google authorization URL", error))?;
    url.query_pairs_mut()
        .append_pair("client_id", client_id)
        .append_pair("redirect_uri", redirect)
        .append_pair("response_type", "code")
        .append_pair("scope", DRIVE_FILE_SCOPE)
        .append_pair("code_challenge", challenge)
        .append_pair("code_challenge_method", "S256")
        .append_pair("state", state)
        .append_pair("access_type", "offline")
        .append_pair("prompt", "consent");
    Ok(url)
}

fn await_callback(listener: &TcpListener, expected_state: &str, timeout: Duration) -> Result<String, QcmError> {
    let deadline = Instant::now() + timeout;
    loop {
        match listener.accept() {
            Ok((mut stream, _)) => return read_callback(&mut stream, expected_state),
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                if Instant::now() >= deadline {
                    return Err(auth_message("Google sign-in timed out"));
                }
                std::thread::sleep(Duration::from_millis(25));
            }
            Err(error) => return Err(auth_internal("accept Google sign-in callback", error)),
        }
    }
}

fn read_callback(stream: &mut TcpStream, expected_state: &str) -> Result<String, QcmError> {
    stream
        .set_read_timeout(Some(Duration::from_secs(5)))
        .map_err(|error| auth_internal("configure Google callback read", error))?;
    let mut request = vec![0_u8; MAX_CALLBACK_BYTES];
    let read = stream
        .read(&mut request)
        .map_err(|error| auth_internal("read Google sign-in callback", error))?;
    request.truncate(read);
    let text = std::str::from_utf8(&request).map_err(|error| auth_internal("decode Google callback", error))?;
    let target = text
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .ok_or_else(|| auth_message("Google callback was malformed"))?;
    let callback = Url::parse(&format!("http://127.0.0.1{target}"))
        .map_err(|error| auth_internal("parse Google callback", error))?;
    let mut state = None;
    let mut code = None;
    let mut oauth_error = None;
    for (key, value) in callback.query_pairs() {
        match key.as_ref() {
            "state" => state = Some(value.into_owned()),
            "code" => code = Some(value.into_owned()),
            "error" => oauth_error = Some(value.into_owned()),
            _ => {}
        }
    }
    let result = if oauth_error.is_some() {
        Err(auth_message("Google sign-in was cancelled or rejected"))
    } else if state.as_deref() != Some(expected_state) {
        Err(auth_message("Google sign-in state did not match"))
    } else {
        code.ok_or_else(|| auth_message("Google sign-in callback had no authorization code"))
    };
    let message = if result.is_ok() {
        "You are signed in. You can close this tab."
    } else {
        "Sign-in failed. You can close this tab."
    };
    let body = format!("<!doctype html><html><body><p>{message}</p></body></html>");
    let response = format!(
        "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        body.len(),
        body
    );
    let _ = stream.write_all(response.as_bytes());
    let _ = stream.flush();
    result
}

fn auth_message(message: &str) -> QcmError {
    QcmError::Internal(InternalError {
        what: "Google authentication",
        detail: OsDetail::new(message),
    })
}

fn auth_internal(what: &'static str, error: impl std::fmt::Display) -> QcmError {
    QcmError::Internal(InternalError {
        what,
        detail: OsDetail::new(error.to_string()),
    })
}

#[cfg(test)]
mod tests {
    use super::{authorization_url, challenge};

    #[test]
    fn pkce_s256_matches_rfc_7636_example() {
        let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        assert_eq!(challenge(verifier), "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    #[test]
    fn auth_url_has_pkce_state_and_drive_file_scope_only() {
        let url = authorization_url("client", "http://127.0.0.1:9999/", "state", "challenge")
            .expect("valid auth URL");
        let values = url.query_pairs().collect::<std::collections::BTreeMap<_, _>>();
        assert_eq!(values.get("scope").map(|v| v.as_ref()), Some(super::DRIVE_FILE_SCOPE));
        assert_eq!(values.get("state").map(|v| v.as_ref()), Some("state"));
        assert_eq!(values.get("code_challenge_method").map(|v| v.as_ref()), Some("S256"));
        assert_eq!(values.get("access_type").map(|v| v.as_ref()), Some("offline"));
    }
}
