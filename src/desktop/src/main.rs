#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::time::Duration;

use serde::Serialize;
use tauri::{AppHandle, Manager, RunEvent, State};
use tauri_plugin_shell::ShellExt;
use tauri_plugin_shell::process::{Command, CommandChild, CommandEvent};
use tokio::sync::{Mutex, oneshot};

const READY_PREFIX: &str = "MARKETGRADE_READY port=";
const READY_TIMEOUT: Duration = Duration::from_secs(20);
const STOP_TIMEOUT: Duration = Duration::from_secs(5);

#[derive(Clone, Serialize)]
struct BackendInfo {
    port: u16,
    token: String,
}

struct BackendProcess {
    child: CommandChild,
    exited: oneshot::Receiver<()>,
}

impl BackendProcess {
    fn is_running(&mut self) -> bool {
        matches!(
            self.exited.try_recv(),
            Err(oneshot::error::TryRecvError::Empty)
        )
    }

    async fn stop(self) {
        let _ = self.child.kill();
        let _ = tokio::time::timeout(STOP_TIMEOUT, self.exited).await;
    }
}

#[derive(Default)]
struct BackendState {
    process: Option<BackendProcess>,
    info: Option<BackendInfo>,
}

#[derive(Default)]
struct Backend(Mutex<BackendState>);

/// The only difference between dev and production: which executable runs the backend.
/// In dev it is the build's apphost, so the process is named Marketgrade.Backend (for dotnet-counters).
#[cfg(debug_assertions)]
fn backend_command(app: &AppHandle) -> Result<Command, String> {
    let exe = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(format!(
        "../../artifacts/bin/Marketgrade.Backend/debug/Marketgrade.Backend{}",
        std::env::consts::EXE_SUFFIX
    ));
    Ok(app.shell().command(exe))
}

#[cfg(not(debug_assertions))]
fn backend_command(app: &AppHandle) -> Result<Command, String> {
    app.shell()
        .sidecar("marketgrade-backend")
        .map_err(|e| format!("Backend executable not found: {e}"))
}

fn generate_token() -> Result<String, String> {
    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).map_err(|e| format!("Could not generate token: {e}"))?;
    Ok(hex::encode(bytes))
}

/// Spawns the backend and returns it together with a receiver for the port it reports.
fn spawn_backend(
    app: &AppHandle,
    token: &str,
) -> Result<(BackendProcess, oneshot::Receiver<Result<u16, String>>), String> {
    // Settings and recordings live in the operating system's app data folder, in dev and production.
    let data_dir = app
        .path()
        .app_data_dir()
        .map_err(|e| format!("No app data folder: {e}"))?;
    let (mut events, child) = backend_command(app)?
        .env("MARKETGRADE_TOKEN", token)
        .env("MARKETGRADE_PARENT_PID", std::process::id().to_string())
        .env("MARKETGRADE_DATA_DIR", data_dir)
        .spawn()
        .map_err(|e| format!("Could not start backend: {e}"))?;

    let (ready_tx, ready_rx) = oneshot::channel();
    let (exited_tx, exited_rx) = oneshot::channel();

    tauri::async_runtime::spawn(async move {
        let mut ready_tx = Some(ready_tx);
        while let Some(event) = events.recv().await {
            match event {
                CommandEvent::Stdout(line) => {
                    let line = String::from_utf8_lossy(&line);
                    let port = line
                        .trim()
                        .strip_prefix(READY_PREFIX)
                        .and_then(|p| p.parse().ok());
                    match (port, ready_tx.take()) {
                        (Some(port), Some(tx)) => {
                            let _ = tx.send(Ok(port));
                        }
                        (_, tx) => {
                            ready_tx = tx;
                            eprintln!("backend stdout: {}", line.trim_end());
                        }
                    }
                }
                CommandEvent::Stderr(line) => {
                    eprintln!("{}", String::from_utf8_lossy(&line).trim_end())
                }
                CommandEvent::Error(error) => eprintln!("backend error: {error}"),
                CommandEvent::Terminated(status) => {
                    if let Some(tx) = ready_tx.take() {
                        let _ = tx.send(Err(format!(
                            "Backend exited before it was ready (exit code {:?})",
                            status.code
                        )));
                    }
                    break;
                }
                _ => {}
            }
        }
        let _ = exited_tx.send(());
    });

    Ok((
        BackendProcess {
            child,
            exited: exited_rx,
        },
        ready_rx,
    ))
}

/// Returns the running backend's port and token, starting it first if it is not running.
async fn ensure_backend(app: &AppHandle, backend: &Backend) -> Result<BackendInfo, String> {
    let mut guard = backend.0.lock().await;
    let state = &mut *guard;

    if let (Some(info), Some(process)) = (&state.info, &mut state.process)
        && process.is_running()
    {
        return Ok(info.clone());
    }

    // Never run two backends at once: the old one must be gone before a new one starts.
    state.info = None;
    if let Some(process) = state.process.take() {
        process.stop().await;
    }

    let token = generate_token()?;
    let (process, ready) = spawn_backend(app, &token)?;
    state.process = Some(process);

    let result = match tokio::time::timeout(READY_TIMEOUT, ready).await {
        Ok(Ok(Ok(port))) => Ok(BackendInfo { port, token }),
        Ok(Ok(Err(error))) => Err(error),
        Ok(Err(_)) => Err("Backend exited before it was ready".to_string()),
        Err(_) => Err(format!(
            "Backend did not report ready within {} seconds",
            READY_TIMEOUT.as_secs()
        )),
    };

    match &result {
        Ok(info) => state.info = Some(info.clone()),
        Err(_) => {
            if let Some(process) = state.process.take() {
                process.stop().await;
            }
        }
    }
    result
}

#[tauri::command]
async fn get_backend_info(
    app: AppHandle,
    backend: State<'_, Backend>,
) -> Result<BackendInfo, String> {
    ensure_backend(&app, &backend).await
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(Backend::default())
        .invoke_handler(tauri::generate_handler![get_backend_info])
        .setup(|app| {
            let app = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                if let Err(error) = ensure_backend(&app, &app.state::<Backend>()).await {
                    eprintln!("{error}");
                }
            });
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building the application")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                // If a start is still in progress the lock is held; the backend then
                // notices the missing parent process and exits by itself.
                if let Ok(mut state) = app.state::<Backend>().0.try_lock()
                    && let Some(process) = state.process.take()
                {
                    tauri::async_runtime::block_on(process.stop());
                }
            }
        });
}
