using System.Diagnostics;
using LifeScript;

const string Usage = """
    lifescript check  <file.ls>                 Parse and compile; print errors and warnings.
    lifescript info   <file.ls>                 Show how the script compiles (types, ids, neighborhoods, lookup tables).
    lifescript run    <file.ls> [options]       Step and print the grid as text.
        -n <gens>      generations to run (default: Generations property, or 20)
        --every <k>    print every k generations (default: only the last)
        --z <slice>    z-slice to print for 3D worlds (default: middle)
        --interp       don't use lookup tables
        --quiet        print only the summary
    lifescript parity <file.ls> [-n <gens>]     Compare lookup-table and interpreter runs generation by generation.
    lifescript bench  <file.ls> [-n <gens>]     Time stepping with and without lookup tables.
    """;

if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

string command = args[0];
string path = args[1];
var options = ParseOptions(args.Skip(2).ToArray());

CompiledProgram program;
try
{
    program = Compiler.Compile(File.ReadAllText(path));
}
catch (LifeScriptException e)
{
    foreach (var d in e.Diagnostics)
    {
        Console.Error.WriteLine($"{path}:{d}");
    }
    return 1;
}
foreach (var w in program.Warnings)
{
    Console.Error.WriteLine($"{path}:{w}");
}

int generations = options.TryGetValue("n", out var n) ? int.Parse(n) : (int)(program.Generations ?? 20);

try
{
    switch (command)
    {
        case "check":
            Console.WriteLine($"{path}: ok");
            return 0;
        case "info":
            PrintInfo(program);
            return 0;
        case "run":
            return Run(program, generations, options);
        case "parity":
            return Parity(program, generations);
        case "bench":
            Bench(program, generations);
            return 0;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (LifeScriptRuntimeException e)
{
    Console.Error.WriteLine($"{path}:{e.Message}");
    return 1;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>();
    for (int i = 0; i < args.Length; i++)
    {
        string key = args[i].TrimStart('-');
        bool isFlag = key is "interp" or "quiet";
        result[key] = isFlag ? "true" : args[++i];
    }
    return result;
}

static void PrintInfo(CompiledProgram p)
{
    Console.WriteLine($"Title:        {p.Title}");
    Console.WriteLine($"World:        {string.Join(" x ", p.Size)} ({p.CellCount:N0} cells), wrap {string.Join(",", p.Wrap.Select(w => w ? "yes" : "no"))}, seed {p.Seed}");
    Console.WriteLine($"Neighborhood: {p.UnionOffsets.Length} offsets across all types");
    Console.WriteLine($"Update mode:  {p.UpdateMode}{(p.UpdateModeReasons.Count > 0 ? " (" + string.Join("; ", p.UpdateModeReasons) + ")" : "")}");
    if (p.GlobalNames.Length > 0)
    {
        Console.WriteLine($"Globals:      {string.Join(", ", p.GlobalNames)}");
    }
    Console.WriteLine($"World block:  {(p.World == null ? "no" : "yes")}");
    Console.WriteLine();
    foreach (var t in p.Types)
    {
        Console.WriteLine($"type \"{t.Name}\"{(t.Index == p.DefaultType ? " (default)" : "")}: ids {t.BaseId}..{t.BaseId + t.StateCount - 1}, {t.StateCount} states, {t.Weights.Length} weighted neighbors");
        foreach (var q in t.Queries)
        {
            Console.WriteLine($"    query: {q.Describe(p)}");
        }
        Console.WriteLine(
            t.Lut != null
                ? $"    backend: lookup table, {t.Lut.Entries} entries"
                : $"    backend: interpreter ({string.Join(", ", t.LutBlockers)})"
        );
    }
}

static int Run(CompiledProgram program, int generations, Dictionary<string, string> options)
{
    var sim = new Simulation(program, useLut: !options.ContainsKey("interp"));
    int every = options.TryGetValue("every", out var e) ? int.Parse(e) : 0;
    int? z = options.TryGetValue("z", out var zs) ? int.Parse(zs) : null;
    bool quiet = options.ContainsKey("quiet");
    var watch = Stopwatch.StartNew();
    if (every > 0 && !quiet)
    {
        Print(sim, z);
    }
    for (int g = 1; g <= generations; g++)
    {
        sim.Step();
        if (!quiet && every > 0 && g % every == 0)
        {
            Print(sim, z);
        }
        foreach (var line in sim.WorldLog)
        {
            Console.WriteLine($"[world] {line}");
        }
    }
    if (!quiet && every == 0)
    {
        Print(sim, z);
    }
    var census = sim.Census();
    var populated = Enumerable.Range(0, program.IdCount).Where(id => census[id] > 0 && id != program.DefaultId);
    Console.WriteLine(
        $"generation {sim.Generation}, {watch.Elapsed.TotalMilliseconds / Math.Max(1, generations):0.###} ms/gen, hash {sim.Hash():x16}, "
            + string.Join(", ", populated.Select(id => $"{program.DescribeId((byte)id)}: {census[id]}"))
    );
    return 0;
}

static void Print(Simulation sim, int? z)
{
    Console.WriteLine($"-- generation {sim.Generation}");
    Console.Write(AsciiRenderer.Render(sim.Program, sim.Ids, z));
}

static int Parity(CompiledProgram program, int generations)
{
    if (program.Types.All(t => t.Lut == null))
    {
        Console.WriteLine("No type compiles to a lookup table; nothing to compare.");
        return 0;
    }
    var lut = new Simulation(program, useLut: true);
    var interp = new Simulation(program, useLut: false);
    for (int g = 1; g <= generations; g++)
    {
        lut.Step();
        interp.Step();
        if (lut.Hash() != interp.Hash())
        {
            Console.WriteLine($"MISMATCH at generation {g}");
            return 1;
        }
    }
    Console.WriteLine($"lookup table and interpreter agree for {generations} generations (hash {lut.Hash():x16})");
    return 0;
}

static void Bench(CompiledProgram program, int generations)
{
    foreach (bool useLut in new[] { false, true })
    {
        var sim = new Simulation(program, useLut);
        var watch = Stopwatch.StartNew();
        for (int g = 0; g < generations; g++)
        {
            sim.Step();
        }
        Console.WriteLine($"{(useLut ? "lookup tables" : "interpreter  ")}: {watch.Elapsed.TotalMilliseconds / generations:0.###} ms/gen over {generations} generations");
    }
}
