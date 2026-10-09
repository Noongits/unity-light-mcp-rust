//! Unity's legacy framed TCP transport and persistent local WebSocket hub.
use crate::bridge::{next_tracking_id, UnityBridge};
use anyhow::{anyhow, bail, Context, Result};
use async_trait::async_trait;
use axum::extract::ws::{Message, WebSocket};
use futures_util::SinkExt;
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{Arc, Mutex},
    time::{Duration, SystemTime},
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::TcpStream,
    sync::{mpsc, oneshot, watch, Mutex as AsyncMutex},
    time::{timeout, Instant},
};
use uuid::Uuid;

const MAX_FRAME: u64 = 64 * 1024 * 1024;
fn retry(reason: &str) -> Value {
    json!({"success":false,"error":reason,"hint":"retry","data":{"retry_after_ms":250}})
}

fn heartbeat_time(data: &Value, fallback: SystemTime) -> SystemTime {
    data["last_heartbeat"]
        .as_str()
        .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
        .and_then(|t| {
            let millis = t.timestamp_millis();
            if millis >= 0 {
                SystemTime::UNIX_EPOCH.checked_add(Duration::from_millis(millis as u64))
            } else {
                SystemTime::UNIX_EPOCH.checked_sub(Duration::from_millis(millis.unsigned_abs()))
            }
        })
        .unwrap_or(fallback)
}
fn safe_reload_response(value: &Value) -> bool {
    value["success"] == false
        && (value["executed"] == false || value["data"]["executed"] == false)
        && (value["state"] == "reloading" || value["data"]["reason"] == "reloading")
}
fn reload_response() -> Value {
    json!({"success":false,"error":"Unity is reloading; please retry","hint":"retry","data":{"reason":"reloading","executed":false,"retry_after_ms":250}})
}
fn bounded_env(name: &str, default: f64, max: f64) -> Duration {
    Duration::from_secs_f64(
        std::env::var(name)
            .ok()
            .and_then(|s| s.parse::<f64>().ok())
            .filter(|n| n.is_finite())
            .unwrap_or(default)
            .clamp(0.0, max),
    )
}
fn positive_seconds(raw: Option<&str>, default: f64) -> Duration {
    let seconds = raw
        .and_then(|s| s.parse::<f64>().ok())
        .filter(|n| n.is_finite() && *n > 0.0 && *n < u64::MAX as f64)
        .unwrap_or(default);
    Duration::from_secs_f64(seconds)
}
fn positive_env(name: &str, default: f64) -> Duration {
    positive_seconds(std::env::var(name).ok().as_deref(), default)
}
fn custom_definitions(response: &Value) -> Vec<Value> {
    response
        .pointer("/data/tools")
        .or_else(|| response.get("data").filter(|d| d.is_array()))
        .or_else(|| response.get("tools"))
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|t| t["is_built_in"] == false && t["enabled"] != false && t["name"].is_string())
        .cloned()
        .collect()
}

fn fast(command: &str) -> bool {
    matches!(command, "ping" | "read_console" | "get_editor_state")
}
fn budgets(command: &str, params: &Value) -> (Duration, Duration) {
    if fast(command) {
        return (Duration::from_secs(2), Duration::from_secs(2));
    }
    let requested = params
        .get("timeout_seconds")
        .or_else(|| params.get("timeoutSeconds"));
    let number = requested.and_then(|v| {
        v.as_f64()
            .or_else(|| v.as_str().and_then(|s| s.trim().parse().ok()))
    });
    let (unity, server) = if command == "blender_bridge" {
        let n = number
            .filter(|n| {
                n.is_finite() && n.fract() == 0.0 && *n >= i32::MIN as f64 && *n <= i32::MAX as f64
            })
            .unwrap_or(180.0)
            .clamp(5.0, 3600.0)
            + 30.0;
        (n, n + 5.0)
    } else if let Some(n) = number.filter(|n| n.is_finite()) {
        let n = n.clamp(1.0, 3600.0);
        (n.max(30.0), (n + 5.0).max(30.0))
    } else {
        (30.0, 30.0)
    };
    (
        Duration::from_secs_f64(unity),
        Duration::from_secs_f64(server),
    )
}
fn select<'a>(items: &'a [Value], identifier: Option<&str>) -> Result<&'a Value> {
    let ids: Vec<_> = items.iter().filter_map(|i| i["id"].as_str()).collect();
    let Some(identifier) = identifier.map(str::trim).filter(|s| !s.is_empty()) else {
        return match items { [item] => Ok(item), [] => bail!("No Unity Editor instances found"), _ => bail!("Multiple Unity instances are connected. Call set_active_instance with one of: {ids:?}") };
    };
    let exact: Vec<_> = items
        .iter()
        .filter(|i| i["id"].as_str() == Some(identifier))
        .collect();
    if let [item] = exact.as_slice() {
        return Ok(item);
    }
    let matched: Vec<_> = items
        .iter()
        .filter(|i| {
            let hash = i["hash"].as_str().unwrap_or("");
            i["name"].as_str() == Some(identifier)
                || (!hash.is_empty() && hash.starts_with(identifier))
                || i["path"].as_str() == Some(identifier)
                || i["port"]
                    .as_u64()
                    .is_some_and(|p| p.to_string() == identifier)
                || identifier.rsplit_once('@').is_some_and(|(name, suffix)| {
                    !suffix.is_empty()
                        && i["name"].as_str() == Some(name)
                        && (hash.starts_with(suffix)
                            || i["port"].as_u64().is_some_and(|p| p.to_string() == suffix))
                })
        })
        .collect();
    match matched.as_slice() {
        [item] => Ok(item),
        [] => bail!("Unity instance '{identifier}' not found. Available instances: {ids:?}"),
        _ => bail!("Unity instance '{identifier}' matches multiple instances: {ids:?}"),
    }
}
async fn connect(port: u16) -> Result<TcpStream> {
    let mut stream = TcpStream::connect(("127.0.0.1", port)).await?;
    stream.set_nodelay(true)?;
    let mut header = Vec::new();
    loop {
        let byte = stream.read_u8().await?;
        header.push(byte);
        if byte == b'\n' {
            break;
        }
        if header.len() >= 512 {
            bail!("Unity handshake exceeds 512 bytes");
        }
    }
    let header = std::str::from_utf8(&header)?;
    if !(header.starts_with("MCP/0.1 ") || header.starts_with("WELCOME UNITY-MCP 1 "))
        || !header.split_whitespace().any(|s| s == "FRAMING=1")
    {
        bail!("Unity requires a recognized FRAMING=1 handshake");
    }
    Ok(stream)
}
async fn read_frame(stream: &mut TcpStream) -> Result<Vec<u8>> {
    let start = Instant::now();
    for heartbeat_count in 0..16 {
        let length = if heartbeat_count == 0 {
            stream.read_u64().await?
        } else {
            timeout(
                Duration::from_secs(2).saturating_sub(start.elapsed()),
                stream.read_u64(),
            )
            .await
            .context("Unity heartbeat deadline exceeded")??
        };
        if length == 0 {
            if start.elapsed() > Duration::from_secs(2) {
                bail!("Unity heartbeat deadline exceeded");
            }
            continue;
        }
        if length > MAX_FRAME {
            bail!("Unity frame exceeds 64 MiB limit");
        }
        let mut body = vec![0; length as usize];
        stream.read_exact(&mut body).await?;
        return Ok(body);
    }
    bail!("Unity sent too many heartbeat frames without a payload")
}
fn unwrap_response(value: Value) -> Result<Value> {
    if value["status"] == "error" {
        bail!(
            "{}",
            value
                .get("error")
                .or_else(|| value.get("message"))
                .unwrap_or(&json!("Unity command failed"))
        );
    }
    if value["status"] == "success" {
        Ok(value.get("result").cloned().unwrap_or_else(|| json!({})))
    } else {
        Ok(value)
    }
}
type Connection = Arc<AsyncMutex<Option<TcpStream>>>;
const TOOL_CACHE_TTL: Duration = Duration::from_secs(5);
const TOOL_CACHE_CAPACITY: usize = 64;
const IDLE_CONNECTION_CAPACITY: usize = 64;
const HUB_PENDING_CAPACITY: usize = 128;
const SOCKET_WRITE_TIMEOUT: Duration = Duration::from_secs(5);
struct CachedTools {
    instance: String,
    fetched: Instant,
    definitions: Vec<Value>,
}
pub struct TcpBridge {
    tracking_id: u64,
    status_dir: PathBuf,
    default_instance: Option<String>,
    connections: Mutex<HashMap<u16, Connection>>,
    tool_cache: Mutex<HashMap<u16, CachedTools>>,
}
impl TcpBridge {
    pub fn new(status_dir: Option<PathBuf>, default_instance: Option<String>) -> Self {
        Self {
            tracking_id: next_tracking_id(),
            status_dir: status_dir
                .or_else(|| std::env::var_os("UNITY_MCP_STATUS_DIR").map(PathBuf::from))
                .unwrap_or_else(|| {
                    PathBuf::from(
                        std::env::var_os("HOME")
                            .or_else(|| std::env::var_os("USERPROFILE"))
                            .unwrap_or_default(),
                    )
                    .join(".unity-mcp")
                }),
            default_instance,
            connections: Mutex::new(HashMap::new()),
            tool_cache: Mutex::new(HashMap::new()),
        }
    }
    fn connection(&self, port: u16) -> Connection {
        let mut connections = self.connections.lock().unwrap();
        let target_size = IDLE_CONNECTION_CAPACITY - usize::from(!connections.contains_key(&port));
        // The map owns idle sockets. Never evict a socket borrowed by an active
        // discovery/command: doing so could open a second stream for that port.
        while connections.len() > target_size {
            let idle = connections
                .iter()
                .find(|(key, connection)| **key != port && Arc::strong_count(connection) == 1)
                .map(|(port, _)| *port);
            let Some(idle) = idle else {
                break;
            };
            connections.remove(&idle);
        }
        if let Some(connection) = connections.get(&port) {
            return connection.clone();
        }
        let connection = Arc::new(AsyncMutex::new(None));
        connections.insert(port, connection.clone());
        connection
    }
    fn cache_tools(&self, port: u16, instance: String, response: &Value) {
        let mut cache = self.tool_cache.lock().unwrap();
        cache.retain(|_, entry| entry.fetched.elapsed() < TOOL_CACHE_TTL);
        if cache.len() >= TOOL_CACHE_CAPACITY && !cache.contains_key(&port) {
            if let Some(oldest) = cache
                .iter()
                .min_by_key(|(_, entry)| entry.fetched)
                .map(|(port, _)| *port)
            {
                cache.remove(&oldest);
            }
        }
        cache.insert(
            port,
            CachedTools {
                instance,
                fetched: Instant::now(),
                definitions: custom_definitions(response),
            },
        );
    }
    async fn discover(&self) -> Result<Vec<Value>> {
        self.tool_cache
            .lock()
            .unwrap()
            .retain(|_, entry| entry.fetched.elapsed() < TOOL_CACHE_TTL);
        let mut entries = Vec::new();
        let mut dir = match tokio::fs::read_dir(&self.status_dir).await {
            Ok(dir) => dir,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(vec![]),
            Err(e) => return Err(e.into()),
        };
        while let Some(entry) = dir.next_entry().await? {
            let name = entry.file_name().to_string_lossy().into_owned();
            if !(name.starts_with("unity-mcp-status-") || name.starts_with("unity-mcp-port"))
                || !name.ends_with(".json")
            {
                continue;
            }
            let Ok(bytes) = tokio::fs::read(entry.path()).await else {
                continue;
            };
            let Ok(data) = serde_json::from_slice::<Value>(&bytes) else {
                continue;
            };
            let Some(port) = data["unity_port"]
                .as_u64()
                .filter(|p| *p > 0 && *p <= 65535)
            else {
                continue;
            };
            let modified = entry
                .metadata()
                .await
                .ok()
                .and_then(|m| m.modified().ok())
                .unwrap_or(SystemTime::UNIX_EPOCH);
            entries.push((
                !name.starts_with("unity-mcp-status-"),
                std::cmp::Reverse(heartbeat_time(&data, modified)),
                name,
                port as u16,
                data,
            ));
        }
        entries.sort_by(|a, b| (&a.0, &a.1).cmp(&(&b.0, &b.1)));
        let mut result = Vec::new();
        for (_, modified, name, port, data) in entries {
            if result.iter().any(|i: &Value| i["port"] == port) {
                continue;
            }
            let reload = data["reloading"] == true
                && modified.0.elapsed().unwrap_or_default() < Duration::from_secs(60);
            let connection = self.connection(port);
            let alive = if let Ok(mut slot) = connection.try_lock() {
                if slot.is_some() {
                    true
                } else {
                    match timeout(Duration::from_millis(300), connect(port)).await {
                        Ok(Ok(stream)) => {
                            self.tool_cache.lock().unwrap().remove(&port);
                            *slot = Some(stream);
                            true
                        }
                        _ => false,
                    }
                }
            } else {
                true
            };
            if !alive && !reload {
                continue;
            }
            let hash = data["project_hash"]
                .as_str()
                .or_else(|| {
                    name.strip_prefix("unity-mcp-status-")
                        .or_else(|| name.strip_prefix("unity-mcp-port-"))
                        .and_then(|s| s.strip_suffix(".json"))
                })
                .unwrap_or("legacy");
            let path = data["project_path"].as_str().unwrap_or("");
            let clean_path = path
                .trim_end_matches(['/', '\\'])
                .trim_end_matches("Assets")
                .trim_end_matches(['/', '\\']);
            let project = data["project_name"]
                .as_str()
                .or_else(|| clean_path.rsplit(['/', '\\']).find(|s| !s.is_empty()))
                .unwrap_or("Unknown");
            result.push(json!({"id":format!("{project}@{hash}"),"name":project,"hash":hash,"path":path,"port":port,"status":if reload {"reloading"} else {"running"},"last_heartbeat":data["last_heartbeat"],"unity_version":data["unity_version"],"project_scoped_tools":data["project_scoped_tools"].as_bool().unwrap_or(false)}));
        }
        Ok(result)
    }
    pub async fn tools(&self, instance: Option<&str>) -> Result<Vec<Value>> {
        // Metadata listing must never enter the command/reload retry loop. Keep
        // the legacy default-port probe, but fail immediately on connection refusal.
        let response = timeout(
            Duration::from_secs(2),
            self.send_inner("get_tool_states", json!({}), instance, true),
        )
        .await
        .context("Unity tool discovery deadline exceeded")??;
        Ok(custom_definitions(&response))
    }
    async fn send_inner(
        &self,
        command: &str,
        params: Value,
        instance: Option<&str>,
        listing: bool,
    ) -> Result<Value> {
        let target = instance.or(self.default_instance.as_deref());
        let mut items = self.discover().await?;
        if items.is_empty() && target.is_none() {
            items.push(json!({"id":"Unknown@legacy","name":"Unknown","hash":"legacy","port":6400,"status":"running"}));
        }
        let mut item = select(&items, target)?.clone();
        let pinned = item["id"].as_str().unwrap().to_owned();
        let payload = if command == "ping" {
            b"ping".to_vec()
        } else {
            serde_json::to_vec(&json!({"type":command,"params":params}))?
        };
        let attempts = if listing { 1 } else { 3 };
        for attempt in 0..attempts {
            if item["status"] == "reloading" {
                if let Some(port) = item["port"].as_u64() {
                    self.tool_cache.lock().unwrap().remove(&(port as u16));
                    *self.connection(port as u16).lock().await = None;
                }
                let mut response = reload_response();
                response["unity_instance"] = json!(pinned);
                return Ok(response);
            }
            let port = item["port"]
                .as_u64()
                .context("Unity instance is missing port")? as u16;
            let connection = self.connection(port);
            let mut slot = connection.lock().await;
            if listing && slot.is_some() {
                if let Some(cached) = self.tool_cache.lock().unwrap().get(&port) {
                    if cached.instance == pinned && cached.fetched.elapsed() < TOOL_CACHE_TTL {
                        return Ok(json!({"data":{"tools":cached.definitions}}));
                    }
                }
            }
            // Invalidate before I/O, including cancellation, disconnect and reload.
            self.tool_cache.lock().unwrap().remove(&port);
            // Taking the socket out means cancellation always discards a partially consumed stream.
            let mut stream = match slot.take() {
                Some(stream) => stream,
                None => match timeout(
                    positive_env("UNITY_MCP_CONNECTION_TIMEOUT", 300.0),
                    connect(port),
                )
                .await
                {
                    Ok(Ok(stream)) => stream,
                    _ if attempt + 1 < attempts => {
                        drop(slot);
                        tokio::time::sleep(Duration::from_millis(250)).await;
                        if let Ok(found) = select(&self.discover().await?, Some(&pinned)) {
                            item = found.clone();
                        }
                        continue;
                    }
                    _ => bail!("Unable to connect to Unity on port {port}"),
                },
            };
            // Never replay a command after bytes may have reached Unity: mutation outcome is unknown.
            let connection_timeout = positive_env("UNITY_MCP_CONNECTION_TIMEOUT", 300.0);
            timeout(connection_timeout, async {
                stream.write_u64(payload.len() as u64).await?;
                stream.write_all(&payload).await
            })
            .await
            .context("Unity write timeout exceeded")??;
            let raw = timeout(connection_timeout, read_frame(&mut stream))
                .await
                .context("Unity connection read timeout exceeded")??;
            let response = serde_json::from_slice(&raw)?;
            *slot = Some(stream);
            let mut response = unwrap_response(response)?;
            if command == "get_tool_states" && response["success"] != false {
                self.cache_tools(port, pinned.clone(), &response);
            }
            if safe_reload_response(&response) {
                response["unity_instance"] = json!(pinned);
            }
            return Ok(response);
        }
        bail!("Unity unavailable")
    }
}
#[async_trait]
impl UnityBridge for TcpBridge {
    fn tracking_id(&self) -> u64 {
        self.tracking_id
    }
    async fn send(&self, command: &str, params: Value, instance: Option<&str>) -> Result<Value> {
        let wait = positive_env("UNITY_MCP_COMMAND_TOTAL_TIMEOUT", 600.0);
        let deadline = Instant::now() + wait;
        let mut reload_deadline = None;
        let mut last_reload = None;
        let mut pinned = instance
            .or(self.default_instance.as_deref())
            .map(str::to_owned);
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return last_reload.ok_or_else(|| anyhow!("Unity command deadline exceeded"));
            }
            match timeout(
                remaining,
                self.send_inner(command, params.clone(), pinned.as_deref(), false),
            )
            .await
            {
                Ok(Ok(value)) if safe_reload_response(&value) => {
                    let reload_until = *reload_deadline.get_or_insert_with(|| {
                        Instant::now() + bounded_env("UNITY_MCP_RELOAD_MAX_WAIT_S", 20.0, 20.0)
                    });
                    if Instant::now() >= reload_until {
                        return Ok(value);
                    }
                    if let Some(id) = value["unity_instance"].as_str() {
                        pinned = Some(id.to_owned());
                    }
                    last_reload = Some(value);
                }
                Ok(result) => return result,
                Err(_) => {
                    return last_reload.ok_or_else(|| anyhow!("Unity command deadline exceeded"))
                }
            }
            tokio::time::sleep(
                Duration::from_millis(250).min(deadline.saturating_duration_since(Instant::now())),
            )
            .await;
        }
    }
    async fn instances(&self) -> Result<Value> {
        Ok(json!({"instances":self.discover().await?}))
    }
    async fn custom_tools(&self, instance: Option<&str>) -> Result<Vec<Value>> {
        self.tools(instance).await
    }
}

struct Session {
    info: Value,
    outgoing: mpsc::Sender<Message>,
    tools: Vec<Value>,
}
struct Pending {
    session: String,
    response: oneshot::Sender<Result<Value>>,
}
#[derive(Default)]
struct HubState {
    sessions: HashMap<String, Session>,
    pending: HashMap<String, Pending>,
}
pub struct HubBridge {
    tracking_id: u64,
    state: Arc<Mutex<HubState>>,
    shutdown: watch::Sender<bool>,
}
impl Default for HubBridge {
    fn default() -> Self {
        Self {
            tracking_id: next_tracking_id(),
            state: Arc::new(Mutex::new(HubState::default())),
            shutdown: watch::channel(false).0,
        }
    }
}
struct PendingGuard {
    state: Arc<Mutex<HubState>>,
    id: String,
}
impl Drop for PendingGuard {
    fn drop(&mut self) {
        self.state.lock().unwrap().pending.remove(&self.id);
    }
}
struct SessionGuard {
    hub: Arc<HubBridge>,
    id: String,
}
impl Drop for SessionGuard {
    fn drop(&mut self) {
        self.hub.disconnect(&self.id);
    }
}
async fn send_socket<S>(socket: &mut S, message: Message) -> Result<()>
where
    S: futures_util::Sink<Message> + Unpin,
    S::Error: std::fmt::Display,
{
    timeout(SOCKET_WRITE_TIMEOUT, socket.send(message))
        .await
        .context("Unity WebSocket write deadline exceeded")?
        .map_err(|error| anyhow!("Unity WebSocket write failed: {error}"))
}
impl HubBridge {
    // Only unsatisfied commands owned by this live connection may leave its
    // queue. PendingGuard removes cancelled/timed-out entries before dequeue.
    fn queued_message_is_live(&self, session: Option<&str>, message: &Message) -> bool {
        let Message::Text(text) = message else {
            return true;
        };
        let Ok(data) = serde_json::from_str::<Value>(text) else {
            return true;
        };
        if data["type"] != "execute" {
            return true;
        }
        let state = self.state.lock().unwrap();
        session.is_some_and(|session| {
            state.sessions.contains_key(session)
                && data["id"]
                    .as_str()
                    .and_then(|id| state.pending.get(id))
                    .is_some_and(|pending| {
                        pending.session == session && !pending.response.is_closed()
                    })
        })
    }
    pub fn new() -> Self {
        Self::default()
    }
    pub fn shutdown(&self) {
        let mut state = self.state.lock().unwrap();
        self.shutdown.send_replace(true);
        state.sessions.clear();
        for (_, pending) in state.pending.drain() {
            let _ = pending
                .response
                .send(Ok(retry("Unity hub is shutting down")));
        }
    }
    fn disconnect(&self, id: &str) {
        let mut state = self.state.lock().unwrap();
        if let Some(session) = state.sessions.remove(id) {
            let _ = session.outgoing.try_send(Message::Close(None));
        }
        let ids: Vec<_> = state
            .pending
            .iter()
            .filter(|(_, p)| p.session == id)
            .map(|(id, _)| id.clone())
            .collect();
        for id in ids {
            if let Some(pending) = state.pending.remove(&id) {
                let _ = pending.response.send(Ok(retry(
                    "Unity plugin disconnected while awaiting command_result",
                )));
            }
        }
    }
    fn register(&self, data: &Value, outgoing: mpsc::Sender<Message>) -> Result<String> {
        let hash = data["project_hash"]
            .as_str()
            .filter(|s| !s.is_empty())
            .context("Registration requires project_hash")?;
        let name = data["project_name"].as_str().unwrap_or("Unknown Project");
        let id = Uuid::new_v4().to_string();
        let mut state = self.state.lock().unwrap();
        if *self.shutdown.borrow() {
            bail!("Unity hub is shutting down");
        }
        let old: Vec<_> = state
            .sessions
            .iter()
            .filter(|(_, s)| s.info["hash"] == hash)
            .map(|(id, _)| id.clone())
            .collect();
        for old in old {
            if let Some(session) = state.sessions.remove(&old) {
                let _ = session.outgoing.try_send(Message::Close(None));
            }
            let pending: Vec<_> = state
                .pending
                .iter()
                .filter(|(_, p)| p.session == old)
                .map(|(id, _)| id.clone())
                .collect();
            for key in pending {
                if let Some(p) = state.pending.remove(&key) {
                    let _ = p
                        .response
                        .send(Ok(retry("Unity plugin reconnected; retry command")));
                }
            }
        }
        state.sessions.insert(id.clone(), Session { info: json!({"id":format!("{name}@{hash}"),"name":name,"hash":hash,"path":data["project_path"],"unity_version":data["unity_version"],"status":"running","session_id":id}), outgoing, tools:vec![] });
        Ok(id)
    }
    fn receive(&self, session_id: &str, data: Value) {
        let mut state = self.state.lock().unwrap();
        if !state.sessions.contains_key(session_id) {
            return;
        }
        match data["type"].as_str() {
            Some("register_tools") => {
                if let Some(tools) = data["tools"].as_array() {
                    state.sessions.get_mut(session_id).unwrap().tools = tools
                        .iter()
                        .filter(|t| t["name"].as_str().is_some())
                        .cloned()
                        .collect();
                }
            }
            Some("command_result") => {
                if let Some(id) = data["id"].as_str() {
                    // A socket can only satisfy commands actually addressed to that socket.
                    if state
                        .pending
                        .get(id)
                        .is_some_and(|p| p.session == session_id)
                    {
                        let pending = state.pending.remove(id).unwrap();
                        let _ = pending.response.send(unwrap_response(
                            data.get("result").cloned().unwrap_or_else(|| json!({})),
                        ));
                    }
                }
            }
            _ => {}
        }
    }
    pub async fn handle_socket(self: Arc<Self>, socket: WebSocket) {
        let mut shutdown = self.shutdown.subscribe();
        if *shutdown.borrow() {
            return;
        }
        // Cancellation drops both socket and SessionGuard, even if a write is
        // backpressured or this peer has not registered a session yet.
        tokio::select! {
            _ = shutdown.changed() => {},
            _ = self.clone().run_socket(socket) => {},
        }
    }
    async fn run_socket(self: Arc<Self>, mut socket: WebSocket) {
        if send_socket(
            &mut socket,
            Message::Text(
                json!({"type":"welcome","serverTimeout":30,"keepAliveInterval":15})
                    .to_string()
                    .into(),
            ),
        )
        .await
        .is_err()
        {
            return;
        }
        let (tx, mut rx) = mpsc::channel(128);
        let mut guard: Option<SessionGuard> = None;
        let mut ping = tokio::time::interval(Duration::from_secs(10));
        ping.tick().await;
        let mut last_pong = Instant::now();
        loop {
            if guard
                .as_ref()
                .is_some_and(|g| !self.state.lock().unwrap().sessions.contains_key(&g.id))
            {
                let _ = timeout(Duration::from_secs(5), socket.send(Message::Close(None))).await;
                break;
            }
            tokio::select! {
                message = rx.recv() => match message {
                    Some(message) => { if !self.queued_message_is_live(guard.as_ref().map(|g| g.id.as_str()), &message) { continue; } let close = matches!(message, Message::Close(_)); if !matches!(send_socket(&mut socket, message).await, Ok(())) || close { break; } },
                    None => break,
                },
                message = socket.recv() => match message {
                    Some(Ok(Message::Text(text))) => {
                        let Ok(data) = serde_json::from_str::<Value>(&text) else { continue; };
                        match data["type"].as_str() {
                            Some("register") if guard.is_none() => {
                                match self.register(&data, tx.clone()) {
                                    Ok(id) => {
                                        let response = json!({"type":"registered","session_id":id});
                                        guard = Some(SessionGuard { hub:self.clone(), id });
                                        if send_socket(&mut socket, Message::Text(response.to_string().into())).await.is_err() { break; }
                                    },
                                    Err(_) => break,
                                }
                            },
                            Some("pong") => { if let Some(g) = &guard { if data["session_id"].is_null() || data["session_id"] == g.id { last_pong = Instant::now(); } } },
                            Some("ping") => { if send_socket(&mut socket, Message::Text(json!({"type":"pong"}).to_string().into())).await.is_err() { break; } },
                            _ => if let Some(g) = &guard { self.receive(&g.id, data); },
                        }
                    },
                    Some(Ok(Message::Ping(bytes))) => { if send_socket(&mut socket, Message::Pong(bytes)).await.is_err() { break; } },
                    Some(Ok(Message::Pong(_))) => last_pong = Instant::now(),
                    Some(Ok(Message::Close(frame))) => {
                        let _ = timeout(Duration::from_secs(5), socket.send(Message::Close(frame))).await;
                        let _ = timeout(Duration::from_secs(5), socket.flush()).await;
                        break;
                    },
                    None | Some(Err(_)) => break,
                    _ => {},
                },
                _ = ping.tick() => {
                    if last_pong.elapsed() >= Duration::from_secs(20) { break; }
                    if send_socket(&mut socket, Message::Text(json!({"type":"ping"}).to_string().into())).await.is_err() { break; }
                },
            }
        }
    }
    async fn resolve(&self, instance: Option<&str>, wait: Duration) -> Result<String> {
        let deadline = Instant::now() + wait;
        loop {
            {
                let state = self.state.lock().unwrap();
                if *self.shutdown.borrow() {
                    bail!("Unity hub is shutting down");
                }
                let infos: Vec<_> = state.sessions.values().map(|s| s.info.clone()).collect();
                if !infos.is_empty() {
                    match select(&infos, instance) {
                        Ok(info) => return Ok(info["session_id"].as_str().unwrap().to_owned()),
                        Err(err)
                            if instance.is_none()
                                || err.to_string().contains("matches multiple")
                                || infos
                                    .iter()
                                    .filter(|i| i["name"].as_str() == instance)
                                    .count()
                                    > 1 =>
                        {
                            return Err(err)
                        }
                        _ => {}
                    }
                }
            }
            if Instant::now() >= deadline {
                bail!("No matching Unity plugin is connected");
            }
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
    }
    pub async fn tools(&self, instance: Option<&str>) -> Result<Vec<Value>> {
        let id = self.resolve(instance, Duration::ZERO).await?;
        Ok(self
            .state
            .lock()
            .unwrap()
            .sessions
            .get(&id)
            .map(|s| s.tools.clone())
            .unwrap_or_default())
    }
}
impl HubBridge {
    async fn dispatch(
        &self,
        command: &str,
        params: Value,
        instance: Option<&str>,
    ) -> Result<Value> {
        let (unity_timeout, wait) = budgets(command, &params);
        let resolve_wait = std::env::var("UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S")
            .ok()
            .and_then(|s| s.parse::<f64>().ok())
            .filter(|n| n.is_finite())
            .unwrap_or(20.0)
            .clamp(0.0, 120.0);
        let session = match self
            .resolve(instance, Duration::from_secs_f64(resolve_wait))
            .await
        {
            Ok(id) => id,
            Err(error) if error.to_string().starts_with("No matching") => {
                return Ok(retry("Unity session not available; please retry"))
            }
            Err(error) => return Err(error),
        };
        let id = Uuid::new_v4().to_string();
        let (tx, rx) = oneshot::channel();
        let _guard = PendingGuard {
            state: self.state.clone(),
            id: id.clone(),
        };
        let pinned;
        {
            let mut state = self.state.lock().unwrap();
            pinned = state
                .sessions
                .get(&session)
                .context("Unity disconnected before dispatch")?
                .info["id"]
                .clone();
            let outgoing = state
                .sessions
                .get(&session)
                .context("Unity disconnected before dispatch")?
                .outgoing
                .clone();
            if state
                .pending
                .values()
                .filter(|pending| pending.session == session)
                .count()
                >= HUB_PENDING_CAPACITY
            {
                bail!("Unity command queue unavailable: too many pending commands");
            }
            state.pending.insert(
                id.clone(),
                Pending {
                    session,
                    response: tx,
                },
            );
            outgoing.try_send(Message::Text(json!({"type":"execute","id":id,"name":command,"params":params,"timeout":unity_timeout.as_secs_f64()}).to_string().into())).map_err(|error| anyhow!("Unity command queue unavailable: {error}"))?;
        }
        match timeout(wait, rx).await {
            Ok(Ok(result)) => {
                let mut value = result?;
                if safe_reload_response(&value) {
                    value["unity_instance"] = pinned;
                }
                Ok(value)
            }
            Ok(Err(_)) => Ok(retry("Unity disconnected while awaiting response")),
            Err(_) if fast(command) => Ok(retry(&format!(
                "Unity did not respond to '{command}' within {:.1}s; please retry",
                wait.as_secs_f64()
            ))),
            Err(_) => bail!(
                "Unity command '{command}' timed out after {:.1}s",
                wait.as_secs_f64()
            ),
        }
    }
}
#[async_trait]
impl UnityBridge for HubBridge {
    fn tracking_id(&self) -> u64 {
        self.tracking_id
    }
    async fn send(&self, command: &str, params: Value, instance: Option<&str>) -> Result<Value> {
        if fast(command) && command != "ping" {
            let readiness = bounded_env("UNITY_MCP_SESSION_READY_WAIT_SECONDS", 6.0, 120.0);
            if !readiness.is_zero() {
                let deadline = Instant::now() + readiness;
                loop {
                    let remaining = deadline.saturating_duration_since(Instant::now());
                    match timeout(remaining, self.dispatch("ping", json!({}), instance)).await {
                        Ok(Ok(value)) if value["message"] == "pong" => break,
                        Ok(Err(error))
                            if error.to_string().contains("Multiple Unity")
                                || error.to_string().contains("matches multiple") =>
                        {
                            return Err(error)
                        }
                        _ => {}
                    }
                    if Instant::now() >= deadline {
                        return Ok(retry(&format!("Unity session not ready for '{command}' (ping not answered); please retry")));
                    }
                    tokio::time::sleep(
                        Duration::from_millis(100)
                            .min(deadline.saturating_duration_since(Instant::now())),
                    )
                    .await;
                }
            }
        }
        let mut pinned = instance.map(str::to_owned);
        let (_, budget) = budgets(command, &params);
        let deadline = Instant::now()
            + budget
            + bounded_env("UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", 20.0, 120.0);
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            let response = timeout(
                remaining,
                self.dispatch(command, params.clone(), pinned.as_deref()),
            )
            .await
            .context("Unity command deadline exceeded")??;
            if !safe_reload_response(&response) {
                return Ok(response);
            }
            if let Some(id) = response["unity_instance"].as_str() {
                pinned = Some(id.to_owned());
            }
            if deadline.saturating_duration_since(Instant::now()) <= Duration::from_millis(250) {
                return Ok(response);
            }
            tokio::time::sleep(Duration::from_millis(250)).await;
        }
    }
    async fn custom_tools(&self, instance: Option<&str>) -> Result<Vec<Value>> {
        self.tools(instance).await
    }
    async fn instances(&self) -> Result<Value> {
        let state = self.state.lock().unwrap();
        let mut instances: Vec<_> = state.sessions.values().map(|s| s.info.clone()).collect();
        instances.sort_by(|a, b| a["id"].as_str().cmp(&b["id"].as_str()));
        Ok(json!({"instances":instances}))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::net::TcpListener;
    #[test]
    fn selection_refuses_ambiguity() {
        let items = vec![
            json!({"id":"A@abc1","name":"A","hash":"abc1","port":6400}),
            json!({"id":"A@abc2","name":"A","hash":"abc2","port":6401}),
        ];
        assert!(select(&items, None).is_err());
        assert!(select(&items, Some("A")).is_err());
        assert!(select(&items, Some("abc")).is_err());
        assert_eq!(select(&items, Some("6401")).unwrap()["hash"], "abc2");
        assert_eq!(select(&items, Some("A@abc1")).unwrap()["hash"], "abc1");
    }
    #[test]
    fn timeout_budgets() {
        assert_eq!(
            budgets("ping", &json!({"timeout_seconds":900})).1,
            Duration::from_secs(2)
        );
        assert_eq!(
            budgets("run_tests", &json!({"timeoutSeconds":900})).1,
            Duration::from_secs(905)
        );
        assert_eq!(
            budgets("blender_bridge", &json!({})).1,
            Duration::from_secs(215)
        );
        assert_eq!(
            budgets("blender_bridge", &json!({"timeout_seconds":true})).1,
            Duration::from_secs(215)
        );
    }
    #[tokio::test]
    async fn tcp_fragmented_handshake_heartbeat_and_response() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            for part in ["WELCOME UNITY-MCP 1 ", "FRAMING=1", "\n"] {
                socket.write_all(part.as_bytes()).await.unwrap();
            }
            let data = read_frame(&mut socket).await.unwrap();
            assert_eq!(
                serde_json::from_slice::<Value>(&data).unwrap()["type"],
                "read_console"
            );
            socket.write_u64(0).await.unwrap();
            let response = br#"{"status":"success","result":{"success":true,"data":["ok"]}}"#;
            socket.write_u64(response.len() as u64).await.unwrap();
            for part in response.chunks(3) {
                socket.write_all(part).await.unwrap();
            }
        });
        let dir = tempfile::tempdir().unwrap();
        tokio::fs::write(
            dir.path().join("unity-mcp-status-abc.json"),
            json!({"unity_port":port,"project_path":"/tmp/Demo/Assets"}).to_string(),
        )
        .await
        .unwrap();
        let bridge = TcpBridge::new(Some(dir.path().to_owned()), None);
        let response = bridge
            .send("read_console", json!({}), Some("Demo@abc"))
            .await
            .unwrap();
        assert_eq!(response["data"][0], "ok");
        server.await.unwrap();
    }
    #[tokio::test]
    async fn tcp_rejects_oversized_and_excess_heartbeat_frames() {
        for oversized in [false, true] {
            let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = listener.local_addr().unwrap().port();
            let server = tokio::spawn(async move {
                let (mut socket, _) = listener.accept().await.unwrap();
                socket.write_all(b"MCP/0.1 FRAMING=1\n").await.unwrap();
                if oversized {
                    socket.write_u64(MAX_FRAME + 1).await.unwrap();
                } else {
                    for _ in 0..16 {
                        socket.write_u64(0).await.unwrap();
                    }
                }
            });
            let mut stream = connect(port).await.unwrap();
            assert!(read_frame(&mut stream).await.is_err());
            server.await.unwrap();
        }
    }
    #[tokio::test]
    async fn hub_routes_results_only_from_selected_socket() {
        let hub = Arc::new(HubBridge::new());
        let (tx, mut rx) = mpsc::channel(128);
        let id = hub
            .register(&json!({"project_name":"Demo","project_hash":"abc"}), tx)
            .unwrap();
        let (other, _) = mpsc::channel(128);
        let other_id = hub
            .register(&json!({"project_name":"Other","project_hash":"xyz"}), other)
            .unwrap();
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.send("manage_scene", json!({}), Some("abc")).await })
        };
        let Message::Text(message) = rx.recv().await.unwrap() else {
            panic!("expected execute");
        };
        let execute: Value = serde_json::from_str(&message).unwrap();
        let result = json!({"type":"command_result","id":execute["id"],"result":{"status":"success","result":{"success":true}}});
        hub.receive(&other_id, result.clone());
        assert_eq!(hub.state.lock().unwrap().pending.len(), 1);
        hub.receive(&id, result);
        assert_eq!(call.await.unwrap().unwrap()["success"], true);
        assert!(hub.state.lock().unwrap().pending.is_empty());
    }
    #[tokio::test]
    async fn hub_reconnect_and_cancellation_clean_pending() {
        let hub = Arc::new(HubBridge::new());
        let (tx, mut rx) = mpsc::channel(128);
        let old = hub
            .register(&json!({"project_name":"Demo","project_hash":"abc"}), tx)
            .unwrap();
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.send("run_tests", json!({}), Some("abc")).await })
        };
        rx.recv().await.unwrap();
        call.abort();
        let _ = call.await;
        assert!(hub.state.lock().unwrap().pending.is_empty());
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.send("run_tests", json!({}), Some("abc")).await })
        };
        rx.recv().await.unwrap();
        let (tx, _) = mpsc::channel(128);
        let new = hub
            .register(&json!({"project_name":"Demo","project_hash":"abc"}), tx)
            .unwrap();
        assert_eq!(call.await.unwrap().unwrap()["hint"], "retry");
        hub.disconnect(&old);
        assert!(hub.state.lock().unwrap().sessions.contains_key(&new));
        assert!(hub.state.lock().unwrap().pending.is_empty());
        hub.receive(
            &new,
            json!({"type":"register_tools","tools":[{"name":"custom","parameters":[]}]}),
        );
        assert_eq!(hub.tools(Some("abc")).await.unwrap()[0]["name"], "custom");
    }
    async fn ws_read(socket: &mut TcpStream) -> Value {
        let opcode = socket.read_u8().await.unwrap();
        assert_eq!(opcode & 15, 1);
        let size = socket.read_u8().await.unwrap() & 127;
        let size = match size {
            126 => socket.read_u16().await.unwrap() as usize,
            127 => socket.read_u64().await.unwrap() as usize,
            n => n as usize,
        };
        let mut bytes = vec![0; size];
        socket.read_exact(&mut bytes).await.unwrap();
        serde_json::from_slice(&bytes).unwrap()
    }
    async fn ws_write(socket: &mut TcpStream, value: Value) {
        let bytes = value.to_string().into_bytes();
        socket.write_u8(0x81).await.unwrap();
        if bytes.len() < 126 {
            socket.write_u8(0x80 | bytes.len() as u8).await.unwrap();
        } else {
            socket.write_u8(0x80 | 126).await.unwrap();
            socket.write_u16(bytes.len() as u16).await.unwrap();
        }
        socket.write_all(&[1, 2, 3, 4]).await.unwrap();
        let masked: Vec<_> = bytes
            .iter()
            .enumerate()
            .map(|(i, b)| b ^ [1, 2, 3, 4][i % 4])
            .collect();
        socket.write_all(&masked).await.unwrap();
    }
    #[tokio::test]
    async fn websocket_network_protocol_and_disconnect() {
        use axum::{extract::WebSocketUpgrade, routing::get, Router};
        let hub = Arc::new(HubBridge::new());
        let route_hub = hub.clone();
        let app = Router::new().route(
            "/hub/plugin",
            get(move |ws: WebSocketUpgrade| {
                let hub = route_hub.clone();
                async move { ws.on_upgrade(move |socket| hub.handle_socket(socket)) }
            }),
        );
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            axum::serve(listener, app).await.unwrap();
        });
        let mut socket = TcpStream::connect(addr).await.unwrap();
        socket.write_all(format!("GET /hub/plugin HTTP/1.1\r\nHost: {addr}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n").as_bytes()).await.unwrap();
        let mut headers = vec![];
        while !headers.ends_with(b"\r\n\r\n") {
            headers.push(socket.read_u8().await.unwrap());
        }
        assert!(String::from_utf8(headers)
            .unwrap()
            .contains("101 Switching Protocols"));
        assert_eq!(ws_read(&mut socket).await["type"], "welcome");
        ws_write(
            &mut socket,
            json!({"type":"register","project_hash":"net","project_name":"Network"}),
        )
        .await;
        assert_eq!(ws_read(&mut socket).await["type"], "registered");
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.send("run_tests", json!({}), Some("net")).await })
        };
        let command = ws_read(&mut socket).await;
        assert_eq!(command["type"], "execute");
        ws_write(&mut socket,json!({"type":"command_result","id":command["id"],"result":{"status":"success","result":{"success":true}}})).await;
        assert_eq!(call.await.unwrap().unwrap()["success"], true);
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.send("run_tests", json!({}), Some("net")).await })
        };
        ws_read(&mut socket).await;
        drop(socket);
        assert_eq!(
            timeout(Duration::from_secs(2), call)
                .await
                .unwrap()
                .unwrap()
                .unwrap()["hint"],
            "retry"
        );
        assert_eq!(hub.instances().await.unwrap()["instances"], json!([]));
        server.abort();
    }
    #[test]
    fn heartbeat_freshness_and_safe_reload_rules() {
        let fallback = SystemTime::UNIX_EPOCH + Duration::from_secs(10);
        assert_eq!(
            heartbeat_time(
                &json!({"last_heartbeat":"1970-01-01T00:00:01.0000000Z"}),
                fallback
            ),
            SystemTime::UNIX_EPOCH + Duration::from_secs(1)
        );
        assert_eq!(
            heartbeat_time(
                &json!({"last_heartbeat":"1970-01-01T01:00:01+01:00"}),
                fallback
            ),
            SystemTime::UNIX_EPOCH + Duration::from_secs(1)
        );
        assert_eq!(
            heartbeat_time(&json!({"last_heartbeat":"invalid"}), fallback),
            fallback
        );
        assert!(safe_reload_response(&reload_response()));
        assert!(!safe_reload_response(&retry(
            "Reload occurred after execution"
        )));
        assert!(!safe_reload_response(
            &json!({"success":false,"data":{"reason":"reloading"}})
        ));
        assert!(!safe_reload_response(
            &json!({"success":true,"executed":false,"state":"reloading"})
        ));
    }
    #[test]
    fn custom_tools_filter_built_in_disabled_and_legacy() {
        let tools = json!([{"name":"builtin","is_built_in":true},{"name":"legacy"},{"name":"disabled","is_built_in":false,"enabled":false},{"name":"custom","is_built_in":false,"parameters":[]}]);
        for response in [
            json!({"data":{"tools":tools}}),
            json!({"data":tools}),
            json!({"tools":tools}),
        ] {
            let filtered = custom_definitions(&response);
            assert_eq!(filtered.len(), 1);
            assert_eq!(filtered[0]["name"], "custom");
        }
    }
    #[tokio::test]
    async fn stale_reload_heartbeat_does_not_keep_dead_instance() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        drop(listener);
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("unity-mcp-status-stale.json");
        tokio::fs::write(
            &path,
            json!({"unity_port":port,"reloading":true,"last_heartbeat":"2000-01-01T00:00:00Z"})
                .to_string(),
        )
        .await
        .unwrap();
        let bridge = TcpBridge::new(Some(dir.path().to_owned()), None);
        assert_eq!(bridge.instances().await.unwrap()["instances"], json!([]));
        let now: chrono::DateTime<chrono::Utc> = SystemTime::now().into();
        tokio::fs::write(
            &path,
            json!({"unity_port":port,"reloading":true,"last_heartbeat":now.to_rfc3339()})
                .to_string(),
        )
        .await
        .unwrap();
        assert_eq!(
            bridge.instances().await.unwrap()["instances"][0]["status"],
            "reloading"
        );
    }
    #[tokio::test]
    async fn tcp_cancel_discards_stream_before_next_command() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let (sent, received) = oneshot::channel();
        let server = tokio::spawn(async move {
            let (mut first, _) = listener.accept().await.unwrap();
            first
                .write_all(b"WELCOME UNITY-MCP 1 FRAMING=1\n")
                .await
                .unwrap();
            read_frame(&mut first).await.unwrap();
            sent.send(()).unwrap();
            let (mut second, _) = listener.accept().await.unwrap();
            second
                .write_all(b"WELCOME UNITY-MCP 1 FRAMING=1\n")
                .await
                .unwrap();
            assert_eq!(read_frame(&mut second).await.unwrap(), b"ping");
            let response = br#"{"status":"success","result":{"message":"pong"}}"#;
            second.write_u64(response.len() as u64).await.unwrap();
            second.write_all(response).await.unwrap();
        });
        let dir = tempfile::tempdir().unwrap();
        tokio::fs::write(
            dir.path().join("unity-mcp-status-cancel.json"),
            json!({"unity_port":port,"project_name":"Cancel"}).to_string(),
        )
        .await
        .unwrap();
        let bridge = Arc::new(TcpBridge::new(Some(dir.path().to_owned()), None));
        let call = {
            let bridge = bridge.clone();
            tokio::spawn(async move { bridge.send("manage_scene", json!({}), None).await })
        };
        received.await.unwrap();
        call.abort();
        let _ = call.await;
        assert_eq!(
            bridge.send("ping", json!({}), None).await.unwrap()["message"],
            "pong"
        );
        server.await.unwrap();
    }
    #[tokio::test]
    async fn hub_readiness_probe_precedes_fast_read() {
        let hub = Arc::new(HubBridge::new());
        let (tx, mut rx) = mpsc::channel(128);
        let id = hub
            .register(&json!({"project_name":"Ready","project_hash":"ready"}), tx)
            .unwrap();
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.send("read_console", json!({}), Some("ready")).await })
        };
        for name in ["ping", "read_console"] {
            let Message::Text(message) = rx.recv().await.unwrap() else {
                panic!("expected execute")
            };
            let execute: Value = serde_json::from_str(&message).unwrap();
            assert_eq!(execute["name"], name);
            let result = if name == "ping" {
                json!({"message":"pong"})
            } else {
                json!({"success":true})
            };
            hub.receive(&id,json!({"type":"command_result","id":execute["id"],"result":{"status":"success","result":result}}));
        }
        assert_eq!(call.await.unwrap().unwrap()["success"], true);
        let before = hub.state.lock().unwrap().sessions.len();
        hub.receive(
            "unknown-session",
            json!({"type":"pong","session_id":"unknown-session"}),
        );
        let state = hub.state.lock().unwrap();
        assert_eq!(state.sessions.len(), before);
        assert!(state.pending.is_empty());
    }
    #[tokio::test]
    async fn hub_retries_only_explicit_unexecuted_reload() {
        let hub = Arc::new(HubBridge::new());
        let (tx, mut rx) = mpsc::channel(128);
        let id = hub
            .register(
                &json!({"project_name":"Reload","project_hash":"reload"}),
                tx,
            )
            .unwrap();
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.send("manage_scene", json!({}), Some("reload")).await })
        };
        for result in [
            reload_response(),
            retry("Reload may have happened after the mutation"),
        ] {
            let Message::Text(message) = rx.recv().await.unwrap() else {
                panic!("expected execute")
            };
            let execute: Value = serde_json::from_str(&message).unwrap();
            hub.receive(&id,json!({"type":"command_result","id":execute["id"],"result":{"status":"success","result":result}}));
        }
        assert_eq!(
            call.await.unwrap().unwrap()["error"],
            "Reload may have happened after the mutation"
        );
        assert!(rx.try_recv().is_err());
    }
    #[tokio::test]
    async fn tcp_listing_no_editor_and_legacy_default_port() {
        // Reserve and release the legacy port so absence is an explicit fixture.
        let listener = TcpListener::bind("127.0.0.1:6400").await.unwrap();
        drop(listener);
        let dir = tempfile::tempdir().unwrap();
        let bridge = Arc::new(TcpBridge::new(Some(dir.path().to_owned()), None));
        let session = crate::protocol::Session::new(
            "no-editor".into(),
            bridge.clone(),
            None,
            "local".into(),
            false,
            false,
            true,
            None,
        );
        let start = Instant::now();
        for _ in 0..3 {
            assert_eq!(crate::protocol::visible_tools(&session).await.len(), 38);
        }
        assert!(start.elapsed() < Duration::from_millis(200));

        // A legacy editor with no status file must still be found on a later list.
        let listener = TcpListener::bind("127.0.0.1:6400").await.unwrap();
        let server = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            socket.write_all(b"MCP/0.1 FRAMING=1\n").await.unwrap();
            let request: Value =
                serde_json::from_slice(&read_frame(&mut socket).await.unwrap()).unwrap();
            assert_eq!(request["type"], "get_tool_states");
            let response = json!({"data":{"tools":[{"name":"legacy_custom","is_built_in":false}]}})
                .to_string();
            socket.write_u64(response.len() as u64).await.unwrap();
            socket.write_all(response.as_bytes()).await.unwrap();
        });
        assert_eq!(
            bridge.custom_tools(None).await.unwrap()[0]["name"],
            "legacy_custom"
        );
        server.await.unwrap();
    }

    #[tokio::test]
    async fn tcp_custom_tools_cache_refresh_expiry_and_reconnect() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = tokio::spawn(async move {
            let mut version = 0;
            for requests in [3, 1] {
                let (mut socket, _) = listener.accept().await.unwrap();
                socket
                    .write_all(b"WELCOME UNITY-MCP 1 FRAMING=1\n")
                    .await
                    .unwrap();
                for _ in 0..requests {
                    let request: Value =
                        serde_json::from_slice(&read_frame(&mut socket).await.unwrap()).unwrap();
                    assert_eq!(request["type"], "get_tool_states");
                    let response=json!({"status":"success","result":{"success":true,"data":{"tools":[{"name":format!("custom_{version}"),"is_built_in":false,"enabled":true,"parameters":[]},{"name":"builtin","is_built_in":true}]}}}).to_string();
                    socket.write_u64(response.len() as u64).await.unwrap();
                    socket.write_all(response.as_bytes()).await.unwrap();
                    version += 1;
                }
            }
        });
        let dir = tempfile::tempdir().unwrap();
        tokio::fs::write(
            dir.path().join("unity-mcp-status-tools.json"),
            json!({"unity_port":port,"project_name":"Tools"}).to_string(),
        )
        .await
        .unwrap();
        let bridge = TcpBridge::new(Some(dir.path().to_owned()), None);
        for _ in 0..3 {
            let tools = bridge.custom_tools(Some("tools")).await.unwrap();
            assert_eq!(tools.len(), 1);
            assert_eq!(tools[0]["name"], "custom_0");
        }
        // manage_tools/sync uses this normal command path and must bypass cache.
        bridge
            .send("get_tool_states", json!({}), Some("tools"))
            .await
            .unwrap();
        assert_eq!(
            bridge.custom_tools(Some("tools")).await.unwrap()[0]["name"],
            "custom_1"
        );
        bridge
            .tool_cache
            .lock()
            .unwrap()
            .get_mut(&port)
            .unwrap()
            .fetched = Instant::now() - Duration::from_secs(6);
        assert_eq!(
            bridge.custom_tools(Some("tools")).await.unwrap()[0]["name"],
            "custom_2"
        );
        *bridge.connection(port).lock().await = None;
        assert_eq!(
            bridge.custom_tools(Some("tools")).await.unwrap()[0]["name"],
            "custom_3"
        );
        server.await.unwrap();
    }
    #[tokio::test]
    async fn tcp_tool_cache_is_bounded_and_expires_departed_instances() {
        let dir = tempfile::tempdir().unwrap();
        let bridge = TcpBridge::new(Some(dir.path().to_owned()), None);
        for port in 1000..1100 {
            bridge.cache_tools(port, format!("instance_{port}"), &json!({"tools":[]}));
        }
        assert_eq!(bridge.tool_cache.lock().unwrap().len(), TOOL_CACHE_CAPACITY);
        for entry in bridge.tool_cache.lock().unwrap().values_mut() {
            entry.fetched = Instant::now() - TOOL_CACHE_TTL;
        }
        bridge.discover().await.unwrap();
        assert!(bridge.tool_cache.lock().unwrap().is_empty());
    }

    #[test]
    fn connection_timeout_validation() {
        for input in [
            None,
            Some("NaN"),
            Some("inf"),
            Some("-1"),
            Some("0"),
            Some("invalid"),
            Some("1e200"),
        ] {
            assert_eq!(positive_seconds(input, 300.0), Duration::from_secs(300));
        }
        assert_eq!(
            positive_seconds(Some("1.25"), 300.0),
            Duration::from_millis(1250)
        );
        assert_eq!(positive_seconds(None, 600.0), Duration::from_secs(600));
    }
    #[test]
    fn bridge_tracking_identity_survives_moves_and_is_never_reused() {
        let tcp = TcpBridge::new(None, None);
        let first = tcp.tracking_id();
        let moved = Box::new(tcp);
        assert_ne!(first, 0);
        assert_eq!(moved.tracking_id(), first);
        drop(moved);
        let replacement = TcpBridge::new(None, None);
        let hub = HubBridge::new();
        let hub_id = hub.tracking_id();
        let moved = Arc::new(hub);
        assert_eq!(moved.tracking_id(), hub_id);
        drop(moved);
        let replacement_hub = HubBridge::new();
        let mut identities = vec![
            first,
            replacement.tracking_id(),
            hub_id,
            replacement_hub.tracking_id(),
        ];
        identities.sort_unstable();
        identities.dedup();
        assert_eq!(identities.len(), 4);
        assert!(!identities.contains(&0));
    }

    #[test]
    fn tcp_connection_registry_evicts_idle_but_preserves_borrowed_sockets() {
        let bridge = TcpBridge::new(None, None);
        let borrowed = bridge.connection(1);
        for port in 2..200 {
            drop(bridge.connection(port));
        }
        assert_eq!(
            bridge.connections.lock().unwrap().len(),
            IDLE_CONNECTION_CAPACITY
        );
        assert!(Arc::ptr_eq(&borrowed, &bridge.connection(1)));
        // An in-flight owner must remain unique even while every slot is busy.
        let active: Vec<_> = (200..300).map(|port| bridge.connection(port)).collect();
        assert!(Arc::ptr_eq(&active[0], &bridge.connection(200)));
        drop(active);
        drop(bridge.connection(400));
        assert_eq!(
            bridge.connections.lock().unwrap().len(),
            IDLE_CONNECTION_CAPACITY
        );
    }

    #[tokio::test]
    async fn hub_cancelled_and_reconnected_queued_commands_are_not_sent() {
        let hub = Arc::new(HubBridge::new());
        let (tx, mut rx) = mpsc::channel(128);
        let session = hub.register(&json!({"project_hash":"queued"}), tx).unwrap();
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move {
                hub.dispatch("manage_scene", json!({}), Some("queued"))
                    .await
            })
        };
        let message = rx.recv().await.unwrap();
        assert!(hub.queued_message_is_live(Some(&session), &message));
        assert!(!hub.queued_message_is_live(Some("other-session"), &message));
        call.abort();
        let _ = call.await;
        assert!(!hub.queued_message_is_live(Some(&session), &message));
        assert!(hub.state.lock().unwrap().pending.is_empty());
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move {
                hub.dispatch("manage_scene", json!({}), Some("queued"))
                    .await
            })
        };
        let message = rx.recv().await.unwrap();
        let (tx, _rx) = mpsc::channel(128);
        let replacement = hub.register(&json!({"project_hash":"queued"}), tx).unwrap();
        assert!(!hub.queued_message_is_live(Some(&session), &message));
        assert!(!hub.queued_message_is_live(Some(&replacement), &message));
        assert_eq!(call.await.unwrap().unwrap()["hint"], "retry");
        // Late cleanup from the old connection must not remove its replacement.
        hub.disconnect(&session);
        assert!(hub
            .state
            .lock()
            .unwrap()
            .sessions
            .contains_key(&replacement));
    }

    #[tokio::test]
    async fn hub_expired_queued_command_and_duplicate_result_are_ignored() {
        let hub = Arc::new(HubBridge::new());
        let (tx, mut rx) = mpsc::channel(128);
        let session = hub
            .register(&json!({"project_hash":"expired"}), tx)
            .unwrap();
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move { hub.dispatch("ping", json!({}), Some("expired")).await })
        };
        let message = rx.recv().await.unwrap();
        assert_eq!(call.await.unwrap().unwrap()["hint"], "retry");
        assert!(!hub.queued_message_is_live(Some(&session), &message));
        let Message::Text(text) = message else {
            panic!("expected command");
        };
        let data: Value = serde_json::from_str(&text).unwrap();
        let late = json!({"type":"command_result","id":data["id"],"result":{"success":true}});
        hub.receive(&session, late.clone());
        hub.receive(&session, late);
        assert!(hub.state.lock().unwrap().pending.is_empty());
    }

    #[tokio::test]
    async fn hub_outstanding_limit_applies_even_when_socket_queue_drains() {
        let hub = HubBridge::new();
        let (tx, mut rx) = mpsc::channel(128);
        let session = hub
            .register(&json!({"project_hash":"pending-limit"}), tx)
            .unwrap();
        let mut receivers = Vec::new();
        for i in 0..HUB_PENDING_CAPACITY {
            let (response, receiver) = oneshot::channel();
            receivers.push(receiver);
            hub.state.lock().unwrap().pending.insert(
                i.to_string(),
                Pending {
                    session: session.clone(),
                    response,
                },
            );
        }
        let result = hub
            .dispatch("manage_scene", json!({}), Some("pending-limit"))
            .await;
        assert!(result.unwrap_err().to_string().contains("too many pending"));
        assert!(rx.try_recv().is_err());
        assert_eq!(
            hub.state.lock().unwrap().pending.len(),
            HUB_PENDING_CAPACITY
        );
        hub.disconnect(&session);
        assert!(hub.state.lock().unwrap().pending.is_empty());
        for receiver in receivers {
            assert_eq!(receiver.await.unwrap().unwrap()["hint"], "retry");
        }
    }

    #[tokio::test]
    async fn websocket_stalled_control_write_has_a_deadline() {
        let mut stalled = Box::pin(futures_util::sink::unfold((), |_, _: Message| {
            std::future::pending::<std::result::Result<(), std::io::Error>>()
        }));
        let result = timeout(
            SOCKET_WRITE_TIMEOUT + Duration::from_secs(1),
            send_socket(&mut stalled, Message::Ping(vec![].into())),
        )
        .await
        .unwrap();
        assert!(result
            .unwrap_err()
            .to_string()
            .contains("deadline exceeded"));
    }

    #[tokio::test]
    async fn bounded_hub_queue_rejects_overflow_and_cleans_pending() {
        let hub = HubBridge::new();
        let (tx, _rx) = mpsc::channel(128);
        hub.register(
            &json!({"project_name":"Full","project_hash":"full"}),
            tx.clone(),
        )
        .unwrap();
        for _ in 0..128 {
            tx.try_send(Message::Ping(vec![].into())).unwrap();
        }
        let result = hub.send("manage_scene", json!({}), Some("full")).await;
        assert!(result
            .unwrap_err()
            .to_string()
            .contains("queue unavailable"));
        assert!(hub.state.lock().unwrap().pending.is_empty());
    }
    #[tokio::test]
    async fn hub_shutdown_closes_registered_and_unregistered_sockets() {
        use axum::{extract::WebSocketUpgrade, routing::get, Router};
        let hub = Arc::new(HubBridge::new());
        let route_hub = hub.clone();
        let (finished, mut completions) = mpsc::channel(2);
        let app = Router::new().route(
            "/hub/plugin",
            get(move |ws: WebSocketUpgrade| {
                let hub = route_hub.clone();
                let finished = finished.clone();
                async move {
                    ws.on_upgrade(move |socket| async move {
                        hub.handle_socket(socket).await;
                        let _ = finished.send(()).await;
                    })
                }
            }),
        );
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            axum::serve(listener, app).await.unwrap();
        });
        let mut sockets = Vec::new();
        for _ in 0..2 {
            let mut socket = TcpStream::connect(addr).await.unwrap();
            socket.write_all(format!("GET /hub/plugin HTTP/1.1\r\nHost: {addr}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n").as_bytes()).await.unwrap();
            let mut headers = vec![];
            while !headers.ends_with(b"\r\n\r\n") {
                headers.push(socket.read_u8().await.unwrap());
            }
            assert_eq!(ws_read(&mut socket).await["type"], "welcome");
            sockets.push(socket);
        }
        ws_write(
            &mut sockets[0],
            json!({"type":"register","project_hash":"shutdown"}),
        )
        .await;
        assert_eq!(ws_read(&mut sockets[0]).await["type"], "registered");
        let call = {
            let hub = hub.clone();
            tokio::spawn(async move {
                hub.dispatch("manage_scene", json!({}), Some("shutdown"))
                    .await
            })
        };
        assert_eq!(ws_read(&mut sockets[0]).await["type"], "execute");
        hub.shutdown();
        hub.shutdown(); // Idempotent even while SessionGuards still own cleanup.
        for _ in 0..2 {
            assert_eq!(
                timeout(Duration::from_secs(1), completions.recv())
                    .await
                    .unwrap(),
                Some(())
            );
        }
        assert_eq!(call.await.unwrap().unwrap()["hint"], "retry");
        assert!(hub.state.lock().unwrap().sessions.is_empty());
        assert!(hub.state.lock().unwrap().pending.is_empty());
        let (tx, _rx) = mpsc::channel(1);
        assert!(hub.register(&json!({"project_hash":"late"}), tx).is_err());
        assert!(hub.resolve(None, Duration::from_secs(20)).await.is_err());
        server.abort();
    }

    #[tokio::test]
    async fn websocket_clean_close_returns_close_frame() {
        use axum::{extract::WebSocketUpgrade, routing::get, Router};
        let hub = Arc::new(HubBridge::new());
        let app = Router::new().route(
            "/hub/plugin",
            get(move |ws: WebSocketUpgrade| {
                let hub = hub.clone();
                async move { ws.on_upgrade(move |socket| hub.handle_socket(socket)) }
            }),
        );
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            axum::serve(listener, app).await.unwrap();
        });
        let mut socket = TcpStream::connect(addr).await.unwrap();
        socket.write_all(format!("GET /hub/plugin HTTP/1.1\r\nHost: {addr}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n").as_bytes()).await.unwrap();
        let mut headers = vec![];
        while !headers.ends_with(b"\r\n\r\n") {
            headers.push(socket.read_u8().await.unwrap());
        }
        ws_read(&mut socket).await;
        // Masked normal-close code 1000 using a zero masking key.
        socket
            .write_all(&[0x88, 0x82, 0, 0, 0, 0, 0x03, 0xe8])
            .await
            .unwrap();
        assert_eq!(
            timeout(Duration::from_secs(2), socket.read_u8())
                .await
                .unwrap()
                .unwrap()
                & 15,
            8
        );
        assert_eq!(socket.read_u8().await.unwrap(), 2);
        assert_eq!(socket.read_u16().await.unwrap(), 1000);
        server.abort();
    }
}
