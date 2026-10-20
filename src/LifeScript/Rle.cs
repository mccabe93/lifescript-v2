namespace LifeScript;

/// <summary>
/// Golly run-length encoded patterns: <c>b</c>/<c>.</c> are empty, <c>o</c> is state 1,
/// <c>A</c>..<c>X</c> are states 1..24, <c>$</c> ends a row, <c>!</c> ends the pattern.
/// A leading header line (<c>x = 3, y = 3, rule = B3/S23</c>) and <c>#</c> comment lines are skipped.
/// </summary>
public static class Rle
{
    public readonly record struct RleCell(int X, int Y, int State);

    public static List<RleCell> Parse(string rle)
    {
        var body = string.Join(
            "",
            rle.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith('#') && !l.StartsWith("x ") && !l.StartsWith("x="))
        );
        var cells = new List<RleCell>();
        int x = 0, y = 0, run = 0;
        foreach (char c in body)
        {
            if (char.IsDigit(c))
            {
                run = run * 10 + (c - '0');
                continue;
            }
            int n = Math.Max(1, run);
            run = 0;
            switch (c)
            {
                case 'b':
                case '.':
                    x += n;
                    break;
                case 'o':
                    for (int i = 0; i < n; i++)
                    {
                        cells.Add(new RleCell(x++, y, 1));
                    }
                    break;
                case >= 'A' and <= 'X':
                    for (int i = 0; i < n; i++)
                    {
                        cells.Add(new RleCell(x++, y, c - 'A' + 1));
                    }
                    break;
                case '$':
                    y += n;
                    x = 0;
                    break;
                case '!':
                    return cells;
                case ' ':
                case '\t':
                case '\r':
                    break;
                default:
                    throw new FormatException($"'{c}' is not valid in an RLE pattern.");
            }
        }
        return cells;
    }
}
