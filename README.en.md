# CipherBox

English | [简体中文](README.md)

> A fully offline file encryption tool for Windows. It packs files — or an entire folder — into a `.cbx` container, and unlocks it whenever you need it.
> No network code, no uploads, no external dependencies. Plaintext never touches the disk, not even while viewing or editing.

Current version **2.0.2** (work in progress, **not audited by any third party** — do not use it for high-value production data).

---

## What it is

The need is plain: **I want to keep a file on my disk or send it to someone, without it being casually opened — but I still want to open it myself whenever I like.**

- **Fully offline** — no networking code at all: no uploads, no telemetry, no license checks
- **Zero external dependencies** — self-contained build; the target machine needs no .NET runtime, just copy the folder and run
- **Plaintext stays off disk** — read-only viewing creates no plaintext file; editing happens in memory and writes the ciphertext back
- **Three shapes** — single file, batch (N independent containers in one folder), and archive (a whole folder as one container)

**Requirements**: 64-bit Windows 10 1809 or later, or Windows 11.
Windows 7 / 8.1 are not supported — the .NET 10 runtime no longer runs there.

---

## Features

| Capability | Notes |
|---|---|
| Single-file encrypt / decrypt | One file → one `.cbx` |
| Batch encryption | Each file in a folder is encrypted separately, **each one decryptable on its own**; the batch shares a random batch ID |
| Batch decryption | Verifies every passphrase first, then checks batch consistency, and only then starts decrypting |
| Folder archive | The whole folder becomes **one** `.cbx`, in one of two levels (see below) |
| Read-only viewer | Shows text and images without producing plaintext files |
| Edit and write back | Re-encrypts over the original after editing; the passphrase is required again |
| Passphrase strength hint | Rates the passphrase as weak / medium / strong while you type it |
| Strength profiles | Fast / Standard / High — only the PBKDF2 iteration count changes; the format stays the same |
| Selectable cipher | AES-256-GCM (hardware accelerated) or ChaCha20-Poly1305 (faster where AES instructions are missing) |
| Bilingual UI | Chinese and English, switchable at runtime |
| Professional mode | Off by default; reveals cipher selection, full encryption and skip-extension-check options |

### The two archive levels

| Level | What you see before decrypting | Partial extraction |
|---|---|---|
| Standard (preview available) | After entering the passphrase you can browse the folder tree and file names | ✅ Pick individual entries |
| Sealed (no preview) | Only the total size; even the archive name is encrypted | ❌ Whole archive only |

> **This is a product-level limitation, not a cryptographic guarantee.**
> Anyone holding the passphrase can still parse the manifest with their own tool. If you need "not even the structure is recoverable", this is not a property this tool offers.

---

## Security design

| Aspect | Approach |
|---|---|
| Symmetric cipher | AES-256-GCM / ChaCha20-Poly1305, AEAD, every chunk authenticated separately |
| Key derivation | PBKDF2-HMAC-SHA256, 600,000 iterations by default (100k fast / 10M high) |
| Chunking | 1 MiB chunks, so large files do not fill memory |
| Integrity | Flipping any byte, reordering chunks or truncating the file makes decryption fail and reports which chunk |
| No plaintext on disk | Viewing and editing happen entirely in memory |
| Name collisions | Automatic `(1)` suffix — existing files are **never overwritten** |
| Failure cleanup | No half-written output is left behind (professional mode can keep it for debugging) |

### What it guarantees

- Without the passphrase, the contents cannot be read
- Without the passphrase, any modified version will fail to decrypt
- Read-only viewing and editing never write plaintext to disk

### What it does not guarantee (inherent limits of local files)

- It cannot stop someone with write access from deleting or replacing the container (denial of service — unsolvable locally)
- It cannot stop screenshots, text copying, or a debugger reading process memory
- It cannot rescue weak passphrases: offline brute-force resistance is entirely a function of passphrase strength

See [SECURITY.md](SECURITY.md) for details.

---

## Quick start

### GUI

Unpack the release, run `CipherBox.exe`, and drag a file or folder into the window.

### Command line

```powershell
# Encrypt (the passphrase is typed twice)
enc report.docx
enc report.docx backup.cbx

# Inspect the container header (no passphrase needed)
dec backup.cbx --info

# Read-only view, plaintext never written to disk
dec notes.txt.cbx --view
dec notes.txt.cbx --view 4096

# Restore
dec backup.cbx
dec backup.cbx D:\output
```

Exit codes: `0` success; `2` expected failure (wrong passphrase / corrupt file / bad arguments); `1` unexpected exception.

---

## Container format (summary)

Extension `.cbx`, all integers **big-endian**. The writer emits v2; the reader accepts v1.

```
magic[8] "CBXFILE1" | ver u8 | algId u8 | kdfId u8 | flags u8
chunkSize u32 | plainSize u64 | chunkCount u32 | kdfIterations u32
saltLen u8 + salt | nonceLen u8 + baseNonce | nameLen u16 + name
--- added in v2 ---
kind u8 | batchLen u8 + batchId | archiveFlags u8
```

The body is N independently authenticated chunks: `ctLen u32 | ciphertext | tag[16]`

- chunk nonce = `baseNonce[0..7] ‖ BE32(chunk index)`
- chunk AAD = `SHA256(raw header bytes) ‖ BE32(chunk index)`

The full description (archive layout, the AAD construction used for encrypted names) is in [release/docs/格式说明.md](release/docs/格式说明.md) (Chinese).

---

## Project layout

```
CipherBox/
├─ src/CBox.Core/        container format, cipher registry, archive and batch orchestration (single implementation)
├─ src/CBox.Encrypt/     → enc.exe
├─ src/CBox.Decrypt/     → dec.exe
├─ src/CBox.App/         → CipherBox.exe (WPF UI)
├─ src/Shared/           small helpers shared by the two console programs
├─ release/              release-only assets: LICENSE, docs, packaging script
├─ tools/                smoke test and check scripts
├─ build.ps1             one-shot build (test build)
└─ publish.ps1           self-contained publish (test / release)
```

The format and the ciphers have exactly one implementation (`CBox.Core`), used by all three executables — so the GUI and the command line can never drift apart.

---

## Building

Requires **.NET 10 SDK** on Windows.

```powershell
# Test build (diagnostics, fault injection)
powershell -ExecutionPolicy Bypass -File .\build.ps1

# Release build only, with documentation
powershell -ExecutionPolicy Bypass -File .\release\publish-release.ps1
```

Release artifacts land in `release\dist\CipherBox-v<version>-win-x64\`.

---

## Check scripts

All of them should pass before publishing:

| Script | What it checks |
|---|---|
| `tools/smoke.ps1` | 16 end-to-end smoke cases |
| `tools/check-privacy.ps1` | local paths, user names, credentials, traces of other projects |
| `tools/check-i18n-pairs.ps1` | Chinese/English key parity and placeholder consistency |
| `tools/check-strings.ps1` | every key used in code exists in the tables |
| `tools/check-i18n-global.ps1` | hardcoded strings, unused keys, missing translations |

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\smoke.ps1
powershell -ExecutionPolicy Bypass -File .\tools\check-privacy.ps1
```

---

## Status

Done, 1.1 → 2.0: container format v1/v2, console encrypt/decrypt programs, GUI, read-only viewer, edit-and-write-back, localization, strength profiles, log levels, dual build system, folder / archive / batch encryption, archive preview, passphrase strength hint, application icon, installer.

Planned: Argon2id, public-key mode (P-256 + ML-KEM), the 2.0 public release.

---

## Download

Installers and portable archives are on the [Releases page](https://github.com/GuCunF/cipherbox/releases).

| File | Notes |
|---|---|
| `CipherBox-v2.0.0-Setup.exe` | Installer, self-contained .NET runtime, nothing else to install |
| `CipherBox-v2.0.0-win-x64.zip` | Portable build, requires the .NET 10 Desktop Runtime |

## Code signing

Windows builds are intended to be code-signed through the **[SignPath Foundation](https://signpath.org/)**, the free code-signing program for open-source projects. The application is in progress; signed builds will be marked on the Releases page.

Until then Windows may show "Unknown publisher". Verify downloads against this repository's Releases page and the SHA256 values listed there — check them with `Get-FileHash <file> -Algorithm SHA256`.

### If Windows shows "Windows protected your PC"

**This is Windows' default prompt for unsigned programs — not a virus warning, and not something this app does.**

Files downloaded from the internet are tagged by Windows, so the first double-click gets intercepted once. To proceed:

1. Click **"More info"** in the dialog
2. A **"Run anyway"** button appears
3. Click it

If you want to be sure the file is intact, verify it first:

```powershell
Get-FileHash CipherBox-v2.0.2-Setup.exe -Algorithm SHA256
```

Compare the output with the SHA256 listed on the Releases page. If they match, the file has not been tampered with.

> Tip: double-clicking from your Desktop or Downloads folder triggers this interception more often than running from a folder you created yourself.


## License

[MIT](LICENSE)