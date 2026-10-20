using System.Numerics;
using Silk.NET.OpenGL;

namespace LifeScript.Viewer;

internal static class Shaders
{
    public static uint Compile(GL gl, string vertex, string fragment)
    {
        uint Stage(ShaderType type, string source)
        {
            uint shader = gl.CreateShader(type);
            gl.ShaderSource(shader, source);
            gl.CompileShader(shader);
            string log = gl.GetShaderInfoLog(shader);
            if (!string.IsNullOrWhiteSpace(log))
            {
                throw new InvalidOperationException($"{type}: {log}");
            }
            return shader;
        }
        uint vs = Stage(ShaderType.VertexShader, vertex);
        uint fs = Stage(ShaderType.FragmentShader, fragment);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        string linkLog = gl.GetProgramInfoLog(program);
        if (!string.IsNullOrWhiteSpace(linkLog))
        {
            throw new InvalidOperationException(linkLog);
        }
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return program;
    }
}

/// <summary>
/// Draws a 2D grid (or one z-slice of a 3D grid) with a full-screen triangle: cell ids live in
/// an integer texture and the fragment shader looks colors up in a 256-entry palette.
/// </summary>
internal sealed unsafe class GridRenderer : IDisposable
{
    private const string Vertex = """
        #version 330 core
        const vec2 corners[3] = vec2[3](vec2(-1, -1), vec2(3, -1), vec2(-1, 3));
        void main() { gl_Position = vec4(corners[gl_VertexID], 0, 1); }
        """;

    private const string Fragment = """
        #version 330 core
        uniform usampler2D uState;
        uniform sampler2D uPalette;
        uniform vec2 uOffset;
        uniform float uScale;
        uniform ivec2 uSize;
        uniform float uViewportHeight;
        uniform ivec2 uSelected;
        uniform vec3 uBackground;
        out vec4 color;
        void main() {
            vec2 pixel = vec2(gl_FragCoord.x, uViewportHeight - gl_FragCoord.y);
            vec2 cellf = (pixel - uOffset) / uScale;
            ivec2 cell = ivec2(floor(cellf));
            if (cell.x < 0 || cell.y < 0 || cell.x >= uSize.x || cell.y >= uSize.y) {
                color = vec4(uBackground, 1);
                return;
            }
            uint id = texelFetch(uState, cell, 0).r;
            vec4 c = texelFetch(uPalette, ivec2(int(id), 0), 0);
            vec2 f = fract(cellf) * uScale;
            if (uScale >= 8.0 && (f.x < 1.0 || f.y < 1.0)) c.rgb *= 0.8;
            if (cell == uSelected) {
                float edge = min(min(f.x, uScale - f.x), min(f.y, uScale - f.y));
                if (edge < max(1.5, uScale * 0.12)) c = vec4(1.0, 0.85, 0.2, 1.0);
            }
            color = c;
        }
        """;

    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _stateTexture;
    private readonly uint _paletteTexture;
    private int _width = -1, _height = -1;

    public GridRenderer(GL gl)
    {
        _gl = gl;
        _program = Shaders.Compile(gl, Vertex, Fragment);
        _vao = gl.GenVertexArray();
        _stateTexture = gl.GenTexture();
        _paletteTexture = gl.GenTexture();
        foreach (var texture in new[] { _stateTexture, _paletteTexture })
        {
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
    }

    public void UploadPalette(uint[] palette)
    {
        _gl.BindTexture(TextureTarget.Texture2D, _paletteTexture);
        fixed (uint* p = palette)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 256, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
    }

    public void UploadState(ReadOnlySpan<byte> ids, int width, int height)
    {
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        _gl.BindTexture(TextureTarget.Texture2D, _stateTexture);
        fixed (byte* p = ids)
        {
            if (width != _width || height != _height)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8ui, (uint)width, (uint)height, 0, PixelFormat.RedInteger, PixelType.UnsignedByte, p);
                (_width, _height) = (width, height);
            }
            else
            {
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height, PixelFormat.RedInteger, PixelType.UnsignedByte, p);
            }
        }
    }

    public void Draw(Vector2 offset, float scale, Vector2 viewport, (int X, int Y)? selected, Vector3 background)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.UseProgram(_program);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _stateTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _paletteTexture);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "uState"), 0);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "uPalette"), 1);
        _gl.Uniform2(_gl.GetUniformLocation(_program, "uOffset"), offset.X, offset.Y);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "uScale"), scale);
        _gl.Uniform2(_gl.GetUniformLocation(_program, "uSize"), _width, _height);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "uViewportHeight"), viewport.Y);
        _gl.Uniform2(_gl.GetUniformLocation(_program, "uSelected"), selected?.X ?? -1, selected?.Y ?? -1);
        _gl.Uniform3(_gl.GetUniformLocation(_program, "uBackground"), background.X, background.Y, background.Z);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_program);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTexture(_stateTexture);
        _gl.DeleteTexture(_paletteTexture);
    }
}

/// <summary>Draws a 3D grid as instanced cubes, one per non-default cell.</summary>
internal sealed unsafe class VoxelRenderer : IDisposable
{
    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aOffset;
        layout(location = 3) in vec4 aColor;
        uniform mat4 uViewProjection;
        uniform vec3 uCenter;
        out vec3 vNormal;
        out vec4 vColor;
        void main() {
            vNormal = aNormal;
            vColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition * 0.92 + aOffset - uCenter, 1.0);
        }
        """;

    private const string Fragment = """
        #version 330 core
        in vec3 vNormal;
        in vec4 vColor;
        out vec4 color;
        void main() {
            vec3 light = normalize(vec3(0.5, 0.8, 0.35));
            float diffuse = max(dot(normalize(vNormal), light), 0.0);
            color = vec4(vColor.rgb * (0.35 + 0.65 * diffuse), 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _meshBuffer;
    private readonly uint _instanceBuffer;
    private float[] _instances = new float[4096];
    private int _count;

    public VoxelRenderer(GL gl)
    {
        _gl = gl;
        _program = Shaders.Compile(gl, Vertex, Fragment);
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        var mesh = CubeMesh();
        _meshBuffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshBuffer);
        fixed (float* p = mesh)
        {
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(mesh.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));

        _instanceBuffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceBuffer);
        // Per instance: offset (3 floats) + color (4 floats).
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), (void*)0);
        gl.VertexAttribDivisor(2, 1);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.VertexAttribDivisor(3, 1);
        gl.BindVertexArray(0);
    }

    public int Count => _count;

    public void Upload(ReadOnlySpan<byte> ids, int[] size, byte defaultId, uint[] palette)
    {
        _count = 0;
        int width = size[0], height = size[1];
        for (int i = 0; i < ids.Length; i++)
        {
            byte id = ids[i];
            if (id == defaultId)
            {
                continue;
            }
            if ((_count + 1) * 7 > _instances.Length)
            {
                Array.Resize(ref _instances, _instances.Length * 2);
            }
            int o = _count * 7;
            _instances[o] = i % width;
            _instances[o + 1] = i / width % height;
            _instances[o + 2] = i / (width * height);
            uint c = palette[id];
            _instances[o + 3] = (c & 0xFF) / 255f;
            _instances[o + 4] = (c >> 8 & 0xFF) / 255f;
            _instances[o + 5] = (c >> 16 & 0xFF) / 255f;
            _instances[o + 6] = 1;
            _count++;
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceBuffer);
        fixed (float* p = _instances)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_count * 7 * sizeof(float)), p, BufferUsageARB.StreamDraw);
        }
    }

    public void Draw(Matrix4x4 viewProjection, Vector3 center)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.UseProgram(_program);
        var m = viewProjection;
        _gl.UniformMatrix4(_gl.GetUniformLocation(_program, "uViewProjection"), 1, false, (float*)&m);
        _gl.Uniform3(_gl.GetUniformLocation(_program, "uCenter"), center.X, center.Y, center.Z);
        _gl.BindVertexArray(_vao);
        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 36, (uint)_count);
        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.DepthTest);
    }

    private static float[] CubeMesh()
    {
        var data = new List<float>();
        void Face(Vector3 n, Vector3 u, Vector3 v)
        {
            Vector3 c = n * 0.5f;
            Vector3[] corners = [c - u * 0.5f - v * 0.5f, c + u * 0.5f - v * 0.5f, c + u * 0.5f + v * 0.5f, c - u * 0.5f + v * 0.5f];
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
            {
                data.AddRange([corners[i].X, corners[i].Y, corners[i].Z, n.X, n.Y, n.Z]);
            }
        }
        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX);
        return data.ToArray();
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_program);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_meshBuffer);
        _gl.DeleteBuffer(_instanceBuffer);
    }
}
