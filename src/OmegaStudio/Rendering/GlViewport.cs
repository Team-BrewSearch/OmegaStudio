using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Omega.Plugins;
using static Avalonia.OpenGL.GlConsts;
namespace OmegaStudio.Rendering;
public enum ShadingMode
{
    Textured,
    Shaded,
    Wireframe,
    Normals,
    Uv,
}
internal static class Gl
{
    public const int Lines = 0x0001;
    public const int UnsignedInt = 0x1405;
    public const int Repeat = 0x2901;
    public const int DynamicDraw = 0x88E8;
    public static T? Resolve<T>(GlInterface gl, string name)
        where T : Delegate
    {
        IntPtr entry = gl.GetProcAddress(name);
        return entry == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(entry);
    }
}
public class GlViewport : OpenGlControlBase
{
    public static readonly StyledProperty<SceneGeometry?> SceneProperty =
        AvaloniaProperty.Register<GlViewport, SceneGeometry?>(nameof(Scene));
    public static readonly StyledProperty<ShadingMode> ShadingProperty =
        AvaloniaProperty.Register<GlViewport, ShadingMode>(nameof(Shading));
    private float _yaw = 0.6f;
    private float _pitch = 0.25f;
    private float _distance = 300f;
    private float _targetDistance = 300f;
    private bool _zoomAnimating;
    private Vector3 _target = new(0, 80, 0);
    private bool _geometryDirty = true;
    private bool _poseDirty;
    private GlProgram? _model;
    private GlProgram? _flat;
    private GlProgram? _vertexColor;
    private GlUniforms? _uniforms;
    private BufferSubDataProc? _bufferSubData;
    private int _vao, _poseVbo, _surfaceVbo, _triangleIbo, _lineIbo, _white;
    private int _uMvp, _uView, _uMode, _uHasTex, _uTex, _uAlphaClip, _uVertexLit;
    private int _gridVao, _gridVbo, _gridMvp, _gridVertexCount;
    private int _gridR, _gridG, _gridB;
    private int _axisVao, _axisVbo, _axisMvp, _axisVertexCount;
    private readonly List<int> _textures = [];
    private MeshBuffer? _buffer;
    private bool _ready;
    private SceneGeometry? _framedFor;
    private bool _linesUploaded;
    private float[]? _posePositions;
    private float[]? _poseNormals;
    private (float R, float G, float B) _background = (0.1f, 0.1f, 0.1f);
    private (float R, float G, float B) _grid = (0.5f, 0.5f, 0.5f);
    static GlViewport()
    {
        _ = ShadingProperty.Changed.AddClassHandler<GlViewport>((v, _) => v.RequestNextFrameRendering());
        _ = SceneProperty.Changed.AddClassHandler<GlViewport>((v, _) => v.InvalidateGeometry());
    }
    public GlViewport()
    {
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += OnThemeChanged;
    }
    public SceneGeometry? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }
    public ShadingMode Shading
    {
        get => GetValue(ShadingProperty);
        set => SetValue(ShadingProperty, value);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReadThemeColours();
        if (_ready)
        {
            InvalidateGeometry();
        }
    }
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ReadThemeColours();
        RequestNextFrameRendering();
    }
    private void ReadThemeColours()
    {
        _background = Resolve("SurfaceWell", (0.11f, 0.11f, 0.11f));
        _grid = Resolve("ViewportGrid", (0.5f, 0.5f, 0.5f));
        (float, float, float) Resolve(string key, (float, float, float) fallback)
        {
            return this.TryFindResource(key, ActualThemeVariant, out object? value)
                && value is ISolidColorBrush brush
                    ? (brush.Color.R / 255f, brush.Color.G / 255f, brush.Color.B / 255f)
                    : fallback;
        }
    }
    public void Orbit(double dx, double dy)
    {
        _yaw -= (float)dx * 0.01f;
        _pitch = Math.Clamp(_pitch + ((float)dy * 0.01f), -1.5f, 1.5f);
        RequestNextFrameRendering();
    }
    public void Pan(double dx, double dy)
    {
        float scale = _distance * 0.0015f;
        Vector3 right = new(MathF.Cos(_yaw), 0, -MathF.Sin(_yaw));
        _target += (right * (float)-dx * scale) + (Vector3.UnitY * (float)dy * scale);
        RequestNextFrameRendering();
    }
    public void Zoom(float delta)
    {
        _targetDistance = Math.Clamp(_targetDistance * (1f - (delta * 0.08f)), 1f, 100000f);
        _zoomAnimating = true;
        RequestNextFrameRendering();
    }
    public void ResetCamera()
    {
        SceneGeometry? scene = Scene;
        if (scene is null || scene.Meshes.Count == 0)
        {
            return;
        }
        float minY = float.MaxValue, maxY = float.MinValue, extent = 1f;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                float y = mesh.Positions[(i * 3) + 1];
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
                extent = MathF.Max(extent, MathF.Abs(mesh.Positions[i * 3]));
                extent = MathF.Max(extent, MathF.Abs(mesh.Positions[(i * 3) + 2]));
            }
        }
        _target = new Vector3(0, (minY + maxY) * 0.5f, 0);
        _distance = _targetDistance = MathF.Max(maxY - minY, extent * 2f) * 1.6f;
        _yaw = 0.6f;
        _pitch = 0.15f;
        RequestNextFrameRendering();
    }
    public void SetAnimatedFrame(float[]? positions, float[]? normals)
    {
        _posePositions = positions;
        _poseNormals = normals;
        _poseDirty = positions is not null && normals is not null;
        RequestNextFrameRendering();
    }
    public void ClearAnimatedFrame()
    {
        if (_posePositions is null && _poseNormals is null)
        {
            return;
        }
        _posePositions = null;
        _poseNormals = null;
        _poseDirty = true;
        RequestNextFrameRendering();
    }
    public void InvalidateGeometry()
    {
        _geometryDirty = true;
        RequestNextFrameRendering();
    }
    private delegate void BufferSubDataProc(int target, IntPtr offset, IntPtr size, float[] data);
    protected override void OnOpenGlInit(GlInterface gl)
    {
        _uniforms = GlUniforms.Create(gl);
        _bufferSubData = Gl.Resolve<BufferSubDataProc>(gl, "glBufferSubData");
        string header = Shaders.Header(GlVersion);
        _model = GlProgram.Build(gl, header, Shaders.ModelVertex, Shaders.ModelFragment, "model");
        _uMvp = _model.Uniform(gl, "uMvp");
        _uView = _model.Uniform(gl, "uView");
        _uMode = _model.Uniform(gl, "uMode");
        _uHasTex = _model.Uniform(gl, "uHasTex");
        _uTex = _model.Uniform(gl, "uTex");
        _uAlphaClip = _model.Uniform(gl, "uAlphaClip");
        _uVertexLit = _model.Uniform(gl, "uVertexLit");
        _flat = GlProgram.Build(gl, header, Shaders.FlatVertex, Shaders.FlatFragment, "grid");
        _gridMvp = _flat.Uniform(gl, "uMvp");
        _gridR = _flat.Uniform(gl, "uR");
        _gridG = _flat.Uniform(gl, "uG");
        _gridB = _flat.Uniform(gl, "uB");
        _vertexColor = GlProgram.Build(
            gl, header, Shaders.VertexColorVertex, Shaders.VertexColorFragment, "axis");
        _axisMvp = _vertexColor.Uniform(gl, "uMvp");
        _vao = gl.GenVertexArray();
        _poseVbo = gl.GenBuffer();
        _surfaceVbo = gl.GenBuffer();
        _triangleIbo = gl.GenBuffer();
        _lineIbo = gl.GenBuffer();
        _white = MakeSolidTexture(gl, 0xFFFFFFFF);
        _gridVao = gl.GenVertexArray();
        _gridVbo = gl.GenBuffer();
        BuildGrid(gl);
        _axisVao = gl.GenVertexArray();
        _axisVbo = gl.GenBuffer();
        BuildAxes(gl);
        _ready = true;
        InvalidateGeometry();
    }
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (!_ready)
        {
            return;
        }
        ReleaseTextures(gl);
        gl.DeleteBuffer(_poseVbo);
        gl.DeleteBuffer(_surfaceVbo);
        gl.DeleteBuffer(_triangleIbo);
        gl.DeleteBuffer(_lineIbo);
        gl.DeleteBuffer(_gridVbo);
        gl.DeleteBuffer(_axisVbo);
        gl.DeleteVertexArray(_vao);
        gl.DeleteVertexArray(_gridVao);
        gl.DeleteVertexArray(_axisVao);
        gl.DeleteTexture(_white);
        _model?.Delete(gl);
        _flat?.Delete(gl);
        _vertexColor?.Delete(gl);
        _model = _flat = _vertexColor = null;
        _uniforms = null;
        _bufferSubData = null;
        _vao = _poseVbo = _surfaceVbo = _triangleIbo = _lineIbo = _white = 0;
        _uMvp = _uView = _uMode = _uHasTex = _uTex = 0;
        _gridVao = _gridVbo = _gridMvp = _gridVertexCount = 0;
        _gridR = _gridG = _gridB = 0;
        _axisVao = _axisVbo = _axisMvp = _axisVertexCount = 0;
        _buffer = null;
        _posePositions = null;
        _poseNormals = null;
        _poseDirty = false;
        _geometryDirty = true;
        _framedFor = null;
        _linesUploaded = false;
        _ready = false;
    }
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_uniforms is null)
        {
            return;
        }
        AnimateZoom();
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling));
        int h = Math.Max(1, (int)(Bounds.Height * scaling));
        gl.Viewport(0, 0, w, h);
        gl.ClearColor(_background.R, _background.G, _background.B, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        gl.Enable(GL_DEPTH_TEST);
        gl.DepthFunc(GL_LESS);
        gl.DepthMask(1);
        gl.Disable(GL_CULL_FACE);
        if (_geometryDirty)
        {
            UploadGeometry(gl);
            _geometryDirty = false;
        }
        else if (_poseDirty)
        {
            UploadPose(gl);
            _poseDirty = false;
        }
        Matrix4x4 view = Matrix4x4.CreateLookAt(EyePosition(), _target, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            0.9f, w / (float)h, MathF.Max(0.05f, _distance * 0.002f), (_distance * 20f) + 5000f);
        Matrix4x4 mvp = view * projection;
        DrawGrid(gl, mvp);
        DrawAxes(gl, mvp);
        DrawModel(gl, mvp, view);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }
    private void AnimateZoom()
    {
        if (!_zoomAnimating)
        {
            return;
        }
        _distance += (_targetDistance - _distance) * 0.25f;
        if (Math.Abs(_distance - _targetDistance) < 0.1f)
        {
            _distance = _targetDistance;
            _zoomAnimating = false;
        }
        else
        {
            RequestNextFrameRendering();
        }
    }
    private void DrawGrid(GlInterface gl, in Matrix4x4 mvp)
    {
        if (_gridVertexCount == 0 || _flat is null || _uniforms is null)
        {
            return;
        }
        gl.DepthMask(0);
        _flat.Use(gl);
        _uniforms.Matrix(_gridMvp, mvp);
        gl.Uniform1f(_gridR, _grid.R);
        gl.Uniform1f(_gridG, _grid.G);
        gl.Uniform1f(_gridB, _grid.B);
        gl.BindVertexArray(_gridVao);
        gl.DrawArrays(Gl.Lines, 0, _gridVertexCount);
        gl.DepthMask(1);
    }
    private void DrawAxes(GlInterface gl, in Matrix4x4 mvp)
    {
        if (_axisVertexCount == 0 || _vertexColor is null || _uniforms is null)
        {
            return;
        }
        _vertexColor.Use(gl);
        _uniforms.Matrix(_axisMvp, mvp);
        gl.BindVertexArray(_axisVao);
        gl.DrawArrays(Gl.Lines, 0, _axisVertexCount);
    }
    private void DrawModel(GlInterface gl, in Matrix4x4 mvp, in Matrix4x4 view)
    {
        if (_buffer is null || _buffer.IndexCount == 0 || _model is null || _uniforms is null)
        {
            return;
        }
        _model.Use(gl);
        _uniforms.Matrix(_uMvp, mvp);
        _uniforms.Matrix(_uView, view);
        gl.Uniform1i(_uTex, 0);
        gl.BindVertexArray(_vao);
        if (Shading == ShadingMode.Wireframe)
        {
            gl.Uniform1i(_uMode, (int)ShadingMode.Wireframe);
            gl.Uniform1f(_uHasTex, 0f);
            gl.Uniform1f(_uAlphaClip, 0f);
            gl.Uniform1f(_uVertexLit, 0f);
            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _lineIbo);
            if (!_linesUploaded)
            {
                BufferData(gl, GL_ELEMENT_ARRAY_BUFFER, _buffer.Indices(lines: true), GL_STATIC_DRAW);
                _linesUploaded = true;
            }
            gl.DrawElements(Gl.Lines, _buffer.LineIndexCount, Gl.UnsignedInt, 0);
            return;
        }
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _triangleIbo);
        gl.Uniform1i(_uMode, (int)Shading);
        gl.Uniform1f(_uVertexLit, _buffer.Scene.Lighting == SceneLighting.VertexBaked ? 1f : 0f);
        foreach ((int first, int count, int texture, bool alphaClip) in _buffer.Batches)
        {
            bool textured = Shading == ShadingMode.Textured && texture != 0;
            gl.ActiveTexture(GL_TEXTURE0);
            gl.BindTexture(GL_TEXTURE_2D, textured ? texture : _white);
            gl.Uniform1f(_uHasTex, textured ? 1f : 0f);
            gl.Uniform1f(_uAlphaClip, textured && alphaClip ? 1f : 0f);
            gl.DrawElements(GL_TRIANGLES, count, Gl.UnsignedInt, first * sizeof(int));
        }
    }
    private Vector3 EyePosition()
    {
        float cp = MathF.Cos(_pitch);
        return _target + (new Vector3(
            MathF.Sin(_yaw) * cp, MathF.Sin(_pitch), MathF.Cos(_yaw) * cp) * _distance);
    }
    private void UploadGeometry(GlInterface gl)
    {
        ReleaseTextures(gl);
        _buffer = null;
        _linesUploaded = false;
        SceneGeometry? scene = Scene;
        if (scene is null || scene.Meshes.Count == 0)
        {
            return;
        }
        foreach (ImageData texture in scene.Textures)
        {
            try
            {
                _textures.Add(MakeTexture(gl, texture, scene.Filtering));
            }
            catch (Exception)
            {
                _textures.Add(0);
            }
        }
        MeshBuffer buffer = MeshBuffer.Build(scene, _textures);
        _buffer = buffer;
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _poseVbo);
        gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(buffer.NormalOffset * 2L), IntPtr.Zero, Gl.DynamicDraw);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, MeshBuffer.PoseStride, 0);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, MeshBuffer.PoseStride, buffer.NormalOffset);
        WritePose(buffer, null, null);
        if (_poseDirty && _posePositions?.Length == buffer.VertexCount * 3)
        {
            WritePose(buffer, _posePositions, _poseNormals);
        }
        else
        {
            _posePositions = null;
            _poseNormals = null;
        }
        _poseDirty = false;
        gl.BindBuffer(GL_ARRAY_BUFFER, _surfaceVbo);
        BufferData(gl, GL_ARRAY_BUFFER, buffer.Surface(), GL_STATIC_DRAW);
        gl.VertexAttribPointer(2, 2, GL_FLOAT, 0, MeshBuffer.SurfaceStride, 0);
        gl.VertexAttribPointer(3, 4, GL_UNSIGNED_BYTE, 1, MeshBuffer.SurfaceStride, MeshBuffer.ColorOffset);
        for (int i = 0; i < 4; i++)
        {
            gl.EnableVertexAttribArray(i);
        }
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _triangleIbo);
        BufferData(gl, GL_ELEMENT_ARRAY_BUFFER, buffer.Indices(lines: false), GL_STATIC_DRAW);
        gl.BindVertexArray(0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        if (!ReferenceEquals(_framedFor, scene))
        {
            _framedFor = scene;
            ResetCamera();
        }
    }
    private void UploadPose(GlInterface gl)
    {
        if (_buffer is null)
        {
            return;
        }
        if (_posePositions is not null && _posePositions.Length != _buffer.VertexCount * 3)
        {
            return;
        }
        gl.BindBuffer(GL_ARRAY_BUFFER, _poseVbo);
        WritePose(_buffer, _posePositions, _poseNormals);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }
    private void WritePose(MeshBuffer buffer, float[]? positions, float[]? normals)
    {
        if (_bufferSubData is not { } write)
        {
            return;
        }
        if (positions is not null && normals is not null)
        {
            write(GL_ARRAY_BUFFER, 0, buffer.NormalOffset, positions);
            int covered = Math.Min(normals.Length / 3, buffer.VertexCount);
            write(GL_ARRAY_BUFFER, buffer.NormalOffset, covered * MeshBuffer.PoseStride, normals);
            return;
        }
        int at = 0;
        foreach (SceneMesh mesh in buffer.Scene.Meshes)
        {
            int length = mesh.Positions.Length;
            write(GL_ARRAY_BUFFER, at, length * sizeof(float), mesh.Positions);
            float[] rest = mesh.Normals.Length == length ? mesh.Normals : new float[length];
            write(GL_ARRAY_BUFFER, buffer.NormalOffset + at, length * sizeof(float), rest);
            at += length * sizeof(float);
        }
    }
    private void ReleaseTextures(GlInterface gl)
    {
        foreach (int texture in _textures)
        {
            gl.DeleteTexture(texture);
        }
        _textures.Clear();
    }
    private void BuildGrid(GlInterface gl)
    {
        const int range = 2000;
        const int step = 20;
        List<float> positions = new(((range / step * 2) + 1) * 12);
        for (int x = -range; x <= range; x += step)
        {
            positions.AddRange([x, 0, -range, x, 0, range]);
        }
        for (int z = -range; z <= range; z += step)
        {
            positions.AddRange([-range, 0, z, range, 0, z]);
        }
        _gridVertexCount = positions.Count / 3;
        UploadStaticLines(gl, _gridVao, _gridVbo, [.. positions], stride: 12, colored: false);
    }
    private void BuildAxes(GlInterface gl)
    {
        const float length = 100f;
        float[] positions =
        [
            0f, 0f, 0f, 1f, 0f, 0f, length, 0f, 0f, 1f, 0f, 0f,
            0f, 0f, 0f, 0f, 1f, 0f, 0f, length, 0f, 0f, 1f, 0f,
            0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, length, 0f, 0f, 1f,
        ];
        _axisVertexCount = positions.Length / 6;
        UploadStaticLines(gl, _axisVao, _axisVbo, positions, stride: 24, colored: true);
    }
    private static void UploadStaticLines(
        GlInterface gl, int vao, int vbo, float[] positions, int stride, bool colored)
    {
        gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
        BufferData(gl, GL_ARRAY_BUFFER, positions, GL_STATIC_DRAW);
        gl.BindVertexArray(vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, stride, 0);
        if (colored)
        {
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, stride, 12);
        }
        gl.BindVertexArray(0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }
    private static void BufferData(GlInterface gl, int target, Array data, int usage)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            gl.BufferData(target, Buffer.ByteLength(data), handle.AddrOfPinnedObject(), usage);
        }
        finally
        {
            handle.Free();
        }
    }
    private static int MakeTexture(GlInterface gl, ImageData texture, TextureFiltering filtering)
    {
        int id = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, id);
        GCHandle handle = GCHandle.Alloc(texture.Rgba, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, texture.Width, texture.Height, 0,
                GL_RGBA, GL_UNSIGNED_BYTE, handle.AddrOfPinnedObject());
        }
        catch
        {
            gl.DeleteTexture(id);
            throw;
        }
        finally
        {
            handle.Free();
        }
        int filter = filtering == TextureFiltering.Nearest ? GL_NEAREST : GL_LINEAR;
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, Gl.Repeat);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, Gl.Repeat);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return id;
    }
    private static int MakeSolidTexture(GlInterface gl, uint rgba)
    {
        int id = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, id);
        byte[] bytes = BitConverter.GetBytes(rgba);
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE,
                handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return id;
    }
}
