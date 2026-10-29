# PHP patch activation

Preview 0.4 adds patch replacement within an installed PHP minor channel. Sites and the CLI default reference the channel, so a change from one `8.4.x` patch to another keeps those assignments. The channel also retains its FastCGI port, enabled extensions, mail integration, and saved startup policy.

## User flow

Open the runtime's actions and choose **Check for updates**. The installer shows that channel's active and official available patch. **Update PHP** downloads a newer patch; a selected older patch is labeled **Switch PHP**. A release already retained locally activates without another download. A selected active patch is disabled, including Windows package rebuilds reporting the same PHP patch version.

Previously active distributions appear under the runtime's **Previous patches** actions. A switch uses today's channel policy and user INI configuration, rather than restoring old extension preferences. An enabled module that is absent or fails to load in the selected distribution blocks activation.

Importing a separately verified extracted PHP distribution also activates a different patch in its existing channel. Import remains an explicit trust decision by the user; official installation uses fresh publisher metadata and archive integrity checks.

## Activation and recovery

1. Copy an imported distribution into a new uniquely named managed directory. Official archives are freshly resolved, integrity-checked, and safely extracted before import.
2. Verify x64 executable layout, matching PHP CLI/CGI patch identities and NTS builds. A verified download's executable patch must also match the trusted release identifier.
3. Build an immutable INI snapshot and check configuration diagnostics and actual loaded modules. No live service or CLI target changes during this validation.
4. If the channel is running, drain its dependent Nginx first, stop only that PHP service, start the candidate, and verify its owned FastCGI endpoint. Resume the previous Nginx definition; the channel endpoint and site routing remain the same. Other PHP channels remain running.
5. Publish the tested snapshot's manager-owned INI files and, for the default channel, atomically point the independent CLI at that exact same snapshot. Save active and retained version metadata. A stopped channel remains stopped.
6. On a handled error or cancellation, stop any candidate transition, restore previous manager-owned INI/CLI bytes, recover the previous managed process definitions with an uncanceled token, and keep the previous desired settings. An incomplete recovery is reported explicitly and candidate files are kept for diagnosis.

User-owned `20-custom.ini` and Nginx sources are not overwritten. A source edit detected during transition rejects activation and remains available to the user. The candidate definition and published CLI both use the validated snapshot, preventing a later source reread from silently selecting different configurations.

Retained metadata is checked for channel/version consistency, duplicates, active-version aliases, managed-path containment and junction traversal. Retained executable identities and loaded modules are checked again before switching. Fresh rejected candidates are cleaned only when unreferenced and recovery is complete; active and retained distributions are preserved.

## Limits

This is handled-error recovery, not a durable cross-file transaction across sudden power loss or forced process termination. A persistent activation journal and startup reconciliation remain future work. Windows Job Objects still contain service processes when their owner exits.

Same-patch rebuild tracking, automatic updates, security-support classification, retained-version removal, Nginx/Mailpit replacement, and application self-update are not included. Retained directories can consume disk space. Checks read release metadata; downloads and activation require an explicit action.

See [the actual-runtime report](REAL-PHP-UPDATES.md) and [verification record](VERIFICATION.md) for tested behavior and remaining acceptance limits.
