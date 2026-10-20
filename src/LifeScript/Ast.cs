namespace LifeScript;

// The syntax tree doubles as the executable tree: the compiler resolves names to slots and
// indices in place, and every node evaluates itself against an EvalContext.

public abstract class Node(SourceSpan span)
{
    public SourceSpan Span { get; } = span;
}

#region Expressions

public abstract class Expr(SourceSpan span) : Node(span)
{
    public abstract double Eval(EvalContext c);

    /// <summary>Folds the expression to a constant if it has no runtime inputs.</summary>
    public virtual double? Constant => null;
}

public sealed class NumberExpr(double value, SourceSpan span) : Expr(span)
{
    public double Value { get; } = value;

    public override double Eval(EvalContext c) => Value;

    public override double? Constant => Value;
}

public sealed class TupleExpr(IReadOnlyList<Expr> items, SourceSpan span) : Expr(span)
{
    public IReadOnlyList<Expr> Items { get; } = items;

    public override double Eval(EvalContext c) =>
        throw new LifeScriptRuntimeException("A tuple is not a number.", Span);
}

public enum VarKind
{
    Unresolved,
    Local,
    Global,
}

public sealed class VarExpr(string name, SourceSpan span) : Expr(span)
{
    public string Name { get; } = name;
    public VarKind Kind { get; set; }
    public int Slot { get; set; }

    /// <summary>Set for globals that are assigned once, at the top level, to a constant.</summary>
    public double? Folded { get; set; }

    public override double Eval(EvalContext c) =>
        Folded ?? (Kind == VarKind.Local ? c.Stack[c.FrameBase + Slot] : c.Globals[Slot]);

    public override double? Constant => Folded;
}

public sealed class UnaryExpr(string op, Expr operand, SourceSpan span) : Expr(span)
{
    public string Op { get; } = op;
    public Expr Operand { get; } = operand;

    public override double Eval(EvalContext c)
    {
        double v = Operand.Eval(c);
        return Op == "-" ? -v : v == 0 ? 1 : 0;
    }

    public override double? Constant =>
        Operand.Constant is double v ? (Op == "-" ? -v : v == 0 ? 1 : 0) : null;
}

public sealed class BinaryExpr(string op, Expr left, Expr right, SourceSpan span) : Expr(span)
{
    public string Op { get; } = op;
    public Expr Left { get; } = left;
    public Expr Right { get; } = right;

    public override double Eval(EvalContext c)
    {
        double l = Left.Eval(c);
        switch (Op)
        {
            case "and":
                return l != 0 && Right.Eval(c) != 0 ? 1 : 0;
            case "or":
                return l != 0 || Right.Eval(c) != 0 ? 1 : 0;
        }
        return Apply(Op, l, Right.Eval(c));
    }

    public override double? Constant =>
        Left.Constant is double l && Right.Constant is double r ? Apply(Op, l, r) : null;

    private static double Apply(string op, double l, double r) =>
        op switch
        {
            "+" => l + r,
            "-" => l - r,
            "*" => l * r,
            // Division and modulo by zero give 0 rather than NaN/Infinity, so a bad script
            // produces odd patterns instead of poisoning every value downstream.
            "/" => r == 0 ? 0 : l / r,
            "%" => r == 0 ? 0 : ((l % r) + r) % r,
            "==" => l == r ? 1 : 0,
            "!=" => l != r ? 1 : 0,
            "<" => l < r ? 1 : 0,
            "<=" => l <= r ? 1 : 0,
            ">" => l > r ? 1 : 0,
            ">=" => l >= r ? 1 : 0,
            "and" => l != 0 && r != 0 ? 1 : 0,
            "or" => l != 0 || r != 0 ? 1 : 0,
            _ => throw new InvalidOperationException(op),
        };
}

/// <summary><c>x in [1, 3..5]</c>: membership in a list of values and inclusive ranges.</summary>
public sealed class InExpr(Expr value, IReadOnlyList<(Expr Low, Expr? High)> items, SourceSpan span) : Expr(span)
{
    public Expr Value { get; } = value;
    public IReadOnlyList<(Expr Low, Expr? High)> Items { get; } = items;

    public override double Eval(EvalContext c)
    {
        double v = Value.Eval(c);
        foreach (var (low, high) in Items)
        {
            double lo = low.Eval(c);
            if (high == null ? v == lo : v >= lo && v <= high.Eval(c))
            {
                return 1;
            }
        }
        return 0;
    }
}

public enum BuiltinValue
{
    Alive,
    State,
    Age,
    Generation,
    CellX,
    CellY,
    CellZ,
    WorldWidth,
    WorldHeight,
    WorldDepth,
}

/// <summary>Built-in values: <c>alive</c>, <c>state</c>, <c>age</c>, <c>generation</c>, <c>cell.x</c>, <c>world.width</c>, ...</summary>
public sealed class BuiltinExpr(BuiltinValue value, SourceSpan span) : Expr(span)
{
    public BuiltinValue Value { get; } = value;

    public override double Eval(EvalContext c) =>
        Value switch
        {
            BuiltinValue.Alive => c.State > 0 ? 1 : 0,
            BuiltinValue.State => c.State,
            BuiltinValue.Age => c.Age,
            BuiltinValue.Generation => c.Generation,
            BuiltinValue.CellX => c.Position[0],
            BuiltinValue.CellY => c.Position[1],
            BuiltinValue.CellZ => c.Position[2],
            BuiltinValue.WorldWidth => c.Program.Size[0],
            BuiltinValue.WorldHeight => c.Program.Size.Length > 1 ? c.Program.Size[1] : 1,
            BuiltinValue.WorldDepth => c.Program.Size.Length > 2 ? c.Program.Size[2] : 1,
            _ => throw new InvalidOperationException(),
        };
}

/// <summary>
/// <c>neighbors [that are "T"] [with state N]</c>: the weighted sum of the neighborhood over
/// neighbors that match (see <see cref="QuerySpec.Matches"/>).
/// </summary>
public sealed class NeighborsExpr(string? typeName, int? state, SourceSpan span) : Expr(span)
{
    public string? TypeName { get; } = typeName;
    public int? StateFilter { get; } = state;

    /// <summary>Index into the owning type's query list.</summary>
    public int QueryIndex { get; set; }

    public override double Eval(EvalContext c) => c.Queries[QueryIndex];
}

/// <summary>
/// <c>neighbor at (dx, dy)</c> is that neighbor's state; <c>neighbor at (dx, dy) is "T"</c>
/// is 1 if it has that type. Out-of-bounds neighbors read as the default type, state 0.
/// </summary>
public sealed class NeighborAtExpr(TupleExpr offset, string? typeName, SourceSpan span) : Expr(span)
{
    public TupleExpr Offset { get; } = offset;
    public string? TypeName { get; } = typeName;
    public int UnionIndex { get; set; }
    public int TypeIndex { get; set; }

    public override double Eval(EvalContext c)
    {
        int id = c.Neighbors[UnionIndex];
        return TypeName == null ? c.Program.StateOf[id] : c.Program.TypeOf[id] == TypeIndex ? 1 : 0;
    }
}

/// <summary>World block only: <c>count "T" [with state N]</c>.</summary>
public sealed class CountExpr(string typeName, int? state, SourceSpan span) : Expr(span)
{
    public string TypeName { get; } = typeName;
    public int? StateFilter { get; } = state;
    public int TypeIndex { get; set; }

    public override double Eval(EvalContext c) => c.World!.Count(TypeIndex, StateFilter);
}

/// <summary>World block only: <c>state of (x, y)</c> or <c>cell at (x, y) is "T"</c>.</summary>
public sealed class CellAtExpr(TupleExpr position, string? typeName, SourceSpan span) : Expr(span)
{
    public TupleExpr Position { get; } = position;
    public string? TypeName { get; } = typeName;
    public int TypeIndex { get; set; }

    public override double Eval(EvalContext c)
    {
        int id = c.World!.Get(c.World.Resolve(Position, c));
        return TypeName == null ? c.Program.StateOf[id] : c.Program.TypeOf[id] == TypeIndex ? 1 : 0;
    }
}

public sealed class CallExpr(string name, IReadOnlyList<Expr> args, SourceSpan span) : Expr(span)
{
    public string Name { get; } = name;
    public IReadOnlyList<Expr> Args { get; } = args;
    public FunctionDecl? Function { get; set; }

    public override double Eval(EvalContext c)
    {
        if (Function == null)
        {
            return Builtins.Call(Name, Args, c, Span);
        }
        return c.Call(Function, Args, Span);
    }

    public override double? Constant
    {
        get
        {
            if (Function != null || !Builtins.IsPure(Name))
            {
                return null;
            }
            foreach (var a in Args)
            {
                if (a.Constant == null)
                {
                    return null;
                }
            }
            return Builtins.Call(Name, Args, null!, Span);
        }
    }
}

#endregion

#region Statements

public enum Flow
{
    Normal,
    Return,
}

public abstract class Stmt(SourceSpan span) : Node(span)
{
    public abstract Flow Exec(EvalContext c);

    public static Flow ExecBlock(IReadOnlyList<Stmt> block, EvalContext c)
    {
        foreach (var s in block)
        {
            if (s.Exec(c) == Flow.Return)
            {
                return Flow.Return;
            }
        }
        return Flow.Normal;
    }
}

public sealed class AssignStmt(string name, Expr value, SourceSpan span) : Stmt(span)
{
    public string Name { get; } = name;
    public Expr Value { get; } = value;
    public VarKind Kind { get; set; }
    public int Slot { get; set; }

    public override Flow Exec(EvalContext c)
    {
        double v = Value.Eval(c);
        if (Kind == VarKind.Local)
        {
            c.Stack[c.FrameBase + Slot] = v;
        }
        else
        {
            c.Globals[Slot] = v;
        }
        return Flow.Normal;
    }
}

public sealed class IfStmt(IReadOnlyList<(Expr Condition, IReadOnlyList<Stmt> Body)> branches, IReadOnlyList<Stmt>? elseBody, SourceSpan span)
    : Stmt(span)
{
    public IReadOnlyList<(Expr Condition, IReadOnlyList<Stmt> Body)> Branches { get; } = branches;
    public IReadOnlyList<Stmt>? Else { get; } = elseBody;

    public override Flow Exec(EvalContext c)
    {
        foreach (var (condition, body) in Branches)
        {
            if (condition.Eval(c) != 0)
            {
                return ExecBlock(body, c);
            }
        }
        return Else == null ? Flow.Normal : ExecBlock(Else, c);
    }
}

public sealed class ForStmt(AssignStmt init, Expr limit, Expr? step, IReadOnlyList<Stmt> body, SourceSpan span) : Stmt(span)
{
    public const int MaxIterations = 10_000;

    public AssignStmt Init { get; } = init;
    public Expr Limit { get; } = limit;
    public Expr? Step { get; } = step;
    public IReadOnlyList<Stmt> Body { get; } = body;

    public override Flow Exec(EvalContext c)
    {
        Init.Exec(c);
        double limit = Limit.Eval(c);
        double step = Step?.Eval(c) ?? 1;
        if (step == 0)
        {
            throw new LifeScriptRuntimeException("for loop step is 0.", Span);
        }
        var counter = new VarExpr(Init.Name, Span) { Kind = Init.Kind, Slot = Init.Slot };
        for (int i = 0; ; i++)
        {
            double v = counter.Eval(c);
            if (step > 0 ? v > limit : v < limit)
            {
                return Flow.Normal;
            }
            if (i >= MaxIterations)
            {
                throw new LifeScriptRuntimeException($"for loop ran more than {MaxIterations} times.", Span);
            }
            if (ExecBlock(Body, c) == Flow.Return)
            {
                return Flow.Return;
            }
            double next = counter.Eval(c) + step;
            if (Init.Kind == VarKind.Local)
            {
                c.Stack[c.FrameBase + Init.Slot] = next;
            }
            else
            {
                c.Globals[Init.Slot] = next;
            }
        }
    }
}

/// <summary><c>alive</c>, <c>dead</c> and <c>setstate to e</c>: set the cell's next state.</summary>
public sealed class SetStateStmt(Expr value, SourceSpan span) : Stmt(span)
{
    public Expr Value { get; } = value;

    public override Flow Exec(EvalContext c)
    {
        c.SetNextState(c.NextType, Value.Eval(c));
        return Flow.Normal;
    }
}

/// <summary><c>become "T" [with state e]</c>. Without a state the cell keeps its current state number.</summary>
public sealed class BecomeStmt(string typeName, Expr? state, SourceSpan span) : Stmt(span)
{
    public string TypeName { get; } = typeName;
    public Expr? State { get; } = state;
    public int TypeIndex { get; set; }

    public override Flow Exec(EvalContext c)
    {
        c.SetNextState(TypeIndex, State?.Eval(c) ?? c.NextState);
        return Flow.Normal;
    }
}

public sealed class PrintStmt(IReadOnlyList<object> parts, SourceSpan span) : Stmt(span)
{
    /// <summary>Strings and expressions.</summary>
    public IReadOnlyList<object> Parts { get; } = parts;

    public override Flow Exec(EvalContext c)
    {
        if (c.Trace == null)
        {
            return Flow.Normal;
        }
        var text = string.Concat(
            Parts.Select(p => p is string s ? s : Format(((Expr)p).Eval(c)))
        );
        c.Trace.Add(text);
        return Flow.Normal;
    }

    public static string Format(double v) =>
        v == Math.Floor(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString() : v.ToString("0.####");
}

public sealed class CallStmt(CallExpr call, SourceSpan span) : Stmt(span)
{
    public CallExpr Call { get; } = call;

    public override Flow Exec(EvalContext c)
    {
        Call.Eval(c);
        return Flow.Normal;
    }
}

public sealed class ReturnStmt(Expr? value, SourceSpan span) : Stmt(span)
{
    public Expr? Value { get; } = value;

    public override Flow Exec(EvalContext c)
    {
        c.ReturnValue = Value?.Eval(c) ?? 0;
        return Flow.Return;
    }
}

/// <summary>World block only: <c>create "T" at (x, y) [with state e]</c>.</summary>
public sealed class CreateStmt(string typeName, TupleExpr position, Expr? state, SourceSpan span) : Stmt(span)
{
    public string TypeName { get; } = typeName;
    public TupleExpr Position { get; } = position;
    public Expr? State { get; } = state;
    public int TypeIndex { get; set; }

    public override Flow Exec(EvalContext c)
    {
        int index = c.World!.Resolve(Position, c);
        c.World.Set(index, TypeIndex, State?.Eval(c) ?? 1, c);
        return Flow.Normal;
    }
}

/// <summary>World block only: <c>kill at (x, y)</c> resets a cell to the default type, state 0.</summary>
public sealed class KillStmt(TupleExpr position, SourceSpan span) : Stmt(span)
{
    public TupleExpr Position { get; } = position;

    public override Flow Exec(EvalContext c)
    {
        int index = c.World!.Resolve(Position, c);
        c.World.Set(index, c.Program.DefaultType, 0, c);
        return Flow.Normal;
    }
}

/// <summary>World block only: <c>setstate of (x, y) to e</c>.</summary>
public sealed class SetStateAtStmt(TupleExpr position, Expr value, SourceSpan span) : Stmt(span)
{
    public TupleExpr Position { get; } = position;
    public Expr Value { get; } = value;

    public override Flow Exec(EvalContext c)
    {
        int index = c.World!.Resolve(Position, c);
        int id = c.World.Get(index);
        c.World.Set(index, c.Program.TypeOf[id], Value.Eval(c), c);
        return Flow.Normal;
    }
}

#endregion

#region Declarations

public abstract record NeighborhoodSpec(SourceSpan Span);

/// <summary>
/// A drawn neighborhood. <c>Neighborhood[3] = { 1, 1, 1,  1, VAR, 1,  1, 1, 1 }</c>; for 3D,
/// <c>Neighborhood[3, 3]</c> with z-slices separated by <c>;</c>. Entries are weights; null is VAR.
/// </summary>
public sealed record MatrixNeighborhood(int Width, int? Height, IReadOnlyList<IReadOnlyList<Expr?>> Slices, SourceSpan Span)
    : NeighborhoodSpec(Span);

/// <summary><c>Neighborhood = moore(1)</c>, <c>vonneumann(2)</c>, <c>none</c>.</summary>
public sealed record BuiltinNeighborhood(string Name, Expr? Radius, SourceSpan Span) : NeighborhoodSpec(Span);

public sealed record ColorSpec(byte R, byte G, byte B);

public sealed class TypeDecl(string name, SourceSpan span) : Node(span)
{
    public string Name { get; } = name;
    public NeighborhoodSpec? Neighborhood { get; set; }
    public Expr? States { get; set; }
    public ColorSpec? Color { get; set; }
    public Dictionary<int, ColorSpec> StateColors { get; } = new();
    public List<Stmt> Body { get; } = new();
    public int LocalCount { get; set; }
}

public sealed class FunctionDecl(string name, IReadOnlyList<string> parameters, IReadOnlyList<Stmt> body, SourceSpan span)
    : Node(span)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Parameters { get; } = parameters;
    public IReadOnlyList<Stmt> Body { get; } = body;
    public int LocalCount { get; set; }
}

public sealed class WorldDecl(IReadOnlyList<Stmt> body, SourceSpan span) : Node(span)
{
    public IReadOnlyList<Stmt> Body { get; } = body;
    public int LocalCount { get; set; }
}

public abstract record StartItem(SourceSpan Span);

/// <summary><c>("T", (x, y))</c> or <c>("T", (x, y), state)</c>.</summary>
public sealed record PlaceStart(string TypeName, TupleExpr Position, Expr? State, SourceSpan Span) : StartItem(Span);

/// <summary><c>random 0.3 of "T" [with state n] [in (x0, y0) to (x1, y1)]</c>.</summary>
public sealed record RandomStart(Expr Probability, string TypeName, Expr? State, TupleExpr? From, TupleExpr? To, SourceSpan Span)
    : StartItem(Span);

/// <summary><c>pattern "bo$2bo$3o!" of "T" at (x, y)</c>: a Golly RLE pattern.</summary>
public sealed record PatternStart(string Rle, string TypeName, TupleExpr Position, SourceSpan Span) : StartItem(Span);

public sealed record Property(string Name, Expr? Value, IReadOnlyList<StartItem>? Start, SourceSpan Span);

public sealed class ScriptAst
{
    public List<Property> Properties { get; } = new();
    public List<TypeDecl> Types { get; } = new();
    public List<FunctionDecl> Functions { get; } = new();
    public List<AssignStmt> Globals { get; } = new();
    public WorldDecl? World { get; set; }
}

#endregion
