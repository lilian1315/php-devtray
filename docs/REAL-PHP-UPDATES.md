# Actual PHP patch update integration

Run: 2026-10-28T21:49:49.9416545+00:00

Dedicated empty profile; official downloads and publisher integrity metadata; loopback only. No PATH/hosts modifications.

- Archived PHP 8.4.26: publisher SBOM SHA-256 `da68394f9193b7f6b89d0c76861a4034ae10efee7fd55a7255d8118c2acf70d7`. [Metadata](https://downloads.php.net/~windows/releases/archives/php-8.4.26-nts-Win32-vs17-x64.zip.cdx.json).
- Replacement: PHP 8.4.27-1 · x64 NTS; official current-manifest SHA-256 `dbae154a0a89791f36c3d48e68bae797878333e7d6cb5721e74617de59b63ab7`.
- PASS: Archived PHP was verified against its official artifact SBOM before extraction/execution; sibling PHP and Nginx used the application's verified installer.
- PASS: Live update 8.4.26 → 8.4.27 changes actual HTTP execution and CLI output while retaining the old patch.
- PASS: A real invalid candidate configuration is rejected without disrupting the active PHP/Nginx route.
- PASS: Explicit rollback drains an active real PHP request, restores actual HTTP/CLI versions, preserves user INI/Nginx sources, and leaves the sibling process untouched.
- PASS: Stopped-channel patch changes leave the service stopped while updating the independent CLI selection.

Result: **PASS**. All owned services stopped. PATH and hosts were unchanged.
