//! remote-cli agent for Linux, in Rust: runs this computer's terminals for the phone.
//!
//! Same contract as the Python agent (../agent-linux/agent.py), which stays the reference
//! implementation: it signs in to the relay like the phone does, reports terminal state and
//! output a few times a second, and carries out the operations the phone sends. Same data
//! layout, same config keys, same protocol. The point of this version is deployment shape:
//! one binary, no Python on the server. The end-to-end contract test (../agent-linux/
//! test_rust_agent.py) runs this binary against the real relay, the way the phone does.
//!
//!     remote-cli-agent                     # data in ~/.local/state/remote-cli-agent
//!     remote-cli-agent --data /var/lib/…   # or --set-password [folder]
//!
//! Synchronous threads, no async runtime: one report loop, one held-pull loop, one reader
//! and one waiter per terminal — the same shape the Python agent uses. Terminal input and
//! output never reach a log.
//!
//! Terminals and project file browsing (file_list / file_read) are covered; reading saved
//! conversations (peek) and starting Claude Code or Codex stay with the Python agent for
//! now, and the features list in the report says so.
use base64::Engine as _;
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet, HashMap, VecDeque};
use std::io::Write as _;
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const VERSION: &str = "0.1.0";
const MAX_TERMINALS: usize = 8;
const MAX_OWN_PROJECTS: usize = 30;
const OP_TTL: f64 = 150.0; // seconds an operation may travel before it is refused
const CHUNK_CHARS: usize = 12000; // one numbered piece of output
const BATCH_BYTES: usize = 512 * 1024;
const BATCH_CHUNKS: usize = 150;
const OUTPUT_FLOOR_BYTES: usize = 2 * 1024 * 1024; // unsent output is dropped past this; the relay dedups by seq
const FILE_PIECE: usize = 737280; // bytes of a file in one answer, as the Python agent cuts them
const FILE_ENTRIES: usize = 3000; // entries of a folder in one answer
const STRIP_ENV: [&str; 3] = [
    "CLAUDECODE",
    "CLAUDE_CODE_ENTRYPOINT",
    "CLAUDE_CODE_SSE_PORT",
];

static STOP: AtomicBool = AtomicBool::new(false);

// ---------- small helpers ----------

fn now() -> f64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs_f64())
        .unwrap_or(0.0)
}

/// A file's mtime in milliseconds since the epoch, the shape the phone reads.
fn millis(stamp: std::io::Result<SystemTime>) -> i64 {
    stamp
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

fn say(message: &str) {
    eprintln!("[{}] {}", chrono_stamp(), message);
}

/// A wall-clock stamp for the log, in UTC+8, computed without a time crate.
fn chrono_stamp() -> String {
    let secs = now() as i64 + 8 * 3600;
    let days = secs.div_euclid(86400);
    let t = secs.rem_euclid(86400);
    let (h, mi, s) = (t / 3600, t % 3600 / 60, t % 60);
    // civil-from-days (Howard Hinnant's algorithm)
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    format!("{y:04}-{m:02}-{d:02} {h:02}:{mi:02}:{s:02}")
}

fn hostname() -> String {
    let mut buf = [0u8; 256];
    let ok = unsafe { libc::gethostname(buf.as_mut_ptr() as *mut libc::c_char, buf.len()) == 0 };
    if ok {
        let end = buf.iter().position(|b| *b == 0).unwrap_or(buf.len());
        String::from_utf8_lossy(&buf[..end]).trim().to_string()
    } else {
        "ubuntu".into()
    }
}

fn is_lower_hex(s: &str, min: usize, max: usize) -> bool {
    s.len() >= min
        && s.len() <= max
        && s.bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn valid_server(s: &str) -> bool {
    s.strip_prefix("https://")
        .or_else(|| s.strip_prefix("http://"))
        .map(|rest| !rest.is_empty() && !rest.contains('/') && !rest.contains(char::is_whitespace))
        == Some(true)
}

fn tidy(text: &str, limit: usize) -> String {
    // strips short tags and control characters, collapses whitespace — the same shape
    // as the Windows agent's Tidy, so names from either side look alike
    let mut out = String::new();
    let mut chars = text.chars().peekable();
    let mut space = true;
    while let Some(c) = chars.next() {
        if c == '<' {
            let rest: String = chars.clone().take(41).collect();
            match rest.find('>') {
                Some(pos) if pos <= 40 => {
                    for _ in 0..=pos {
                        chars.next();
                    }
                    if !space {
                        out.push(' ');
                        space = true;
                    }
                    continue;
                }
                _ => {}
            }
        }
        if c.is_control()
            || c == '\u{7f}'
            || ('\u{80}'..='\u{9f}').contains(&c)
            || c.is_whitespace()
        {
            if !space {
                out.push(' ');
                space = true;
            }
            continue;
        }
        out.push(c);
        space = false;
    }
    let trimmed = out.trim().to_string();
    if trimmed.chars().count() > limit {
        let mut s: String = trimmed.chars().take(limit - 1).collect();
        s.push('…');
        s
    } else {
        trimmed
    }
}

/// like os.path.normpath: collapse //, . and .. lexically (the folder need not exist)
fn lexically_normalize(path: &str) -> String {
    let mut parts: Vec<&str> = Vec::new();
    for part in path.split('/') {
        match part {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            p => parts.push(p),
        }
    }
    format!("/{}", parts.join("/"))
}

fn normal_folder(path: &str) -> Result<String, String> {
    let path = path.trim();
    if path.len() < 2
        || path.len() > 240
        || path.contains('*')
        || path.contains('?')
        || path.contains('\0')
        || path.chars().any(|c| c.is_control())
        || !path.starts_with('/')
    {
        return Err("请填写电脑上的完整文件夹路径，例如 /home/you/demo".into());
    }
    Ok(lexically_normalize(path))
}

fn write_private(path: &Path, text: &str) -> std::io::Result<()> {
    let tmp = path.with_extension("tmp");
    std::fs::OpenOptions::new()
        .create(true)
        .write(true)
        .truncate(true)
        .mode(0o600)
        .open(&tmp)?
        .write_all(text.as_bytes())?;
    std::fs::rename(&tmp, path)
}

fn read_config(data: &Path) -> Value {
    std::fs::read_to_string(data.join("config.json"))
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_else(|| json!({}))
}

fn cfg_str<'a>(cfg: &'a Value, key: &str) -> &'a str {
    cfg.get(key).and_then(|v| v.as_str()).unwrap_or("")
}

fn parse_dirs(cfg: &Value) -> BTreeMap<String, String> {
    let mut out = BTreeMap::new();
    if let Some(items) = cfg.get("RemoteDirs").and_then(|v| v.as_array()) {
        for item in items.iter().take(60) {
            let (name, path) = match item {
                Value::String(s) => match s.find('=') {
                    Some(at) if at > 0 => {
                        (s[..at].trim().to_string(), s[at + 1..].trim().to_string())
                    }
                    _ => continue,
                },
                Value::Object(_) => (
                    item.get("name")
                        .and_then(|v| v.as_str())
                        .unwrap_or("")
                        .trim()
                        .to_string(),
                    item.get("path")
                        .and_then(|v| v.as_str())
                        .unwrap_or("")
                        .trim()
                        .to_string(),
                ),
                _ => continue,
            };
            if name.is_empty()
                || name.chars().count() > 60
                || path.chars().count() > 260
                || !path.starts_with('/')
            {
                continue;
            }
            let path = normal_folder(&path).unwrap_or_default();
            if Path::new(&path).is_dir() {
                out.insert(name, path);
            }
        }
    }
    out
}

fn read_password(data: &Path) -> String {
    if let Ok(given) = std::env::var("RCLI_PASSWORD") {
        let given = given.trim().to_string();
        if !given.is_empty() {
            return given;
        }
    }
    std::fs::read_to_string(data.join("password.txt"))
        .map(|s| s.trim().to_string())
        .unwrap_or_default()
}

/// One agent per data directory: a second instance would report with its own id and the
/// relay would keep closing the first one's terminals ("the computer restarted"). The lock
/// file is held for the process lifetime; the Python agent takes the same lock.
fn acquire_singleton(data: &Path) -> Option<std::fs::File> {
    use std::os::unix::io::AsRawFd;
    let lock = std::fs::OpenOptions::new()
        .create(true)
        .write(true)
        .truncate(false)
        .mode(0o600)
        .open(data.join("agent.lock"))
        .ok()?;
    let fd = lock.as_raw_fd();
    let ok = unsafe { libc::flock(fd, libc::LOCK_EX | libc::LOCK_NB) == 0 };
    if ok {
        Some(lock)
    } else {
        None
    }
}

fn random_hex(len: usize) -> String {
    // bytes from the OS, the way secrets.token_hex does it
    let mut out = String::with_capacity(len);
    let mut buf = [0u8; 32];
    while out.len() < len {
        let want = len - out.len();
        let n = std::fs::File::open("/dev/urandom")
            .and_then(|mut f| {
                use std::io::Read;
                let read = f.read(&mut buf)?;
                Ok(read.min(want.div_ceil(2)))
            })
            .unwrap_or(0);
        if n == 0 {
            break;
        }
        for b in &buf[..n] {
            out.push_str(&format!("{b:02x}"));
        }
    }
    if out.len() < len {
        // /dev/urandom unavailable: pad from the clock so the id still differs per start
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.subsec_nanos())
            .unwrap_or(0);
        out.push_str(&format!("{:08x}{:08x}", nanos, std::process::id()));
    }
    out.truncate(len);
    out
}

// ---------- HTTP ----------

struct Http {
    agent: ureq::Agent,
}

impl Http {
    fn new() -> Http {
        Http {
            agent: ureq::AgentBuilder::new().redirects(0).build(),
        }
    }

    /// POST JSON; returns (status, body). Transport failures are Err; HTTP errors carry
    /// their status and body back, so 401 and 429 can be told apart by the callers.
    fn post(
        &self,
        url: &str,
        payload: &Value,
        token: &str,
        timeout: Duration,
    ) -> Result<(u16, String), String> {
        let mut req = self.agent.post(url).timeout(timeout);
        if !token.is_empty() {
            req = req.set("Authorization", &format!("Bearer {token}"));
        }
        match req.send_json(payload) {
            Ok(resp) => {
                let status = resp.status();
                let body = resp.into_string().map_err(|e| e.to_string())?;
                Ok((status, body))
            }
            Err(ureq::Error::Status(code, resp)) => {
                let body = resp.into_string().unwrap_or_default();
                Ok((code, body))
            }
            Err(e) => Err(e.to_string()),
        }
    }
}

// ---------- file browsing ----------

/// What a project folder holds, or a piece of one of its files. Nothing outside the
/// project's own folder is given out: a link inside it that leads elsewhere is refused.
/// The same answers the Python agent's files() gives, the way the phone's file pages read.
fn files(
    root: &str,
    relative: &str,
    action: &str,
    offset: Result<i64, ()>,
) -> Result<Value, String> {
    let root = lexically_normalize(root.trim());
    let relative = relative.replace('\\', "/");
    let relative = relative.trim_matches('/');
    let full = if relative.is_empty() {
        root.clone()
    } else {
        lexically_normalize(&format!("{root}/{relative}"))
    };
    let inside = format!("{}/", root.trim_end_matches('/'));
    if full != root && !full.starts_with(&inside) {
        return Err("路径不在项目文件夹内".into());
    }
    let shown = if full == root {
        String::new()
    } else {
        full[inside.len()..].to_string()
    };
    // a link inside the project that leads elsewhere is refused. A path that does not
    // resolve (a file that is not there, a link to nowhere) falls back to the lexical
    // check above and is reported by the action itself.
    if let (Ok(real_root), Ok(real)) = (std::fs::canonicalize(&root), std::fs::canonicalize(&full))
    {
        let real_root = real_root.to_string_lossy();
        let real = real.to_string_lossy();
        let real_inside = format!("{}/", real_root.trim_end_matches('/'));
        if real != real_root && !real.starts_with(&real_inside) {
            return Err("这个位置链接到项目文件夹之外，不能打开".into());
        }
    }
    if action == "file_list" {
        let target = Path::new(&full);
        if !target.is_dir() {
            return Err(if target.exists() {
                "这不是文件夹"
            } else {
                "文件夹不存在"
            }
            .into());
        }
        let mut entries: Vec<Value> = Vec::new();
        let mut more = false;
        let list = std::fs::read_dir(target).map_err(|_| "读取失败".to_string())?;
        for item in list {
            if entries.len() >= FILE_ENTRIES {
                more = true;
                break;
            }
            let Ok(item) = item else { continue };
            // a link that leads nowhere is still an entry: stat it following links, else as itself
            let Ok(info) = std::fs::metadata(item.path()).or_else(|_| item.metadata()) else {
                continue;
            };
            let name = item.file_name().to_string_lossy().to_string();
            let is_dir = info.is_dir();
            entries.push(json!({
                "name": name, "dir": is_dir, "size": if is_dir { 0 } else { info.len() },
                "modified": millis(info.modified()), "hidden": name.starts_with('.'),
            }));
        }
        return Ok(json!({"path": shown, "entries": entries, "more": more}));
    }
    if Path::new(&full).is_dir() {
        return Err("这是文件夹".into());
    }
    let offset = match offset {
        Ok(o) => o,
        Err(_) => return Err("位置无效".into()),
    };
    // opened without waiting, so a pipe or a device left in a project cannot hold the agent
    let mut file = std::fs::OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NONBLOCK)
        .open(&full)
        .map_err(|e| {
            match e.kind() {
                std::io::ErrorKind::NotFound => "文件不存在",
                std::io::ErrorKind::PermissionDenied => "没有权限读取",
                _ => "读取失败",
            }
            .to_string()
        })?;
    let info = file.metadata().map_err(|_| "读取失败".to_string())?;
    if !info.is_file() {
        return Err("这不是普通文件，不能打开".into());
    }
    let size = info.len() as i64;
    let offset = match offset {
        o if o >= 0 && o <= size => o,
        _ => return Err("位置无效".into()),
    };
    let want = std::cmp::min(FILE_PIECE as i64, size - offset) as usize;
    use std::io::{Read, Seek};
    file.seek(std::io::SeekFrom::Start(offset as u64))
        .map_err(|_| "读取失败".to_string())?;
    let mut data = vec![0u8; want];
    let mut filled = 0usize;
    while filled < want {
        match file.read(&mut data[filled..]) {
            Ok(0) => break,
            Ok(n) => filled += n,
            Err(ref e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(_) => return Err("读取失败".into()),
        }
    }
    data.truncate(filled);
    Ok(json!({
        "path": shown, "size": size, "modified": millis(info.modified()), "offset": offset,
        "data": base64::engine::general_purpose::STANDARD.encode(&data),
        "end": offset + filled as i64 >= size,
    }))
}

// ---------- terminal ----------

struct Chunk {
    seq: i64,
    data: String,
}

struct LiveInner {
    fd: i32,
    project: String, // the project's name, which a rename follows
    dir: String,
    cols: u16,
    rows: u16,
    pending: String,    // decoded text read from the program, not yet numbered
    output: Vec<Chunk>, // numbered, not yet acknowledged by the relay
    out_bytes: usize,   // bytes in `output`, maintained by seal/trim
    seq: i64,
    closed: bool,      // the process has been reaped
    drained: bool,     // the reader delivered everything
    told_closed: bool, // the relay accepted a report that said this terminal ended
    exit_code: Option<i64>,
    closed_at: Option<Instant>,
    closing_at: Option<Instant>, // TERM sent; KILL follows if this lapses three seconds
}

struct Live {
    id: String,
    pid: i32,
    inner: Mutex<LiveInner>,
}

struct SpawnPre {
    cwd: std::ffi::CString,
    file: std::ffi::CString,
    argv: Vec<*const libc::c_char>, // [file, null]; the buffer is owned by `file` above
    envp: Vec<*const libc::c_char>, // points into _envp_strings, which moves along inside
    _envp_strings: Vec<std::ffi::CString>, // owns every buffer envp points into
}

fn spawn_pre(cwd: &str, shell: &str) -> Result<SpawnPre, String> {
    use std::ffi::CString;
    let file = CString::new(shell).map_err(|_| "路径无效".to_string())?;
    let cwd = CString::new(cwd).map_err(|_| "路径无效".to_string())?;
    // vars_os: a non-UTF-8 variable must not panic the whole agent
    let has_utf8_locale = std::env::vars_os().any(|(k, v)| {
        let k = k.to_string_lossy();
        (k == "LANG" || k == "LC_ALL") && v.to_string_lossy().to_lowercase().contains("utf-8")
    });
    let mut envs: Vec<String> = std::env::vars_os()
        .filter(|(k, _)| {
            let k = k.to_string_lossy();
            if STRIP_ENV.iter().any(|s| s == &k) {
                return false; // never hand a parent coding session down
            }
            // these are set below; a duplicate would never win (getenv takes the first
            // match), so an inherited value must make way
            !(k == "TERM" || k == "COLORTERM" || (!has_utf8_locale && k == "LANG"))
        })
        .map(|(k, v)| format!("{}={}", k.to_string_lossy(), v.to_string_lossy()))
        .collect();
    envs.push("TERM=xterm-256color".into());
    envs.push("COLORTERM=truecolor".into());
    if !has_utf8_locale {
        // a systemd user manager often has no LANG at all; without a UTF-8 locale the
        // shell's readline treats every byte of Chinese input as one character
        envs.push("LANG=C.UTF-8".into());
    }
    let mut strings: Vec<CString> = Vec::with_capacity(envs.len());
    for e in &envs {
        strings.push(CString::new(e.as_str()).map_err(|_| "环境无效".to_string())?);
    }
    let mut envp: Vec<*const libc::c_char> = strings
        .iter()
        .map(|c| c.as_ptr() as *const libc::c_char)
        .collect();
    envp.push(std::ptr::null());
    // every pointer above stays valid: file and _envp_strings move into the struct and
    // keep the buffers alive until execvpe has run
    let argv = vec![file.as_ptr() as *const libc::c_char, std::ptr::null()];
    Ok(SpawnPre {
        cwd,
        file,
        argv,
        envp,
        _envp_strings: strings,
    })
}

impl Live {
    fn spawn(
        id: &str,
        project: &str,
        cwd: &str,
        cols: u16,
        rows: u16,
        shell: &str,
        wake: Arc<Parker>,
    ) -> Result<Arc<Live>, String> {
        let pre = spawn_pre(cwd, shell)?;
        let mut master: i32 = 0;
        let mut slave: i32 = 0;
        let ws = libc::winsize {
            ws_col: cols,
            ws_row: rows,
            ws_xpixel: 0,
            ws_ypixel: 0,
        };
        unsafe {
            if libc::openpty(
                &mut master,
                &mut slave,
                std::ptr::null_mut(),
                std::ptr::null(),
                &ws,
            ) != 0
            {
                return Err("终端创建失败".into());
            }
            // a master left without CLOEXEC leaks into every later child; non-blocking
            // keeps one program that stopped reading from holding the whole write
            libc::fcntl(master, libc::F_SETFD, libc::FD_CLOEXEC);
            libc::fcntl(slave, libc::F_SETFD, libc::FD_CLOEXEC);
            libc::fcntl(master, libc::F_SETFL, libc::O_NONBLOCK);
            match libc::fork() {
                0 => {
                    // child: new session, controlling terminal, exec — pointers only
                    libc::setsid();
                    libc::ioctl(slave, libc::TIOCSCTTY, 0usize);
                    for fd in 0..3 {
                        libc::dup2(slave, fd);
                    }
                    if slave > 2 {
                        libc::close(slave);
                    }
                    libc::close(master);
                    if libc::chdir(pre.cwd.as_ptr()) != 0 {
                        libc::_exit(127); // the folder is gone: do not start somewhere else
                    }
                    libc::execvpe(pre.file.as_ptr(), pre.argv.as_ptr(), pre.envp.as_ptr());
                    libc::_exit(127);
                }
                pid if pid > 0 => {
                    libc::close(slave);
                    let live = Arc::new(Live {
                        id: id.to_string(),
                        pid,
                        inner: Mutex::new(LiveInner {
                            fd: master,
                            project: project.to_string(),
                            dir: cwd.to_string(),
                            cols,
                            rows,
                            pending: String::new(),
                            output: Vec::new(),
                            out_bytes: 0,
                            seq: 0,
                            closed: false,
                            drained: false,
                            told_closed: false,
                            exit_code: None,
                            closed_at: None,
                            closing_at: None,
                        }),
                    });
                    {
                        let reader = live.clone();
                        let w = wake.clone();
                        std::thread::spawn(move || Live::read_loop(reader, master, w));
                    }
                    {
                        let waiter = live.clone();
                        std::thread::spawn(move || Live::wait_loop(waiter, pid, wake));
                    }
                    Ok(live)
                }
                _ => {
                    libc::close(master);
                    libc::close(slave);
                    Err("终端创建失败".into())
                }
            }
        }
    }

    fn read_loop(live: Arc<Live>, fd: i32, wake: Arc<Parker>) {
        let mut leftover: Vec<u8> = Vec::new();
        let mut buf = [0u8; 8192];
        loop {
            let n = unsafe { libc::read(fd, buf.as_mut_ptr() as *mut libc::c_void, buf.len()) };
            if n < 0 {
                let e = unsafe { *libc::__errno_location() };
                if e == libc::EAGAIN || e == libc::EWOULDBLOCK || e == libc::EINTR {
                    // the master is non-blocking (CLOEXEC keeps it out of later children):
                    // wait for data instead of mistaking this for EOF
                    let mut pfd = libc::pollfd {
                        fd,
                        events: libc::POLLIN,
                        revents: 0,
                    };
                    unsafe {
                        libc::poll(&mut pfd, 1, 500);
                    }
                    continue;
                }
                break; // EIO: the terminal was closed or the program left
            }
            if n == 0 {
                break; // EOF
            }
            leftover.extend_from_slice(&buf[..n as usize]);
            {
                let mut inner = live.inner.lock().unwrap();
                // incremental UTF-8: a multibyte sequence may arrive split across reads
                loop {
                    match std::str::from_utf8(&leftover) {
                        Ok(text) => {
                            inner.pending.push_str(text);
                            leftover.clear();
                            break;
                        }
                        Err(e) => {
                            let valid = e.valid_up_to();
                            if valid > 0 {
                                inner.pending.push_str(unsafe {
                                    std::str::from_utf8_unchecked(&leftover[..valid])
                                });
                            }
                            match e.error_len() {
                                Some(bad) => {
                                    inner.pending.push('\u{fffd}');
                                    leftover.drain(..valid + bad);
                                }
                                None => {
                                    leftover.drain(..valid); // keep the tail for the next read
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            wake.wake();
        }
        {
            let mut inner = live.inner.lock().unwrap();
            inner.drained = true;
        }
        wake.wake();
    }

    fn wait_loop(live: Arc<Live>, pid: i32, wake: Arc<Parker>) {
        let mut status = 0;
        let code = unsafe {
            if libc::waitpid(pid, &mut status, 0) == pid {
                if libc::WIFSIGNALED(status) {
                    128 + libc::WTERMSIG(status) as i64
                } else if libc::WIFEXITED(status) {
                    libc::WEXITSTATUS(status) as i64
                } else {
                    1
                }
            } else {
                1
            }
        };
        {
            let mut inner = live.inner.lock().unwrap();
            inner.exit_code = Some(code);
            inner.closed = true;
            inner.closed_at = Some(Instant::now());
        }
        wake.wake();
    }

    fn write(&self, text: &str) -> Result<(), String> {
        // non-blocking with a one-second budget: a program that stopped reading must not
        // hold this (and with it every other terminal) for as long as it likes. Each
        // piece is written under the lock, so the fd cannot be closed and reused between
        // the check and the write.
        let bytes = text.as_bytes();
        let mut done = 0usize;
        let deadline = Instant::now() + Duration::from_secs(1);
        while done < bytes.len() {
            let fd;
            let n;
            {
                let inner = self.inner.lock().unwrap();
                if inner.fd < 0 {
                    return Err("终端已结束".into());
                }
                n = unsafe {
                    libc::write(
                        inner.fd,
                        bytes[done..].as_ptr() as *const libc::c_void,
                        bytes.len() - done,
                    )
                };
                fd = inner.fd;
            }
            if n >= 0 {
                done += n as usize;
                if done == bytes.len() {
                    return Ok(());
                }
                continue;
            }
            let e = unsafe { *libc::__errno_location() };
            if e != libc::EAGAIN && e != libc::EWOULDBLOCK && e != libc::EINTR {
                return Err("终端已结束".into());
            }
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                return Err("终端里的程序暂时没有读取输入，请稍后再试".into());
            }
            let mut pfd = libc::pollfd {
                fd,
                events: libc::POLLOUT,
                revents: 0,
            };
            unsafe {
                libc::poll(&mut pfd, 1, left.as_millis() as i32);
            }
        }
        Ok(())
    }

    fn resize(&self, cols: u16, rows: u16) {
        let mut inner = self.inner.lock().unwrap();
        if inner.fd >= 0 {
            let ws = libc::winsize {
                ws_col: cols,
                ws_row: rows,
                ws_xpixel: 0,
                ws_ypixel: 0,
            };
            unsafe { libc::ioctl(inner.fd, libc::TIOCSWINSZ, &ws as *const libc::winsize) };
        }
        inner.cols = cols;
        inner.rows = rows;
    }

    /// Kills the session: the child is a session leader, so its group covers the tree;
    /// closing the master later delivers SIGHUP to a foreground group.
    fn kill(&self) {
        unsafe { libc::killpg(self.pid, libc::SIGTERM) };
        let deadline = Instant::now() + Duration::from_secs(3);
        loop {
            let closed_or_gone = {
                let inner = self.inner.lock().unwrap();
                inner.closed || inner.fd < 0
            };
            if closed_or_gone || Instant::now() >= deadline {
                break;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        let still_running = {
            let inner = self.inner.lock().unwrap();
            !inner.closed && inner.fd >= 0
        };
        if still_running {
            unsafe { libc::killpg(self.pid, libc::SIGKILL) };
        }
    }

    /// Sends TERM now; the report loop escalates to KILL later, outside the state lock.
    fn begin_close(&self) {
        unsafe { libc::killpg(self.pid, libc::SIGTERM) };
        let mut inner = self.inner.lock().unwrap();
        if inner.closing_at.is_none() {
            inner.closing_at = Some(Instant::now());
        }
    }

    /// Escalates a close to KILL once TERM has had its three seconds.
    fn escalate(&self) {
        let due = {
            let inner = self.inner.lock().unwrap();
            !inner.closed
                && inner.fd >= 0
                && inner
                    .closing_at
                    .map(|t| t.elapsed() > Duration::from_secs(3))
                    .unwrap_or(false)
        };
        if due {
            unsafe { libc::killpg(self.pid, libc::SIGKILL) };
        }
    }

    fn trim(&self, upto: i64) {
        let mut inner = self.inner.lock().unwrap();
        let mut kept = Vec::with_capacity(inner.output.len());
        let mut bytes = 0usize;
        for chunk in inner.output.drain(..) {
            if chunk.seq > upto {
                bytes += chunk.data.len();
                kept.push(chunk);
            }
        }
        inner.output = kept;
        inner.out_bytes = bytes;
    }

    fn can_forget(&self) -> bool {
        let inner = self.inner.lock().unwrap();
        inner.closed
            && inner.told_closed
            && inner.pending.is_empty()
            && inner.output.is_empty()
            && (inner.drained
                || inner
                    .closed_at
                    .map(|t| t.elapsed() > Duration::from_secs(15))
                    .unwrap_or(false))
    }

    fn dispose(&self) {
        let mut inner = self.inner.lock().unwrap();
        if inner.fd >= 0 {
            unsafe { libc::close(inner.fd) };
            inner.fd = -1;
        }
    }
}

/// Turns pending text into numbered pieces; only the report thread calls this. Pieces the
/// relay has not confirmed are dropped once they outgrow OUTPUT_FLOOR_BYTES: the relay
/// deduplicates by seq, so a reader that asks past dropped output gets its reset flag.
fn seal(live: &Live) {
    let mut inner = live.inner.lock().unwrap();
    let text = std::mem::take(&mut inner.pending);
    let mut rest = text.as_str();
    let mut fresh: Vec<Chunk> = Vec::new();
    while !rest.is_empty() {
        inner.seq += 1;
        // split at a character boundary, CHUNK_CHARS characters at a time
        let end = rest
            .char_indices()
            .nth(CHUNK_CHARS)
            .map(|(i, _)| i)
            .unwrap_or(rest.len());
        fresh.push(Chunk {
            seq: inner.seq,
            data: rest[..end].to_string(),
        });
        rest = &rest[end..];
    }
    for chunk in &fresh {
        inner.out_bytes += chunk.data.len();
    }
    inner.output.extend(fresh);
    while inner.out_bytes > OUTPUT_FLOOR_BYTES && inner.output.len() > 1 {
        let dropped = inner.output.remove(0);
        let dropped_len = dropped.data.len();
        inner.out_bytes -= dropped_len;
    }
}

fn output_batch(terminals: &HashMap<String, Arc<Live>>) -> Vec<Value> {
    let mut batch: Vec<Value> = Vec::new();
    let mut size = 0usize;
    for live in terminals.values() {
        let chunks: Vec<Chunk> = {
            let inner = live.inner.lock().unwrap();
            inner
                .output
                .iter()
                .take(30)
                .map(|c| Chunk {
                    seq: c.seq,
                    data: c.data.clone(),
                })
                .collect()
        };
        for c in chunks {
            let value = json!({"terminal": live.id, "seq": c.seq, "data": c.data});
            // measure the very object that goes out — one serialization, not two
            let encoded = serde_json::to_string(&value).map(|s| s.len()).unwrap_or(0);
            if !batch.is_empty() && size + encoded > BATCH_BYTES {
                return batch;
            }
            size += encoded;
            batch.push(value);
            if batch.len() >= BATCH_CHUNKS {
                return batch;
            }
        }
    }
    batch
}

// ---------- projects ----------

struct Projects {
    file: PathBuf,
    own: Vec<(String, String)>, // (name, path), phone-added only
}

impl Projects {
    fn load(data: &Path) -> Projects {
        let file = data.join("terminal-projects.json");
        let own = std::fs::read_to_string(&file)
            .ok()
            .and_then(|s| serde_json::from_str::<Value>(&s).ok())
            .and_then(|v| v.as_array().cloned())
            .map(|items| {
                items
                    .iter()
                    .filter_map(|item| {
                        let name = item.get("name")?.as_str()?.to_string();
                        let path = item.get("path")?.as_str()?.to_string();
                        if name.is_empty() || path.is_empty() {
                            None
                        } else {
                            Some((name, path))
                        }
                    })
                    .collect()
            })
            .unwrap_or_default();
        Projects { file, own }
    }

    fn save(&self) -> Result<(), String> {
        let list: Vec<Value> = self
            .own
            .iter()
            .map(|(n, p)| json!({"name": n, "path": p}))
            .collect();
        write_private(&self.file, &serde_json::to_string(&list).unwrap())
            .map_err(|_| "记录保存失败".to_string())
    }

    fn all(&self, cfg: &Value) -> BTreeMap<String, String> {
        self.all_from(parse_dirs(cfg))
    }

    /// all(), over an already parsed fixed list — the report loop parses once per turn.
    fn all_from(&self, mut dirs: BTreeMap<String, String>) -> BTreeMap<String, String> {
        for (name, path) in &self.own {
            if !dirs.contains_key(name) {
                let path = normal_folder(path).unwrap_or_default();
                if Path::new(&path).is_dir() {
                    dirs.insert(name.clone(), path);
                }
            }
        }
        dirs
    }

    fn entries_from(&self, fixed: &BTreeMap<String, String>) -> Vec<Value> {
        let mut out: Vec<Value> = fixed
            .iter()
            .map(|(n, p)| json!({"name": n, "path": p, "fixed": true, "exists": true}))
            .collect();
        for (name, path) in &self.own {
            if !fixed.contains_key(name) {
                out.push(json!({"name": name, "path": path, "fixed": false,
                                "exists": Path::new(path).is_dir()}));
            }
        }
        out
    }

    fn change(
        &mut self,
        op: &Value,
        action: &str,
        cfg: &Value,
        terminals: &HashMap<String, Arc<Live>>,
    ) -> Result<(), String> {
        let fixed = parse_dirs(cfg);
        let name = tidy(op.get("name").and_then(|v| v.as_str()).unwrap_or(""), 40);
        if action == "project_add" {
            let path = normal_folder(op.get("path").and_then(|v| v.as_str()).unwrap_or(""))?;
            if fixed.values().any(|p| *p == path) || self.own.iter().any(|(_, p)| *p == path) {
                return Err("这个文件夹已经在项目里".into());
            }
            if self.own.len() >= MAX_OWN_PROJECTS {
                return Err(format!("项目已达 {MAX_OWN_PROJECTS} 个，请先移除不用的"));
            }
            if !Path::new(&path).is_dir() {
                if op.get("create").and_then(|v| v.as_bool()) != Some(true) {
                    return Err("电脑上没有这个文件夹".into());
                }
                let parent = Path::new(&path).parent().unwrap_or(Path::new("/"));
                if !parent.is_dir() {
                    return Err("上一级文件夹不存在，不能新建".into());
                }
                std::fs::create_dir(&path).map_err(|_| "新建文件夹失败".to_string())?;
            }
            let mut name = if name.is_empty() {
                let base = path.trim_end_matches('/');
                tidy(base.rsplit('/').next().unwrap_or(base), 40)
            } else {
                name
            };
            if name.is_empty() {
                name = path.clone();
            }
            let wanted = name.clone();
            let mut n = 1;
            while fixed.contains_key(&name) || self.own.iter().any(|(x, _)| *x == name) {
                n += 1;
                name = format!("{wanted} {n}");
            }
            self.own.push((name, path));
        } else {
            let at = match self.own.iter().position(|(x, _)| *x == name) {
                Some(at) => at,
                None => {
                    return Err(if fixed.contains_key(&name) {
                        "这个项目写在电脑的配置文件里，请在电脑上修改".into()
                    } else {
                        "没有这个项目".into()
                    })
                }
            };
            if action == "project_remove" {
                let busy = terminals.iter().any(|(_, live)| {
                    let inner = live.inner.lock().unwrap();
                    inner.project == name && !inner.closed
                });
                if busy {
                    return Err("这个项目还有终端在运行，请先结束".into());
                }
                self.own.remove(at);
            } else {
                let to = tidy(op.get("to").and_then(|v| v.as_str()).unwrap_or(""), 40);
                if to.is_empty() {
                    return Err("名称需为 1 至 40 字".into());
                }
                if to != name && (fixed.contains_key(&to) || self.own.iter().any(|(x, _)| *x == to))
                {
                    return Err("已有同名项目".into());
                }
                self.own[at].0 = to.clone();
                for live in terminals.values() {
                    let mut inner = live.inner.lock().unwrap();
                    if inner.project == name {
                        inner.project = to.clone(); // the folder itself is unchanged
                    }
                }
            }
        }
        self.save()
    }
}

// ---------- the agent ----------

struct Ack {
    error: String,
    result: Option<Value>, // the answer of a file request, handed over once
}

struct State {
    server: String,
    token: String,
    next_login: f64,
    terminals: HashMap<String, Arc<Live>>,
    completed: BTreeMap<String, Ack>,
    completed_order: VecDeque<String>, // insertion order, for evicting the oldest
    reported: BTreeSet<String>,
    last_activity: f64,
    last_input: f64,
    soon: bool,
    paired: bool,
}

struct Parker(Mutex<bool>, Condvar);

impl Parker {
    fn wake(&self) {
        let mut set = self.0.lock().unwrap();
        *set = true;
        self.1.notify_all();
    }

    /// Waits up to `d`, returning immediately if woken meanwhile.
    fn wait(&self, d: Duration) {
        let mut set = self.0.lock().unwrap();
        if !*set {
            let (guard, _) = self
                .1
                .wait_timeout(set, d)
                .unwrap_or_else(|p| p.into_inner());
            set = guard;
        }
        *set = false;
    }
}

struct Agent {
    data: PathBuf,
    password: String,
    instance: String,
    projects: Mutex<Projects>,
    state: Mutex<State>,
    parker: Arc<Parker>,
    report_http: Http,
    pull_http: Http,
}

impl Agent {
    fn new(data: PathBuf, password: String) -> Agent {
        Agent {
            instance: random_hex(32),
            projects: Mutex::new(Projects::load(&data)),
            state: Mutex::new(State {
                server: String::new(),
                token: String::new(),
                next_login: 0.0,
                terminals: HashMap::new(),
                completed: BTreeMap::new(),
                completed_order: VecDeque::new(),
                reported: BTreeSet::new(),
                last_activity: now(),
                last_input: 0.0,
                soon: false,
                paired: false,
            }),
            parker: Arc::new(Parker(Mutex::new(false), Condvar::new())),
            report_http: Http::new(),
            pull_http: Http::new(),
            data,
            password,
        }
    }

    fn shell(&self, cfg: &Value) -> String {
        let configured = cfg_str(cfg, "Shell").trim();
        let shell = if configured.is_empty() {
            std::env::var("SHELL").unwrap_or_else(|_| "/bin/bash".into())
        } else {
            configured.to_string()
        };
        if Path::new(&shell).is_file() {
            shell
        } else {
            "/bin/bash".into()
        }
    }

    fn login(&self, state: &mut State) -> bool {
        if !valid_server(&state.server) {
            return false;
        }
        state.next_login = now() + 60.0; // one attempt a minute until it works
        let attempt = self.report_http.post(
            &format!("{}/api/login", state.server),
            &json!({"password": self.password}),
            "",
            Duration::from_secs(20),
        );
        let (status, body) = match attempt {
            Ok(r) => r,
            Err(_) => return false,
        };
        let token = serde_json::from_str::<Value>(&body)
            .ok()
            .and_then(|v| v.get("token").and_then(|t| t.as_str()).map(String::from))
            .filter(|t| status == 200 && t.len() >= 32);
        match token {
            Some(token) => {
                state.token = token;
                say(&format!("已连接中转 {}", state.server));
                true
            }
            None => false,
        }
    }

    fn report(&self) {
        let cfg = read_config(&self.data);
        let (server_before, token, dirs, payload, ack_ids, closed_ids) =
            {
                let mut state = self.state.lock().unwrap();
                let server = cfg_str(&cfg, "Server")
                    .trim()
                    .trim_end_matches('/')
                    .to_string();
                if server != state.server {
                    state.server = server;
                    state.token.clear();
                }
                // even when nothing can go out, pending must be sealed: it is the only
                // buffer without a bound, and the floor on output carries the agent
                // through a long relay outage
                for live in state.terminals.values() {
                    seal(live);
                }
                if state.server.is_empty() {
                    return;
                }
                if state.token.is_empty() && (now() < state.next_login || !self.login(&mut state)) {
                    return;
                }
                if !state.paired {
                    state.paired = true;
                    let name = tidy(cfg_str(&cfg, "Name"), 60);
                    let name = if name.is_empty() { hostname() } else { name };
                    if unsafe { libc::isatty(2) } == 1 {
                        print_pairing(&pairing_address(&cfg, &state.server), &self.password, &name);
                    } else {
                        say("配对信息不写入日志；在终端运行本程序 --pair 查看地址、密码和二维码");
                    }
                }
                let enabled = cfg.get("RemoteEnabled").and_then(|v| v.as_bool()) == Some(true);
                if !enabled {
                    for live in state.terminals.values() {
                        let closed = live.inner.lock().unwrap().closed;
                        if !closed {
                            live.begin_close();
                        }
                    }
                }
                let fixed = parse_dirs(&cfg);
                let dirs = self.projects.lock().unwrap().all_from(fixed.clone());
                for live in state.terminals.values() {
                    live.escalate();
                }
                for live in state.terminals.values() {
                    seal(live);
                }
                let output = output_batch(&state.terminals);
                if !output.is_empty() {
                    state.last_activity = now();
                }
                // Pieces of files are large: a report carries as many as fit, the rest go
                // with the next — the same budget the Python agent keeps. Oldest first, so
                // a file answer reaches the waiting phone page without a long queue ahead.
                let mut acks: Vec<Value> = Vec::new();
                let mut room = (3 * FILE_PIECE) as i64;
                for id in state.completed_order.iter() {
                    if state.reported.contains(id) {
                        continue;
                    }
                    let Some(a) = state.completed.get(id) else {
                        continue;
                    };
                    let data = a
                        .result
                        .as_ref()
                        .and_then(|r| r.get("data"))
                        .and_then(|v| v.as_str())
                        .map(|s| s.len())
                        .unwrap_or(0);
                    room -= data as i64;
                    if !acks.is_empty() && room < 0 {
                        break;
                    }
                    acks.push(match &a.result {
                        Some(r) => json!({"id": id, "error": a.error, "result": r}),
                        None => json!({"id": id, "error": a.error}),
                    });
                    if acks.len() >= 200 {
                        break;
                    }
                }
                let ack_ids: Vec<String> = acks
                    .iter()
                    .map(|a| {
                        a.get("id")
                            .and_then(|v| v.as_str())
                            .unwrap_or("")
                            .to_string()
                    })
                    .collect();
                let mut closed_ids: Vec<String> = Vec::new();
                let terminals: Vec<Value> = state.terminals.values().map(|live| {
                let inner = live.inner.lock().unwrap();
                if inner.closed {
                    closed_ids.push(live.id.clone());
                }
                json!({"id": live.id, "state": if inner.closed { "closed" } else { "running" },
                       "cols": inner.cols, "rows": inner.rows, "session": "", "status": "",
                       "exit_code": inner.exit_code})
            }).collect();
                let entries = self.projects.lock().unwrap().entries_from(&fixed);
                let shell_name = self
                    .shell(&cfg)
                    .rsplit('/')
                    .next()
                    .unwrap_or_default()
                    .to_string();
                let payload = json!({
                    "info": {"instance": self.instance, "enabled": enabled, "tools": ["shell"],
                             "features": ["terminal-exit", "files"], "version": VERSION,
                             "shell": shell_name,
                             "workspaces": dirs.keys().collect::<Vec<_>>(),
                             "projects": entries},
                    "terminals": terminals,
                    "output": output,
                    "acks": acks,
                });
                (
                    state.server.clone(),
                    state.token.clone(),
                    dirs,
                    payload,
                    ack_ids,
                    closed_ids,
                )
            };
        let result = self.report_http.post(
            &format!("{server_before}/api/terminal/agent"),
            &payload,
            &token,
            Duration::from_secs(20),
        );
        let (status, body) = match result {
            Ok(r) => r,
            Err(_) => return,
        };
        if status == 401 {
            self.state.lock().unwrap().token.clear();
            return;
        }
        if status != 200 {
            return;
        }
        let answer: Value = match serde_json::from_str(&body) {
            Ok(v) => v,
            Err(_) => return,
        };
        let mut state = self.state.lock().unwrap();
        // a report that went through said which terminals ended: only now may they be
        // forgotten, or the phone would show a terminal running that died in transit
        for id in closed_ids {
            if let Some(live) = state.terminals.get(&id) {
                live.inner.lock().unwrap().told_closed = true;
            }
        }
        for id in ack_ids {
            if let Some(a) = state.completed.get_mut(&id) {
                a.result = None; // handed over once: nothing of a file is kept
            }
            state.reported.insert(id);
        }
        if let Some(ops) = answer.get("operations").and_then(|v| v.as_array()) {
            let ops: Vec<Value> = ops.iter().filter(|op| op.is_object()).cloned().collect();
            for op in &ops {
                self.execute(op, &cfg, &dirs, &mut state);
            }
        }
        if let Some(upto_map) = answer.get("output_ack").and_then(|v| v.as_object()) {
            for live in state.terminals.values() {
                if let Some(upto) = upto_map.get(&live.id).and_then(|v| v.as_i64()) {
                    live.trim(upto);
                }
            }
        }
        if state.completed.len() > 4000 {
            // evict the oldest executed (insertion order), never by id: an ack dropped
            // while the relay still remembers the operation would run it a second time
            for _ in 0..1000 {
                match state.completed_order.pop_front() {
                    Some(id) => {
                        state.completed.remove(&id);
                        state.reported.remove(&id);
                    }
                    None => break,
                }
            }
        }
        let forget: Vec<String> = state
            .terminals
            .iter()
            .filter(|(_, live)| live.can_forget())
            .map(|(id, _)| id.clone())
            .collect();
        for id in forget {
            if let Some(live) = state.terminals.remove(&id) {
                live.dispose();
            }
        }
    }

    fn execute(&self, op: &Value, cfg: &Value, dirs: &BTreeMap<String, String>, state: &mut State) {
        let action = op
            .get("action")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        let project = matches!(
            action.as_str(),
            "project_add" | "project_remove" | "project_rename"
        );
        let file_op = matches!(action.as_str(), "file_list" | "file_read");
        let op_id = op
            .get("id")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        let terminal = op
            .get("terminal")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        // shape and freshness, before anything is carried out
        if !is_lower_hex(&op_id, 16, 32)
            || (!project && !file_op && !is_lower_hex(&terminal, 32, 32))
        {
            say("拒绝操作：操作编号无效");
            return;
        }
        let at = match op.get("at") {
            Some(Value::Number(n)) => n.as_f64(),
            Some(Value::String(s)) => s.trim().parse::<f64>().ok(),
            _ => None,
        };
        if !matches!(at, Some(at) if (now() - at).abs() <= OP_TTL) {
            say("拒绝操作：操作已过期");
            return;
        }
        if state.completed.contains_key(&op_id) {
            state.reported.remove(&op_id); // the ack may have been lost: send it again
            return;
        }
        let mut error = String::new();
        let mut result: Option<Value> = None;
        if cfg.get("RemoteEnabled").and_then(|v| v.as_bool()) != Some(true) {
            error = "电脑远控已关闭".into();
        } else if project {
            if let Err(e) = self
                .projects
                .lock()
                .unwrap()
                .change(op, &action, cfg, &state.terminals)
            {
                error = e;
            }
        } else if action == "start" {
            error = self
                .start_terminal(op, dirs, &terminal, cfg, state)
                .err()
                .unwrap_or_default();
        } else if file_op {
            let root = dirs.get(op.get("dir").and_then(|v| v.as_str()).unwrap_or(""));
            match root {
                None => error = "没有这个项目".to_string(),
                Some(root) => {
                    let relative = op.get("path").and_then(|v| v.as_str()).unwrap_or("");
                    let offset = match op.get("offset") {
                        None => Ok(0),
                        Some(Value::Number(n)) => n.as_i64().ok_or(()),
                        _ => Err(()),
                    };
                    match files(root, relative, &action, offset) {
                        Ok(answer) => result = Some(answer),
                        Err(e) => error = e,
                    }
                }
            }
        } else {
            let existing = state
                .terminals
                .get(&terminal)
                .map(Arc::clone)
                .filter(|l| !l.inner.lock().unwrap().closed);
            match existing {
                None => error = "终端已结束".into(),
                Some(live) => {
                    // a terminal can always be ended; typing into it needs its folder
                    // still allowed (a removed project must not keep taking input)
                    if action != "close"
                        && !dirs.values().any(|p| *p == live.inner.lock().unwrap().dir)
                    {
                        error = "目录不再获允许".into();
                    } else {
                        error = match action.as_str() {
                            "input" => self.do_input(&live, op, state),
                            "resize" => self.do_resize(&live, op),
                            "close" => {
                                live.begin_close();
                                String::new()
                            }
                            _ => "操作无效".into(),
                        };
                    }
                }
            }
        }
        if !error.is_empty() {
            say(&format!(
                "操作 {} 失败：{}",
                &op_id[..op_id.len().min(8)],
                error
            ));
        }
        state.completed_order.push_back(op_id.clone());
        state.completed.insert(op_id.clone(), Ack { error, result });
        state.reported.remove(&op_id);
        state.last_activity = now();
        state.soon = true;
    }

    fn do_input(&self, live: &Arc<Live>, op: &Value, state: &mut State) -> String {
        let data = op.get("data").and_then(|v| v.as_str()).unwrap_or("");
        if data.is_empty() || data.chars().count() > 16000 {
            return "输入无效".into();
        }
        state.last_input = now();
        live.write(data).err().unwrap_or_default()
    }

    fn do_resize(&self, live: &Arc<Live>, op: &Value) -> String {
        let cols = op.get("cols").and_then(|v| v.as_i64());
        let rows = op.get("rows").and_then(|v| v.as_i64());
        match (cols, rows) {
            (Some(c), Some(r)) if (20..=240).contains(&c) && (6..=100).contains(&r) => {
                live.resize(c as u16, r as u16);
                String::new()
            }
            _ => "尺寸无效".into(),
        }
    }

    fn start_terminal(
        &self,
        op: &Value,
        dirs: &BTreeMap<String, String>,
        terminal: &str,
        cfg: &Value,
        state: &mut State,
    ) -> Result<(), String> {
        if state.terminals.contains_key(terminal) {
            return Ok(()); // already started; the operation is a repeat
        }
        let tool = op.get("tool").and_then(|v| v.as_str()).unwrap_or("");
        if tool != "shell" {
            return Err("电脑没有这个工具".into());
        }
        let name = op.get("dir").and_then(|v| v.as_str()).unwrap_or("");
        let cwd = dirs.get(name).cloned().ok_or("目录未获允许")?;
        if !Path::new(&cwd).is_dir() {
            return Err("电脑上没有这个文件夹".into());
        }
        let running = state
            .terminals
            .values()
            .filter(|l| !l.inner.lock().unwrap().closed)
            .count();
        if running >= MAX_TERMINALS {
            return Err(format!("最多同时运行 {MAX_TERMINALS} 个终端，请先结束一个"));
        }
        let shell = self.shell(cfg);
        let live = Live::spawn(terminal, name, &cwd, 80, 24, &shell, self.parker.clone())?;
        state.terminals.insert(terminal.to_string(), live);
        Ok(())
    }

    fn listen(&self) {
        loop {
            if STOP.load(Ordering::SeqCst) {
                return;
            }
            let (token, server) = {
                let state = self.state.lock().unwrap();
                (state.token.clone(), state.server.clone())
            };
            if token.is_empty() || server.is_empty() {
                std::thread::sleep(Duration::from_secs(1));
                continue;
            }
            let started = Instant::now();
            let mut brought = false;
            let payload = json!({"instance": self.instance, "wait": 12});
            match self.pull_http.post(
                &format!("{server}/api/terminal/agent/pull"),
                &payload,
                &token,
                Duration::from_secs(20),
            ) {
                Ok((401, _)) => self.state.lock().unwrap().token.clear(),
                Ok((200, body)) => {
                    if let Ok(result) = serde_json::from_str::<Value>(&body) {
                        if let Some(ops) = result.get("operations").and_then(|v| v.as_array()) {
                            let ops: Vec<Value> =
                                ops.iter().filter(|op| op.is_object()).cloned().collect();
                            if !ops.is_empty() {
                                brought = true;
                                let cfg = read_config(&self.data);
                                let mut state = self.state.lock().unwrap();
                                let dirs = self.projects.lock().unwrap().all(&cfg);
                                for op in &ops {
                                    self.execute(op, &cfg, &dirs, &mut state);
                                }
                                drop(state);
                                self.parker.wake();
                            }
                        }
                    }
                }
                Ok(_) => std::thread::sleep(Duration::from_secs(2)), // 429/5xx: back off, like the Python agent
                Err(_) => std::thread::sleep(Duration::from_secs(2)), // network trouble: the same
            }
            // an answer that came back at once must not become a busy loop; plain sleep —
            // the shared wake event belongs to the report loop and must not be consumed here.
            // One that brought something is asked for again at once: in a drag or fast typing
            // the next is already waiting.
            if !brought && started.elapsed() < Duration::from_millis(40) {
                std::thread::sleep(Duration::from_millis(40));
            }
        }
    }

    // the agent lives for the whole process (Box::leak in main), so the listener thread
    // can hold a plain 'static reference
    fn run(agent: &'static Agent) -> i32 {
        std::thread::spawn(move || agent.listen());
        while !STOP.load(Ordering::SeqCst) {
            agent.report();
            let (soon, last_activity, last_input) = {
                let state = agent.state.lock().unwrap();
                (state.soon, state.last_activity, state.last_input)
            };
            agent.state.lock().unwrap().soon = false;
            let pause = if soon {
                Duration::from_millis(20)
            } else if now() - last_activity < 20.0 {
                Duration::from_millis(250)
            } else {
                Duration::from_millis(700)
            };
            agent.parker.wait(pause);
            if now() - last_input < 0.15 {
                std::thread::sleep(Duration::from_millis(12));
            }
        }
        let mut state = agent.state.lock().unwrap();
        for live in state.terminals.values() {
            let closed = live.inner.lock().unwrap().closed;
            if !closed {
                live.kill();
            }
            live.dispose();
        }
        state.terminals.clear();
        0
    }
}

fn print_pairing(server: &str, password: &str, name: &str) {
    let enc = |s: &str| {
        s.bytes()
            .map(|b| match b {
                b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                    (b as char).to_string()
                }
                _ => format!("%{b:02X}"),
            })
            .collect::<String>()
    };
    let payload = format!(
        "remotecli://connect?u={}&p={}&n={}",
        enc(server),
        enc(password),
        enc(name)
    );
    eprintln!("\n配对：手机 App 点“扫码添加电脑”，扫下面的二维码，或手动输入地址与密码。");
    eprintln!("  地址：{server}\n  名称：{name}\n  密码：{password}\n");
    if let Ok(out) = std::process::Command::new("qrencode")
        .arg("-t")
        .arg("ANSIUTF8")
        .arg(&payload)
        .output()
    {
        if out.status.success() && !out.stdout.is_empty() {
            eprintln!("{}", String::from_utf8_lossy(&out.stdout));
        }
    }
}

/// Where the phone reaches the relay. It differs from Server when the relay runs on this
/// computer: the agent talks to it locally, the phone through PairUrl.
fn pairing_address(cfg: &Value, server: &str) -> String {
    let public = cfg_str(cfg, "PairUrl")
        .trim()
        .trim_end_matches('/')
        .to_string();
    if valid_server(&public) {
        public
    } else {
        server.to_string()
    }
}

/// --pair: what the phone needs to add this computer, for the person at the console.
fn show_pairing(data: &Path) -> u8 {
    let cfg = read_config(data);
    let server = cfg_str(&cfg, "Server")
        .trim()
        .trim_end_matches('/')
        .to_string();
    let password = read_password(data);
    if !valid_server(&server) || password.is_empty() {
        say("缺少中转地址或密码：先在 config.json 填写 Server，并用 --set-password 存入密码");
        return 2;
    }
    let name = tidy(cfg_str(&cfg, "Name"), 60);
    let name = if name.is_empty() { hostname() } else { name };
    print_pairing(&pairing_address(&cfg, &server), &password, &name);
    0
}

fn set_password(folder: &Path) -> u8 {
    let mut secret = String::new();
    if std::io::stdin().read_line(&mut secret).is_err() {
        return 2;
    }
    let secret = secret.trim().to_string();
    if secret.is_empty() {
        return 2;
    }
    let _ = std::fs::create_dir_all(folder);
    match write_private(&folder.join("password.txt"), &format!("{secret}\n")) {
        Ok(()) => {
            println!(
                "密码已写入 {}（权限 600）",
                folder.join("password.txt").display()
            );
            0
        }
        Err(_) => 2,
    }
}

extern "C" fn on_signal(_sig: libc::c_int) {
    STOP.store(true, Ordering::SeqCst);
}

fn main() -> std::process::ExitCode {
    let argv: Vec<String> = std::env::args().collect();
    let mut data = std::env::var("REMOTECLI_DATA").unwrap_or_else(|_| {
        format!(
            "{}/.local/state/remote-cli-agent",
            std::env::var("HOME").unwrap_or_default()
        )
    });
    if let Some(at) = argv.iter().position(|a| a == "--data") {
        if at + 1 < argv.len() {
            data = argv[at + 1].clone();
        }
    }
    if argv.iter().any(|a| a == "--pair") {
        return std::process::ExitCode::from(show_pairing(Path::new(&data)));
    }
    if argv.len() > 1 && argv[1] == "--set-password" {
        let folder = argv.get(2).cloned().unwrap_or(data);
        return std::process::ExitCode::from(set_password(Path::new(&folder)));
    }
    let data = PathBuf::from(data);
    let _ = std::fs::create_dir_all(&data);
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(&data, std::fs::Permissions::from_mode(0o700));
    }
    let _singleton = match acquire_singleton(&data) {
        Some(file) => file,
        None => {
            say("此数据目录已有 agent 在运行（agent.lock 被占用），退出");
            return std::process::ExitCode::from(3);
        }
    };
    let password = read_password(&data);
    if password.is_empty() {
        say(&format!(
            "缺少中转密码：运行 {} --set-password {} 并输入中转的密码，或设置环境变量 RCLI_PASSWORD",
            argv.first().map(String::as_str).unwrap_or("remote-cli-agent"), data.display()));
        return std::process::ExitCode::from(2);
    }
    say(&format!(
        "remote-cli agent (Linux, Rust) {VERSION}，数据目录 {}",
        data.display()
    ));
    unsafe {
        libc::signal(libc::SIGTERM, on_signal as *const () as libc::sighandler_t);
        libc::signal(libc::SIGINT, on_signal as *const () as libc::sighandler_t);
    }
    let agent: &'static Agent = Box::leak(Box::new(Agent::new(data, password)));
    std::process::ExitCode::from(Agent::run(agent) as u8)
}
