# P5S v1.0.1 installer source

This source corresponds to P5S-Simplified-Chinese-v1.0.1.zip in the v1.0.1 GitHub Release. Localization payloads and resulting game resource hashes are identical to v1.0.0.

The installer checks the selected game's corresponding Steam configuration before installation. Known non-Traditional-Chinese settings display language-specific instructions and stop installation. Pending downloads or incomplete language changes stop write operations. Missing or unassociated metadata produces a warning; full resource hashes remain required.

File failures show the path, expected and actual size/hash, with advice for game resources, installation files or backups. Known v1.0.0 installation records are accepted by their exact pinned manifest identity. Stale records after Steam restores the original files can reinstall while preserving verified backups. Uninstallation supports known mixed original/patched resources and rejects unknown changes.

Run Build.cmd on Windows with .NET Framework 4.8 to compile the executable. Copy the four matching Core/patches files from the release package into build/Core/patches to use the rebuilt installer. The manifest is pinned in src/P5SPatch.cs. Review checksums are in review/release-file-hashes.json.

The installer does not edit Steam settings, start downloads, access the network, change services or add antivirus exclusions. Fault injection is restricted to explicitly marked test sandboxes. Functional installer validation does not constitute new gameplay acceptance or a security verdict.

Historical v1.0.0 source remains available in Git history at commit f87f347e79c8adda7852ea9473e17c141ad67a13. Rights and font-authorization statements remain in src/Credits.txt.
