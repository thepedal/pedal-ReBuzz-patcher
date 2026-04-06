# Pedal Patcher 1.0

A managed audio patchbay effect machine for [ReBuzz](https://github.com/wasteddesign/ReBuzz).

## Features

- **6×6 routing matrix** — independently route any input channel to any output channel
- **48 patches** — store and switch between 48 complete routing configurations
- **Per-channel audio** — true independent routing via ReBuzz connection buffers, not just pass/mute
- **Click-free switching** — configurable fade time (0–500 ms) prevents audio clicks when changing patches
- **Pattern editor commands** — automate routing changes from the sequencer (Plug, Unplug, Connect All, etc.)
- **Copy / Paste / Merge** — duplicate patch states across slots
- **Renameable labels** — double-click any In/Out label to rename it; names are saved with the song

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz)
- [.NET 10 Desktop Runtime (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Installation

1. Build with `dotnet build -c Release` (see [Building](#building))
2. Copy `Pedal Patch.NET.dll` from `Release\net10.0-windows\` to your ReBuzz `Gear\Effects\` folder
3. Restart ReBuzz — **Pedal Patch** appears under Effects

## Building

```powershell
dotnet build -c Release /p:BuzzDir="C:\Program Files\ReBuzz"
```

Override `BuzzDir` if ReBuzz is installed elsewhere. To build without auto-deploying:

```powershell
dotnet build -c Release /p:DeployAfterBuild=false
```

## Usage

### Connections

Connect source machines to Pedal Patcher's input pins and destination machines to its output pins. Each connection has a **circle selector** — click it to assign the connection to a channel number (0–5). Channel numbers correspond to the rows (inputs) and columns (outputs) in the routing matrix.

### Routing matrix

Left-click a cell to connect that input to that output. Right-click to disconnect. Click and drag to set multiple cells at once. Active connections are shown in blue.

### Patches

Use the **Patch** dropdown to switch between the 48 stored routing configurations. Patch state is saved with the song.

### Pattern editor commands

Add tracks to Pedal Patcher and use the **Command** / **Argument** parameters to automate routing:

| Command | Value | Argument (`iioo`) |
|---------|-------|-------------------|
| Unplug | 0 | High byte = input, low byte = output |
| Plug | 1 | High byte = input, low byte = output |
| Plug Exclusive | 2 | Plug input→output, disconnect all other inputs from that output |
| Connect Input | 3 | Connect input `ii` to all outputs |
| Connect Output | 4 | Connect all inputs to output `oo` |
| Disconnect Input | 5 | Disconnect input `ii` from all outputs |
| Disconnect Output | 6 | Disconnect all inputs from output `oo` |
| Connect All | 7 | Connect all inputs to all outputs |
| Clear Patch | 8 | Clear all connections in current patch |

## License

MIT
