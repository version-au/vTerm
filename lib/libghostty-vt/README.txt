libghostty-vt 1.0.1 - Ghostty's terminal engine as a C library (MIT, see LICENSE) - https://lib.ghostty.org/vt/

ghostty-vt.dll is bin\ghostty-vt.dll from the release's Windows x64 zip:
  https://release.files.ghostty.org/libghostty-vt/1.0.1/ghostty-vt-1.0.1-x86_64-windows-gnu.zip
  (the zip's SHA-256 matched the release's SHA256SUMS: 8e2eb9e1822e85d786b456c8beb86f49d09c318c91f0c2913f91e7ffa12cda29)
  ghostty-vt.dll SHA-256: 2e5155b193f92a079a87c0824197719e2c93ed8431adca8f207420deeba1fbb3

To update: bench\get-ghostty.ps1 -Version <new>, then copy bench\native\ghostty-vt.dll and LICENSE here. The C ABI is stable across 1.x.
