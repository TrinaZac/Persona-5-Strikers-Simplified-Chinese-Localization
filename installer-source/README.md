# P5S Simplified Chinese Patch 1.0.0 - installer review source

This is the exact C# installer source and embedded credit text used for the public
1.0.0 release uploaded at https://www.nexusmods.com/persona5strikers/mods/16 .
It is provided for security review and reproducible compilation. It does not
include the copyrighted game data, converted font, or binary patch payloads.

## Build on Windows x64

1. Use Windows with .NET Framework 4.8 installed.
2. Extract this source archive (or clone the repository).
3. Run `Build.cmd` from Command Prompt. No NuGet packages or network access are needed.
4. The compiled file is `build\Core\P5SPatch.exe`.
5. Inspect usage without installation: `build\Core\P5SPatch.exe --help`.
   Inspect credits without installation: `build\Core\P5SPatch.exe --credits`.
6. For an actual installation test, extract the existing public mod ZIP, replace
   only its `Core\P5SPatch.exe` with the newly compiled executable, and run the
   original entry script against a separate copy of the supported game files.

`Build.cmd` includes the full compiler invocation. It uses the built-in x64
.NET Framework C# compiler, optimization enabled, references to
System.Web.Extensions, System.Windows.Forms and System.Core, and embeds
`src/Credits.txt` as `P5S.Credits.txt`. It does not install or launch the game.
The legacy compiler does not provide a deterministic-build guarantee: rebuilt
PE timestamps and module identifiers can differ. The uploaded release hashes
are in `review/release-file-hashes.json`.

## Behavior to review

- Reads Steam install-location registry keys and `libraryfolders.vdf` to locate
  the game. It does not write registry values.
- The user confirms the selected game directory before installation/restoration.
- Checks exact SHA-256 hashes of the manifest, payloads, original resources,
  protected executable and resource index, backups, and generated outputs.
- Changes only the four resource paths listed by the pinned manifest, with
  backups/state/staging under `.p5s-simplified-chinese` in the selected game root.
- Applies gzip-compressed `P5SDLT1` XOR-difference records to staged original
  copies and atomically commits only fully verified generated resources.
- Includes uninstall and interrupted-transaction recovery.
- Refuses running games, unexpected external modifications, incompatible
  baselines, read-only targets, and symlink/junction paths.
- Contains sandbox-gated fault-injection hooks for rollback testing; they require
  a marker matching the manifest hash and explicit test environment variables.
- Contains no network/download APIs, child-process launch calls, process-memory
  patching, system-service changes, scheduled tasks, or antivirus exclusions.
- The public executable is unsigned, unencrypted and unobfuscated.

## Compressed payloads and quarantine

The ZIP contains four `.p5sd.gz` files. Each expands to a single custom binary
delta stream (magic `P5SDLT1` plus NUL), not to another installable program.
The public C# reader is `ApplyDelta` in `src/P5SPatch.cs`. Sizes, compressed
and expanded hashes are documented in `review/release-file-hashes.json`.
Whether these gzip streams or the unsigned executable caused this specific
quarantine has not been confirmed; please review the original submitted file.

## Uploaded release identifiers

- ZIP: `P5S Simplified Chinese.zip`
- ZIP SHA-256: `0f5faafbe5f8ea60b70230b7de0f463e73da8cdff21190ab6ef92d3206d009dc`
- Patcher SHA-256: `78bc335836246d199f2cfd8d8cd361241adef08f91ad9e9bd9c33cf02cd0b0de`
- Manifest SHA-256: `1be8a849582dd9842f42144b9f58e63f07071d5f71bf088a326fc14cffa197b4`

The locally repacked upload has the same nine file contents as the original
tested release; the enclosing folder and ZIP metadata changed. Its checksum
above identifies the local upload copy. The online copy has not been downloaded
or independently hashed during this review preparation.

The local functional validation completed 26 checks, including rollback and
interrupted-operation recovery. This is functional evidence, not an antivirus
verdict or a guarantee of Nexus approval.

## Review and reuse

The author permits inspection and compilation of this installer source for
security review and personal testing. No rights to the original game content
or third-party font files are granted by this source publication. Credits and
the author's font-authorization statement are in `src/Credits.txt`.
