# Sargon 1978 on the Sharp PC-G850VS

The 1978 Sargon move logic, unchanged, with a new text driver for the PC-G850VS.
No board is shown. You type your move as `E2E4` and Sargon answers with its move
in the same form.

```
SARGON 1978
PLAY W OR B? W
DEPTH 1-6? 2
YOUR MOVE? E2E4
SARGON: E7E5
YOUR MOVE?
```

## Files

Paths are relative to `Sargon`.

| File | What it is |
| --- | --- |
| `Sharp_PC_G850VS\sargon-g850.asm` | The port. Assemble on the PC with zmac. |
| `Sharp_PC_G850VS\prebuilt\sargon-g850.hex` | Ready-to-send program, tested on a PC-G850VS. Use it if you don't want to build. |
| `_build\` | zmac output (created by the build, not in git): `sargon-g850.hex` is sent to the calculator; the `.lst` files hold the listing and symbols. |
| `Original\sargon-z80.asm` | The original 1978 program (Z80 mnemonics), unchanged. It is the reference for the tests. |
| `SargonSim\` | C# Z80 simulator used to test the port before it goes on the calculator. |
| `SargonSim\games.txt`, `SargonSim\games-slow.txt` | Test games for `SargonSim test`. |

## How the port differs from `Original\sargon-z80.asm`

- `ORG 0100H` before `START:`, so the program sits at `0100h`–`19FAh` (about 6.3 KB).
  `START` must stay a multiple of `100h`, because `TBASE = START+100H` must start a
  256-byte page.
- The move logic (everything before `PC-G850VS DRIVER`) is otherwise byte-for-byte
  the original. This was checked by assembling the original at `0100h` and comparing.
  The only difference is that the graphics data table at `START+180h` is gone.
- The graphics data table, the messages and the Jupiter III driver are replaced by a
  new driver. It keeps the original `CPTRMV`, `FCDMAT`, `PLYRMV`, `VALMOV`, `ASNTBI`,
  `BITASN`, `DIVIDE` and `MLTPLY` logic, minus all board drawing.
- Input and output use the PC-G850VS ROM routines (IOCS calls):

  | Address | Use |
  | --- | --- |
  | `BE62h` | put a character |
  | `BFEBh` | scroll up |
  | `BCFDh` | wait for a key |
  | `BE56h` | key code to character |

  Addresses and register use are from <https://ver0.sakura.ne.jp/doc/pcg800iocs.html>.
  On a PC-G850VS, Enter gives character `0Dh` (key code `28h`) and BS gives `08h`
  (key code `3Ah`). This was checked on the calculator.
- Entry point `G850` (`1600h`) saves every register, including IX, IY and the
  alternate set, and returns to BASIC cleanly when you quit.
- Interrupts are disabled while Sargon thinks (`DISRCH EQU 1`). Sargon uses `EXX`
  and `EX AF,AF'`, and the ROM may use the alternate registers in its interrupt
  handler. The catch is that BREAK does not work during a search. Set
  `DISRCH EQU 0` to try it with interrupts on.

Move entry:
- **Enter:** accepts the move once 4 characters are typed. Enter on an incomplete
  move erases it, so you can retype.
- **BS:** erases the last character. Any other control key does the same.
- **Castling:** type the king's move (`E1G1`, `E1C1`, `E8G8`, `E8C8`). Sargon moves
  the rook.
- **Promotion:** always to a queen (Sargon's own rule).
- **Quit:** `Q` at any prompt returns to BASIC.

## Build (PC)

### Get zmac

The build uses zmac, a Z80 cross-assembler maintained by George Phillips. Its GitHub
repository, <https://github.com/gp48k/zmac>, includes a ready-built Windows
executable, `zmac.exe`, at the top of the repository. There is no need to build
zmac yourself.

1. Download `zmac.exe` from
   <https://github.com/gp48k/zmac/raw/master/zmac.exe>, or open the repository
   page, click `zmac.exe`, then the download button.
2. Put it in a folder of your choice, for example `C:\tools\zmac`.
3. Add that folder to your `PATH`, then open a new command prompt.
4. Check it with `zmac --version`. It should print the version, for example
   `zmac version 18oct2022`.

zmac's home page is <http://48k.ca/zmac.html>.

### Assemble

```bat
if not exist _build mkdir _build
zmac --oo hex,lst -c --od _build Sharp_PC_G850VS\sargon-g850.asm
```

This produces `_build\sargon-g850.hex` (sent to the calculator) and
`_build\sargon-g850.lst`. Look up `G850` and `PGMEND` in the listing's symbol table
to check the entry point (`1600`) and the end address. The end address must stay
below the `USER` value used when loading.

For the simulator tests, also build the reference (the original 1978 program) into
the same folder:

```bat
zmac --oo hex,lst -c --od _build Original\sargon-z80.asm
```

## Test in the simulator (PC)

You need the .NET 8 SDK. The Z80 core is Z80.NET by Konamiman (MIT licence),
fetched from NuGet.

```bat
dotnet run --project SargonSim -c Release -- test --games SargonSim\games.txt
```

Run it from `Sargon`: SargonSim reads both programs from `_build`
(`--orig-hex`, `--orig-lst`, `--port-hex` and `--port-lst` override that).

Each game in `games.txt` is played through the original build and through the port.
Sargon must choose the same moves in both, which checks that the port did not
change the engine. The 3 games take about 2–3 minutes. `games-slow.txt` adds a
depth-3 game (about 5 minutes).

Play interactively in the simulator. It shows the 24×6 screen and waits for keys:

```bat
dotnet run --project SargonSim -c Release -- --hex _build\sargon-g850.hex --lst _build\sargon-g850.lst --mode g850 --entry G850 --interactive
```

Notes on the simulator:
- **The opening move is random.** As white, Sargon picks `E2E4` or `D2D4` from the
  Z80 refresh register. The `compare`/`test` commands fix it (`--fix-r 0`) so both
  builds play the same first move.
- **Each move's cost is printed** in T-states and as seconds at 8 MHz (the G850VS
  clock as far as I know; change it with `--mhz`). Expected times on the calculator:

  | Depth | Typical move | Slowest move seen |
  | --- | --- | --- |
  | 1 | 2–4 s | |
  | 2 | 10–20 s | about 1 minute |
  | 3 | | about 5 minutes |

## Transfer and run on the PC-G850VS

1. **Serial settings.** On the calculator, open TEXT → S (Sio) → F (Format) and set:
   - baud rate = 1200, data bit = 8, stop bit = 1, parity = none
   - end of line = CR LF, end of file = 1A
   - flow control = RS/CS
2. **Reserve the memory.** In BASIC type `MON` and press Enter, then `USER19FF`.
   This reserves the machine-code area up to `19FFh`.
3. **Start receiving.** In the monitor type `R` and press Enter. The calculator now
   waits for an Intel HEX file.
4. **Send from the PC.** In SerialFiler:
   1. Open the COM port.
   2. Untick **Trim / add lines #**.
   3. Leave **Line delay (ms)** at 50; raise it if the load fails.
   4. Use **File → Send…** to send `_build\sargon-g850.hex` (or the ready-built
      `Sharp_PC_G850VS\prebuilt\sargon-g850.hex`). It is about 10 KB, so
      roughly 100 s at 1200 baud.

   SerialFiler ends the file with Ctrl-Z (`1Ah`), which ends the `R` command. The
   monitor prompt comes back when the load is complete.
5. **Check (optional).** `D1600` in the monitor should show `F5 C5 D5 E5 DD E5`.
6. **Run it.** Use `G1600` in the monitor, or `CALL &H1600` from BASIC.

If the load fails with DEVICE I/O ERROR, or the prompt never returns:
- Increase the line delay to 100–200 ms.
- In Device Manager, open the USB-serial port's Properties → Port Settings →
  Advanced… and untick "Use FIFO buffers".

## Publishing a new prebuilt HEX

`_build` is ignored by git, so building never changes the repository. When a new
build has passed the simulator tests and works on the calculator, copy it into the
tracked `prebuilt` folder and commit it with the source change:

```bat
copy /Y _build\sargon-g850.hex Sharp_PC_G850VS\prebuilt\sargon-g850.hex
```

`.gitattributes` at the repository root keeps `.hex` files in CR LF line endings,
which the calculator's monitor needs. (The folder is not called `release` because
the Visual Studio `.gitignore` ignores every folder with that name.)
