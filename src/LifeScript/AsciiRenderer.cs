using System.Text;

namespace LifeScript;

/// <summary>Text rendering for the CLI and tests. 3D worlds show one z-slice.</summary>
public static class AsciiRenderer
{
    public static char CharFor(CompiledProgram program, byte id)
    {
        if (id == program.DefaultId)
        {
            return '.';
        }
        int state = program.StateOf[id];
        if (program.Types.Length == 1)
        {
            return state == 1 ? '#' : state < 10 ? (char)('0' + state) : (char)('a' + state - 10);
        }
        char letter = program.Types[program.TypeOf[id]].Name[0];
        return state == 0 ? char.ToLowerInvariant(letter) : char.ToUpperInvariant(letter);
    }

    public static string Render(CompiledProgram program, ReadOnlySpan<byte> ids, int? z = null)
    {
        int width = program.Size[0];
        int height = program.Size.Length > 1 ? program.Size[1] : 1;
        int slice = program.Size.Length > 2 ? z ?? program.Size[2] / 2 : 0;
        var sb = new StringBuilder();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                sb.Append(CharFor(program, ids[x + y * width + slice * width * height]));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
