# Releasing

A release is built by the manual workflow `.github/workflows/release.yml`. It builds the installers on GitHub's
runners, smoke-tests each backend and creates a **draft** release; nothing is public until the draft is
published by hand.

## 1. Bump the version

The version lives in the root `package.json`; a script writes it to the other files that need it as text
(`src/ui/package.json`, `Directory.Build.props`, `src/desktop/Cargo.toml` and `Cargo.lock`).
`tauri.conf.json` reads it from `package.json` itself.

```sh
pnpm version:set 0.2.0
pnpm version:check
```

Add the entry to `CHANGELOG.md`, headed `## [0.2.0] - <date>`, with a link definition at the end of the file.
Its text becomes the release notes.

If a dependency changed, regenerate the third-party notices (needs network access for the licence texts):

```sh
dotnet restore Marketgrade.slnx
pnpm install
cargo fetch --manifest-path src/desktop/Cargo.toml
node tools/release/notices.mjs
```

Run the checks from the README, commit, and push to `main`.

## 2. Run the workflow

GitHub, Actions, **Release**, **Run workflow**, on `main`, with the version without the `v` (`0.2.0`). The
workflow:

1. Fails unless every file has that version, the changelog has an entry for it, and the tag `v0.2.0` does not
   exist yet.
2. Builds on four runners at once: `macos-15` (Apple Silicon DMG), `macos-15-intel` (Intel DMG),
   `windows-2025` (NSIS installer) and `ubuntu-22.04` (AppImage and `.deb`). Each publishes the backend for its
   target, starts it with a test token and checks `/health`, `/engine/status`, `/settings/recording` and
   `/history/delay`, builds the user interface and the installers, and on macOS checks the ad-hoc signature
   and runs the same test on the backend inside the app. The job summary lists each file's size.
3. Collects the installers, writes `SHA256SUMS.txt` and creates the draft release `v0.2.0` with the changelog
   entry as its notes.

A full run takes about 20 to 30 minutes. A failed job can be re-run on its own from the run's page.

## 3. Check the draft

On the Releases page, open the draft:

- Five installers and `SHA256SUMS.txt`: `Marketgrade_<version>_aarch64.dmg`, `Marketgrade_<version>_x64.dmg`,
  `Marketgrade_<version>_x64-setup.exe`, `Marketgrade_<version>_amd64.AppImage`, `Marketgrade_<version>_amd64.deb`.
- Download at least the installer for your own computer, compare `shasum -a 256 <file>` (macOS, Linux) or
  `Get-FileHash <file>` (Windows) with its line in `SHA256SUMS.txt`, install it, open it as the README
  describes, and see all three exchanges connect.
- Read the notes.

## 4. Publish

**Publish release** on the draft. GitHub then creates the tag `v<version>` on the commit the workflow built.
To start again instead, delete the draft (no tag exists yet) and run the workflow again.

## Runner images

- `macos-15-intel` is GitHub's last Intel macOS image; GitHub plans to retire it in fall 2027. After that the
  Intel DMG has to be built on an Apple Silicon runner with `--target x86_64-apple-darwin`, and its smoke test
  run under Rosetta.
- `ubuntu-22.04` gives the Linux builds their minimum of glibc 2.35. GitHub began retiring the image on
  17 September 2026 and stops supporting it on 17 April 2027; until then a run can hit a scheduled brownout,
  which a re-run fixes. After that, build in an `ubuntu:22.04` container on `ubuntu-24.04` to keep the same
  minimum.
