using System.Globalization;
using System.Text;

namespace LifeScript;

public enum TokenKind
{
    Identifier,
    Number,
    String,
    HexColor,
    Symbol,
    End,
}

public readonly record struct Token(TokenKind Kind, string Text, double Number, SourceSpan Span)
{
    public bool Is(string text) => (Kind == TokenKind.Symbol || Kind == TokenKind.Identifier) && Text == text;

    public override string ToString() =>
        Kind switch
        {
            TokenKind.End => "end of file",
            TokenKind.String => $"\"{Text}\"",
            _ => $"'{Text}'",
        };
}

/// <summary>
/// Splits source into tokens. Whitespace and newlines are insignificant, as in the original
/// lifescript; keywords are ordinary identifiers that the parser recognizes by context.
/// </summary>
public static class Lexer
{
    private static readonly string[] Symbols =
    [
        "==", "!=", "<=", ">=", "..", "&&", "||",
        "(", ")", "{", "}", "[", "]", ",", "=", "<", ">", "+", "-", "*", "/", "%", "&", "|", "!", ":", ";", ".",
    ];

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, col = 1;

        void Advance(int n)
        {
            for (int k = 0; k < n; k++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
                i++;
            }
        }

        while (i < source.Length)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
            {
                Advance(1);
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    Advance(1);
                }
                continue;
            }

            int startLine = line, startCol = col, start = i;
            SourceSpan Span() => new(startLine, startCol, i - start) { Offset = start };

            if (char.IsLetter(c) || c == '_')
            {
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    Advance(1);
                }
                tokens.Add(new Token(TokenKind.Identifier, source[start..i], 0, Span()));
            }
            else if (char.IsDigit(c))
            {
                while (i < source.Length && char.IsDigit(source[i]))
                {
                    Advance(1);
                }
                // "2..3" is a range, not "2." followed by ".3".
                if (i + 1 < source.Length && source[i] == '.' && char.IsDigit(source[i + 1]))
                {
                    Advance(1);
                    while (i < source.Length && char.IsDigit(source[i]))
                    {
                        Advance(1);
                    }
                }
                string text = source[start..i];
                tokens.Add(new Token(TokenKind.Number, text, double.Parse(text, CultureInfo.InvariantCulture), Span()));
            }
            else if (c == '"')
            {
                Advance(1);
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= source.Length || source[i] == '\n')
                    {
                        throw new LifeScriptException("Unterminated string.", new SourceSpan(startLine, startCol, 1));
                    }
                    if (source[i] == '"')
                    {
                        Advance(1);
                        break;
                    }
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        Advance(1);
                        sb.Append(source[i] switch { 'n' => '\n', 't' => '\t', var other => other });
                        Advance(1);
                        continue;
                    }
                    sb.Append(source[i]);
                    Advance(1);
                }
                tokens.Add(new Token(TokenKind.String, sb.ToString(), 0, Span()));
            }
            else if (c == '#' && i + 1 < source.Length && Uri.IsHexDigit(source[i + 1]))
            {
                Advance(1);
                while (i < source.Length && Uri.IsHexDigit(source[i]))
                {
                    Advance(1);
                }
                tokens.Add(new Token(TokenKind.HexColor, source[start..i], 0, Span()));
            }
            else
            {
                string? symbol = Symbols.FirstOrDefault(s => string.CompareOrdinal(source, i, s, 0, s.Length) == 0);
                if (symbol == null)
                {
                    throw new LifeScriptException($"Unexpected character '{c}'.", new SourceSpan(startLine, startCol, 1));
                }
                Advance(symbol.Length);
                tokens.Add(new Token(TokenKind.Symbol, symbol, 0, Span()));
            }
        }
        tokens.Add(new Token(TokenKind.End, "", 0, new SourceSpan(line, col, 0) { Offset = i }));
        return tokens;
    }
}
