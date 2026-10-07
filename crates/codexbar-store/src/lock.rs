//! An exclusive lock file shared by every CodexBar writer of a file in `~/.codexbar`.

use std::fs::{File, OpenOptions};
use std::io;
use std::path::Path;

/// Held for a whole read-check-write-replace. Opening the lock file with no sharing fails fast while another writer
/// holds it, so a competing writer gets [`io::ErrorKind::WouldBlock`] instead of blocking the UI.
pub(crate) struct FileLock {
    _file: File,
}

impl FileLock {
    pub(crate) fn acquire(path: &Path) -> io::Result<Self> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let mut options = OpenOptions::new();
        options.read(true).write(true).create(true).truncate(false);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.share_mode(0);
        }
        match options.open(path) {
            Ok(file) => Ok(Self { _file: file }),
            // ERROR_SHARING_VIOLATION, or access denied while another writer holds it.
            Err(err) if err.raw_os_error() == Some(32) || err.kind() == io::ErrorKind::PermissionDenied => {
                Err(io::Error::new(
                    io::ErrorKind::WouldBlock,
                    "another CodexBar process is saving; try again shortly",
                ))
            }
            Err(err) => Err(err),
        }
    }
}
