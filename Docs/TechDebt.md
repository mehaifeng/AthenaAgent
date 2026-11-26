# Tech Debt Register

Known costs we have measured, understood, and deliberately not paid yet.

An entry earns a place here only if it names **what it actually costs**, **why the
obvious fix is not obviously right**, and **what evidence would settle it**. A vague
"this could be better" belongs in a commit message, not here. When an entry is paid
off, delete it and move the reasoning into `CLAUDE.md` — this file is a queue, not an
archive.

---

The conversation-switch layout stall was addressed by indexed archive loading and a mixed-height message viewport. The baseline, native measurements and remaining limits are recorded in [the desktop performance report](DesktopPerformance_CN.md).

## First launch from entirely new macOS program files

With 100 archived chats, the first native launch from a newly published directory took
5.687 seconds. A fresh file-by-file copy took 3.645 seconds. The installed application
started in 1.619–1.959 seconds, and its first launch after an in-place update took
1.810–1.824 seconds. Single-file and IL-only releases took longer on first open.
The report records the process timestamps and test conditions. A launch trace on a
freshly installed release bundle would help attribute the remaining time before and
during managed startup. [Measurements and reproduction](DesktopPerformance_CN.md)
