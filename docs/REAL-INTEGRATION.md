# Actual runtime integration

Run: 2026-10-28T15:46:39.0773372+00:00

Verified upstream downloads, isolated application profile, loopback-only listeners. No user PATH or hosts modifications.

- PASS: Downloaded, verified, and registered actual PHP 8.4, PHP 8.5, Nginx, and Mailpit releases.
- Installed PHP 8.4.27 (8.4).
- Installed PHP 8.5.12 (8.5).
- Installed Nginx 1.31.8 (official Windows ZIP, verified detached signature).
- Installed Mailpit 1.32.0 (official GitHub Windows asset, verified SHA256).
- PASS: Two real PHP channels execute through separate Nginx sites, including document roots with spaces and non-ASCII characters.
- PASS: OpenSSL enable/disable is reflected by actual PHP requests while the other channel remains available.
- PASS: Bundled OPcache uses Zend-extension loading and can be disabled again.
- PASS: Per-site PHP switching changes actual request routing independently of the CLI default.
- PASS: An invalid user-edited Nginx candidate leaves actual working request routing and site metadata intact.
- PASS: Actual Nginx/PHP rejects nonexistent scripts and path-guessing URLs.
- PASS: Actual PHP mail() and framework-style SMTP messages are captured and readable through Mailpit's API.
- PASS: Stop All drains an in-flight PHP request before stopping the actual PHP and Mailpit processes.
- PASS: Mail persists after restart; independent CLI survives manager exit and forwards the selected version and exit code.

Result: **PASS**. No PATH/hosts changes and no managed services left running.
