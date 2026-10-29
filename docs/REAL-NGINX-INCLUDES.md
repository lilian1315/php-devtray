# Actual Nginx include verification

Run: 2026-10-29T11:01:29.0404714+00:00

Previously publisher-verified PHP/Nginx distributions, copied into a fresh isolated profile. No PATH/hosts changes.

- PASS: Actual startup and HTTP requests accept absolute direct file/glob includes, nested relative/glob includes, and nested absolute includes.
- PASS: Broken nested configuration fails actual nginx -t without publishing a bootstrap or stopping the working instance.
- PASS: Relative includes written directly in sites-available are rejected while the working route remains available.
- PASS: Sources are preserved and temporary validation wrappers are removed.

Result: **PASS**. Owned services stopped; unrelated listeners were not touched.
