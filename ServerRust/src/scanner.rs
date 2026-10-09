//! Conservative, bounded external file-change detection, matching the Python server.
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    path::{Path, PathBuf},
    sync::{Mutex, OnceLock},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Default)]
struct State {
    root: Option<PathBuf>,
    last_scan: Option<u128>,
    newest: Option<u128>,
    dirty: bool,
    dirty_since: Option<u128>,
    last_seen: Option<u128>,
    last_cleared: Option<u128>,
}
impl State {
    fn value(&self) -> Value {
        json!({"external_changes_dirty":self.dirty,"external_changes_last_seen_unix_ms":self.last_seen,"external_changes_dirty_since_unix_ms":self.dirty_since,"external_changes_last_cleared_unix_ms":self.last_cleared})
    }
}
fn now() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
}
#[derive(Default)]
pub struct Scanner {
    states: HashMap<String, State>,
}
impl Scanner {
    fn reserve(&mut self, id: &str) {
        if !self.states.contains_key(id) && self.states.len() >= 1024 {
            if let Some(old) = self
                .states
                .iter()
                .min_by_key(|(_, v)| v.last_scan.unwrap_or(0))
                .map(|(k, _)| k.clone())
            {
                self.states.remove(&old);
            }
        }
    }
    pub fn clear(&mut self, id: &str) {
        self.reserve(id);
        let s = self.states.entry(id.to_string()).or_default();
        s.dirty = false;
        s.dirty_since = None;
        s.last_cleared = Some(now());
        s.newest = None;
    }
    pub fn update(&mut self, id: &str, root: Option<&Path>, interval_ms: u128) -> Value {
        self.reserve(id);
        let state = self.states.entry(id.to_string()).or_default();
        if let Some(root) = root {
            if state.root.as_deref() != Some(root) {
                // Instance IDs can be reused after a project reconnects. A root
                // change starts a new baseline, including the throttle window.
                *state = State {
                    root: Some(root.to_path_buf()),
                    ..State::default()
                };
            }
        }
        let timestamp = now();
        if state
            .last_scan
            .is_some_and(|last| timestamp.saturating_sub(last) < interval_ms)
        {
            return state.value();
        }
        state.last_scan = Some(timestamp);
        let Some(root) = state.root.as_ref() else {
            return state.value();
        };
        let mut roots = vec![
            root.join("Assets"),
            root.join("ProjectSettings"),
            root.join("Packages"),
        ];
        if let Some(text) = read_manifest(&root.join("Packages/manifest.json")) {
            if let Ok(manifest) = serde_json::from_str::<Value>(&text) {
                if let Some(deps) = manifest["dependencies"].as_object() {
                    for dep in deps.values().filter_map(Value::as_str) {
                        if let Some(path) = dep.trim().strip_prefix("file:") {
                            let p = PathBuf::from(path.trim());
                            let p = if p.is_absolute() {
                                p
                            } else {
                                root.join("Packages").join(p)
                            };
                            if p.is_dir() && !roots.contains(&p) {
                                roots.push(p);
                            }
                        }
                    }
                }
            }
        }
        if let Some(newest) = max_mtime(&roots, 20_000) {
            if state.newest.is_some_and(|previous| newest > previous) {
                state.last_seen = Some(timestamp);
                if !state.dirty {
                    state.dirty = true;
                    state.dirty_since = Some(timestamp);
                }
            }
            if state.newest.is_none_or(|previous| newest > previous) {
                state.newest = Some(newest);
            }
        }
        state.value()
    }
}
fn read_manifest(path: &Path) -> Option<String> {
    use std::io::Read;
    const LIMIT: u64 = 1024 * 1024;
    let mut text = String::new();
    std::fs::File::open(path)
        .ok()?
        .take(LIMIT + 1)
        .read_to_string(&mut text)
        .ok()?;
    (text.len() as u64 <= LIMIT).then_some(text)
}
fn max_mtime(roots: &[PathBuf], limit: usize) -> Option<u128> {
    let mut newest = None;
    let mut entries = 0;
    let mut dirs = roots.to_vec();
    while let Some(dir) = dirs.pop() {
        entries += 1;
        if entries > limit {
            break;
        }
        let name = dir
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .to_lowercase();
        if ["library", "temp", "logs", "obj", ".git", "node_modules"].contains(&name.as_str()) {
            continue;
        }
        let Ok(children) = std::fs::read_dir(dir) else {
            continue;
        };
        for entry in children {
            // Hidden files and failed directory entries consume work too.
            entries += 1;
            if entries > limit {
                return newest;
            }
            let Ok(entry) = entry else {
                continue;
            };
            if entry.file_name().to_string_lossy().starts_with('.') {
                continue;
            }
            let Ok(kind) = entry.file_type() else {
                continue;
            };
            if kind.is_dir() {
                if dirs.len() + entries >= limit {
                    return newest;
                }
                dirs.push(entry.path());
                continue;
            }
            if let Some(stamp) = entry
                .metadata()
                .ok()
                .and_then(|m| m.modified().ok())
                .and_then(|m| m.duration_since(UNIX_EPOCH).ok())
                .map(|m| m.as_nanos())
            {
                newest = Some(newest.map_or(stamp, |n: u128| n.max(stamp)));
            }
        }
    }
    newest
}
fn global() -> &'static Mutex<Scanner> {
    static SCANNER: OnceLock<Mutex<Scanner>> = OnceLock::new();
    SCANNER.get_or_init(|| Mutex::new(Scanner::default()))
}
pub fn update(id: &str, root: Option<&Path>) -> Value {
    global()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .update(id, root, 1500)
}
// Acquire admission before spawning: cancelled callers waiting for admission
// leave no blocking work behind. The worker owns the permit until it exits.
async fn run_scan<F>(
    gate: &'static tokio::sync::Semaphore,
    scan: F,
) -> Result<Value, tokio::task::JoinError>
where
    F: FnOnce() -> Value + Send + 'static,
{
    let permit = gate.acquire().await.expect("scanner gate stays open");
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        scan()
    })
    .await
}
pub async fn update_async(
    id: String,
    root: Option<PathBuf>,
) -> Result<Value, tokio::task::JoinError> {
    static GATE: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(1);
    run_scan(&GATE, move || update(&id, root.as_deref())).await
}
pub fn clear_dirty(id: &str) {
    global().lock().unwrap_or_else(|e| e.into_inner()).clear(id);
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn manifest_read_is_bounded() {
        let f = tempfile::NamedTempFile::new().unwrap();
        std::fs::write(f.path(), "{}").unwrap();
        assert_eq!(read_manifest(f.path()).as_deref(), Some("{}"));
        std::fs::write(f.path(), vec![b' '; 1024 * 1024 + 1]).unwrap();
        assert!(read_manifest(f.path()).is_none());
    }
    #[tokio::test]
    async fn cancellation_does_not_release_running_scan_admission() {
        use std::sync::{
            atomic::{AtomicBool, Ordering},
            Arc,
        };
        static GATE: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(1);
        let (started_tx, started_rx) = tokio::sync::oneshot::channel();
        let (release_tx, release_rx) = std::sync::mpsc::channel();
        let first = tokio::spawn(run_scan(&GATE, move || {
            let _ = started_tx.send(());
            release_rx.recv().unwrap();
            json!({})
        }));
        started_rx.await.unwrap();
        first.abort();
        let _ = first.await;
        let ran = Arc::new(AtomicBool::new(false));
        let flag = ran.clone();
        let second = tokio::spawn(run_scan(&GATE, move || {
            flag.store(true, Ordering::SeqCst);
            json!({})
        }));
        tokio::task::yield_now().await;
        assert_eq!(GATE.available_permits(), 0);
        second.abort();
        let _ = second.await;
        release_tx.send(()).unwrap();
        let permit = tokio::time::timeout(std::time::Duration::from_secs(2), GATE.acquire())
            .await
            .unwrap()
            .unwrap();
        assert!(!ran.load(Ordering::SeqCst));
        drop(permit);
        assert_eq!(
            run_scan(&GATE, || json!({"later":true})).await.unwrap()["later"],
            true
        );
    }
    #[test]
    fn root_replacement_resets_baseline_and_throttle() {
        let a = tempfile::tempdir().unwrap();
        let b = tempfile::tempdir().unwrap();
        let mut scanner = Scanner::default();
        scanner.update("reused", Some(a.path()), 0);
        let state = scanner.states.get_mut("reused").unwrap();
        state.dirty = true;
        state.newest = Some(u128::MAX);
        state.dirty_since = Some(1);
        assert_eq!(
            scanner.update("reused", Some(b.path()), u128::MAX)["external_changes_dirty"],
            false
        );
        assert_eq!(scanner.states["reused"].newest, None);
        assert_eq!(scanner.states["reused"].root.as_deref(), Some(b.path()));
    }
    #[test]
    fn caps_history_at_1024_instances() {
        let mut scanner = Scanner::default();
        for i in 0..1100 {
            scanner.update(&format!("instance-{i}"), None, 0);
        }
        assert_eq!(scanner.states.len(), 1024);
        assert!(scanner.states.contains_key("instance-1099"));
        for i in 1100..1200 {
            scanner.clear(&format!("instance-{i}"));
        }
        assert_eq!(scanner.states.len(), 1024);
    }
    #[test]
    fn baseline_change_and_clear() {
        let root = tempfile::tempdir().unwrap();
        std::fs::create_dir(root.path().join("Assets")).unwrap();
        let file = root.path().join("Assets/Test.cs");
        std::fs::write(&file, "before").unwrap();
        let mut scanner = Scanner::default();
        assert_eq!(
            scanner.update("test", Some(root.path()), 0)["external_changes_dirty"],
            false
        );
        std::thread::sleep(std::time::Duration::from_millis(10));
        std::fs::write(&file, "after").unwrap();
        assert_eq!(
            scanner.update("test", None, 0)["external_changes_dirty"],
            true
        );
        scanner.clear("test");
        assert_eq!(
            scanner.update("test", None, 0)["external_changes_dirty"],
            false
        );
    }
    #[test]
    fn local_packages_and_hidden_paths() {
        let root = tempfile::tempdir().unwrap();
        std::fs::create_dir(root.path().join("Packages")).unwrap();
        std::fs::create_dir(root.path().join("Local")).unwrap();
        std::fs::write(
            root.path().join("Packages/manifest.json"),
            r#"{"dependencies":{"local":"file:../Local"}}"#,
        )
        .unwrap();
        let file = root.path().join("Local/Script.cs");
        std::fs::write(&file, "before").unwrap();
        let mut scanner = Scanner::default();
        scanner.update("test", Some(root.path()), 0);
        std::thread::sleep(std::time::Duration::from_millis(10));
        std::fs::write(&file, "after").unwrap();
        assert_eq!(
            scanner.update("test", None, 0)["external_changes_dirty"],
            true
        );
    }
}
