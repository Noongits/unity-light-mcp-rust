use anyhow::{anyhow, Context, Result};
use axum::{
    extract::{DefaultBodyLimit, Query, State, WebSocketUpgrade},
    http::{HeaderMap, StatusCode},
    response::{
        sse::{Event, KeepAlive},
        IntoResponse, Response, Sse,
    },
    routing::{get, post},
    Json, Router,
};
use clap::{Parser, Subcommand};
use futures_util::StreamExt;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    convert::Infallible,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Weak,
    },
    time::{Duration, Instant},
};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    sync::{Mutex, RwLock},
};
use tokio_stream::wrappers::BroadcastStream;
use unity_mcp_light::{
    bridge::UnityBridge,
    protocol::{self, Session},
    transport::{HubBridge, TcpBridge},
};

#[derive(Parser, Clone)]
#[command(version, about = "Native Rust Unity MCP Light server")]
struct Args {
    #[arg(long, env="UNITY_MCP_TRANSPORT", default_value="stdio", value_parser=["stdio","http"])]
    transport: String,
    #[arg(
        long,
        env = "UNITY_MCP_HTTP_URL",
        default_value = "http://127.0.0.1:8080"
    )]
    http_url: String,
    #[arg(long, env = "UNITY_MCP_HTTP_HOST")]
    http_host: Option<String>,
    #[arg(long, env = "UNITY_MCP_HTTP_PORT")]
    http_port: Option<u16>,
    #[arg(long, env = "UNITY_MCP_DEFAULT_INSTANCE")]
    default_instance: Option<String>,
    #[arg(long, env = "UNITY_MCP_STATUS_DIR")]
    status_dir: Option<PathBuf>,
    #[arg(long, env = "UNITY_MCP_HTTP_REMOTE_HOSTED")]
    http_remote_hosted: bool,
    #[arg(long, env = "UNITY_MCP_API_KEY_VALIDATION_URL")]
    api_key_validation_url: Option<String>,
    #[arg(long, env = "UNITY_MCP_API_KEY_LOGIN_URL")]
    api_key_login_url: Option<String>,
    #[arg(long, env = "UNITY_MCP_API_KEY_CACHE_TTL", default_value_t = 300.0)]
    api_key_cache_ttl: f64,
    #[arg(long, env = "UNITY_MCP_API_KEY_SERVICE_TOKEN_HEADER")]
    api_key_service_token_header: Option<String>,
    #[arg(long, env = "UNITY_MCP_API_KEY_SERVICE_TOKEN")]
    api_key_service_token: Option<String>,
    #[arg(long)]
    unity_instance_token: Option<String>,
    #[arg(long)]
    pidfile: Option<PathBuf>,
    #[arg(long, env = "UNITY_MCP_PROJECT_SCOPED_TOOLS")]
    project_scoped_tools: bool,
    #[command(subcommand)]
    command: Option<Command>,
}
#[derive(Subcommand, Clone)]
enum Command {
    /// Send a raw Unity command through the compatible REST API.
    Call {
        name: String,
        #[arg(default_value = "{}")]
        parameters: String,
        #[arg(long)]
        instance: Option<String>,
    },
    /// List active Unity Editor connections through the REST API.
    Instances,
    /// Check the server health endpoint.
    Status,
}
struct RemoteBridge(Arc<HubBridge>);
#[async_trait::async_trait]
impl UnityBridge for RemoteBridge {
    fn tracking_id(&self) -> u64 {
        // Remote editors must not own host-OS focus nudges, even though the
        // underlying local hub has a stable tracking identity.
        0
    }
    fn local_filesystem_allowed(&self) -> bool {
        false
    }
    async fn send(&self, name: &str, params: Value, instance: Option<&str>) -> Result<Value> {
        self.0.send(name, params, instance).await
    }
    async fn instances(&self) -> Result<Value> {
        self.0.instances().await
    }
    async fn custom_tools(&self, instance: Option<&str>) -> Result<Vec<Value>> {
        self.0.custom_tools(instance).await
    }
}
struct App {
    args: Args,
    shutting_down: AtomicBool,
    local_hub: Arc<HubBridge>,
    user_hubs: Mutex<HashMap<String, Weak<HubBridge>>>,
    sessions: RwLock<HashMap<String, Arc<Session>>>,
    auth_cache: Mutex<HashMap<String, (Instant, String)>>,
    client: reqwest::Client,
}
impl App {
    async fn identity(&self, headers: &HeaderMap) -> Result<String> {
        if !self.args.http_remote_hosted {
            return Ok("local".into());
        }
        let key = headers
            .get("x-api-key")
            .and_then(|v| v.to_str().ok())
            .filter(|v| !v.is_empty())
            .ok_or_else(|| anyhow!("API key required"))?;
        let hash = format!("{:x}", Sha256::digest(key.as_bytes()));
        {
            let cache = self.auth_cache.lock().await;
            if let Some((expiry, user)) = cache.get(&hash) {
                if *expiry > Instant::now() {
                    return Ok(user.clone());
                }
            }
        }
        let url = self
            .args
            .api_key_validation_url
            .as_ref()
            .ok_or_else(|| anyhow!("API key validation service not configured"))?;
        let mut request = self.client.post(url).json(&json!({"api_key":key}));
        if let (Some(header), Some(token)) = (
            &self.args.api_key_service_token_header,
            &self.args.api_key_service_token,
        ) {
            request = request.header(header, token);
        }
        let result: Value = request
            .send()
            .await
            .context("API key validation service unavailable")?
            .error_for_status()
            .context("API key rejected")?
            .json()
            .await
            .context("Invalid API key validation response")?;
        let user = result["user_id"]
            .as_str()
            .filter(|s| !s.is_empty())
            .ok_or_else(|| anyhow!("Invalid API key"))?;
        if result["valid"] != true {
            return Err(anyhow!("Invalid API key"));
        }
        let ttl = self.args.api_key_cache_ttl.clamp(0.0, 3600.0);
        if ttl > 0.0 {
            let mut cache = self.auth_cache.lock().await;
            cache.retain(|_, (expires, _)| *expires > Instant::now());
            if cache.len() >= 1024 {
                if let Some(key) = cache
                    .iter()
                    .min_by_key(|(_, v)| v.0)
                    .map(|(k, _)| k.clone())
                {
                    cache.remove(&key);
                }
            }
            cache.insert(
                hash,
                (
                    Instant::now() + Duration::from_secs_f64(ttl),
                    user.to_owned(),
                ),
            );
        }
        Ok(user.to_owned())
    }
    async fn hub(&self, user: &str) -> Arc<HubBridge> {
        if !self.args.http_remote_hosted {
            return self.local_hub.clone();
        }
        let mut hubs = self.user_hubs.lock().await;
        hubs.retain(|_, v| v.strong_count() > 0);
        if let Some(hub) = hubs.get(user).and_then(Weak::upgrade) {
            return hub;
        }
        let hub = Arc::new(HubBridge::new());
        hubs.insert(user.to_owned(), Arc::downgrade(&hub));
        hub
    }
    async fn existing_session(&self, headers: &HeaderMap, user: &str) -> Result<Arc<Session>> {
        let id = headers
            .get("mcp-session-id")
            .and_then(|v| v.to_str().ok())
            .ok_or_else(|| anyhow!("Mcp-Session-Id header required"))?;
        let session = self
            .sessions
            .read()
            .await
            .get(id)
            .cloned()
            .ok_or_else(|| anyhow!("MCP session not found; initialize a new session"))?;
        if session.user != user {
            return Err(anyhow!("MCP session not found"));
        }
        Ok(session)
    }
}
fn failure(status: StatusCode, message: impl Into<String>) -> Response {
    (status, Json(json!({"error":message.into()}))).into_response()
}
fn valid_origin(headers: &HeaderMap) -> bool {
    let Some(origin) = headers.get("origin").and_then(|v| v.to_str().ok()) else {
        return true;
    };
    let Some(host) = headers.get("host").and_then(|v| v.to_str().ok()) else {
        return false;
    };
    let Ok(url) = url::Url::parse(origin) else {
        return false;
    };
    let authority = match url.port() {
        Some(p) => format!("{}:{p}", url.host_str().unwrap_or("")),
        None => url.host_str().unwrap_or("").to_owned(),
    };
    authority.eq_ignore_ascii_case(host)
}
struct TaskCleanup {
    session: Weak<Session>,
    key: String,
}
impl Drop for TaskCleanup {
    fn drop(&mut self) {
        if let Some(s) = self.session.upgrade() {
            s.jobs.lock().unwrap().remove(&self.key);
        }
    }
}
struct AbortOnDrop(tokio::task::AbortHandle);
impl Drop for AbortOnDrop {
    fn drop(&mut self) {
        self.0.abort();
    }
}
async fn execute(session: Arc<Session>, request: Value) -> Option<Value> {
    if request.get("id").is_none() {
        return protocol::dispatch(&session, request).await;
    }
    let id = request["id"].clone();
    let key = id.to_string();
    let job = {
        let mut jobs = session.jobs.lock().unwrap();
        if session.is_closed() {
            return Some(protocol::rpc_error(id, -32000, "MCP session is closed"));
        }
        if jobs.contains_key(&key) {
            return Some(protocol::rpc_error(
                id,
                -32600,
                "Duplicate in-flight request id",
            ));
        }
        if jobs.len() >= 64 {
            return Some(protocol::rpc_error(
                id,
                -32000,
                "Too many concurrent requests",
            ));
        }
        let s = session.clone();
        let (start_tx, start_rx) = tokio::sync::oneshot::channel();
        let cleanup = TaskCleanup {
            session: Arc::downgrade(&session),
            key: key.clone(),
        };
        let job = tokio::spawn(async move {
            let _cleanup = cleanup;
            let _ = start_rx.await;
            protocol::dispatch(&s, request).await
        });
        jobs.insert(key, job.abort_handle());
        let _ = start_tx.send(());
        job
    };
    let _abort = AbortOnDrop(job.abort_handle());
    match job.await {
        Ok(value) => value,
        Err(e) if e.is_cancelled() => Some(protocol::rpc_error(id, -32800, "Request cancelled")),
        Err(_) => Some(protocol::rpc_error(id, -32603, "Internal server error")),
    }
}
async fn mcp_post(
    State(app): State<Arc<App>>,
    headers: HeaderMap,
    body: axum::body::Bytes,
) -> Response {
    if !valid_origin(&headers) {
        return failure(StatusCode::FORBIDDEN, "Untrusted Origin");
    }
    let user = match app.identity(&headers).await {
        Ok(u) => u,
        Err(e) => return failure(StatusCode::UNAUTHORIZED, e.to_string()),
    };
    let request: Value = match serde_json::from_slice(&body) {
        Ok(v) => v,
        Err(_) => {
            return (
                StatusCode::BAD_REQUEST,
                Json(protocol::rpc_error(Value::Null, -32700, "Parse error")),
            )
                .into_response()
        }
    };
    if !protocol::valid_request(&request) {
        return (
            StatusCode::BAD_REQUEST,
            Json(protocol::rpc_error(
                request.get("id").cloned().unwrap_or(Value::Null),
                -32600,
                "Invalid JSON-RPC request",
            )),
        )
            .into_response();
    }
    let initialize = request["method"] == "initialize";
    let session = if initialize {
        if request.get("id").is_none() {
            return failure(StatusCode::BAD_REQUEST, "Initialize must be a request");
        }
        let mut sessions = app.sessions.write().await;
        if app.shutting_down.load(Ordering::Acquire) {
            return failure(StatusCode::SERVICE_UNAVAILABLE, "Server is shutting down");
        }
        if sessions.len() >= 1024 {
            return failure(
                StatusCode::SERVICE_UNAVAILABLE,
                "Maximum active MCP sessions reached",
            );
        }
        let hub = app.hub(&user).await;
        let id = uuid::Uuid::new_v4().to_string();
        let bridge: Arc<dyn UnityBridge> = if app.args.http_remote_hosted {
            Arc::new(RemoteBridge(hub.clone()))
        } else {
            hub.clone()
        };
        let session = Session::new(
            id.clone(),
            bridge,
            Some(hub),
            user,
            true,
            app.args.http_remote_hosted,
            app.args.project_scoped_tools,
            app.args.default_instance.clone(),
        );
        sessions.insert(id, session.clone());
        session
    } else {
        match app.existing_session(&headers, &user).await {
            Ok(s) => s,
            Err(e) => return failure(StatusCode::NOT_FOUND, e.to_string()),
        }
    };
    let id = session.id.clone();
    let response = execute(session, request).await;
    let mut response = match response {
        Some(v) => Json(v).into_response(),
        None => StatusCode::ACCEPTED.into_response(),
    };
    if initialize {
        response
            .headers_mut()
            .insert("mcp-session-id", id.parse().unwrap());
    }
    response
}
async fn mcp_events(State(app): State<Arc<App>>, headers: HeaderMap) -> Response {
    if !valid_origin(&headers) {
        return failure(StatusCode::FORBIDDEN, "Untrusted Origin");
    }
    let user = match app.identity(&headers).await {
        Ok(u) => u,
        Err(e) => return failure(StatusCode::UNAUTHORIZED, e.to_string()),
    };
    let session = match app.existing_session(&headers, &user).await {
        Ok(s) => s,
        Err(e) => return failure(StatusCode::NOT_FOUND, e.to_string()),
    };
    let stream =
        BroadcastStream::new(session.events.subscribe()).filter_map(|message| async move {
            match message {
                Ok(v) => Some(Ok::<Event, Infallible>(
                    Event::default().event("message").data(v.to_string()),
                )),
                Err(_) => None,
            }
        });
    let stream = stream.take_until(async move { session.closed().await });
    Sse::new(stream)
        .keep_alive(
            KeepAlive::new()
                .interval(Duration::from_secs(15))
                .text("keepalive"),
        )
        .into_response()
}
async fn mcp_delete(State(app): State<Arc<App>>, headers: HeaderMap) -> Response {
    if !valid_origin(&headers) {
        return failure(StatusCode::FORBIDDEN, "Untrusted Origin");
    }
    let user = match app.identity(&headers).await {
        Ok(u) => u,
        Err(e) => return failure(StatusCode::UNAUTHORIZED, e.to_string()),
    };
    let session = match app.existing_session(&headers, &user).await {
        Ok(s) => s,
        Err(e) => return failure(StatusCode::NOT_FOUND, e.to_string()),
    };
    app.sessions.write().await.remove(&session.id);
    session.close().await;
    StatusCode::OK.into_response()
}
async fn plugin(State(app): State<Arc<App>>, headers: HeaderMap, ws: WebSocketUpgrade) -> Response {
    if !valid_origin(&headers) {
        return failure(StatusCode::FORBIDDEN, "Untrusted Origin");
    }
    let user = match app.identity(&headers).await {
        Ok(u) => u,
        Err(e) => return failure(StatusCode::UNAUTHORIZED, e.to_string()),
    };
    let hub = app.hub(&user).await;
    if app.shutting_down.load(Ordering::Acquire) {
        hub.shutdown();
        return failure(StatusCode::SERVICE_UNAVAILABLE, "Server is shutting down");
    }
    ws.on_upgrade(move |socket| hub.handle_socket(socket))
        .into_response()
}
async fn health() -> Json<Value> {
    Json(
        json!({"status":"healthy","timestamp":std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_secs_f64(),"version":env!("CARGO_PKG_VERSION"),"message":"MCP for Unity Rust server is running"}),
    )
}
async fn login_url(State(app): State<Arc<App>>) -> Response {
    match &app.args.api_key_login_url {
        Some(url) => Json(json!({"success":true,"login_url":url})).into_response(),
        None => failure(StatusCode::NOT_FOUND, "API key management not configured"),
    }
}
async fn api_instances(State(app): State<Arc<App>>) -> Response {
    if app.args.http_remote_hosted {
        return StatusCode::NOT_FOUND.into_response();
    }
    match app.local_hub.instances().await {
        Ok(mut v) => {
            v["success"] = json!(true);
            if let Some(items) = v["instances"].as_array_mut() {
                for i in items {
                    i["project"] = i["name"].clone();
                }
            }
            Json(v).into_response()
        }
        Err(e) => failure(StatusCode::SERVICE_UNAVAILABLE, e.to_string()),
    }
}
async fn api_tools(
    State(app): State<Arc<App>>,
    Query(query): Query<HashMap<String, String>>,
) -> Response {
    if app.args.http_remote_hosted {
        return StatusCode::NOT_FOUND.into_response();
    }
    match app
        .local_hub
        .tools(query.get("instance").map(String::as_str))
        .await
    {
        Ok(tools) => Json(json!({"success":true,"tools":tools})).into_response(),
        Err(e) => failure(StatusCode::SERVICE_UNAVAILABLE, e.to_string()),
    }
}
async fn api_command(
    State(app): State<Arc<App>>,
    headers: HeaderMap,
    Json(body): Json<Value>,
) -> Response {
    if app.args.http_remote_hosted {
        return StatusCode::NOT_FOUND.into_response();
    }
    if !valid_origin(&headers) {
        return failure(StatusCode::FORBIDDEN, "Untrusted Origin");
    }
    let Some(name) = body["type"].as_str() else {
        return failure(StatusCode::BAD_REQUEST, "Missing 'type' field");
    };
    let params = body.get("params").cloned().unwrap_or_else(|| json!({}));
    let response = if name == "execute_custom_tool" {
        let instance = if let Some(id) = body["unity_instance"].as_str() {
            Some(id.to_owned())
        } else {
            app.local_hub.instances().await.ok().and_then(|v| {
                let a = v["instances"].as_array()?;
                if a.len() == 1 {
                    a[0]["id"].as_str().map(str::to_owned)
                } else {
                    None
                }
            })
        };
        let target = params["tool_name"]
            .as_str()
            .or_else(|| params["name"].as_str())
            .unwrap_or("");
        let arguments = params
            .get("parameters")
            .or_else(|| params.get("params"))
            .cloned()
            .unwrap_or_else(|| json!({}));
        unity_mcp_light::tools::call_custom(
            app.local_hub.as_ref(),
            target,
            arguments,
            instance.as_deref(),
        )
        .await
    } else {
        app.local_hub
            .send(name, params, body["unity_instance"].as_str())
            .await
    };
    match response {
        Ok(v) => Json(v).into_response(),
        Err(e) => (
            StatusCode::SERVICE_UNAVAILABLE,
            Json(json!({"success":false,"error":e.to_string()})),
        )
            .into_response(),
    }
}
async fn stdio(args: &Args) -> Result<()> {
    let bridge = Arc::new(TcpBridge::new(
        args.status_dir.clone(),
        args.default_instance.clone(),
    ));
    let session = Session::new(
        "stdio".into(),
        bridge,
        None,
        "local".into(),
        false,
        false,
        args.project_scoped_tools,
        args.default_instance.clone(),
    );
    let output = Arc::new(Mutex::new(tokio::io::stdout()));
    let mut notifications = session.events.subscribe();
    let out = output.clone();
    let notify = tokio::spawn(async move {
        while let Ok(value) = notifications.recv().await {
            let mut out = out.lock().await;
            let _ = out.write_all(format!("{value}\n").as_bytes()).await;
            let _ = out.flush().await;
        }
    });
    let mut input = BufReader::new(tokio::io::stdin());
    let mut workers = tokio::task::JoinSet::new();
    loop {
        // Bound completed responses waiting for a slow stdout consumer as well as
        // notification tasks, which are not represented by session.jobs.
        while workers.len() >= 128 {
            workers.join_next().await;
        }
        let mut line = Vec::new();
        // Take caps each message before allocating beyond the protocol limit.
        let count = tokio::io::AsyncReadExt::take(&mut input, 16 * 1024 * 1024 + 1)
            .read_until(b'\n', &mut line)
            .await?;
        if count == 0 {
            break;
        }
        if line.len() > 16 * 1024 * 1024 {
            return Err(anyhow!("MCP message exceeds 16 MiB limit"));
        }
        if line.iter().all(u8::is_ascii_whitespace) {
            continue;
        }
        let value = serde_json::from_slice::<Value>(&line);
        let out = output.clone();
        let s = session.clone();
        workers.spawn(async move {
            let response = match value {
                Ok(v) => execute(s, v).await,
                Err(_) => Some(protocol::rpc_error(Value::Null, -32700, "Parse error")),
            };
            if let Some(v) = response {
                let mut out = out.lock().await;
                let _ = out.write_all(format!("{v}\n").as_bytes()).await;
                let _ = out.flush().await;
            }
        });
        while workers.try_join_next().is_some() {}
    }
    while workers.join_next().await.is_some() {}
    session.close().await;
    notify.abort();
    Ok(())
}
async fn http(args: Args) -> Result<()> {
    let url = url::Url::parse(&args.http_url)?;
    let host = args
        .http_host
        .clone()
        .unwrap_or_else(|| url.host_str().unwrap_or("127.0.0.1").to_owned());
    let port = args
        .http_port
        .unwrap_or(url.port_or_known_default().unwrap_or(8080));
    if args.http_remote_hosted && args.api_key_validation_url.is_none() {
        return Err(anyhow!(
            "--http-remote-hosted requires --api-key-validation-url"
        ));
    }
    if !args.api_key_cache_ttl.is_finite() || args.api_key_cache_ttl < 0.0 {
        return Err(anyhow!("API key cache TTL must be finite and non-negative"));
    }
    let app = Arc::new(App {
        args,
        shutting_down: AtomicBool::new(false),
        local_hub: Arc::new(HubBridge::new()),
        user_hubs: Mutex::new(HashMap::new()),
        sessions: RwLock::new(HashMap::new()),
        auth_cache: Mutex::new(HashMap::new()),
        client: reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .timeout(Duration::from_secs(10))
            .build()?,
    });
    let a = app.clone();
    let janitor = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_secs(2));
        let mut fingerprints: HashMap<String, Vec<u8>> = HashMap::new();
        loop {
            interval.tick().await;
            let sessions = a
                .sessions
                .read()
                .await
                .values()
                .cloned()
                .collect::<Vec<_>>();
            fingerprints.retain(|id, _| sessions.iter().any(|s| &s.id == id));
            for s in sessions {
                if s.expire_if_idle(Duration::from_secs(3600)).await {
                    a.sessions.write().await.remove(&s.id);
                    s.close().await;
                    continue;
                }
                let tools = protocol::visible_tools(&s).await;
                let fingerprint =
                    Sha256::digest(serde_json::to_vec(&tools).unwrap_or_default()).to_vec();
                if fingerprints
                    .get(&s.id)
                    .is_some_and(|last| last != &fingerprint)
                {
                    s.changed();
                }
                fingerprints.insert(s.id.clone(), fingerprint);
            }
        }
    });
    let router = Router::new()
        .route("/mcp", post(mcp_post).get(mcp_events).delete(mcp_delete))
        .route("/mcp/", post(mcp_post).get(mcp_events).delete(mcp_delete))
        .route("/hub/plugin", get(plugin))
        .route("/health", get(health))
        .route("/api/auth/login-url", get(login_url))
        .route("/api/instances", get(api_instances))
        .route("/api/custom-tools", get(api_tools))
        .route("/api/command", post(api_command))
        .layer(DefaultBodyLimit::max(16 * 1024 * 1024))
        .with_state(app.clone());
    let listener = tokio::net::TcpListener::bind((host.as_str(), port)).await?;
    tracing::info!(address=%listener.local_addr()?,"Unity MCP Light HTTP server listening");
    let shutdown_app = app.clone();
    let janitor_abort = janitor.abort_handle();
    axum::serve(listener, router)
        .with_graceful_shutdown(async move {
            wait_for_shutdown_signal(tokio::signal::ctrl_c()).await;
            shutdown_app.shutting_down.store(true, Ordering::Release);
            janitor_abort.abort();
            shutdown_app.local_hub.shutdown();
            let hubs = shutdown_app
                .user_hubs
                .lock()
                .await
                .values()
                .filter_map(Weak::upgrade)
                .collect::<Vec<_>>();
            for hub in hubs {
                hub.shutdown();
            }
            // Close long-lived SSE streams and requests before waiting for HTTP
            // graceful shutdown; otherwise an open stream prevents this return.
            let sessions = shutdown_app
                .sessions
                .read()
                .await
                .values()
                .cloned()
                .collect::<Vec<_>>();
            for session in sessions {
                session.close().await;
            }
        })
        .await?;
    janitor.abort();
    for (_, s) in app.sessions.write().await.drain() {
        s.close().await;
    }
    Ok(())
}
async fn cli(args: &Args, command: &Command) -> Result<()> {
    let mut url = url::Url::parse(&args.http_url)?;
    if let Some(host) = &args.http_host {
        url.set_host(Some(host))?;
    }
    if let Some(port) = args.http_port {
        url.set_port(Some(port))
            .map_err(|_| anyhow!("Invalid port"))?;
    }
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(300))
        .build()?;
    let request = match command {
        Command::Call {
            name,
            parameters,
            instance,
        } => {
            url.set_path("/api/command");
            client.post(url).json(&json!({"type":name,"params":serde_json::from_str::<Value>(parameters)?,"unity_instance":instance}))
        }
        Command::Instances => {
            url.set_path("/api/instances");
            client.get(url)
        }
        Command::Status => {
            url.set_path("/health");
            client.get(url)
        }
    };
    let response = request.send().await?;
    let success = response.status().is_success();
    let value: Value = response.json().await?;
    println!("{}", serde_json::to_string_pretty(&value)?);
    if !success {
        return Err(anyhow!("Server returned a failure response"));
    }
    Ok(())
}
#[tokio::main]
async fn main() -> Result<()> {
    let args = Args::parse();
    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "warn".into()),
        )
        .init();
    if let Some(command) = &args.command {
        return cli(&args, command).await;
    }
    if let Some(path) = &args.pidfile {
        if let Some(parent) = path.parent() {
            tokio::fs::create_dir_all(parent).await?;
        }
        tokio::fs::write(path, std::process::id().to_string()).await?;
    }
    let result = if args.transport == "http" {
        http(args.clone()).await
    } else {
        stdio(&args).await
    };
    if let Some(path) = &args.pidfile {
        if tokio::fs::read_to_string(path).await.ok().as_deref()
            == Some(&std::process::id().to_string())
        {
            let _ = tokio::fs::remove_file(path).await;
        }
    }
    result
}

// A GUI-launched Windows process may have no console signal source. Registration
// failure is not a shutdown request: the owning Unity process can still stop it.
async fn wait_for_shutdown_signal(signal: impl std::future::Future<Output = std::io::Result<()>>) {
    if let Err(error) = signal.await {
        tracing::warn!(%error, "Console shutdown signal unavailable; continuing to serve");
        std::future::pending::<()>().await;
    }
}

#[cfg(test)]
mod tests {
    #[tokio::test]
    async fn unavailable_console_signal_does_not_stop_http_server() {
        let signal = std::future::ready(Err(std::io::Error::new(
            std::io::ErrorKind::Unsupported,
            "no console",
        )));
        assert!(
            tokio::time::timeout(Duration::from_millis(20), wait_for_shutdown_signal(signal),)
                .await
                .is_err()
        );
    }

    #[tokio::test]
    async fn actual_console_signal_allows_shutdown() {
        tokio::time::timeout(
            Duration::from_secs(1),
            wait_for_shutdown_signal(std::future::ready(Ok(()))),
        )
        .await
        .expect("a received signal should initiate shutdown");
    }

    use super::*;
    struct Hanging;
    #[async_trait::async_trait]
    impl UnityBridge for Hanging {
        async fn send(&self, _: &str, _: Value, _: Option<&str>) -> Result<Value> {
            std::future::pending().await
        }
        async fn instances(&self) -> Result<Value> {
            Ok(json!({"instances":[]}))
        }
    }
    #[tokio::test]
    async fn dropped_http_request_aborts_and_cleans_job() {
        let s = Session::new(
            "test".into(),
            Arc::new(Hanging),
            None,
            "local".into(),
            false,
            false,
            false,
            None,
        );
        let request = json!({"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"read_console","arguments":{}}});
        let clone = s.clone();
        let handler = tokio::spawn(async move { execute(clone, request).await });
        for _ in 0..100 {
            if !s.jobs.lock().unwrap().is_empty() {
                break;
            }
            tokio::task::yield_now().await;
        }
        assert_eq!(s.jobs.lock().unwrap().len(), 1);
        handler.abort();
        let _ = handler.await;
        for _ in 0..100 {
            if s.jobs.lock().unwrap().is_empty() {
                break;
            }
            tokio::task::yield_now().await;
        }
        assert!(s.jobs.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn closing_session_cancels_pending_jobs() {
        let s = Session::new(
            "test".into(),
            Arc::new(Hanging),
            None,
            "local".into(),
            false,
            false,
            false,
            None,
        );
        let clone = s.clone();
        let handler = tokio::spawn(async move {
            execute(clone,json!({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read_console"}})).await
        });
        for _ in 0..100 {
            if !s.jobs.lock().unwrap().is_empty() {
                break;
            }
            tokio::task::yield_now().await;
        }
        s.close().await;
        let result = handler.await.unwrap().unwrap();
        assert_eq!(result["error"]["code"], -32800);
        assert!(s.jobs.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn closed_session_rejects_late_admission() {
        let s = Session::new(
            "test".into(),
            Arc::new(Hanging),
            None,
            "local".into(),
            false,
            false,
            false,
            None,
        );
        s.close().await;
        let response = execute(s.clone(), json!({"jsonrpc":"2.0","id":1,"method":"ping"}))
            .await
            .unwrap();
        assert_eq!(response["error"]["code"], -32000);
        assert!(s.jobs.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn cancellation_reserves_id_until_old_task_is_destroyed() {
        let s = Session::new(
            "test".into(),
            Arc::new(Hanging),
            None,
            "local".into(),
            false,
            false,
            false,
            None,
        );
        let clone = s.clone();
        let handler = tokio::spawn(async move {
            execute(clone, json!({"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"read_console"}})).await
        });
        while s.jobs.lock().unwrap().is_empty() {
            tokio::task::yield_now().await;
        }
        protocol::dispatch(
            &s,
            json!({"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}),
        )
        .await;
        // On this single-thread runtime no aborted task has been polled yet.
        assert!(s.jobs.lock().unwrap().contains_key("7"));
        let duplicate = execute(s.clone(), json!({"jsonrpc":"2.0","id":7,"method":"ping"}))
            .await
            .unwrap();
        assert_eq!(duplicate["error"]["code"], -32600);
        assert_eq!(handler.await.unwrap().unwrap()["error"]["code"], -32800);
        let reused = execute(s.clone(), json!({"jsonrpc":"2.0","id":7,"method":"ping"}))
            .await
            .unwrap();
        assert_eq!(reused["result"], json!({}));
        assert!(s.jobs.lock().unwrap().is_empty());
    }
    #[tokio::test]
    async fn session_close_ends_events_with_other_references_alive() {
        let s = Session::new(
            "test".into(),
            Arc::new(Hanging),
            None,
            "local".into(),
            false,
            false,
            false,
            None,
        );
        let retained = s.clone();
        let stream =
            BroadcastStream::new(s.events.subscribe()).take_until(async move { s.closed().await });
        tokio::pin!(stream);
        retained.close().await;
        assert!(tokio::time::timeout(Duration::from_secs(1), stream.next())
            .await
            .unwrap()
            .is_none());
    }
    #[tokio::test]
    async fn invalid_initialize_never_allocates_session() {
        let app = Arc::new(App {
            args: Args::parse_from(["unity-mcp-light"]),
            shutting_down: AtomicBool::new(false),
            local_hub: Arc::new(HubBridge::new()),
            user_hubs: Mutex::new(HashMap::new()),
            sessions: RwLock::new(HashMap::new()),
            auth_cache: Mutex::new(HashMap::new()),
            client: reqwest::Client::new(),
        });
        for request in [
            json!({"jsonrpc":"1.0","id":1,"method":"initialize"}),
            json!({"jsonrpc":"2.0","id":{},"method":"initialize"}),
            json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":"invalid"}),
        ] {
            let response = mcp_post(
                State(app.clone()),
                HeaderMap::new(),
                request.to_string().into(),
            )
            .await;
            assert_eq!(response.status(), StatusCode::BAD_REQUEST);
            assert!(!response.headers().contains_key("mcp-session-id"));
            assert!(app.sessions.read().await.is_empty());
        }
        app.shutting_down.store(true, Ordering::Release);
        let response = mcp_post(
            State(app.clone()),
            HeaderMap::new(),
            json!({"jsonrpc":"2.0","id":1,"method":"initialize"})
                .to_string()
                .into(),
        )
        .await;
        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        assert!(app.sessions.read().await.is_empty());
    }
    #[test]
    fn remote_bridge_disables_host_focus_tracking() {
        let hub = Arc::new(HubBridge::new());
        assert_ne!(hub.tracking_id(), 0);
        let remote = RemoteBridge(hub);
        assert_eq!(remote.tracking_id(), 0);
        assert!(!remote.local_filesystem_allowed());
    }
    #[test]
    fn origin_rejects_other_websites() {
        let mut h = HeaderMap::new();
        h.insert("host", "localhost:8080".parse().unwrap());
        h.insert("origin", "https://evil.example".parse().unwrap());
        assert!(!valid_origin(&h));
        h.insert("origin", "http://localhost:8080".parse().unwrap());
        assert!(valid_origin(&h));
    }
}
