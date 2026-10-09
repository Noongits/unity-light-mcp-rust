use anyhow::Result;
use async_trait::async_trait;
use serde_json::Value;
use std::sync::atomic::{AtomicU64, Ordering};

pub(crate) fn next_tracking_id() -> u64 {
    static NEXT: AtomicU64 = AtomicU64::new(1);
    let mut id = NEXT.load(Ordering::Relaxed);
    loop {
        let next = id
            .checked_add(1)
            .expect("Unity bridge tracking identity exhausted");
        match NEXT.compare_exchange_weak(id, next, Ordering::Relaxed, Ordering::Relaxed) {
            Ok(_) => return id,
            Err(current) => id = current,
        }
    }
}

#[async_trait]
pub trait UnityBridge: Send + Sync {
    /// Stable process-local owner identity. Zero opts out of owner-bound tracking.
    fn tracking_id(&self) -> u64 {
        0
    }
    fn local_filesystem_allowed(&self) -> bool {
        true
    }
    async fn send(&self, command: &str, params: Value, instance: Option<&str>) -> Result<Value>;
    async fn instances(&self) -> Result<Value>;
    async fn custom_tools(&self, _instance: Option<&str>) -> Result<Vec<Value>> {
        Ok(vec![])
    }
}
