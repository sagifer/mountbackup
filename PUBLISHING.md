# Publishing to the official N.I.N.A. plugin list

Everything is prepared; **nothing publishes until a version tag is pushed.**
The GitHub Action (`.github/workflows/build-release.yml`, adapted from the official
[template](https://github.com/isbeorn/nina.plugin.manifests/blob/main/tools/github-action.yaml))
builds the plugin on a Windows runner, creates a GitHub release with the plugin zip and
the generated manifest, and can open the manifest pull request automatically.

## One-time setup (before the first release)

1. **Fork** [`isbeorn/nina.plugin.manifests`](https://github.com/isbeorn/nina.plugin.manifests)
   under the `sagifer` account, keeping the name exactly `nina.plugin.manifests`.
2. Create a **personal access token** with write access to that fork and add it to the
   `mountbackup` repository secrets as **`PAT`**
   (Settings → Secrets and variables → Actions → New repository secret).
3. In the `mountbackup` repository: Settings → Actions → General → Workflow permissions →
   **Read and write permissions**.

Without step 1–2 the action still builds and creates the GitHub release; only the
automatic manifest PR is skipped (it can then be submitted by hand, see below).

## Releasing a version

1. Make sure `AssemblyVersion`/`AssemblyFileVersion` in
   `MountBackup/Properties/AssemblyInfo.cs` is the version you want — **the tag name must
   match it exactly** (e.g. `1.0.0.0`).
2. Commit and push everything.
3. Tag and push:

   ```
   git tag 1.0.0.0
   git push origin 1.0.0.0
   ```

4. The action then:
   - builds `MountBackup.dll` (Release),
   - generates `manifest.json` from the assembly metadata (checksum included) via the
     official `CreateManifest.ps1`,
   - creates the GitHub release `1.0.0.0` with `MountBackup.1.0.0.0.zip` and
     `MountBackup.1.0.0.0.manifest.json`,
   - if the fork + `PAT` secret exist: pushes the manifest to the fork and opens a PR
     against `isbeorn/nina.plugin.manifests` (path `manifests/m/Mount Backup/`).
5. Wait for the PR to be reviewed and merged — the plugin then appears in
   NINA → Options → Plugins for everyone.

**Never rebuild/replace a released DLL after the manifest is created** — the checksum
would no longer match. Bump the version and release again instead.

## Manual manifest PR (fallback, no PAT needed)

1. Download `MountBackup.<version>.manifest.json` from the GitHub release.
2. Fork `isbeorn/nina.plugin.manifests`, add the file as
   `manifests/m/Mount Backup/manifest.<version>.json`.
3. Open a pull request against `main` and wait for review.

## Later versions

Bump the version in `AssemblyInfo.cs` (all four segments are significant, e.g.
`1.0.1.0`), update the plugin description if needed, commit, tag with the same version
string, push the tag. Each version gets its own manifest file next to the previous ones.
