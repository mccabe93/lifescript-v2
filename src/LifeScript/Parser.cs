namespace LifeScript;

/// <summary>
/// Recursive-descent parser. Newlines are insignificant, so statements are delimited by
/// keywords alone, exactly like the original ANTLR grammar.
/// </summary>
public sealed class Parser
{
    private static readonly string[] PropertyNames =
    [
        "Title", "Cell Size", "Dimensions", "Wraparound", "Start", "Default Type",
        "Generations", "Interval", "Pausable", "Steppable", "Seed",
    ];

    private static readonly HashSet<string> BlockEnds =
        ["endif", "else", "elseif", "endtype", "enddef", "endworld", "next"];

    private static readonly HashSet<string> StatementStarts =
    [
        "if", "for", "while", "alive", "dead", "setstate", "become", "print", "return",
        "create", "kill", "addrow", "addcolumn", "input", "end", "Neighborhood", "States", "born",
        "statecolor", "setcolor",
    ];

    private readonly List<Token> _tokens;
    private int _pos;

    private Parser(List<Token> tokens) => _tokens = tokens;

    public static ScriptAst Parse(string source) => new Parser(Lexer.Tokenize(source)).ParseScript();

    private Token Current => _tokens[_pos];

    private Token Previous => _tokens[Math.Max(0, _pos - 1)];

    private Token Peek(int ahead = 1) => _tokens[Math.Min(_pos + ahead, _tokens.Count - 1)];

    private Token Next() => _tokens[_pos++ < _tokens.Count - 1 ? _pos - 1 : _tokens.Count - 1];

    private bool Accept(string text)
    {
        if (Current.Is(text))
        {
            _pos++;
            return true;
        }
        return false;
    }

    private Token Expect(string text, string? context = null)
    {
        if (!Current.Is(text))
        {
            throw Error($"Expected '{text}'{(context == null ? "" : " " + context)} but found {Current}.");
        }
        return Next();
    }

    private string ExpectString(string what)
    {
        if (Current.Kind != TokenKind.String)
        {
            throw Error($"Expected {what} (a quoted name) but found {Current}.");
        }
        return Next().Text;
    }

    private string ExpectIdentifier(string what)
    {
        if (Current.Kind != TokenKind.Identifier)
        {
            throw Error($"Expected {what} but found {Current}.");
        }
        return Next().Text;
    }

    private LifeScriptException Error(string message, SourceSpan? span = null) => new(message, span ?? Current.Span);

    private ScriptAst ParseScript()
    {
        var script = new ScriptAst();
        while (Current.Kind != TokenKind.End)
        {
            if (Current.Is("Properties"))
            {
                ParseProperties(script);
            }
            else if (Current.Is("type"))
            {
                script.Types.Add(ParseType());
            }
            else if (Current.Is("world") && Peek().Is(":"))
            {
                var span = Next().Span;
                Next();
                if (script.World != null)
                {
                    throw Error("Only one world: block is allowed.", span);
                }
                var body = ParseBlock();
                Expect("endworld", "to close world:");
                script.World = new WorldDecl(body, span);
            }
            else if (Current.Is("def"))
            {
                script.Functions.Add(ParseFunction());
            }
            else if (Current.Kind == TokenKind.Identifier && Peek().Is("="))
            {
                var name = Next();
                Next();
                script.Globals.Add(new AssignStmt(name.Text, ParseExpr(), name.Span));
            }
            else
            {
                throw Error($"Expected Properties:, type, world:, def or a global assignment but found {Current}.");
            }
        }
        return script;
    }

    private void ParseProperties(ScriptAst script)
    {
        Next();
        Expect(":", "after Properties");
        while (TryMatchPropertyName(out string? name, out int length))
        {
            var span = Current.Span;
            _pos += length;
            Expect("=", $"after {name}");
            if (name == "Start")
            {
                script.Properties.Add(new Property(name, null, ParseStart(), span));
            }
            else if (name is "Title" or "Default Type")
            {
                var text = ExpectString(name == "Title" ? "a title" : "a type");
                script.Properties.Add(new Property(name, new StringLiteralExpr(text, span), null, span));
            }
            else
            {
                script.Properties.Add(new Property(name!, ParseExpr(), null, span));
            }
        }
    }

    private bool TryMatchPropertyName(out string? name, out int length)
    {
        foreach (var candidate in PropertyNames)
        {
            var words = candidate.Split(' ');
            bool match = true;
            for (int w = 0; w < words.Length; w++)
            {
                if (!Peek(w).Is(words[w]) || Peek(w).Kind != TokenKind.Identifier)
                {
                    match = false;
                    break;
                }
            }
            if (match && Peek(words.Length).Is("="))
            {
                name = candidate;
                length = words.Length;
                return true;
            }
        }
        name = null;
        length = 0;
        return false;
    }

    private List<StartItem> ParseStart()
    {
        var items = new List<StartItem>();
        bool braced = Accept("{");
        do
        {
            if (braced && Current.Is("}"))
            {
                break;
            }
            items.Add(ParseStartItem());
        } while (Accept(","));
        if (braced)
        {
            Expect("}", "to close Start");
        }
        return items;
    }

    private StartItem ParseStartItem()
    {
        var span = Current.Span;
        if (Accept("random"))
        {
            var probability = ParseExpr();
            Expect("of", "in random start (random 0.3 of \"type\")");
            var type = ExpectString("a type");
            Expr? state = null;
            if (Accept("with"))
            {
                Expect("state");
                state = ParseExpr();
            }
            TupleExpr? from = null, to = null;
            if (Accept("in"))
            {
                from = ParseTuple("region start");
                Expect("to", "in region (in (x0, y0) to (x1, y1))");
                to = ParseTuple("region end");
            }
            return new RandomStart(probability, type, state, from, to, span);
        }
        if (Accept("pattern"))
        {
            if (Current.Kind != TokenKind.String)
            {
                throw Error("Expected an RLE pattern string, e.g. pattern \"bo$2bo$3o!\" of \"alive\" at (10, 10).");
            }
            var rle = Next().Text;
            Expect("of");
            var type = ExpectString("a type");
            Expect("at");
            return new PatternStart(rle, type, ParseTuple("position"), span);
        }
        Expect("(", "to start a placement like (\"type\", (x, y))");
        var typeName = ExpectString("a type");
        Expect(",");
        var position = ParseTuple("position");
        Expr? placedState = Accept(",") ? ParseExpr() : null;
        Expect(")");
        return new PlaceStart(typeName, position, placedState, span);
    }

    private TypeDecl ParseType()
    {
        Next();
        var span = Current.Span;
        string name = Current.Kind == TokenKind.String ? Next().Text : ExpectIdentifier("a type name");
        var colon = Expect(":", "after the type name");
        var type = new TypeDecl(name, span) { HeaderEnd = colon.Span.End };
        while (!Current.Is("endtype"))
        {
            if (Current.Kind == TokenKind.End)
            {
                throw Error($"Missing endtype for type {name}.", span);
            }
            if (Current.Is("Neighborhood"))
            {
                if (type.Neighborhood != null)
                {
                    throw Error("Neighborhood is declared twice.");
                }
                int start = Current.Span.Offset;
                type.Neighborhood = ParseNeighborhood();
                type.NeighborhoodRange = new SourceRange(start, Previous.Span.End);
            }
            else if (Current.Is("born"))
            {
                if (type.BornSurvive != null)
                {
                    throw Error("born ... survive ... is declared twice.");
                }
                var (rule, statement) = ParseBornSurvive();
                type.BornSurvive = rule;
                type.Body.Add(statement);
            }
            else if (Current.Is("States") && Peek().Is("="))
            {
                _pos += 2;
                type.States = ParseExpr();
            }
            else if (Current.Is("statecolor"))
            {
                Next();
                var stateExpr = ParseExpr();
                if (stateExpr.Constant is not double s || s < 0 || s != Math.Floor(s))
                {
                    throw Error("statecolor needs a constant state number.", stateExpr.Span);
                }
                Expect("is", "in statecolor N is (r, g, b)");
                type.StateColors[(int)s] = ParseColor();
            }
            else if (Current.Is("setcolor"))
            {
                Next();
                if (Current.Is("at"))
                {
                    throw Error("setcolor at (x, y) is not supported: colors belong to types and states. Use setcolor to (r, g, b) or statecolor N is (r, g, b).");
                }
                Expect("to", "in setcolor to (r, g, b)");
                type.Color = ParseColor();
            }
            else
            {
                type.Body.Add(ParseStatement());
            }
        }
        type.BodyEnd = Next().Span.Offset;
        return type;
    }

    /// <summary>
    /// <c>born [3] survive [2, 3]</c>, the B3/S23 notation: a dead cell comes alive when its
    /// neighbor sum is listed after born, and a live cell stays alive only when it's listed after
    /// survive. Counts that aren't listed mean dead, unlike the rest of lifescript, where a cell
    /// that writes nothing keeps its state. Becomes the equivalent if statement.
    /// </summary>
    private (BornSurvive, Stmt) ParseBornSurvive()
    {
        var span = Next().Span;
        var born = ParseCountList("born");
        Expect("survive", "after the born list (born [3] survive [2, 3])");
        var survive = ParseCountList("survive");
        var range = new SourceRange(span.Offset, Previous.Span.End);

        InExpr Counts(int[] counts) =>
            new(new NeighborsExpr(null, null, span), counts.Select(c => ((Expr)new NumberExpr(c, span), (Expr?)null)).ToList(), span);
        Stmt Set(int state) => new SetStateStmt(new NumberExpr(state, span), span);
        Expr alive = new BuiltinExpr(BuiltinValue.Alive, span);

        var statement = new IfStmt(
            [
                (alive, [new IfStmt([(new UnaryExpr("!", Counts(survive), span), [Set(0)])], null, span)]),
                (Counts(born), [Set(1)]),
            ],
            null,
            span
        );
        return (new BornSurvive(born, survive, range), statement);
    }

    /// <summary>A list of whole-number neighbor counts like [2, 3] or [3, 6..8].</summary>
    private int[] ParseCountList(string what)
    {
        Expect("[", $"to start the {what} list, like [2, 3]");
        var counts = new SortedSet<int>();
        int Count()
        {
            var e = ParseAdditive();
            if (e.Constant is not double v || v != Math.Floor(v) || v < 0 || v > 10_000)
            {
                throw Error($"The {what} list needs whole numbers like [2, 3] or [3, 6..8].", e.Span);
            }
            return (int)v;
        }
        if (!Current.Is("]"))
        {
            do
            {
                int low = Count();
                int high = Accept("..") ? Count() : low;
                for (int c = low; c <= high; c++)
                {
                    counts.Add(c);
                }
            } while (Accept(","));
        }
        Expect("]", $"to close the {what} list");
        return counts.ToArray();
    }

    private NeighborhoodSpec ParseNeighborhood()
    {
        var span = Next().Span;
        if (Accept("="))
        {
            var name = ExpectIdentifier("a neighborhood (moore, vonneumann or none)");
            Expr? radius = null;
            if (Accept("("))
            {
                radius = ParseExpr();
                Expect(")");
            }
            return new BuiltinNeighborhood(name, radius, span);
        }
        Expect("[", "after Neighborhood (Neighborhood[3] = { ... } or Neighborhood = moore(1))");
        int width = ParseConstantInt("neighborhood width");
        int? height = Accept(",") ? ParseConstantInt("neighborhood height") : null;
        Expect("]");
        Expect("=");
        Expect("{");
        var slices = new List<IReadOnlyList<Expr?>>();
        var slice = new List<Expr?>();
        while (!Current.Is("}"))
        {
            if (Accept("VAR"))
            {
                slice.Add(null);
            }
            else
            {
                slice.Add(ParseExpr());
            }
            if (Accept(";"))
            {
                slices.Add(slice);
                slice = new List<Expr?>();
            }
            else if (!Current.Is("}"))
            {
                Expect(",", "between neighborhood entries");
            }
        }
        Next();
        slices.Add(slice);
        return new MatrixNeighborhood(width, height, slices, span);
    }

    private int ParseConstantInt(string what)
    {
        var e = ParseExpr();
        if (e.Constant is not double v || v != Math.Floor(v) || v < 0)
        {
            throw Error($"Expected a constant whole number for the {what}.", e.Span);
        }
        return (int)v;
    }

    private ColorSpec ParseColor()
    {
        bool paren = Accept("(");
        ColorSpec color;
        if (Current.Kind is TokenKind.HexColor or TokenKind.String)
        {
            var token = Next();
            color = ParseHex(token.Text, token.Span);
        }
        else if (paren)
        {
            byte Channel()
            {
                var e = ParseExpr();
                if (e.Constant is not double v || v < 0 || v > 255)
                {
                    throw Error("Color channels must be constants from 0 to 255.", e.Span);
                }
                return (byte)v;
            }
            byte r = Channel();
            Expect(",");
            byte g = Channel();
            Expect(",");
            byte b = Channel();
            color = new ColorSpec(r, g, b);
        }
        else
        {
            throw Error("Expected a color: (r, g, b), #rrggbb or (\"#rrggbb\").");
        }
        if (paren)
        {
            Expect(")");
        }
        return color;
    }

    private static ColorSpec ParseHex(string text, SourceSpan span)
    {
        var hex = text.TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        }
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            throw new LifeScriptException($"'{text}' is not a color. Use #rgb or #rrggbb.", span);
        }
        return new ColorSpec(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
    }

    private FunctionDecl ParseFunction()
    {
        Next();
        var span = Current.Span;
        var name = ExpectIdentifier("a function name");
        Expect("(");
        var parameters = new List<string>();
        if (!Current.Is(")"))
        {
            do
            {
                parameters.Add(ExpectIdentifier("a parameter name"));
            } while (Accept(","));
        }
        Expect(")");
        Expect(":");
        var body = ParseBlock();
        Expect("enddef", $"to close def {name}");
        return new FunctionDecl(name, parameters, body, span);
    }

    private List<Stmt> ParseBlock()
    {
        var body = new List<Stmt>();
        while (Current.Kind != TokenKind.End && !(Current.Kind == TokenKind.Identifier && BlockEnds.Contains(Current.Text)))
        {
            body.Add(ParseStatement());
        }
        return body;
    }

    private Stmt ParseStatement()
    {
        var token = Current;
        var span = token.Span;
        if (token.Kind != TokenKind.Identifier)
        {
            throw Error($"Expected a statement but found {token}.");
        }
        switch (token.Text)
        {
            case "if":
            {
                Next();
                var branches = new List<(Expr, IReadOnlyList<Stmt>)>();
                var condition = ParseExpr();
                Expect("then", "after the if condition");
                branches.Add((condition, ParseBlock()));
                List<Stmt>? elseBody = null;
                while (true)
                {
                    if (Accept("elseif"))
                    {
                        var c = ParseExpr();
                        Expect("then", "after the elseif condition");
                        branches.Add((c, ParseBlock()));
                    }
                    else if (Accept("else"))
                    {
                        elseBody = ParseBlock();
                        Expect("endif", "to close if");
                        break;
                    }
                    else
                    {
                        Expect("endif", "to close if");
                        break;
                    }
                }
                return new IfStmt(branches, elseBody, span);
            }
            case "for":
            {
                Next();
                var varToken = Current;
                var name = ExpectIdentifier("a loop variable");
                Expect("=");
                var init = new AssignStmt(name, ParseExpr(), varToken.Span);
                Expect("to", "in for i = a to b");
                var limit = ParseExpr();
                Expr? step = Accept("step") ? ParseExpr() : null;
                var body = ParseBlock();
                Expect("next", "to close for");
                if (Current.Is(name) && !Peek().Is("="))
                {
                    Next();
                }
                return new ForStmt(init, limit, step, body, span);
            }
            case "born":
                throw Error("born ... survive ... describes a whole type's rule, so it goes directly inside the type, not inside if, for, def or world.");
            case "while":
                throw Error("while loops are not supported: every cell must finish in bounded time. Use for i = a to b.");
            case "input":
            case "end":
                throw Error($"'{token.Text}' from the original lifescript is not supported.");
            case "addrow":
            case "addcolumn":
                throw Error($"{token.Text} is not supported: the world has a fixed size. To watch a 1D rule grow, give it a 2D world where each row reads the row above.");
            case "alive":
                Next();
                return new SetStateStmt(new NumberExpr(1, span), span);
            case "dead":
                Next();
                return new SetStateStmt(new NumberExpr(0, span), span);
            case "setstate":
            {
                Next();
                if (Accept("of"))
                {
                    var position = ParseTuple("position");
                    Expect("to");
                    return new SetStateAtStmt(position, ParseExpr(), span);
                }
                Expect("to", "in setstate to N");
                return new SetStateStmt(ParseExpr(), span);
            }
            case "become":
            {
                Next();
                var type = ExpectString("a type");
                Expr? state = null;
                if (Accept("with"))
                {
                    Expect("state");
                    state = ParseExpr();
                }
                return new BecomeStmt(type, state, span);
            }
            case "print":
            {
                Next();
                var parts = new List<object>();
                do
                {
                    parts.Add(Current.Kind == TokenKind.String ? Next().Text : ParseExpr());
                } while (Accept(","));
                return new PrintStmt(parts, span);
            }
            case "return":
            {
                Next();
                bool hasValue =
                    Current.Kind != TokenKind.End
                    && !(Current.Kind == TokenKind.Identifier && (BlockEnds.Contains(Current.Text) || StatementStarts.Contains(Current.Text)))
                    && !(Current.Kind == TokenKind.Identifier && Peek().Is("="));
                return new ReturnStmt(hasValue ? ParseExpr() : null, span);
            }
            case "create":
            {
                Next();
                var type = ExpectString("a type");
                Expect("at");
                var position = ParseTuple("position");
                Expr? state = null;
                if (Accept("with"))
                {
                    Expect("state");
                    state = ParseExpr();
                }
                return new CreateStmt(type, position, state, span);
            }
            case "kill":
            {
                Next();
                if (Current.Kind == TokenKind.String)
                {
                    throw Error("kill \"type\" at is not supported; use kill at (x, y).");
                }
                Expect("at");
                return new KillStmt(ParseTuple("position"), span);
            }
        }
        if (Peek().Is("="))
        {
            Next();
            Next();
            return new AssignStmt(token.Text, ParseExpr(), span);
        }
        if (Peek().Is("("))
        {
            var call = (CallExpr)ParseAtom();
            return new CallStmt(call, span);
        }
        throw Error($"Unknown statement '{token.Text}'.");
    }

    private TupleExpr ParseTuple(string what)
    {
        var span = Current.Span;
        if (!Current.Is("("))
        {
            throw Error($"Expected a {what} like (x, y).");
        }
        var e = ParseAtom();
        return e as TupleExpr ?? new TupleExpr([e is ParenExpr p ? p.Inner : e], span);
    }

    // --- Expressions ---

    private Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Current.Is("|") || Current.Is("||") || Current.Is("or"))
        {
            var op = Next();
            left = new BinaryExpr("or", left, ParseAnd(), op.Span);
        }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseRelational();
        while (Current.Is("&") || Current.Is("&&") || Current.Is("and"))
        {
            var op = Next();
            left = new BinaryExpr("and", left, ParseRelational(), op.Span);
        }
        return left;
    }

    private Expr ParseRelational()
    {
        var left = ParseAdditive();
        while (true)
        {
            if (Current.Kind == TokenKind.Symbol && Current.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                var op = Next();
                left = new BinaryExpr(op.Text, left, ParseAdditive(), op.Span);
            }
            else if (Current.Is("in") && Peek().Is("["))
            {
                var span = Next().Span;
                Next();
                var items = new List<(Expr, Expr?)>();
                if (!Current.Is("]"))
                {
                    do
                    {
                        var low = ParseAdditive();
                        Expr? high = Accept("..") ? ParseAdditive() : null;
                        items.Add((low, high));
                    } while (Accept(","));
                }
                Expect("]");
                left = new InExpr(left, items, span);
            }
            else
            {
                return left;
            }
        }
    }

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Is("+") || Current.Is("-"))
        {
            var op = Next();
            left = new BinaryExpr(op.Text, left, ParseMultiplicative(), op.Span);
        }
        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Is("*") || Current.Is("/") || Current.Is("%"))
        {
            var op = Next();
            left = new BinaryExpr(op.Text, left, ParseUnary(), op.Span);
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Current.Is("-"))
        {
            var span = Next().Span;
            return new UnaryExpr("-", ParseUnary(), span);
        }
        if (Current.Is("!") || Current.Is("not"))
        {
            var span = Next().Span;
            return new UnaryExpr("!", ParseUnary(), span);
        }
        return ParseAtom();
    }

    private int? ParseWithState()
    {
        if (!(Current.Is("with") && Peek().Is("state")))
        {
            return null;
        }
        _pos += 2;
        // Parse only a number here, so "neighbors with state 1 == 2" compares the count.
        var e = ParseUnary();
        if (e.Constant is not double v || v != Math.Floor(v) || v < 0)
        {
            throw Error("Expected a constant state number after 'with state'.", e.Span);
        }
        return (int)v;
    }

    private Expr ParseAtom()
    {
        var token = Current;
        var span = token.Span;
        if (token.Kind == TokenKind.Number)
        {
            Next();
            return new NumberExpr(token.Number, span);
        }
        if (token.Is("("))
        {
            Next();
            var items = new List<Expr> { ParseExpr() };
            while (Accept(","))
            {
                items.Add(ParseExpr());
            }
            Expect(")");
            return items.Count == 1 ? new ParenExpr(items[0], span) : new TupleExpr(items, span);
        }
        if (token.Kind != TokenKind.Identifier)
        {
            throw Error($"Expected a value but found {token}.");
        }
        Next();
        switch (token.Text)
        {
            case "true":
                return new NumberExpr(1, span);
            case "false":
                return new NumberExpr(0, span);
            case "alive":
                return new BuiltinExpr(BuiltinValue.Alive, span);
            case "dead":
                return new UnaryExpr("!", new BuiltinExpr(BuiltinValue.Alive, span), span);
            case "age":
                return new BuiltinExpr(BuiltinValue.Age, span);
            case "generation":
                return new BuiltinExpr(BuiltinValue.Generation, span);
            case "state":
                if (Accept("of"))
                {
                    return new CellAtExpr(ParseTuple("position"), null, span);
                }
                return new BuiltinExpr(BuiltinValue.State, span);
            case "neighbors":
            {
                if (Current.Is("of"))
                {
                    throw Error("neighbors of (x, y) is not supported in v2: a cell can only look at its own neighborhood.");
                }
                string? type = null;
                if (Accept("that"))
                {
                    Expect("are", "in neighbors that are \"type\"");
                    type = ExpectString("a type");
                }
                return new NeighborsExpr(type, ParseWithState(), span);
            }
            case "neighbor":
            {
                Expect("at", "in neighbor at (dx, dy)");
                var offset = ParseTuple("offset");
                string? type = Accept("is") ? ExpectString("a type") : null;
                return new NeighborAtExpr(offset, type, span);
            }
            case "cell":
                if (Accept("."))
                {
                    var field = ExpectIdentifier("x, y or z");
                    return field switch
                    {
                        "x" => new BuiltinExpr(BuiltinValue.CellX, span),
                        "y" => new BuiltinExpr(BuiltinValue.CellY, span),
                        "z" => new BuiltinExpr(BuiltinValue.CellZ, span),
                        _ => throw Error($"cell.{field} does not exist; use cell.x, cell.y or cell.z.", span),
                    };
                }
                Expect("at", "in cell at (x, y) or cell.x");
                {
                    var position = ParseTuple("position");
                    string? type = Accept("is") ? ExpectString("a type") : null;
                    return new CellAtExpr(position, type, span);
                }
            case "world":
            {
                Expect(".", "in world.width");
                var field = ExpectIdentifier("width, height or depth");
                return field switch
                {
                    "width" => new BuiltinExpr(BuiltinValue.WorldWidth, span),
                    "height" => new BuiltinExpr(BuiltinValue.WorldHeight, span),
                    "depth" => new BuiltinExpr(BuiltinValue.WorldDepth, span),
                    _ => throw Error($"world.{field} does not exist; use world.width, world.height or world.depth.", span),
                };
            }
            case "count":
            {
                var type = ExpectString("a type");
                return new CountExpr(type, ParseWithState(), span);
            }
        }
        if (Current.Is("("))
        {
            Next();
            var args = new List<Expr>();
            if (!Current.Is(")"))
            {
                do
                {
                    args.Add(ParseExpr());
                } while (Accept(","));
            }
            Expect(")", $"to close the call to {token.Text}");
            return new CallExpr(token.Text, args, span);
        }
        return new VarExpr(token.Text, span);
    }
}

public sealed class ParenExpr(Expr inner, SourceSpan span) : Expr(span)
{
    public Expr Inner { get; } = inner;

    public override double Eval(EvalContext c) => Inner.Eval(c);

    public override double? Constant => Inner.Constant;
}

/// <summary>A string-valued property (Title, Default Type); never evaluated.</summary>
public sealed class StringLiteralExpr(string text, SourceSpan span) : Expr(span)
{
    public string Text { get; } = text;

    public override double Eval(EvalContext c) => 0;
}
