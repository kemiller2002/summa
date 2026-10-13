# Upgrading foundations through Conditor

Summa's foundations (Ordo, Praxis, Arca, Fides, Limen, Forma, Folio and
Visual Engineering) are pinned by Conditor. No pin, lockfile or
`.echelon/foundations.json` is ever edited by hand. Each registry set is
applied in its own pull request, one set at a time. WI-0052 went from
1.19.0 to 1.22.0 this way, in #86, #87, #92 and #93.

## Steps for one registry set

1. **Verify Conditor.** The binary's sha256 must match its release's
   `checksums.txt`, and `gh attestation verify` must name the release
   workflow at the expected tag.
2. **Verify the set file.** Fetch the set file from
   `echelon-registry/channels/echelon-current/<platform>.json` at the
   set's commit, and check its sha256.
3. **Record drift before.** Run
   `conditor drift --registry <echelon-registry checkout> --target .` and
   keep the output for the pull request.
4. **Plan.** Run
   `conditor upgrade --current --check --stepwise --registry <checkout>
   --resolved-set <file> --resolved-set-sha256 <sha>`. Compare the plan
   with the upgrade notice. If they disagree, or the check refuses, stop and
   report. Do not work around it.
5. **Authorize from a clean tree.** Run the authorize through
   `tools/authorize-clean.sh`:
   ```
   tools/authorize-clean.sh -- conditor upgrade --current --resolved-set <file> \
     --resolved-set-sha256 <sha> --authorize <plan digest>
   ```
   The Conditor lock records structural findings for files on disk, so a
   build output present at authorize time would end up in the lock. The
   script removes every `bin`, `obj`, `build`, `test-results` and
   `playwright-report` directory outside `node_modules` and `.git`. It
   refuses (exit 3) if any remain, and only then runs the command.
   `tools/authorize-clean.sh --verify-only` lists build outputs without
   removing them.
6. **Check consistency after.** Make sure that:
   - `--check` and `drift` agree, with every pin the same;
   - every path in `.conditor/lock.json` is a tracked file;
   - for an Ordo step, `ordo verify` exits 0.
7. **Update the baseline index.** Add the set's row to
   `docs/measurement/baseline-sources.md` before pushing, and update any
   conformance test that pins a version.
8. **Test before every push.** Run the full `dotnet test`, Limen strict
   verification and the browser suite. Compare the printed invoice and
   credit memo with the earlier captures. Any visual change is rebaselined
   through CI with image review.
9. **Open the pull request** with drift before and after, then merge it
   through the gate.
