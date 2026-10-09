//! Optional, bounded focus nudges for stalled Unity test jobs.
use crate::bridge::UnityBridge;
use async_trait::async_trait;
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex, OnceLock,
    },
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use tokio::sync::watch;

const MAX_JOB_NUDGES: u32 = 3;
const MAX_STATES: usize = 256;
const STATE_TTL: Duration = Duration::from_secs(3600);
static OBSERVATIONS: AtomicU64 = AtomicU64::new(1);
static TRACKER: OnceLock<Mutex<Tracker>> = OnceLock::new();

type JobKey = (u64, String, String);

#[derive(Clone, Copy)]
struct Config {
    disabled: bool,
    base_interval: Duration,
    max_interval: Duration,
    duration: Duration,
}

fn truthy(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on"
    )
}

fn positive_duration(value: Option<&str>, default: f64) -> Duration {
    value
        .and_then(|s| s.parse::<f64>().ok())
        .filter(|v| v.is_finite() && *v > 0.0)
        .and_then(|v| Duration::try_from_secs_f64(v).ok())
        .unwrap_or_else(|| Duration::from_secs_f64(default))
}

impl Config {
    fn from_env() -> Self {
        Self {
            disabled: std::env::var("UNITY_MCP_DISABLE_FOCUS_NUDGE")
                .map(|s| truthy(&s))
                .unwrap_or(false),
            base_interval: positive_duration(
                std::env::var("UNITY_MCP_NUDGE_BASE_INTERVAL_S")
                    .ok()
                    .as_deref(),
                1.0,
            ),
            max_interval: positive_duration(
                std::env::var("UNITY_MCP_NUDGE_MAX_INTERVAL_S")
                    .ok()
                    .as_deref(),
                10.0,
            ),
            duration: positive_duration(
                std::env::var("UNITY_MCP_NUDGE_DURATION_S").ok().as_deref(),
                3.0,
            ),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct Snapshot {
    progress: [u64; 4],
    attempts: u32,
    last_attempt: Option<Instant>,
}

struct JobState {
    last_seen: Instant,
    snapshot: Snapshot,
    latest_observation: u64,
    run_in_background: bool,
    editor_is_focused: bool,
    task: Option<(u64, watch::Sender<bool>)>,
}

#[derive(Default)]
struct Tracker {
    jobs: HashMap<JobKey, JobState>,
    terminal: HashMap<JobKey, Instant>,
    active: Option<u64>,
    next_task: u64,
}

fn progress(data: &mut Value) -> &mut Value {
    if !data["progress"].is_object() {
        data["progress"] = json!({});
    }
    &mut data["progress"]
}

impl Tracker {
    fn observe(
        &mut self,
        key: &JobKey,
        data: &mut Value,
        observation: u64,
        now: Instant,
        wall_ms: u64,
        config: Config,
    ) -> Option<Snapshot> {
        self.terminal
            .retain(|_, seen| now.saturating_duration_since(*seen) <= STATE_TTL);
        self.jobs.retain(|_, state| {
            state.task.is_some() || now.saturating_duration_since(state.last_seen) <= STATE_TTL
        });
        match data["status"].as_str() {
            Some("succeeded" | "failed" | "cancelled") => {
                self.terminal.insert(key.clone(), now);
                if self.terminal.len() > MAX_STATES {
                    if let Some(oldest) = self
                        .terminal
                        .iter()
                        .min_by_key(|(_, at)| *at)
                        .map(|(key, _)| key.clone())
                    {
                        self.terminal.remove(&oldest);
                    }
                }
                if let Some(state) = self.jobs.remove(key) {
                    if let Some((_, cancel)) = state.task {
                        let _ = cancel.send(true);
                    }
                }
                return None;
            }
            Some("running") => {}
            _ => return None,
        }
        if self.terminal.contains_key(key) {
            progress(data)["focus_nudge_status"] = json!("terminal_already_observed");
            return None;
        }
        if !self.jobs.contains_key(key) && self.jobs.len() >= MAX_STATES {
            progress(data)["focus_nudge_status"] = json!("tracking_limit");
            return None;
        }
        let state = self.jobs.entry(key.clone()).or_insert_with(|| JobState {
            last_seen: now,
            snapshot: Snapshot {
                progress: [0; 4],
                attempts: 0,
                last_attempt: None,
            },
            latest_observation: 0,
            run_in_background: false,
            editor_is_focused: true,
            task: None,
        });
        state.last_seen = now;
        let current = [
            data["last_update_unix_ms"].as_u64(),
            data["progress"]["completed"].as_u64(),
            data["progress"]["current_test_started_unix_ms"].as_u64(),
            data["progress"]["last_finished_unix_ms"].as_u64(),
        ];
        let mut advanced = false;
        for (previous, value) in state.snapshot.progress.iter_mut().zip(current) {
            if let Some(value) = value.filter(|value| value > previous) {
                *previous = value;
                advanced = true;
            }
        }
        if advanced {
            state.snapshot.attempts = 0;
            state.snapshot.last_attempt = None;
        }
        if observation > state.latest_observation {
            state.latest_observation = observation;
            state.run_in_background = data["progress"]["run_in_background"] == true;
            state.editor_is_focused = data["progress"]["editor_is_focused"]
                .as_bool()
                .unwrap_or(true);
        }
        // A newly observed recovery owns cancellation of an outstanding nudge.
        // The child still restores the previous app and releases its token.
        if advanced || state.run_in_background || state.editor_is_focused {
            if let Some((_, cancel)) = &state.task {
                let _ = cancel.send(true);
            }
        }
        let p = progress(data);
        p["focus_nudge_attempts"] = json!(state.snapshot.attempts);
        p["focus_nudge_limit"] = json!(MAX_JOB_NUDGES);
        if state.snapshot.attempts >= MAX_JOB_NUDGES {
            p["stuck_suspected"] = json!(true);
            p["focus_nudge_status"] = json!("attempt_limit_reached");
            return None;
        }
        if state.run_in_background {
            p["focus_nudge_status"] = json!("background_execution_enabled");
            return None;
        }
        if config.disabled
            || state.editor_is_focused
            || (state.snapshot.progress[0] != 0
                && wall_ms.saturating_sub(state.snapshot.progress[0]) <= 3000)
            || self.active.is_some()
        {
            return None;
        }
        let interval = config
            .base_interval
            .saturating_mul(1 << state.snapshot.attempts)
            .min(config.max_interval);
        if state
            .snapshot
            .last_attempt
            .is_some_and(|at| now.saturating_duration_since(at) < interval)
        {
            return None;
        }
        Some(state.snapshot)
    }

    fn reserve(
        &mut self,
        key: &JobKey,
        snapshot: Snapshot,
        now: Instant,
    ) -> Option<(u64, watch::Receiver<bool>, watch::Sender<bool>)> {
        let state = self.jobs.get_mut(key)?;
        // Project lookup can await registry locks while a newer poll arrives.
        if self.active.is_some()
            || state.snapshot != snapshot
            || state.run_in_background
            || state.editor_is_focused
        {
            return None;
        }
        self.next_task += 1;
        let token = self.next_task;
        let (cancel, receiver) = watch::channel(false);
        state.snapshot.attempts += 1;
        state.snapshot.last_attempt = Some(now);
        state.task = Some((token, cancel.clone()));
        self.active = Some(token);
        Some((token, receiver, cancel))
    }

    fn finished(&mut self, key: &JobKey, token: u64) {
        if self.active == Some(token) {
            self.active = None;
        }
        if let Some(state) = self.jobs.get_mut(key) {
            if state.task.as_ref().map(|(id, _)| *id) == Some(token) {
                state.task = None;
            }
        }
    }
}

fn tracker() -> &'static Mutex<Tracker> {
    TRACKER.get_or_init(|| Mutex::new(Tracker::default()))
}

/// Capture before requesting job status, so older replies cannot overwrite focus state.
pub fn next_observation() -> u64 {
    OBSERVATIONS.fetch_add(1, Ordering::Relaxed)
}

pub async fn update_job_nudge(
    bridge: &dyn UnityBridge,
    instance: Option<&str>,
    job_id: &str,
    data: &mut Value,
    wait: bool,
) {
    update_job_nudge_observed(bridge, instance, job_id, data, wait, next_observation()).await;
}

pub async fn update_job_nudge_observed(
    bridge: &dyn UnityBridge,
    instance: Option<&str>,
    job_id: &str,
    data: &mut Value,
    wait: bool,
    observation: u64,
) {
    // A remote project's path is not authority to change this host's desktop.
    if !bridge.local_filesystem_allowed() {
        return;
    }
    let Some(instance) = instance.filter(|s| !s.is_empty()) else {
        return;
    };
    if !data.is_object() {
        return;
    }
    let hash = instance.rsplit('@').next().unwrap_or("");
    if hash.is_empty() {
        return;
    }
    // Heap addresses can be reused while terminal/attempt records are retained.
    // Only a lifetime-stable, non-reused bridge ID may own these records.
    let owner = bridge.tracking_id();
    if owner == 0 {
        return;
    }
    let key = (owner, hash.to_owned(), job_id.to_owned());
    let config = Config::from_env();
    let wall_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u64::MAX as u128) as u64;
    let snapshot =
        tracker()
            .lock()
            .unwrap()
            .observe(&key, data, observation, Instant::now(), wall_ms, config);
    let Some(snapshot) = snapshot else { return };
    // Linux deliberately stays a no-op, even when xdotool is installed.
    if !cfg!(any(target_os = "macos", target_os = "windows")) {
        return;
    }
    let path = bridge
        .instances()
        .await
        .ok()
        .and_then(|instances| project_path(&instances, instance));
    let Some(path) = path else {
        progress(data)["focus_nudge_status"] = json!("project_path_unavailable");
        return;
    };
    let reservation = tracker()
        .lock()
        .unwrap()
        .reserve(&key, snapshot, Instant::now());
    let Some((token, receiver, cancel)) = reservation else {
        return;
    };
    progress(data)["focus_nudge_attempts"] = json!(snapshot.attempts + 1);
    progress(data)["focus_nudge_status"] = json!("scheduled");
    let task = tokio::spawn(async move {
        perform_nudge(&OsBackend, &path, config.duration, receiver).await;
        tracker().lock().unwrap().finished(&key, token);
    });
    if wait {
        // Dropping the request cancels the wait, but restoration still runs in the child.
        let mut cancellation = CancelOnDrop(Some(cancel));
        let _ = task.await;
        cancellation.0 = None;
    }
}

struct CancelOnDrop(Option<watch::Sender<bool>>);
impl Drop for CancelOnDrop {
    fn drop(&mut self) {
        if let Some(cancel) = &self.0 {
            let _ = cancel.send(true);
        }
    }
}

fn normalized_path(path: &str, strip_assets: bool) -> Option<String> {
    if path.is_empty() || path.contains('\0') {
        return None;
    }
    let windows = path.contains('\\') || path.as_bytes().get(1) == Some(&b':');
    let source = if windows {
        path.replace('\\', "/")
    } else {
        path.to_owned()
    };
    let prefix = if windows && source.starts_with("//") {
        "//".to_owned()
    } else if windows
        && source.len() >= 3
        && source.as_bytes()[0].is_ascii_alphabetic()
        && &source[1..3] == ":/"
    {
        source[..3].to_owned()
    } else if !windows && source.starts_with('/') {
        "/".to_owned()
    } else {
        return None;
    };
    let mut parts = Vec::new();
    for part in source[prefix.len()..].split('/') {
        match part {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            _ => parts.push(part),
        }
    }
    if strip_assets
        && parts
            .last()
            .is_some_and(|s| s.eq_ignore_ascii_case("assets"))
    {
        parts.pop();
    }
    let normalized = format!("{prefix}{}", parts.join("/"));
    Some(if windows {
        normalized.replace('/', "\\")
    } else {
        normalized
    })
}

fn project_path(instances: &Value, instance: &str) -> Option<String> {
    let hash = instance.rsplit('@').next()?;
    let entries = instances
        .as_array()
        .or_else(|| instances["instances"].as_array())?;
    let matches: Vec<_> = entries
        .iter()
        .filter(|entry| {
            entry["id"].as_str() == Some(instance) || entry["hash"].as_str() == Some(hash)
        })
        .collect();
    if matches.len() != 1 {
        return None;
    }
    let item = matches[0];
    normalized_path(item["path"].as_str()?, item.get("port").is_some())
}

struct Frontmost {
    name: String,
    id: i64,
}

#[async_trait]
trait NudgeBackend: Send + Sync {
    async fn frontmost(&self) -> Option<Frontmost>;
    async fn activate_unity(&self, project: &str) -> bool;
    async fn restore(&self, app: &Frontmost) -> bool;
}

async fn perform_nudge(
    backend: &dyn NudgeBackend,
    path: &str,
    duration: Duration,
    mut cancel: watch::Receiver<bool>,
) {
    if *cancel.borrow() {
        return;
    }
    let Some(original) = backend.frontmost().await else {
        return;
    };
    if *cancel.borrow() || (!cfg!(target_os = "windows") && original.name.contains("Unity")) {
        return;
    }
    // Activation may change focus even when it reports failure; always restore afterwards.
    if backend.activate_unity(path).await && !*cancel.borrow() {
        tokio::select! {
            _ = tokio::time::sleep(duration.saturating_add(Duration::from_millis(500))) => {},
            _ = cancel.changed() => {},
        }
    }
    if !backend.restore(&original).await {
        tracing::warn!("Could not restore focus after Unity test nudge");
    }
}

struct OsBackend;
#[async_trait]
impl NudgeBackend for OsBackend {
    async fn frontmost(&self) -> Option<Frontmost> {
        #[cfg(target_os = "macos")]
        {
            return platform::mac_frontmost().await;
        }
        #[cfg(target_os = "windows")]
        {
            return platform::windows_frontmost().await;
        }
        #[cfg(not(any(target_os = "macos", target_os = "windows")))]
        {
            None
        }
    }
    async fn activate_unity(&self, _project: &str) -> bool {
        #[cfg(target_os = "macos")]
        {
            return platform::mac_activate_unity(_project).await;
        }
        #[cfg(target_os = "windows")]
        {
            return platform::windows_activate_unity(_project).await;
        }
        #[cfg(not(any(target_os = "macos", target_os = "windows")))]
        {
            false
        }
    }
    async fn restore(&self, _app: &Frontmost) -> bool {
        #[cfg(target_os = "macos")]
        {
            return platform::mac_activate_pid(_app.id).await;
        }
        #[cfg(target_os = "windows")]
        {
            return platform::windows_activate_handle(_app.id).await;
        }
        #[cfg(not(any(target_os = "macos", target_os = "windows")))]
        {
            let _ = _app.id;
            false
        }
    }
}

#[cfg(any(test, target_os = "macos", target_os = "windows"))]
#[allow(dead_code)]
mod platform {
    use super::{normalized_path, Frontmost};
    use serde_json::Value;
    use std::time::Duration;
    use tokio::process::Command;

    async fn command(program: &str, args: &[&str]) -> Option<String> {
        let mut command = Command::new(program);
        command.args(args).kill_on_drop(true);
        #[cfg(target_os = "windows")]
        command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
        use std::process::Stdio;
        use tokio::io::AsyncReadExt;
        command
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .stdin(Stdio::null());
        let mut child = command.spawn().ok()?;
        tokio::time::timeout(Duration::from_secs(5), async move {
            const MAX_OUTPUT: u64 = 1024 * 1024;
            let stdout = child.stdout.take()?;
            let mut output = Vec::new();
            stdout
                .take(MAX_OUTPUT + 1)
                .read_to_end(&mut output)
                .await
                .ok()?;
            if output.len() as u64 > MAX_OUTPUT {
                return None;
            }
            child
                .wait()
                .await
                .ok()?
                .success()
                .then(|| String::from_utf8_lossy(&output).trim().to_owned())
        })
        .await
        .ok()
        .flatten()
    }

    async fn osascript(script: &str) -> Option<String> {
        command("osascript", &["-e", script]).await
    }
    async fn powershell(script: &str) -> Option<String> {
        command(
            "powershell",
            &["-NoProfile", "-NonInteractive", "-Command", script],
        )
        .await
    }

    pub(super) async fn mac_frontmost() -> Option<Frontmost> {
        let output = osascript(
            r#"tell application "System Events"
    set frontProc to first process whose frontmost is true
    return ((unix id of frontProc) as text) & linefeed & (name of frontProc)
end tell"#,
        )
        .await?;
        let (pid, name) = output.split_once('\n')?;
        let id = pid.trim().parse::<i64>().ok().filter(|id| *id > 0)?;
        Some(Frontmost {
            name: name.to_owned(),
            id,
        })
    }

    pub(super) async fn mac_activate_pid(pid: i64) -> bool {
        if pid <= 0 {
            return false;
        }
        osascript(&format!("tell application \"System Events\" to set frontmost of (first process whose unix id is {pid}) to true")).await.is_some()
    }

    fn mac_unity_pid(output: &str, project: &str) -> Option<i64> {
        // Anchor the complete argument so /Project never selects /ProjectOther.
        let escaped = regex::escape(project);
        let argument = regex::Regex::new(&format!(
            r#"(?i)(?:^|\s)-projectpath(?:=|\s+)(?:"{escaped}"|'{escaped}'|{escaped})(?:$|\s+-)"#
        ))
        .ok()?;
        let mut matches = std::collections::HashSet::new();
        for line in output.lines() {
            let Some((pid, cmd)) = line.trim().split_once(char::is_whitespace) else {
                continue;
            };
            if !cmd.contains("Unity.app/Contents/MacOS/Unity") || !argument.is_match(cmd) {
                continue;
            }
            if let Some(pid) = pid.parse::<i64>().ok().filter(|pid| *pid > 0) {
                matches.insert(pid);
            }
        }
        if matches.len() == 1 {
            matches.into_iter().next()
        } else {
            None
        }
    }

    pub(super) async fn mac_activate_unity(project: &str) -> bool {
        let Some(processes) = command("ps", &["-axo", "pid=,command="]).await else {
            return false;
        };
        let Some(pid) = mac_unity_pid(&processes, project) else {
            return false;
        };
        // Wake the selected process, then activate its bundle as in the Python server.
        osascript(&format!(
            r#"tell application "System Events"
    set targetProc to first process whose unix id is {pid}
    set frontmost of targetProc to true
    set bundleID to bundle identifier of targetProc
end tell
tell application id bundleID to activate"#
        ))
        .await
        .is_some()
    }

    pub(super) async fn windows_frontmost() -> Option<Frontmost> {
        let output = powershell(WINDOWS_FRONTMOST).await?;
        let info: Value = serde_json::from_str(&output).ok()?;
        let id = info["window_handle"].as_i64().filter(|id| *id > 0)?;
        Some(Frontmost {
            name: info["name"].as_str().unwrap_or("").to_owned(),
            id,
        })
    }

    fn windows_unity_pid(processes: &Value, project: &str) -> Option<u64> {
        let target = normalized_path(project, false)?.to_lowercase();
        let entries = match processes {
            Value::Array(entries) => entries.iter().collect::<Vec<_>>(),
            Value::Object(_) => vec![processes],
            _ => return None,
        };
        let mut matches = std::collections::HashSet::new();
        for process in entries {
            let Some(arguments) = process["Arguments"].as_array() else {
                continue;
            };
            let Some(arguments) = arguments
                .iter()
                .map(Value::as_str)
                .collect::<Option<Vec<_>>>()
            else {
                continue;
            };
            let mut projects = Vec::new();
            for (index, argument) in arguments.iter().enumerate().skip(1) {
                if argument.eq_ignore_ascii_case("-projectpath") {
                    projects.push(arguments.get(index + 1).copied().unwrap_or(""));
                } else if argument
                    .get(..13)
                    .is_some_and(|prefix| prefix.eq_ignore_ascii_case("-projectpath="))
                {
                    projects.push(&argument[13..]);
                }
            }
            if projects.len() != 1
                || normalized_path(projects[0], false).map(|p| p.to_lowercase())
                    != Some(target.clone())
            {
                continue;
            }
            if let Some(pid) = process["ProcessId"].as_u64().filter(|pid| *pid > 0) {
                matches.insert(pid);
            }
        }
        if matches.len() == 1 {
            matches.into_iter().next()
        } else {
            None
        }
    }

    pub(super) async fn windows_activate_unity(project: &str) -> bool {
        let Some(output) = powershell(WINDOWS_PROCESSES).await else {
            return false;
        };
        let Ok(processes) = serde_json::from_str::<Value>(&output) else {
            return false;
        };
        let Some(pid) = windows_unity_pid(&processes, project) else {
            return false;
        };
        let target =
            format!("$targetHwnd = (Get-Process -Id {pid} -ErrorAction Stop).MainWindowHandle\n");
        powershell(&format!(
            "{WINDOWS_ACTIVATE_PREFIX}{target}{WINDOWS_ACTIVATE_SUFFIX}"
        ))
        .await
        .is_some()
    }

    pub(super) async fn windows_activate_handle(handle: i64) -> bool {
        if handle <= 0 {
            return false;
        }
        let target = format!("$targetHwnd = [IntPtr]{handle}\n");
        powershell(&format!(
            "{WINDOWS_ACTIVATE_PREFIX}{target}{WINDOWS_ACTIVATE_SUFFIX}"
        ))
        .await
        .is_some()
    }

    const WINDOWS_FRONTMOST: &str = r###"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);
}
"@
$hwnd = [Win32]::GetForegroundWindow()
if ($hwnd -eq [IntPtr]::Zero) { exit 1 }
$length = [Win32]::GetWindowTextLengthW($hwnd) + 1
$sb = New-Object System.Text.StringBuilder $length
[void][Win32]::GetWindowTextW($hwnd, $sb, $length)
@{ name = $sb.ToString(); window_handle = $hwnd.ToInt64() } | ConvertTo-Json -Compress
"###;

    const WINDOWS_PROCESSES: &str = r###"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public class UnityCommandLine {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    public static string[] Parse(string commandLine) {
        if (String.IsNullOrWhiteSpace(commandLine)) return new string[0];
        int count;
        IntPtr argv = CommandLineToArgvW(commandLine, out count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            string[] args = new string[count];
            for (int i = 0; i < count; i++) {
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
            }
            return args;
        } finally {
            LocalFree(argv);
        }
    }
}
"@
$processes = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | ForEach-Object {
    [PSCustomObject]@{
        ProcessId = $_.ProcessId
        Arguments = [UnityCommandLine]::Parse($_.CommandLine)
    }
})
ConvertTo-Json -InputObject $processes -Depth 3 -Compress
"###;

    const WINDOWS_ACTIVATE_PREFIX: &str = r###"
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
"@
"###;

    const WINDOWS_ACTIVATE_SUFFIX: &str = r###"
if (-not [Win32]::IsWindow($targetHwnd)) { exit 1 }
if ([Win32]::IsIconic($targetHwnd)) {
    [void][Win32]::ShowWindow($targetHwnd, 9)
}
if (-not [Win32]::SetForegroundWindow($targetHwnd)) { exit 1 }
if ([Win32]::GetForegroundWindow() -ne $targetHwnd) { exit 1 }
exit 0
"###;

    #[cfg(test)]
    mod tests {
        use super::*;
        use serde_json::json;

        #[cfg(unix)]
        #[tokio::test]
        async fn native_command_output_is_bounded() {
            assert_eq!(
                command("sh", &["-c", "printf hello"]).await.as_deref(),
                Some("hello")
            );
            assert!(command("sh", &["-c", "head -c 1048577 /dev/zero"])
                .await
                .is_none());
            assert!(command("sh", &["-c", "exit 1"]).await.is_none());
        }
        #[test]
        fn windows_resolution_requires_one_exact_absolute_project_argument() {
            let processes = json!([
                {"ProcessId":123,"Arguments":["Unity.exe", "-projectPath", "C:\\Projects\\Game"]},
                {"ProcessId":456,"Arguments":["Unity.exe", "-projectPath", "C:\\Projects\\Game Extra"]}
            ]);
            assert_eq!(windows_unity_pid(&processes, "c:/Projects/Game"), Some(123));
            assert_eq!(windows_unity_pid(&processes, "Game"), None);
            assert_eq!(
                windows_unity_pid(
                    &json!([{ "ProcessId":1,"Arguments":["Unity.exe","-projectPath=C:\\Game"]}]),
                    "C:\\Game"
                ),
                Some(1)
            );
            assert_eq!(
                windows_unity_pid(
                    &json!([{ "ProcessId":1,"Arguments":["Unity.exe","-projectPath","C:\\Game","-projectPath=C:\\Game"]}]),
                    "C:\\Game"
                ),
                None
            );
            let duplicate = json!([
                {"ProcessId":1,"Arguments":["Unity.exe","-projectPath","C:\\Game"]},
                {"ProcessId":2,"Arguments":["Unity.exe","-projectPath","C:\\Game"]}
            ]);
            assert_eq!(windows_unity_pid(&duplicate, "C:\\Game"), None);
            assert_eq!(
                windows_unity_pid(
                    &json!([{ "ProcessId":1,"Arguments":["Unity.exe","-other","contains -projectPath C:\\Game"]}]),
                    "C:\\Game"
                ),
                None
            );
        }

        #[test]
        fn mac_resolution_does_not_match_project_prefixes_or_ambiguous_instances() {
            let command = "45 /Applications/Unity.app/Contents/MacOS/Unity -projectpath /tmp/My Game -batchmode";
            assert_eq!(mac_unity_pid(command, "/tmp/My Game"), Some(45));
            assert_eq!(mac_unity_pid(command, "/tmp/My"), None);
            assert_eq!(
                mac_unity_pid(
                    "45 /Applications/Unity.app/Contents/MacOS/Unity -projectPath \"/tmp/My Game\"",
                    "/tmp/My Game"
                ),
                Some(45)
            );
            assert_eq!(mac_unity_pid(&format!("{command}\n46 /Applications/Unity.app/Contents/MacOS/Unity -projectpath /tmp/My Game"), "/tmp/My Game"), None);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{
        atomic::{AtomicI64, AtomicUsize},
        Arc,
    };

    fn config() -> Config {
        Config {
            disabled: false,
            base_interval: Duration::from_secs(1),
            max_interval: Duration::from_secs(10),
            duration: Duration::from_secs(3),
        }
    }
    fn key(job: &str) -> JobKey {
        (1, "instance".into(), job.into())
    }
    fn running() -> Value {
        json!({"status":"running","last_update_unix_ms":1000,"progress":{"editor_is_focused":false,"completed":0}})
    }

    #[test]
    fn new_bridge_identity_does_not_inherit_terminal_or_attempt_state() {
        let mut tracker = Tracker::default();
        let now = Instant::now();
        let first = (
            crate::bridge::next_tracking_id(),
            "instance".into(),
            "job".into(),
        );
        let second = (
            crate::bridge::next_tracking_id(),
            "instance".into(),
            "job".into(),
        );
        assert_ne!(first.0, second.0);
        tracker.observe(
            &first,
            &mut json!({"status":"succeeded"}),
            1,
            now,
            10000,
            config(),
        );
        assert!(tracker
            .observe(&first, &mut running(), 2, now, 10000, config())
            .is_none());
        assert!(tracker
            .observe(&second, &mut running(), 3, now, 10000, config())
            .is_some());
        assert_eq!(tracker.jobs[&second].snapshot.attempts, 0);
    }
    #[test]
    fn recovered_job_cancels_outstanding_nudge_but_stale_focus_does_not() {
        for recovery in ["focused", "background", "progress"] {
            let mut tracker = Tracker::default();
            let now = Instant::now();
            let mut data = running();
            let snapshot = tracker
                .observe(&key("job"), &mut data, 2, now, 10000, config())
                .unwrap();
            let (token, receiver, _) = tracker.reserve(&key("job"), snapshot, now).unwrap();
            let mut stale = running();
            stale["progress"]["editor_is_focused"] = json!(true);
            tracker.observe(&key("job"), &mut stale, 1, now, 10000, config());
            assert!(!*receiver.borrow());
            match recovery {
                "focused" => data["progress"]["editor_is_focused"] = json!(true),
                "background" => data["progress"]["run_in_background"] = json!(true),
                _ => data["progress"]["completed"] = json!(1),
            }
            tracker.observe(&key("job"), &mut data, 3, now, 10000, config());
            assert!(*receiver.borrow(), "{recovery}");
            assert_eq!(tracker.active, Some(token)); // Restoration owns release.
            tracker.finished(&key("job"), token);
            assert!(tracker.active.is_none());
        }
    }
    #[test]
    fn disable_environment_values_and_positive_durations_match_python_contract() {
        for value in ["1", " true ", "YES", "On"] {
            assert!(truthy(value));
        }
        for value in ["", "0", "false", "off", "enabled"] {
            assert!(!truthy(value));
        }
        for value in [
            None,
            Some("bad"),
            Some("0"),
            Some("-2"),
            Some("NaN"),
            Some("inf"),
            Some("1e100"),
        ] {
            assert_eq!(positive_duration(value, 3.0), Duration::from_secs(3));
        }
        assert_eq!(
            positive_duration(Some("0.25"), 3.0),
            Duration::from_millis(250)
        );
    }

    #[test]
    fn focus_background_disable_and_recent_progress_gate_nudges() {
        let now = Instant::now();
        for (focused, background, disabled, last_update) in [
            (true, false, false, 1000),
            (false, true, false, 1000),
            (false, false, true, 1000),
            (false, false, false, 8000),
            (false, false, false, 12000),
        ] {
            let mut tracker = Tracker::default();
            let mut data = running();
            data["progress"]["editor_is_focused"] = json!(focused);
            data["progress"]["run_in_background"] = json!(background);
            data["last_update_unix_ms"] = json!(last_update);
            let cfg = Config {
                disabled,
                ..config()
            };
            assert!(tracker
                .observe(&key("job"), &mut data, 1, now, 10000, cfg)
                .is_none());
            assert_eq!(data["progress"]["focus_nudge_attempts"], 0);
        }
        let mut data = running();
        data["last_update_unix_ms"] = Value::Null;
        assert!(Tracker::default()
            .observe(&key("job"), &mut data, 1, now, 10000, config())
            .is_some());
    }

    #[test]
    fn budget_is_bounded_and_only_new_progress_resets_it() {
        let mut tracker = Tracker::default();
        let mut data = running();
        let key = key("job");
        let base = Instant::now();
        for attempt in 0..MAX_JOB_NUDGES {
            let now = base + Duration::from_secs(attempt as u64 * 20);
            let snapshot = tracker
                .observe(&key, &mut data, attempt as u64 + 1, now, 10000, config())
                .unwrap();
            let (token, _, _) = tracker.reserve(&key, snapshot, now).unwrap();
            tracker.finished(&key, token);
        }
        assert!(tracker
            .observe(
                &key,
                &mut data,
                4,
                base + Duration::from_secs(80),
                10000,
                config()
            )
            .is_none());
        assert_eq!(
            data["progress"]["focus_nudge_status"],
            "attempt_limit_reached"
        );
        assert_eq!(data["progress"]["focus_nudge_attempts"], 3);
        assert_eq!(data["progress"]["stuck_suspected"], true);
        data["last_update_unix_ms"] = json!(999);
        assert!(tracker
            .observe(
                &key,
                &mut data,
                5,
                base + Duration::from_secs(90),
                10000,
                config()
            )
            .is_none());
        data["progress"]["completed"] = json!(1);
        let snapshot = tracker
            .observe(
                &key,
                &mut data,
                6,
                base + Duration::from_secs(100),
                10000,
                config(),
            )
            .unwrap();
        assert_eq!(snapshot.attempts, 0);
        assert_eq!(snapshot.progress, [1000, 1, 0, 0]);
        assert_eq!(snapshot.last_attempt, None);
    }

    #[test]
    fn observations_preserve_newer_focus_and_monotonic_progress() {
        let mut tracker = Tracker::default();
        let now = Instant::now();
        let mut newer = running();
        newer["progress"]["editor_is_focused"] = json!(true);
        newer["progress"]["completed"] = json!(3);
        assert!(tracker
            .observe(&key("job"), &mut newer, 2, now, 10000, config())
            .is_none());
        let mut older = running();
        older["progress"]["completed"] = json!(1);
        assert!(tracker
            .observe(&key("job"), &mut older, 1, now, 10000, config())
            .is_none());
        let state = &tracker.jobs[&key("job")];
        assert!(state.editor_is_focused);
        assert_eq!(state.snapshot.progress[1], 3);
        assert_eq!(state.latest_observation, 2);
        assert!(tracker
            .observe(&key("job"), &mut older, 3, now, 10000, config())
            .is_some());
    }

    #[test]
    fn terminal_observation_cancels_worker_and_blocks_late_running_reply() {
        let mut tracker = Tracker::default();
        let mut data = running();
        let now = Instant::now();
        let key = key("job");
        let snapshot = tracker
            .observe(&key, &mut data, 1, now, 10000, config())
            .unwrap();
        let (token, cancel, _) = tracker.reserve(&key, snapshot, now).unwrap();
        assert!(tracker
            .observe(
                &key,
                &mut json!({"status":"succeeded"}),
                3,
                now,
                10000,
                config()
            )
            .is_none());
        assert!(*cancel.borrow());
        assert!(!tracker.jobs.contains_key(&key));
        assert_eq!(tracker.active, Some(token)); // Remains reserved until restoration.
        assert!(tracker
            .observe(&key, &mut data, 2, now, 10000, config())
            .is_none());
        assert_eq!(
            data["progress"]["focus_nudge_status"],
            "terminal_already_observed"
        );
        tracker.finished(&key, token);
        assert!(tracker.active.is_none());
    }

    #[test]
    fn reservation_rechecks_progress_focus_and_global_desktop_lock() {
        let mut tracker = Tracker::default();
        let now = Instant::now();
        let mut data = running();
        let key = key("job");
        let stale = tracker
            .observe(&key, &mut data, 1, now, 10000, config())
            .unwrap();
        data["progress"]["completed"] = json!(1);
        let fresh = tracker
            .observe(&key, &mut data, 2, now, 10000, config())
            .unwrap();
        assert!(tracker.reserve(&key, stale, now).is_none());
        data["progress"]["editor_is_focused"] = json!(true);
        tracker.observe(&key, &mut data, 3, now, 10000, config());
        assert!(tracker.reserve(&key, fresh, now).is_none());
        data["progress"]["editor_is_focused"] = json!(false);
        let fresh = tracker
            .observe(&key, &mut data, 4, now, 10000, config())
            .unwrap();
        let (token, _, _) = tracker.reserve(&key, fresh, now).unwrap();
        assert!(tracker
            .observe(
                &(2, "instance".into(), "other".into()),
                &mut running(),
                5,
                now,
                10000,
                config()
            )
            .is_none());
        tracker.finished(&key, token);
        assert!(tracker
            .observe(
                &key,
                &mut data,
                6,
                now + Duration::from_millis(1500),
                10000,
                config()
            )
            .is_none());
        assert!(tracker
            .observe(
                &key,
                &mut data,
                7,
                now + Duration::from_secs(2),
                10000,
                config()
            )
            .is_some());
    }

    #[test]
    fn capacity_never_renews_running_job_budget_and_ttl_retains_live_tasks() {
        let mut tracker = Tracker::default();
        let now = Instant::now();
        for i in 0..MAX_STATES {
            tracker.observe(
                &key(&i.to_string()),
                &mut running(),
                1,
                now,
                10000,
                config(),
            );
        }
        let mut overflow = running();
        assert!(tracker
            .observe(&key("overflow"), &mut overflow, 1, now, 10000, config())
            .is_none());
        assert_eq!(overflow["progress"]["focus_nudge_status"], "tracking_limit");
        let first = key("0");
        let snapshot = tracker.jobs[&first].snapshot;
        let (token, _, _) = tracker.reserve(&first, snapshot, now).unwrap();
        tracker.observe(
            &key("new"),
            &mut running(),
            1,
            now + STATE_TTL + Duration::from_secs(1),
            10000,
            config(),
        );
        assert_eq!(tracker.jobs.len(), 2);
        assert_eq!(tracker.jobs[&first].snapshot.attempts, 1);
        tracker.finished(&first, token);
    }

    #[test]
    fn project_resolution_is_unique_absolute_and_strips_legacy_assets_only() {
        let legacy = json!({"instances":[{"id":"Game@abc","hash":"abc","path":"/tmp/Game/Assets/","port":6400}]});
        assert_eq!(project_path(&legacy, "abc"), Some("/tmp/Game".into()));
        assert_eq!(project_path(&legacy, "Game@abc"), Some("/tmp/Game".into()));
        assert_eq!(project_path(&legacy, "other"), None);
        let http = json!({"instances":[{"id":"Game@abc","hash":"abc","path":"/tmp/Assets","session_id":"s"}]});
        assert_eq!(project_path(&http, "abc"), Some("/tmp/Assets".into()));
        assert_eq!(
            normalized_path("C:\\Game\\Assets", true),
            Some("C:\\Game".into())
        );
        assert_eq!(normalized_path("Game/Assets", true), None);
        assert_eq!(normalized_path("\\Game\\Assets", true), None);
        assert_eq!(normalized_path("C:Game", true), None);
        assert_eq!(
            normalized_path("/tmp/./Game/../Actual", false),
            Some("/tmp/Actual".into())
        );
        assert_eq!(
            project_path(
                &json!({"instances":[legacy["instances"][0],legacy["instances"][0]]}),
                "abc"
            ),
            None
        );
    }

    struct FakeBackend {
        captures: AtomicUsize,
        activations: AtomicUsize,
        restored: AtomicI64,
        activation_succeeds: bool,
        activated: tokio::sync::Notify,
    }
    impl FakeBackend {
        fn new(activation_succeeds: bool) -> Self {
            Self {
                captures: AtomicUsize::new(0),
                activations: AtomicUsize::new(0),
                restored: AtomicI64::new(0),
                activation_succeeds,
                activated: tokio::sync::Notify::new(),
            }
        }
    }
    #[async_trait]
    impl NudgeBackend for FakeBackend {
        async fn frontmost(&self) -> Option<Frontmost> {
            self.captures.fetch_add(1, Ordering::Relaxed);
            Some(Frontmost {
                name: "Code".into(),
                id: 42,
            })
        }
        async fn activate_unity(&self, _: &str) -> bool {
            self.activations.fetch_add(1, Ordering::Relaxed);
            self.activated.notify_one();
            self.activation_succeeds
        }
        async fn restore(&self, app: &Frontmost) -> bool {
            self.restored.store(app.id, Ordering::Relaxed);
            true
        }
    }

    #[tokio::test]
    async fn failed_activation_still_restores_original_window() {
        let backend = FakeBackend::new(false);
        let (_cancel, receiver) = watch::channel(false);
        perform_nudge(&backend, "/tmp/Game", Duration::from_secs(60), receiver).await;
        assert_eq!(backend.activations.load(Ordering::Relaxed), 1);
        assert_eq!(backend.restored.load(Ordering::Relaxed), 42);
    }

    #[tokio::test]
    async fn cancellation_interrupts_delay_but_preserves_restoration() {
        let backend = Arc::new(FakeBackend::new(true));
        let (cancel, receiver) = watch::channel(false);
        let task_backend = backend.clone();
        let task = tokio::spawn(async move {
            perform_nudge(
                task_backend.as_ref(),
                "/tmp/Game",
                Duration::from_secs(60),
                receiver,
            )
            .await;
        });
        backend.activated.notified().await;
        cancel.send(true).unwrap();
        tokio::time::timeout(Duration::from_secs(1), task)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(backend.restored.load(Ordering::Relaxed), 42);
    }

    #[tokio::test]
    async fn cancellation_before_start_never_touches_desktop() {
        let backend = FakeBackend::new(true);
        let (cancel, receiver) = watch::channel(false);
        cancel.send(true).unwrap();
        perform_nudge(&backend, "/tmp/Game", Duration::from_secs(60), receiver).await;
        assert_eq!(backend.captures.load(Ordering::Relaxed), 0);
        assert_eq!(backend.activations.load(Ordering::Relaxed), 0);
    }

    #[test]
    fn cancelling_waiter_requests_cooperative_cleanup() {
        let (cancel, receiver) = watch::channel(false);
        drop(CancelOnDrop(Some(cancel)));
        assert!(*receiver.borrow());
    }

    #[tokio::test]
    async fn remote_bridge_cannot_track_or_focus_host_desktop() {
        struct Remote(u64);
        #[async_trait]
        impl UnityBridge for Remote {
            fn tracking_id(&self) -> u64 {
                self.0
            }
            fn local_filesystem_allowed(&self) -> bool {
                false
            }
            async fn send(&self, _: &str, _: Value, _: Option<&str>) -> anyhow::Result<Value> {
                panic!("remote focus command")
            }
            async fn instances(&self) -> anyhow::Result<Value> {
                panic!("remote focus resolution")
            }
        }
        let id = crate::bridge::next_tracking_id();
        let mut data = running();
        update_job_nudge(&Remote(id), Some("Game@remote"), "job", &mut data, true).await;
        assert!(!tracker()
            .lock()
            .unwrap()
            .jobs
            .contains_key(&(id, "remote".into(), "job".into())));
        assert!(data["progress"]["focus_nudge_attempts"].is_null());
    }
    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    #[tokio::test]
    async fn unsupported_platform_never_resolves_or_changes_desktop() {
        struct NoCalls(u64);
        #[async_trait]
        impl UnityBridge for NoCalls {
            fn tracking_id(&self) -> u64 {
                self.0
            }
            async fn send(&self, _: &str, _: Value, _: Option<&str>) -> anyhow::Result<Value> {
                panic!("unexpected command")
            }
            async fn instances(&self) -> anyhow::Result<Value> {
                panic!("Linux must not resolve focus targets")
            }
        }
        let mut data = running();
        update_job_nudge(
            &NoCalls(crate::bridge::next_tracking_id()),
            Some("Game@focus-test-noop"),
            "linux-noop",
            &mut data,
            true,
        )
        .await;
        assert_eq!(data["progress"]["focus_nudge_attempts"], 0);
        assert_ne!(data["progress"]["focus_nudge_status"], "scheduled");
    }
}
