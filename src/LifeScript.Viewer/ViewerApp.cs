using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace LifeScript.Viewer;

/// <summary>
/// The testbed window: script editor on the left, the running automaton in the middle,
/// controls and the cell inspector on the right.
/// </summary>
internal sealed class ViewerApp
{
    private const float RightWidth = 380;
    private static readonly Vector3 Background = new(0.05f, 0.06f, 0.08f);
    private static readonly Vector4 ErrorColor = new(1f, 0.45f, 0.4f, 1);
    private static readonly Vector4 WarningColor = new(1f, 0.8f, 0.35f, 1);
    private static readonly Vector4 DimColor = new(0.6f, 0.65f, 0.7f, 1);

    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiController _imgui = null!;
    private GridRenderer _grid = null!;
    private VoxelRenderer _voxels = null!;

    // Script
    private string _path;
    private string _text = "";
    private string _savedText = "";
    private string _compiledText = "";
    private bool _live = true;
    private readonly Stopwatch _sinceEdit = Stopwatch.StartNew();
    private List<Diagnostic> _diagnostics = new();
    private string? _runtimeError;
    private FileSystemWatcher? _watcher;
    private volatile bool _fileChanged;
    private bool _externalChange;
    private readonly string[] _samples;

    // Simulation
    private CompiledProgram? _program;
    private Simulation? _sim;
    private bool _playing = true;
    private float _speed = 10;
    private bool _unlimited;
    private double _accumulator;
    private bool _needsUpload = true;
    private int? _selected;
    private byte _brush;

    // View
    private Vector2 _offset;
    private float _scale = 6;
    private bool _fitPending = true;
    private bool _sliceView;
    private int _slice;
    private float _yaw = 0.7f, _pitch = 0.45f, _distance = 80;

    // Mouse
    private Vector2 _mouse;
    private bool _painting;
    private (int X, int Y)? _lastPaint;
    private Vector2? _rightDown;
    private bool _dragging;

    public ViewerApp(string path)
    {
        _path = Path.GetFullPath(path);
        _samples = FindSamples();
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1680, 980),
            Title = "lifescript sandbox",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = true,
        };
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += size => _gl.Viewport(size);
        _window.Closing += () =>
        {
            _watcher?.Dispose();
            _imgui.Dispose();
            _grid.Dispose();
            _voxels.Dispose();
        };
    }

    public void Run() => _window.Run();

    private bool Is3D => _program?.Size.Length == 3;

    private bool Flat => !Is3D || _sliceView;

    #region Loading and compiling

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        var font = new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
            "/usr/share/fonts/truetype/noto/NotoMono-Regular.ttf",
            "C:/Windows/Fonts/consola.ttf",
            "/System/Library/Fonts/Menlo.ttc",
        }.FirstOrDefault(File.Exists);
        _imgui = font != null
            ? new ImGuiController(_gl, _window, _input, new ImGuiFontConfig(font, 16))
            : new ImGuiController(_gl, _window, _input);
        if (font == null)
        {
            ImGui.GetIO().FontGlobalScale = 1.3f;
        }
        var style = ImGui.GetStyle();
        style.WindowRounding = 4;
        style.FrameRounding = 3;
        _grid = new GridRenderer(_gl);
        _voxels = new VoxelRenderer(_gl);

        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }
        Load(_path);
    }

    private static string[] FindSamples()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var samples = Path.Combine(dir.FullName, "samples");
                if (Directory.Exists(samples) && Directory.GetFiles(samples, "*.ls").Length > 0)
                {
                    return Directory.GetFiles(samples, "*.ls").Order().ToArray();
                }
            }
        }
        return [];
    }

    private void Load(string path)
    {
        _path = Path.GetFullPath(path);
        _text = File.Exists(_path) ? File.ReadAllText(_path) : "";
        _savedText = _text;
        _externalChange = false;
        _window.Title = $"lifescript sandbox — {Path.GetFileName(_path)}";
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _fileChanged = true;
        _watcher.Created += (_, _) => _fileChanged = true;
        _watcher.Renamed += (_, _) => _fileChanged = true;
        _fitPending = true;
        _selected = null;
        Compile();
    }

    private void Compile()
    {
        _compiledText = _text;
        try
        {
            var program = Compiler.Compile(_text);
            var sim = new Simulation(program);
            bool sameShape = _program != null && _program.Size.SequenceEqual(program.Size);
            _program = program;
            _sim = sim;
            _diagnostics = program.Warnings.ToList();
            _runtimeError = null;
            _needsUpload = true;
            _grid.UploadPalette(program.Palette);
            if (!sameShape)
            {
                _fitPending = true;
                _selected = null;
                _slice = program.Size.Length == 3 ? program.Size[2] / 2 : 0;
                _distance = program.Size.Max() * 2.2f;
            }
            if (program.Interval > 0 && !sameShape)
            {
                _speed = (float)Math.Clamp(1 / program.Interval, 1, 240);
            }
            if (_brush >= program.IdCount || _brush == program.DefaultId || !sameShape)
            {
                // Paint with the first type that isn't the background, alive.
                var paintType = program.Types.FirstOrDefault(t => t.Index != program.DefaultType) ?? program.Types[program.DefaultType];
                _brush = program.IdOf(paintType.Index, 1);
            }
        }
        catch (LifeScriptException e)
        {
            _diagnostics = e.Diagnostics.ToList();
        }
        catch (LifeScriptRuntimeException e)
        {
            _diagnostics = [new Diagnostic(Severity.Error, e.Message, e.Span)];
        }
    }

    private void Save()
    {
        File.WriteAllText(_path, _text);
        _savedText = _text;
        _externalChange = false;
        if (_text != _compiledText)
        {
            Compile();
        }
    }

    #endregion

    #region Update

    private void OnUpdate(double dt)
    {
        if (_fileChanged)
        {
            _fileChanged = false;
            try
            {
                string onDisk = File.ReadAllText(_path);
                if (onDisk != _savedText)
                {
                    if (_text == _savedText)
                    {
                        _text = _savedText = onDisk;
                        Compile();
                    }
                    else
                    {
                        _externalChange = true;
                    }
                }
            }
            catch (IOException)
            {
                // The editor that saved it may still hold the file; the next event retries.
                _fileChanged = true;
            }
        }

        if (_live && _text != _compiledText && _sinceEdit.ElapsedMilliseconds > 400)
        {
            Compile();
        }

        if (_sim == null || !_playing)
        {
            return;
        }
        try
        {
            if (_unlimited)
            {
                var budget = Stopwatch.StartNew();
                do
                {
                    StepOnce();
                } while (_playing && budget.ElapsedMilliseconds < 12);
            }
            else
            {
                _accumulator = Math.Min(_accumulator + dt * _speed, 60);
                while (_playing && _accumulator >= 1)
                {
                    _accumulator--;
                    StepOnce();
                }
            }
        }
        catch (LifeScriptRuntimeException e)
        {
            _runtimeError = e.Message;
            _playing = false;
        }
    }

    private void StepOnce()
    {
        _sim!.Step();
        _needsUpload = true;
        if (_program!.Generations is long limit && _sim.Generation >= limit)
        {
            _playing = false;
        }
    }

    #endregion

    #region Render

    private (float Left, float Width, float Height) Area()
    {
        var size = _window.FramebufferSize;
        float left = LeftWidth();
        return (left, Math.Max(1, size.X - left - RightWidth), size.Y);
    }

    private float LeftWidth() => Math.Clamp(_window.FramebufferSize.X * 0.36f, 420, 720);

    private void OnRender(double dt)
    {
        _imgui.Update((float)dt);
        var size = _window.FramebufferSize;
        _gl.Viewport(size);
        _gl.ClearColor(Background.X, Background.Y, Background.Z, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_sim != null && _program != null)
        {
            DrawWorld();
        }
        DrawScriptWindow();
        DrawRunWindow();
        DrawInspectorWindow();
        _imgui.Render();
    }

    private void DrawWorld()
    {
        var (left, width, height) = Area();
        var program = _program!;
        int w = program.Size[0];
        int h = program.Size.Length > 1 ? program.Size[1] : 1;
        if (Flat)
        {
            if (_fitPending)
            {
                _scale = MathF.Max(1, MathF.Min(width / w, height / h) * 0.94f);
                _offset = new Vector2(left + (width - w * _scale) / 2, (height - h * _scale) / 2);
                _fitPending = false;
            }
            if (_needsUpload)
            {
                var ids = _sim!.Ids;
                if (Is3D)
                {
                    _slice = Math.Clamp(_slice, 0, program.Size[2] - 1);
                    ids = ids.Slice(_slice * w * h, w * h);
                }
                _grid.UploadState(ids, w, h);
                _needsUpload = false;
            }
            (int, int)? selected = null;
            if (_selected is int s)
            {
                var p = _sim!.PositionOf(s);
                if (!Is3D || p[2] == _slice)
                {
                    selected = (p[0], p.Length > 1 ? p[1] : 0);
                }
            }
            _grid.Draw(_offset, _scale, new Vector2(_window.FramebufferSize.X, height), selected, Background);
        }
        else
        {
            _fitPending = false;
            if (_needsUpload)
            {
                _voxels.Upload(_sim!.Ids, program.Size, program.DefaultId, program.Palette);
                _needsUpload = false;
            }
            _gl.Viewport((int)left, 0, (uint)width, (uint)height);
            var eye = new Vector3(
                MathF.Cos(_pitch) * MathF.Sin(_yaw),
                MathF.Sin(_pitch),
                MathF.Cos(_pitch) * MathF.Cos(_yaw)
            ) * _distance;
            var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, width / height, 0.1f, _distance * 4);
            var center = new Vector3(program.Size[0], program.Size[1], program.Size[2]) / 2 - new Vector3(0.5f);
            _voxels.Draw(view * projection, center);
            _gl.Viewport(_window.FramebufferSize);
        }
    }

    private void DrawScriptWindow()
    {
        var size = _window.FramebufferSize;
        float width = LeftWidth();
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, size.Y), ImGuiCond.Always);
        ImGui.Begin("Script", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##samples", Path.GetFileName(_path)))
        {
            foreach (var sample in _samples)
            {
                if (ImGui.Selectable(Path.GetFileName(sample), sample == _path))
                {
                    Load(sample);
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        bool dirty = _text != _savedText;
        if (ImGui.Button(dirty ? "Save*" : "Save"))
        {
            Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert") && dirty)
        {
            _text = _savedText;
            Compile();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Live", ref _live);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Recompile 0.4 s after you stop typing. Otherwise use Ctrl+Enter.");
        }
        if (!_live)
        {
            ImGui.SameLine();
            if (ImGui.Button("Compile"))
            {
                Compile();
            }
        }
        ImGui.TextColored(DimColor, _path);

        if (_externalChange)
        {
            ImGui.TextColored(WarningColor, "The file changed on disk.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Load it"))
            {
                Load(_path);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Keep mine"))
            {
                _externalChange = false;
            }
        }

        float diagnosticsHeight = Math.Clamp(_diagnostics.Count + (_runtimeError != null ? 1 : 0), 1, 6) * ImGui.GetTextLineHeightWithSpacing() + 12;
        var editorSize = new Vector2(-1, ImGui.GetContentRegionAvail().Y - diagnosticsHeight);
        if (ImGui.InputTextMultiline("##source", ref _text, 1 << 20, editorSize, ImGuiInputTextFlags.AllowTabInput))
        {
            _sinceEdit.Restart();
        }

        if (_diagnostics.Count == 0 && _runtimeError == null)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.5f, 1), _text == _compiledText ? "Compiled." : "Editing...");
        }
        foreach (var d in _diagnostics)
        {
            ImGui.TextColored(d.Severity == Severity.Error ? ErrorColor : WarningColor, d.ToString());
        }
        if (_runtimeError != null)
        {
            ImGui.TextColored(ErrorColor, "runtime: " + _runtimeError);
        }
        ImGui.End();
    }

    private void DrawRunWindow()
    {
        var size = _window.FramebufferSize;
        ImGui.SetNextWindowPos(new Vector2(size.X - RightWidth, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(RightWidth, size.Y * 0.62f), ImGuiCond.Always);
        ImGui.Begin("Run", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
        if (_sim == null || _program == null)
        {
            ImGui.TextWrapped("Fix the errors in the script to start.");
            ImGui.End();
            return;
        }
        var program = _program;
        ImGui.Text(program.Title);
        ImGui.TextColored(DimColor, $"{string.Join(" × ", program.Size)} · seed {program.Seed} · wrap {string.Join("", program.Wrap.Select(w => w ? "y" : "n"))}");
        ImGui.Separator();

        if (ImGui.Button(_playing ? "Pause" : "Play", new Vector2(70, 0)))
        {
            _playing = !_playing;
        }
        ImGui.SameLine();
        if (ImGui.Button("Step"))
        {
            SafeStep();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset"))
        {
            ResetSimulation();
        }
        ImGui.SameLine();
        ImGui.Text($"gen {_sim.Generation}");

        ImGui.Checkbox("Max speed", ref _unlimited);
        if (!_unlimited)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            ImGui.SliderFloat("##speed", ref _speed, 1, 240, "%.0f gen/s", ImGuiSliderFlags.Logarithmic);
        }
        if (Is3D)
        {
            ImGui.Checkbox("Slice view", ref _sliceView);
            if (_sliceView)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##slice", ref _slice, 0, program.Size[2] - 1, "z = %d"))
                {
                    _needsUpload = true;
                }
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextColored(DimColor, $"{_voxels.Count:N0} voxels");
            }
        }
        ImGui.TextColored(DimColor, $"{_sim.LastStepMs:0.00} ms/step · {_sim.LastActiveCells:N0} of {program.CellCount:N0} cells evaluated");
        ImGui.TextColored(DimColor, $"{program.UpdateMode}");
        if (program.UpdateModeReasons.Count > 0 && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(string.Join("\n", program.UpdateModeReasons));
        }

        ImGui.SeparatorText("Types");
        foreach (var type in program.Types)
        {
            ImGui.Text(type.Name + (type.Index == program.DefaultType ? " (default)" : ""));
            ImGui.SameLine();
            if (type.Lut != null)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.5f, 1), $"lookup table, {type.Lut.Entries} entries");
            }
            else
            {
                ImGui.TextColored(WarningColor, "interpreter");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(string.Join("\n", type.LutBlockers));
                }
            }
        }

        ImGui.SeparatorText("Cells (click a color to paint with it)");
        var census = _sim.Census();
        foreach (var type in program.Types)
        {
            int total = 0;
            for (int s = 0; s < type.StateCount; s++)
            {
                int id = type.BaseId + s;
                total += census[id];
                uint c = program.Palette[id];
                var color = new Vector4((c & 0xFF) / 255f, (c >> 8 & 0xFF) / 255f, (c >> 16 & 0xFF) / 255f, 1);
                if (id == _brush)
                {
                    // Outline the brush swatch.
                    var min = ImGui.GetCursorScreenPos() - new Vector2(2);
                    ImGui.GetWindowDrawList().AddRect(min, min + new Vector2(22), 0xFF33D9FF, 2, ImDrawFlags.None, 2);
                }
                if (ImGui.ColorButton($"##id{id}", color, ImGuiColorEditFlags.NoTooltip, new Vector2(18, 18)))
                {
                    _brush = (byte)id;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{type.Name} state {s}: {census[id]:N0} cells");
                }
                ImGui.SameLine();
            }
            ImGui.Text($"{type.Name}: {total:N0}");
        }
        ImGui.TextColored(DimColor, $"brush: {program.Types[program.TypeOf[_brush]].Name} state {program.StateOf[_brush]}");

        if (program.World != null)
        {
            ImGui.SeparatorText("World log");
            foreach (var line in _sim.WorldLog.TakeLast(8))
            {
                ImGui.TextWrapped(line);
            }
        }

        ImGui.SeparatorText("Controls");
        ImGui.TextColored(DimColor, Flat
            ? "Space play · S step · R reset · F fit\nLeft drag: paint · Right click: inspect\nRight drag: pan · Wheel: zoom · Ctrl+S save"
            : "Space play · S step · R reset\nDrag: orbit · Wheel: zoom · Ctrl+S save");
        ImGui.End();
    }

    private void DrawInspectorWindow()
    {
        var size = _window.FramebufferSize;
        ImGui.SetNextWindowPos(new Vector2(size.X - RightWidth, size.Y * 0.62f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(RightWidth, size.Y * 0.38f), ImGuiCond.Always);
        ImGui.Begin("Inspector", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
        if (_sim == null || _selected is not int index || index >= _sim.Ids.Length)
        {
            ImGui.TextWrapped("Right-click a cell to see what its rule sees: neighbor sums, locals, print output and its next state.");
            ImGui.End();
            return;
        }
        var info = _sim.Inspect(index);
        ImGui.Text($"({string.Join(", ", info.Position)})  {info.Type}  state {info.State}  age {info.Age}");
        ImGui.Text($"next: {info.Next}");
        if (info.Error != null)
        {
            ImGui.TextColored(ErrorColor, info.Error);
        }
        if (info.Queries.Count > 0)
        {
            ImGui.SeparatorText("Neighbors");
            foreach (var (query, value) in info.Queries)
            {
                ImGui.Text($"{query} = {PrintStmt.Format(value)}");
            }
        }
        if (info.Locals.Count > 0)
        {
            ImGui.SeparatorText("Locals");
            foreach (var (name, value) in info.Locals)
            {
                ImGui.Text($"{name} = {PrintStmt.Format(value)}");
            }
        }
        if (info.Prints.Count > 0)
        {
            ImGui.SeparatorText("print");
            foreach (var line in info.Prints)
            {
                ImGui.TextWrapped(line);
            }
        }
        ImGui.End();
    }

    #endregion

    #region Input

    private void SafeStep()
    {
        if (_sim == null)
        {
            return;
        }
        try
        {
            StepOnce();
        }
        catch (LifeScriptRuntimeException e)
        {
            _runtimeError = e.Message;
            _playing = false;
        }
    }

    private void ResetSimulation()
    {
        if (_sim == null)
        {
            return;
        }
        try
        {
            _sim.Reset();
            _runtimeError = null;
        }
        catch (LifeScriptRuntimeException e)
        {
            _runtimeError = e.Message;
        }
        _needsUpload = true;
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        if (ctrl && key == Key.S)
        {
            Save();
            return;
        }
        if (ctrl && key == Key.Enter)
        {
            Compile();
            return;
        }
        if (ImGui.GetIO().WantCaptureKeyboard)
        {
            return;
        }
        switch (key)
        {
            case Key.Space:
                _playing = !_playing;
                break;
            case Key.S:
            case Key.Right:
                SafeStep();
                break;
            case Key.R:
                ResetSimulation();
                break;
            case Key.F:
                _fitPending = true;
                break;
            case Key.Escape:
                _selected = null;
                break;
        }
    }

    private bool OverWorld(Vector2 position)
    {
        var (left, width, _) = Area();
        return !ImGui.GetIO().WantCaptureMouse && position.X >= left && position.X < left + width;
    }

    private (int X, int Y) CellAt(Vector2 position)
    {
        var cell = (position - _offset) / _scale;
        return ((int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y));
    }

    private int IndexOf((int X, int Y) cell)
    {
        var program = _program!;
        int w = program.Size[0], h = program.Size.Length > 1 ? program.Size[1] : 1;
        if (cell.X < 0 || cell.Y < 0 || cell.X >= w || cell.Y >= h)
        {
            return -1;
        }
        return cell.X + cell.Y * w + (Is3D ? _slice * w * h : 0);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        _mouse = mouse.Position;
        if (_sim == null || !OverWorld(_mouse))
        {
            return;
        }
        if (button == MouseButton.Left && Flat)
        {
            _painting = true;
            _lastPaint = null;
            PaintTo(CellAt(_mouse));
        }
        else if (button == MouseButton.Left || button == MouseButton.Right || button == MouseButton.Middle)
        {
            _rightDown = _mouse;
            _dragging = button == MouseButton.Middle;
        }
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            _painting = false;
        }
        if (_rightDown != null && !_dragging && button == MouseButton.Right && Flat && _sim != null)
        {
            int index = IndexOf(CellAt(mouse.Position));
            _selected = index >= 0 ? index : null;
        }
        _rightDown = null;
        _dragging = false;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        var delta = position - _mouse;
        _mouse = position;
        if (_painting)
        {
            PaintTo(CellAt(position));
            return;
        }
        if (_rightDown is Vector2 down)
        {
            if (!_dragging && Vector2.Distance(down, position) > 3)
            {
                _dragging = true;
            }
            if (_dragging)
            {
                if (Flat)
                {
                    _offset += delta;
                }
                else
                {
                    _yaw -= delta.X * 0.008f;
                    _pitch = Math.Clamp(_pitch + delta.Y * 0.008f, -1.5f, 1.5f);
                }
            }
        }
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_sim == null || !OverWorld(mouse.Position))
        {
            return;
        }
        float factor = MathF.Pow(1.15f, wheel.Y);
        if (Flat)
        {
            float scale = Math.Clamp(_scale * factor, 0.5f, 80);
            // Zoom around the cursor.
            _offset = mouse.Position - (mouse.Position - _offset) * (scale / _scale);
            _scale = scale;
        }
        else
        {
            _distance = Math.Clamp(_distance / factor, 2, 2000);
        }
    }

    /// <summary>Paints a line of cells from the last painted cell, so fast strokes don't leave gaps.</summary>
    private void PaintTo((int X, int Y) cell)
    {
        var from = _lastPaint ?? cell;
        var cells = new List<int>();
        int dx = Math.Abs(cell.X - from.X), dy = -Math.Abs(cell.Y - from.Y);
        int sx = from.X < cell.X ? 1 : -1, sy = from.Y < cell.Y ? 1 : -1, err = dx + dy;
        var (x, y) = from;
        while (true)
        {
            int index = IndexOf((x, y));
            if (index >= 0)
            {
                cells.Add(index);
            }
            if (x == cell.X && y == cell.Y)
            {
                break;
            }
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }
        _lastPaint = cell;
        _sim!.Paint(cells, _brush);
        _needsUpload = true;
    }

    #endregion
}
