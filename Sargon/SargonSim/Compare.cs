// "compare" and "test" sub-commands: the same game through the original build
// (Jove personality) and the port (G850 personality); Sargon must answer with
// the same moves in both.
namespace SargonSim;

static class Compare
{
    // Default locations, relative to the current directory: run from
    // Sargon\Sharp_PC_G850VS, where the build puts both programs in _build.
    class Paths
    {
        public string OrigHex = Path.Combine("_build", "sargon-z80.hex");
        public string OrigLst = Path.Combine("_build", "sargon-z80.lst");
        public string PortHex = Path.Combine("_build", "sargon-g850.hex");
        public string PortLst = Path.Combine("_build", "sargon-g850.lst");
    }

    // compare --side W|B --depth N --moves "e2e4 d2d4 ..."   (one game)
    // test --games games.txt                                 (every game in the file)
    public static int Run(string[] args, bool fromFile)
    {
        var p = new Paths();
        string side = "W", moves = "", gamesFile = null;
        int depth = 2;
        double mhz = 8;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"Missing value after {args[i]}");
            switch (args[i])
            {
                case "--orig-hex": p.OrigHex = Next(); break;
                case "--orig-lst": p.OrigLst = Next(); break;
                case "--port-hex": p.PortHex = Next(); break;
                case "--port-lst": p.PortLst = Next(); break;
                case "--side": side = Next().ToUpperInvariant(); break;
                case "--depth": depth = int.Parse(Next()); break;
                case "--moves": moves = Next(); break;
                case "--games": gamesFile = Next(); break;
                case "--mhz": mhz = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new UsageException($"Unknown option {args[i]}");
            }
        }

        if (!fromFile)
            return Game(p, side, depth, moves, mhz) ? 0 : 1;

        if (gamesFile == null) throw new UsageException("test needs --games FILE.");
        int passed = 0, failed = 0;
        foreach (var raw in File.ReadLines(gamesFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // Format: SIDE DEPTH move move move ...
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) throw new UsageException($"Bad line in {gamesFile}: {line}");
            if (Game(p, parts[0].ToUpperInvariant(), int.Parse(parts[1]), parts[2], mhz)) passed++; else failed++;
            Console.WriteLine();
        }
        Console.WriteLine($"{passed} game(s) the same, {failed} different or incomplete.");
        return failed == 0 ? 0 : 1;
    }

    static bool Game(Paths p, string side, int depth, string moves, double mhz)
    {
        if (side != "W" && side != "B") throw new UsageException("Side must be W or B (the player's colour).");
        if (depth < 1 || depth > 6) throw new UsageException("Depth must be 1-6.");

        var playerMoves = moves.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                               .Select(m => m.Replace("-", "").ToUpperInvariant()).ToList();
        foreach (var m in playerMoves)
            if (m.Length != 4) throw new UsageException($"Move '{m}' is not in the E2E4 form.");

        // Original: Y (play), W/B, depth, then moves typed as e2-e4 (no Enter).
        string joveKeys = "Y" + side + depth + string.Concat(playerMoves.Select(m => m[..2] + "-" + m[2..]));
        // Port: W/B, depth, then moves typed as E2E4 + Enter.
        string portKeys = side + depth + string.Concat(playerMoves.Select(m => m + "|"));

        var orig = RunOne(p.OrigHex, p.OrigLst, "jove", "DRIVER", joveKeys);
        var port = RunOne(p.PortHex, p.PortLst, "g850", "G850", portKeys);

        // One answer per player move, plus the opening move when Sargon is white.
        // A game that ends in mate can have one answer fewer.
        int expected = playerMoves.Count + (side == "B" ? 1 : 0);
        Console.WriteLine($"Player {side}, depth {depth}, {playerMoves.Count} player moves");
        Console.WriteLine($"  original: {string.Join(" ", orig.Moves)}");
        Console.WriteLine($"  port    : {string.Join(" ", port.Moves)}");

        bool same = orig.Moves.SequenceEqual(port.Moves);
        bool complete = orig.Moves.Count >= expected - 1 && port.Moves.Count >= expected - 1;
        if (!complete)
            Console.WriteLine($"  WARNING: expected {expected} computer moves - is one of the player's moves illegal?");
        ulong slowest = port.MoveTStates.Count > 0 ? port.MoveTStates.Max() : 0;
        Console.WriteLine($"  slowest port move: {slowest:N0} T-states, ~{slowest / (mhz * 1e6):F1} s at {mhz} MHz");
        Console.WriteLine(same && complete ? "  RESULT: SAME" : same ? "  RESULT: SAME (but incomplete)" : "  RESULT: DIFFERENT");
        return same && complete;
    }

    static Simulator RunOne(string hex, string lst, string mode, string entry, string keys)
    {
        // FixedR = 0: Sargon picks its first move as white from the refresh
        // register, which differs between builds; fixing it keeps games comparable.
        var opt = new Options { HexFile = hex, LstFile = lst, Mode = mode, Entry = entry, Keys = keys, Quiet = true, FixedR = 0 };
        var sim = new Simulator(opt);
        var saved = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { sim.Run(); }
        finally { Console.SetOut(saved); }
        return sim;
    }
}
