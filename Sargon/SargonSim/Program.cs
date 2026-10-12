// SargonSim - runs a zmac-assembled Sargon build in a Z80 emulator.
//
// Two personalities:
//   --mode jove : stands in for the Jove monitor (RST 38h calls) used by the
//                 original 1978 code (Sargon\Original\sargon-z80.asm). This build is the
//                 reference ("oracle").
//   --mode g850 : stands in for the PC-G850VS ROM routines (IOCS calls) used by
//                 the port (Sargon\Sharp_PC_G850VS\sargon-g850.asm), with a 24x6 virtual screen.
//
// Whatever the mode, the computer's chosen move is read straight out of Sargon's
// variables when execution reaches the label given by --watch (default CP0C,
// just after FNDMOV in CPTRMV). That makes the comparison independent of how
// each build prints moves.
//
// Usage examples:
//   SargonSim --hex sargon-z80.hex --lst sargon-z80.lst --mode jove --entry DRIVER --keys "YW2e2-e4"
//   SargonSim --hex sargon-g850.hex --lst sargon-g850.lst --mode g850 --entry G850 --keys "W|2|E2E4|"
//   SargonSim ... --interactive      (keyboard input once the scripted keys run out)
//
// In --keys, '|' stands for the Enter key.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Konamiman.Z80dotNet;

namespace SargonSim;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "compare")
                return Compare.Run(args[1..], fromFile: false);
            if (args.Length > 0 && args[0] == "test")
                return Compare.Run(args[1..], fromFile: true);
            var opt = Options.Parse(args);
            var sim = new Simulator(opt);
            return sim.Run();
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.Help);
            return 2;
        }
    }
}

class UsageException(string message) : Exception(message);

class Options
{
    public string HexFile;
    public string LstFile;
    public string Mode = "g850";
    public string Entry;
    public string Keys = "";
    public bool Interactive;
    public bool ShowScreens;
    public bool Quiet;
    public string Watch = "CP0C";
    public double Mhz = 8.0;
    public ulong MaxTStates = 20_000_000_000UL;
    public int FixedR = -1;   // >= 0: LD A,R returns this value (Sargon's book picks E2E4/D2D4 from R)

    public const string Help =
@"SargonSim test --games games.txt
SargonSim compare --side W|B --depth N --moves ""e2e4 d2d4 ...""
  Plays the same game(s) through the original build (Jove personality, entry DRIVER)
  and the port (G850 personality, entry G850) and checks that Sargon answers with
  the same moves. Side = the PLAYER's colour. Builds are read from
  _build\sargon-z80.hex/.lst and _build\sargon-g850.hex/.lst unless
  --orig-hex, --orig-lst, --port-hex, --port-lst say otherwise.

SargonSim --hex FILE.hex --lst FILE.lst --entry LABEL|ADDR [options]
  --mode jove|g850     I/O personality (default g850)
  --keys TEXT          scripted key presses; '|' = Enter
  --keys-file FILE     same, read from a file (line breaks ignored)
  --interactive        read keys from the console when the script runs out
  --screens            (g850) print the virtual screen at every key wait
  --quiet              do not print the transcript
  --watch LABEL        label where the computer's move is captured (default CP0C)
  --mhz N              clock used to turn T-states into seconds (default 8)
  --max-tstates N      safety stop (default 20e9)
  --fix-r N            make LD A,R return N, so the book's first move is repeatable
                       (Sargon uses the refresh register as its random number)";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"Missing value after {args[i]}");
            switch (args[i])
            {
                case "--hex": o.HexFile = Next(); break;
                case "--lst": o.LstFile = Next(); break;
                case "--mode": o.Mode = Next().ToLowerInvariant(); break;
                case "--entry": o.Entry = Next(); break;
                case "--keys": o.Keys += Next(); break;
                case "--keys-file": o.Keys += File.ReadAllText(Next()).Replace("\r", "").Replace("\n", ""); break;
                case "--interactive": o.Interactive = true; break;
                case "--screens": o.ShowScreens = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--watch": o.Watch = Next(); break;
                case "--mhz": o.Mhz = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--max-tstates": o.MaxTStates = ulong.Parse(Next()); break;
                case "--fix-r": o.FixedR = int.Parse(Next()); break;
                default: throw new UsageException($"Unknown option {args[i]}");
            }
        }
        if (o.HexFile == null || o.LstFile == null || o.Entry == null)
            throw new UsageException("--hex, --lst and --entry are required.");
        if (o.Mode != "jove" && o.Mode != "g850")
            throw new UsageException("--mode must be jove or g850.");
        return o;
    }
}

class Simulator
{
    const ushort Sentinel = 0xFFF0;     // return address pushed before calling the entry point
    const ushort CallerStack = 0x7FF0;  // stands in for BASIC's stack

    readonly Options opt;
    readonly Z80Processor cpu = new();
    readonly Dictionary<string, ushort> symbols = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<char> keys = new();
    readonly StringBuilder transcript = new();
    readonly G850Screen screen = new();
    readonly List<(string move, ulong tstates)> computerMoves = new();

    ulong tstatesAtLastKey;
    ushort lowestSp = 0xFFFF;
    ushort watchAddr;
    string stopMessage;
    bool inputExhausted;

    public IReadOnlyList<string> Moves => computerMoves.Select(m => m.move).ToList();
    public IReadOnlyList<ulong> MoveTStates => computerMoves.Select(m => m.tstates).ToList();
    public string StopMessage => stopMessage;
    public string Transcript => transcript.ToString();

    public Simulator(Options o)
    {
        opt = o;
        foreach (var c in o.Keys) keys.Enqueue(c == '|' ? '\r' : c);
    }

    public int Run()
    {
        LoadSymbols(opt.LstFile);
        var (lo, hi) = LoadIntelHex(opt.HexFile);
        Console.WriteLine($"Loaded {opt.HexFile}: {lo:X4}h-{hi:X4}h ({hi - lo + 1} bytes)");
        if (opt.Mode == "g850" && hi >= 0x8000)
            Console.WriteLine($"WARNING: program reaches {hi:X4}h, past the G850VS RAM (0000h-7FFFh).");

        ushort entry = Resolve(opt.Entry);
        watchAddr = symbols.TryGetValue(opt.Watch, out var w) ? w : (ushort)0;
        if (watchAddr == 0) Console.WriteLine($"WARNING: watch label {opt.Watch} not found; moves will not be captured.");

        // DI; HALT at the sentinel, in case anything ever executes it.
        cpu.Memory[Sentinel] = 0xF3;
        cpu.Memory[Sentinel + 1] = 0x76;

        cpu.ClockSynchronizer = null;   // run flat out, not at real-time speed
        cpu.Reset();
        cpu.Registers.SP = unchecked((short)CallerStack);
        // Recognisable values in the caller's registers, checked on return.
        cpu.Registers.IX = 0x1111; cpu.Registers.IY = 0x2222;
        cpu.Registers.Alternate.BC = 0x3333; cpu.Registers.Alternate.DE = 0x4444; cpu.Registers.Alternate.HL = 0x5555;
        Push(Sentinel);
        cpu.Registers.PC = entry;

        cpu.BeforeInstructionFetch += OnBeforeFetch;
        cpu.AfterInstructionExecution += (_, _) =>
        {
            var sp = (ushort)cpu.Registers.SP;
            if (sp < lowestSp) lowestSp = sp;
        };

        cpu.Continue();

        if (opt.Mode == "g850") transcript.Append(screen.FlushAll());
        if (!opt.Quiet)
        {
            Console.WriteLine("----- transcript -----");
            Console.WriteLine(transcript.ToString().TrimEnd());
            Console.WriteLine("----------------------");
        }
        Console.WriteLine(stopMessage ?? $"Stopped: {cpu.StopReason}");
        Console.WriteLine($"Lowest SP seen: {lowestSp:X4}h");
        if (symbols.TryGetValue("STACK", out var stackTop))
            Console.WriteLine($"Sargon stack depth used: {Math.Max(0, stackTop - lowestSp)} bytes (STACK = {stackTop:X4}h)");
        Console.WriteLine("Computer moves:");
        int n = 1;
        foreach (var (move, t) in computerMoves)
            Console.WriteLine($"  {n++,3}. {move}   {t,14:N0} T-states  ~{t / (opt.Mhz * 1e6):F1} s at {opt.Mhz} MHz");
        Console.WriteLine("MOVES: " + string.Join(" ", computerMoves.Select(m => m.move)));
        return 0;
    }

    // ---------------------------------------------------------------- traps

    void OnBeforeFetch(object sender, BeforeInstructionFetchEventArgs e)
    {
        ushort pc = (ushort)cpu.Registers.PC;

        if (cpu.TStatesElapsedSinceStart > opt.MaxTStates)
        {
            stopMessage = "Stopped: T-state limit reached.";
            e.ExecutionStopper.Stop();
            return;
        }

        if (pc == Sentinel)
        {
            var r = cpu.Registers;
            bool ok = (ushort)r.SP == CallerStack && (ushort)r.IX == 0x1111 && (ushort)r.IY == 0x2222
                      && (ushort)r.Alternate.BC == 0x3333 && (ushort)r.Alternate.DE == 0x4444 && (ushort)r.Alternate.HL == 0x5555;
            stopMessage = "Program returned to its caller (BASIC). Caller's SP, IX, IY and alternate registers "
                          + (ok ? "restored." : "NOT restored!");
            e.ExecutionStopper.Stop();
            return;
        }

        if (pc == watchAddr && watchAddr != 0)
            CaptureComputerMove();

        if (opt.FixedR >= 0 && cpu.Memory[pc] == 0xED && cpu.Memory[(ushort)(pc + 1)] == 0x5F)
        {
            // LD A,R replaced by a fixed value.
            SetA((byte)opt.FixedR);
            cpu.Registers.PC = (ushort)(pc + 2);
            return;
        }

        bool handled = opt.Mode == "jove" ? JoveTrap(pc, e) : G850Trap(pc, e);
        if (handled || inputExhausted) return;

        if (opt.Mode == "g850" && pc >= 0x8000)
        {
            stopMessage = $"Stopped: call to ROM address {pc:X4}h that the simulator does not know.";
            e.ExecutionStopper.Stop();
        }
    }

    void CaptureComputerMove()
    {
        ushort ptr = Word(symbols["MLPTRJ"]);
        byte from = cpu.Memory[ptr + symbols["MLFRP"]];
        byte to = cpu.Memory[ptr + symbols["MLTOP"]];
        ulong t = cpu.TStatesElapsedSinceStart - tstatesAtLastKey;
        computerMoves.Add((Square(from) + Square(to), t));
    }

    static string Square(byte idx)
    {
        // Sargon board: 10 x 12, a1 = 21, h8 = 98.
        int file = idx % 10, rank = idx / 10 - 1;
        if (file < 1 || file > 8 || rank < 1 || rank > 8) return $"?{idx}";
        return $"{(char)('a' + file - 1)}{rank}";
    }

    // Jove monitor (Jupiter III) as used by the 1978 listing: RST 38h followed by
    // in-line parameter bytes.
    bool JoveTrap(ushort pc, BeforeInstructionFetchEventArgs e)
    {
        if (pc != 0x0038) return false;
        ushort ret = Pop();
        byte fn = cpu.Memory[ret];
        switch (fn)
        {
            case 0x81: // 81h,00h = read a character into A; 81h,xx = write A
                if (cpu.Memory[ret + 1] == 0)
                {
                    if (!NextKey(out char c, e)) { Push(ret); cpu.Registers.PC = 0x0038; return true; }
                    SetA((byte)c);
                }
                else Out((char)GetA());
                ret += 2;
                break;
            case 0x92: // new line
                Out('\n');
                ret += 4;
                break;
            case 0xB2: // print line
            case 0xB3: // print block
                {
                    ushort addr = Word((ushort)(ret + 2));
                    ushort len = Word((ushort)(ret + 4));
                    for (int i = 0; i < len; i++)
                    {
                        byte b = cpu.Memory[addr + i];
                        if (b == 0x1C) Out('\n'); // clear screen code
                        else Out(b >= 0x20 && b < 0x7F ? (char)b : '#');
                    }
                    if (fn == 0xB2) Out(' ');
                    ret += 6;
                    break;
                }
            case 0x1F: // exit to monitor
                stopMessage = "Program exited to the Jove monitor.";
                e.ExecutionStopper.Stop();
                return true;
            default:
                stopMessage = $"Stopped: unknown Jove call {fn:X2}h at {ret - 1:X4}h.";
                e.ExecutionStopper.Stop();
                return true;
        }
        cpu.Registers.PC = ret;
        return true;
    }

    // PC-G850VS IOCS calls. Addresses and register conventions from
    // https://ver0.sakura.ne.jp/doc/pcg800iocs.html . Position: D = row (0-5), E = column (0-23).
    bool G850Trap(ushort pc, BeforeInstructionFetchEventArgs e)
    {
        var r = cpu.Registers;
        switch (pc)
        {
            case 0xBE62: // put one character A at DE
            case 0xBE5F:
                screen.Put(D, E, GetA());
                break;
            case 0xBFF1: // put string HL, length B, at DE (wraps and scrolls)
                {
                    int row = D, col = E, scrolls = 0;
                    ushort hl = (ushort)r.HL;
                    int len = r.B == 0 ? 256 : r.B;
                    for (int i = 0; i < len; i++)
                    {
                        if (col > 23) { col = 0; row++; }
                        if (row > 5) { transcript.Append(screen.ScrollUp()); row = 5; scrolls++; }
                        screen.Put(row, col, cpu.Memory[hl]);
                        if (i < len - 1) { hl++; col++; }
                    }
                    r.HL = unchecked((short)hl);
                    r.D = (byte)row; r.E = (byte)col; r.C = (byte)scrolls; r.B = 0;
                    break;
                }
            case 0xBFEE: // put character A, B times, at DE
                {
                    int row = D, col = E, len = r.B == 0 ? 256 : r.B, scrolls = 0;
                    for (int i = 0; i < len; i++)
                    {
                        if (col > 23) { col = 0; row++; }
                        if (row > 5) { transcript.Append(screen.ScrollUp()); row = 5; scrolls++; }
                        screen.Put(row, col, GetA());
                        if (i < len - 1) col++;
                    }
                    r.D = (byte)row; r.E = (byte)col; r.C = (byte)scrolls; r.B = 0;
                    break;
                }
            case 0xBFEB: // scroll whole screen up one line
                transcript.Append(screen.ScrollUp());
                break;
            case 0xBCFD: // wait for a key, A = key code
                {
                    if (opt.ShowScreens) Console.WriteLine(screen.Render());
                    if (!NextKey(out char c, e)) return true;
                    // The simulator uses the character itself as the key code;
                    // BE56h below converts it back unchanged.
                    SetA((byte)c);
                    break;
                }
            case 0xBE56: // key code -> character
                break;
            default:
                return false;
        }
        cpu.ExecuteRet();
        return true;
    }

    bool NextKey(out char c, BeforeInstructionFetchEventArgs e)
    {
        tstatesAtLastKey = cpu.TStatesElapsedSinceStart;
        if (keys.Count > 0) { c = keys.Dequeue(); return true; }
        if (opt.Interactive)
        {
            if (opt.Mode == "g850") Console.WriteLine(screen.Render());
            else Console.Write(transcript.ToString()[lastShown..]);
            lastShown = transcript.Length;
            var k = Console.ReadKey(intercept: true);
            c = k.Key == ConsoleKey.Enter ? '\r' : k.Key == ConsoleKey.Backspace ? '\b' : char.ToUpperInvariant(k.KeyChar);
            return true;
        }
        c = '\0';
        inputExhausted = true;
        stopMessage = "Stopped: the program is waiting for a key and the scripted keys have run out.";
        e.ExecutionStopper.Stop();
        return false;
    }
    int lastShown;

    void Out(char c) => transcript.Append(c);

    // ---------------------------------------------------------------- helpers

    byte D => cpu.Registers.D;
    byte E => cpu.Registers.E;
    byte GetA() => cpu.Registers.A;
    void SetA(byte v) => cpu.Registers.A = v;

    ushort Word(ushort addr) => (ushort)(cpu.Memory[addr] | (cpu.Memory[(ushort)(addr + 1)] << 8));

    void Push(ushort v)
    {
        ushort sp = (ushort)(cpu.Registers.SP - 2);
        cpu.Memory[sp] = (byte)v;
        cpu.Memory[(ushort)(sp + 1)] = (byte)(v >> 8);
        cpu.Registers.SP = unchecked((short)sp);
    }

    ushort Pop()
    {
        ushort sp = (ushort)cpu.Registers.SP;
        ushort v = Word(sp);
        cpu.Registers.SP = unchecked((short)(sp + 2));
        return v;
    }

    ushort Resolve(string s)
    {
        if (symbols.TryGetValue(s, out var a)) return a;
        var hex = s.TrimEnd('h', 'H');
        if (ushort.TryParse(hex, NumberStyles.HexNumber, null, out a)) return a;
        throw new UsageException($"Entry point '{s}' is neither a label in {opt.LstFile} nor a hex address.");
    }

    // zmac symbol table lines look like: "CP0C            1B36      6966" or "MLFRP          =02        2"
    void LoadSymbols(string path)
    {
        var re = new Regex(@"^([A-Za-z_][A-Za-z0-9_]*)\s+=?\s*([0-9A-Fa-f]+)'?\s+(\d+)\s*$");
        foreach (var line in File.ReadLines(path))
        {
            var m = re.Match(line);
            if (m.Success)
                symbols[m.Groups[1].Value] = (ushort)int.Parse(m.Groups[3].Value);
        }
        if (symbols.Count == 0) throw new UsageException($"No symbols found in {path} (assemble with zmac --oo lst).");
    }

    (int lo, int hi) LoadIntelHex(string path)
    {
        int lo = 0xFFFF, hi = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length < 11 || line[0] != ':') continue;
            int count = Convert.ToInt32(line.Substring(1, 2), 16);
            int addr = Convert.ToInt32(line.Substring(3, 4), 16);
            int type = Convert.ToInt32(line.Substring(7, 2), 16);
            if (type == 1) break;
            if (type != 0) continue;
            for (int i = 0; i < count; i++)
            {
                cpu.Memory[addr + i] = Convert.ToByte(line.Substring(9 + 2 * i, 2), 16);
                lo = Math.Min(lo, addr + i);
                hi = Math.Max(hi, addr + i);
            }
        }
        return (lo, hi);
    }
}

// 24 x 6 character screen of the PC-G850VS.
class G850Screen
{
    public const int Cols = 24, Rows = 6;
    readonly char[,] cells = new char[Rows, Cols];

    public G850Screen() => Clear();

    public void Clear()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++) cells[r, c] = ' ';
    }

    public void Put(int row, int col, byte ch)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return;
        cells[row, col] = ch >= 0x20 && ch < 0x7F ? (char)ch : '#';
    }

    string Line(int r)
    {
        var sb = new StringBuilder(Cols);
        for (int c = 0; c < Cols; c++) sb.Append(cells[r, c]);
        return sb.ToString().TrimEnd();
    }

    // Returns the line that scrolled off the top, for the transcript.
    public string ScrollUp()
    {
        string top = Line(0) + "\n";
        for (int r = 1; r < Rows; r++)
            for (int c = 0; c < Cols; c++) cells[r - 1, c] = cells[r, c];
        for (int c = 0; c < Cols; c++) cells[Rows - 1, c] = ' ';
        return top;
    }

    public string FlushAll()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < Rows; r++) sb.Append(Line(r)).Append('\n');
        return sb.ToString();
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append('+').Append('-', Cols).Append("+\n");
        for (int r = 0; r < Rows; r++) sb.Append('|').Append(Line(r).PadRight(Cols)).Append("|\n");
        sb.Append('+').Append('-', Cols).Append('+');
        return sb.ToString();
    }
}
