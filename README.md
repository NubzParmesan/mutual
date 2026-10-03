<p align="center">
  <img src="docs/icon.png" width="96" alt="Mutual">
</p>

<h1 align="center">Mutual</h1>

<p align="center">
  Screen sharing, remote play, file sending and SSH for two friends.<br>
  Nothing connects until both of you say yes.
</p>

<p align="center">
  <a href="https://github.com/NubzParmesan/mutual/actions/workflows/build.yml"><img src="https://github.com/NubzParmesan/mutual/actions/workflows/build.yml/badge.svg" alt="build"></a>
  <img src="https://img.shields.io/badge/.NET-8-512BD4" alt=".NET 8">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT"></a>
</p>

<p align="center">
  <img src="docs/main.png" width="380" alt="the main window">
  &nbsp;
  <img src="docs/pairing.png" width="412" alt="pairing with a friend">
</p>

## WHY

My friend and I wanted to play RimWorld together. The multiplayer mod desynced every time, so we ended up running a pile of separate tools: Parsec for the picture, Hamachi for the connection, a hacked together second mouse, and some PowerShell scripts for SSH. It worked, kind of, but it was a mess.

Mutual is all of that in one app. The one rule it's built around is that **nothing happens on your PC unless you clicked yes on your PC**. A request only asks. Streaming, control, files and SSH all wait for the other person to accept.

## WHAT IT DOES

- **Stream** a whole screen, one window (even with other windows on top of it), or a box you drag and resize while it's live
- **Let your friend play.** Their mouse and keyboard work on whatever you're sharing, and only on that. Your cursor shows up on their side too
- **Sound** comes through with the picture, compressed with Opus
- **Send files** straight to their Downloads, checked on arrival so a damaged file never shows up
- **SSH** that only turns on while you're both connected and turns itself back off after
- **Play RimWorld together** in one click with the SplitColony mod: the screen splits, your friend gets the right half with their own mouse, and you keep yours
- **Reconnects on its own** if the connection drops in the middle of a stream
- Clipboard sharing if you both turn it on, a summary of every session (time, fps, ping, loss), and a tray icon that shows when your friend is online

## HOW PRIVATE IS IT

- You pair once by swapping codes. A code only holds a public key and some addresses, so it's safe to paste in Discord. Afterwards you both see the same safety code, and if it matches nobody got in the middle
- Every connection between the two PCs is encrypted, and each side only accepts the one key it paired with
- There's no account and no cloud. The optional rendezvous server (for when you're not on the same VPN) only ever sees an id and encrypted bytes. It can't read anything, and requests going through it are signed so it can't fake one either
- The activity list shows what happened on your own PC. Never command history, file contents or what was on screen

## GETTING STARTED

1. Download `Mutual.exe` from [Releases](https://github.com/NubzParmesan/mutual/releases) and run it. It offers to install itself into your own programs folder (no admin) with Start menu and desktop shortcuts
2. Click **Pair with a friend**, copy your code and send it to them, then paste theirs
3. Read the safety code to each other
4. Click **Set up** once so Windows lets your friend reach Mutual (one admin prompt, and the firewall rules only allow your friend's address)
5. Hit **Stream** and pick what to share

It works best when you're both on the same VPN (Hamachi, Tailscale, whatever) or the same network. If you're not, put a rendezvous server in Settings. Mutual tries to open its port on your router, and if that doesn't work it relays through the server.

## HOW IT WORKS

| | |
|---|---|
| **Capture** | DXGI Desktop Duplication for screens and boxes, Windows Graphics Capture for single windows |
| **Video** | Hardware H.264 through Media Foundation (NVIDIA, AMD or Intel), low latency, constant bitrate |
| **Transport** | Video over UDP with Reed-Solomon parity, so a few lost packets get rebuilt instead of freezing the picture. Falls back to TCP if UDP is blocked |
| **Security** | Pinned mutual TLS for the link, input and everything else. AES-GCM for the UDP packets, keyed through the TLS link |
| **Sound** | WASAPI loopback, Opus at about 90 kbit/s |
| **Adapts** | Bitrate backs off when the connection fills up and creeps back when it clears. Parity grows with the loss the viewer reports |

Some numbers from testing on one PC:

- **About 19 ms** from something being drawn on the host to it being decoded on the viewer. The network adds whatever your ping is
- **0 frames lost** with 25% of video packets thrown away on purpose, once the parity caught up
- Encoding a frame takes about 2 ms on an RTX card, decoding about 0.2 ms

## BUILDING IT

You need the .NET 8 SDK on Windows.

```
dotnet build Mutual.sln
dotnet test tests/Mutual.Core.Tests
deploy/publish.sh                      # one file Mutual.exe in publish/
```

| Folder | What's in it |
|---|---|
| `src/Mutual` | The app: windows, tray, pairing, settings, SSH |
| `src/Mutual.Core` | The connection side: pairing, TLS links, requests, file transfer, rendezvous, firewall |
| `src/Mutual.Stream` | Everything streaming: capture, encode, decode, UDP lane, sound, input, the viewer |
| `tools/Mutual.Rendezvous` | The rendezvous server. Runs anywhere with .NET 8: `dotnet run --project tools/Mutual.Rendezvous -- 28810` |
| `tools/Mutual.Lab` | Tests that run the real thing on one PC: streaming, input, sound, latency, and a full rehearsal with two copies of Mutual paired with each other |
| `tests` | The automated tests |

## COMING FROM MUTUAL SSH

If you used the older Mutual SSH scripts, Mutual picks up that pairing by itself and you don't have to pair again. The **Set up** button switches you over from the old notifier. Mutual also still speaks the old SSH protocol, so if your friend hasn't switched yet it works anyway.

## LICENSE

MIT. Mutual uses [Concentus](https://github.com/lostromb/concentus) for Opus and [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) for DirectX. Their licenses are in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
