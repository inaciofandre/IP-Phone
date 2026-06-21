# Dialex

A console-based SIP softphone for .NET Framework 4.8 that registers with any RFC-compliant PBX (Yeastar Cloud, 3CX, FreeSWITCH, Asterisk) and provides multi-line call control, DTMF, hold, transfer, recording, and more — all from the command line.

## Features

- **Registration** — SIP REGISTER with automatic re-registration and keep-alive (NOTIFY/OPTIONS)
- **Multi-line** — configurable number of independent call lines
- **Call control** — place, answer, reject, hangup, redial
- **DTMF** — RFC 2833 and SIP INFO (application/dtmf-relay)
- **Hold/Unhold** — per-line hold with re-INVITE
- **Transfers** — blind and attended transfer
- **DND & Forwarding** — do-not-disturb and call forwarding
- **Auto-answer** — whitelist numbers for automatic pickup
- **Recording** — call recording to WAV (PCMU to 16-bit PCM)
- **Phonebook** — name-based dialing with JSON persistence
- **Speed dials** — assignable key-to-number shortcuts
- **Call history** — persistent log with duration
- **Encrypted accounts** — all account data (server, extension, password) stored via Windows DPAPI
- **NAT detection** — auto-detects public IP and Via-based NAT
- **TLS signaling & SRTP** — encrypted SIP and media with DTLS-SRTP or SDES
- **CLI with Tab completion** — command aliases, live status prompt

## Requirements

- Windows 7+
- .NET Framework 4.8

## Configuration

### `settings.json`

```json
{
  "Port": 5060,
  "LineCount": 2,
  "PublicIP": "",
  "UseTls": false,
  "TlsPort": 5061,
  "ValidateServerCert": false,
  "SrtpMode": "none"
}
```

- `Port` — SIP UDP port (default 5060)
- `LineCount` — number of concurrent lines (1-5 recommended)
- `PublicIP` — set manually if behind symmetric NAT (leave empty for auto-detect)
- `UseTls` — enable TLS signaling and `sips:` URIs
- `TlsPort` — SIP TLS port (default 5061)
- `ValidateServerCert` — set `false` for self-signed PBX certificates
- `SrtpMode` — `"none"`, `"dtls"`, or `"sdes"`

### Accounts

On first run, the app prompts for all account details: Server, Proxy, Domain, Port, Extension, Display Name, Username, and Password. Accounts are encrypted with Windows DPAPI and stored in `accounts.enc`. Manage with `account` commands.

## Usage

```
Dialex.exe                # normal start
Dialex.exe --verbose      # show SIP debug traces
Dialex.exe --reset-credentials  # clear all stored accounts
```

### Commands

| Command | Alias | Description |
|---------|-------|-------------|
| `call <num\|name>` | | Dial a number, phonebook name, or speed dial key |
| `answer` | `a` | Answer incoming call |
| `reject` | `r`, `rej` | Reject incoming call |
| `hangup` | `h` | Hang up active call |
| `redial` | `rd` | Redial last number |
| `dtmf <0-9>` | `dt` | Send DTMF tone (RFC 2833) |
| `dtmf info <0-9>` | `dt i` | Send DTMF tone (SIP INFO) |
| `hold` | `hl` | Put call on hold |
| `unhold` | `uh` | Take call off hold |
| `mute` | `m` | Toggle microphone mute |
| `rec` | | Toggle call recording |
| `dnd` | | Toggle Do Not Disturb |
| `forward <num>` | `fw` | Enable call forwarding |
| `forward off` | `fw off` | Disable call forwarding |
| `transfer <num\|name>` | `tr` | Blind transfer |
| `atransfer <num\|name>` | `at` | Attended transfer |
| `speed <key>` | | Dial a speed dial, or manage with set/remove/list/clear |
| `history` | `hist` | Show recent call history |
| `phonebook` | `pb` | Manage contacts (add/remove/clear) |
| `account list` | `acc l` | List SIP accounts with registration status |
| `account add` | `acc a` | Add a new SIP account |
| `account remove <idx>` | `acc r <idx>` | Remove an account |
| `account edit <idx>` | `acc e <idx>` | Edit an account |
| `account default <idx>` | `acc d <idx>` | Switch to a different account |
| `line <N>` | `l` | Switch to line N |
| `lines` | `ls` | List all lines with status |
| `help` | | Show command list |
| `exit` | `quit` | Shutdown and exit |

## Building

Open `Dialex.slnx` in Visual Studio 2022 and build. NuGet packages restore automatically.

Or build with MSBuild:
```
msbuild Dialex.slnx /t:Build /p:Configuration=Release
```

## License

MIT
