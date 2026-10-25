using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using WorldGen;

namespace WorldGen.App.Preview3D;

/// <summary>
/// GPU terrain preview: draws the shared <see cref="TerrainMeshData"/> with an unshaded albedo texture
/// through Avalonia's OpenGL control.
/// </summary>
/// <remarks>
/// Avalonia binds a framebuffer with a depth attachment before calling <see cref="OnOpenGlRender"/>, so the
/// renderer only has to set viewport, clear, and draw. Shaders are emitted in two dialects from one body:
/// GLSL ES 1.00 for ANGLE and GLSL 1.20 for desktop compatibility profiles.
/// </remarks>
public sealed class Terrain3DGlControl : OpenGlControlBase
{
    private const int GlDepthTest = 0x0B71;
    private const int GlLequal = 0x0203;
    private const int GlTexture0 = 0x84C0;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlLinear = 0x2601;
    private const int GlLinearMipmapLinear = 0x2703;
    private const int GlClampToEdge = 0x812F;
    private const int GlRgba8 = 0x8058;
    private const int GlUnsignedInt = 0x1405;
    private const int GlArrayBuffer = 0x8892;
    private const int GlElementArrayBuffer = 0x8893;
    private const int GlStaticDraw = 0x88E4;
    private const int GlColorBufferBit = 0x4000;
    private const int GlDepthBufferBit = 0x0100;
    private const int GlBlend = 0x0BE2;
    private const int GlSrcAlpha = 0x0302;
    private const int GlOneMinusSrcAlpha = 0x0303;
    private const int GlLines = 0x0001;

    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.55f, -0.38f, 0.74f));
    private static readonly Vector3 SkyColor = new(0.045f, 0.062f, 0.085f);
    private static readonly Vector3 SpaceColor = new(0.012f, 0.016f, 0.028f);

    private TerrainScene? _scene;
    private IPreviewCamera _camera = new OrbitCamera();
    private bool _initialized;
    private bool _failed;

    /// <summary>When set, the control draws this mesh instead of the camera-following regional mesh.</summary>
    public TerrainMeshData? FixedMesh { get; set; }

    public bool GlobeMode { get; set; }

    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _vertexBuffer;
    private int _indexBuffer;
    private int _vertexArray;
    private int _texture;

    private int _uniformViewProjection = -1;
    private int _uniformSunDirection = -1;
    private int _uniformCameraPosition = -1;
    private int _uniformFogColor = -1;
    private int _uniformFogScale = -1;
    private int _uniformAlbedo = -1;
    private int _uniformGlobe = -1;

    private bool _textureDirty;

    private int _uploadedIndexCount;
    private TerrainMeshData? _uploadedMesh;

    private Uniform3f? _uniform3f;
    private Uniform1i? _uniform1i;
    private GenerateMipmap? _generateMipmap;
    private ReadPixels? _readPixels;
    private BlendFunc? _blendFunc;

    private GridGeometry? _grid;
    private bool _showGrid = true;
    private int _gridProgram;
    private int _gridVertexShader;
    private int _gridFragmentShader;
    private int _gridBuffer;
    private int _gridViewProjection = -1;
    private int _gridBias = -1;
    private int _gridVertexCount;
    private GridGeometry? _uploadedGrid;

    public GridGeometry? Grid
    {
        get => _grid;
        set
        {
            if (ReferenceEquals(_grid, value))
            {
                return;
            }

            _grid = value;
            RequestFrame();
        }
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            if (_showGrid == value)
            {
                return;
            }

            _showGrid = value;
            RequestFrame();
        }
    }

    public event EventHandler<string>? RendererFailed;

    public event EventHandler? FirstFrameRendered;

    public bool HasRenderedFrame { get; private set; }

    public bool IsRendererFailed => _failed;

    public string RendererDescription { get; private set; } = "not initialized";

    /// <summary>
    /// When set, the next rendered frame is read back from the control framebuffer and raised through
    /// <see cref="FrameCaptured"/>. Used for diagnostics and for comparing the GPU and CPU images.
    /// </summary>
    public bool CaptureNextFrame { get; set; }

    public event EventHandler<TerrainFrameCapture>? FrameCaptured;

    public IPreviewCamera Camera
    {
        get => _camera;
        set => _camera = value ?? throw new ArgumentNullException(nameof(value));
    }

    public TerrainScene? Scene
    {
        get => _scene;
        set
        {
            if (ReferenceEquals(_scene, value))
            {
                return;
            }

            if (_scene is not null)
            {
                _scene.GpuMeshes.MeshUpdated -= OnMeshUpdated;
            }

            _scene = value;
            _uploadedMesh = null;
            _textureDirty = true;

            if (_scene is not null)
            {
                _scene.GpuMeshes.MeshUpdated += OnMeshUpdated;
            }

            RequestFrame();
        }
    }

    public void RequestFrame() => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _program = CreateProgram(gl);
            _gridProgram = CreateGridProgram(gl);
            _uniformViewProjection = gl.GetUniformLocationString(_program, "uViewProj");
            _gridViewProjection = gl.GetUniformLocationString(_gridProgram, "uViewProj");
            _gridBias = gl.GetUniformLocationString(_gridProgram, "uBias");
            _uniformSunDirection = gl.GetUniformLocationString(_program, "uSunDir");
            _uniformCameraPosition = gl.GetUniformLocationString(_program, "uCameraPos");
            _uniformFogColor = gl.GetUniformLocationString(_program, "uFogColor");
            _uniformFogScale = gl.GetUniformLocationString(_program, "uFogScale");
            _uniformAlbedo = gl.GetUniformLocationString(_program, "uAlbedo");
            _uniformGlobe = gl.GetUniformLocationString(_program, "uGlobe");

            _vertexBuffer = gl.GenBuffer();
            _indexBuffer = gl.GenBuffer();
            _gridBuffer = gl.GenBuffer();
            if (gl.IsGenVertexArraysAvailable)
            {
                _vertexArray = gl.GenVertexArray();
            }

            _uniform3f = CreateDelegate<Uniform3f>(gl, "glUniform3f");
            _uniform1i = CreateDelegate<Uniform1i>(gl, "glUniform1i");
            _generateMipmap = CreateDelegate<GenerateMipmap>(gl, "glGenerateMipmap");
            _readPixels = CreateDelegate<ReadPixels>(gl, "glReadPixels");
            _blendFunc = CreateDelegate<BlendFunc>(gl, "glBlendFunc");

            RendererDescription = $"{gl.Renderer} — {gl.Version}";
            _initialized = true;
            _failed = false;
            HasRenderedFrame = false;
        }
        catch (Exception exception)
        {
            _failed = true;
            _initialized = false;
            RendererFailed?.Invoke(this, $"OpenGL initialization failed: {exception.Message}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _initialized = false;
        try
        {
            if (_texture != 0)
            {
                gl.DeleteTexture(_texture);
                _texture = 0;
            }

            if (_vertexBuffer != 0)
            {
                gl.DeleteBuffer(_vertexBuffer);
                _vertexBuffer = 0;
            }

            if (_indexBuffer != 0)
            {
                gl.DeleteBuffer(_indexBuffer);
                _indexBuffer = 0;
            }

            if (_gridBuffer != 0)
            {
                gl.DeleteBuffer(_gridBuffer);
                _gridBuffer = 0;
            }

            if (_gridProgram != 0)
            {
                if (_gridVertexShader != 0)
                {
                    gl.DeleteShader(_gridVertexShader);
                    _gridVertexShader = 0;
                }

                if (_gridFragmentShader != 0)
                {
                    gl.DeleteShader(_gridFragmentShader);
                    _gridFragmentShader = 0;
                }

                gl.DeleteProgram(_gridProgram);
                _gridProgram = 0;
            }

            if (_vertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
            {
                gl.DeleteVertexArray(_vertexArray);
                _vertexArray = 0;
            }

            if (_program != 0)
            {
                if (_vertexShader != 0)
                {
                    gl.DeleteShader(_vertexShader);
                    _vertexShader = 0;
                }

                if (_fragmentShader != 0)
                {
                    gl.DeleteShader(_fragmentShader);
                    _fragmentShader = 0;
                }

                gl.DeleteProgram(_program);
                _program = 0;
            }
        }
        catch
        {
            // Context teardown is best-effort; Avalonia disposes the context next.
        }
    }

    protected override void OnOpenGlLost()
    {
        _initialized = false;
        _uploadedMesh = null;
        _uploadedGrid = null;
        HasRenderedFrame = false;
        RendererFailed?.Invoke(this, "OpenGL context was lost.");
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        gl.Viewport(0, 0, width, height);

        Vector3 background = GlobeMode ? SpaceColor : SkyColor;
        gl.ClearColor(background.X, background.Y, background.Z, 1f);
        gl.ClearDepth(1f);
        gl.Clear(GlColorBufferBit | GlDepthBufferBit);

        if (!_initialized || _failed || _program == 0 || _scene is null)
        {
            return;
        }

        if (!HasRenderedFrame)
        {
            HasRenderedFrame = true;
            FirstFrameRendered?.Invoke(this, EventArgs.Empty);
        }

        TerrainMeshData? mesh = FixedMesh ?? _scene.GpuMeshes.Get(_camera);
        if (mesh is null)
        {
            return;
        }

        UploadMeshIfNeeded(gl, mesh);
        UploadTextureIfNeeded(gl);

        gl.Enable(GlDepthTest);
        gl.DepthFunc(GlLequal);
        gl.UseProgram(_program);

        Matrix4x4 view = _camera.GetViewMatrix(_scene.WorldDiagonal);
        Matrix4x4 projection = _camera.GetProjectionMatrix(width / (float)height, _scene.WorldDiagonal);
        Matrix4x4 viewProjection = view * projection;
        Matrix4x4 upload = viewProjection;
        gl.UniformMatrix4fv(_uniformViewProjection, 1, false, (float*)&upload.M11);

        Vector3 eye = _camera.Eye;
        _uniform3f?.Invoke(_uniformSunDirection, SunDirection.X, SunDirection.Y, SunDirection.Z);
        _uniform3f?.Invoke(_uniformCameraPosition, eye.X, eye.Y, eye.Z);
        _uniform3f?.Invoke(_uniformFogColor, background.X, background.Y, background.Z);

        float fogScale = GlobeMode
            ? 0f
            : 1f / MathF.Max(500f, _camera.FogDistance * 4.5f + _scene.WorldDiagonal * 0.02f);
        gl.Uniform1f(_uniformFogScale, fogScale);
        gl.Uniform1f(_uniformGlobe, GlobeMode ? 1f : 0f);

        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _texture);
        _uniform1i?.Invoke(_uniformAlbedo, 0);

        if (_vertexArray != 0)
        {
            gl.BindVertexArray(_vertexArray);
        }

        gl.BindBuffer(GlArrayBuffer, _vertexBuffer);
        gl.BindBuffer(GlElementArrayBuffer, _indexBuffer);

        const int stride = 8 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, GlConsts.GL_FLOAT, 0, stride, IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, stride, (IntPtr)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 3, GlConsts.GL_FLOAT, 0, stride, (IntPtr)(5 * sizeof(float)));

        gl.DrawElements(GlConsts.GL_TRIANGLES, _uploadedIndexCount, GlUnsignedInt, IntPtr.Zero);

        DrawGrid(gl, upload);

        if (CaptureNextFrame && _readPixels is not null)
        {
            CaptureNextFrame = false;
            CaptureFrame(gl, width, height);
        }

        gl.Disable(GlDepthTest);
    }

    private unsafe void DrawGrid(GlInterface gl, Matrix4x4 viewProjection)
    {
        if (!_showGrid || _grid is null || _grid.VertexCount == 0 || _gridProgram == 0)
        {
            return;
        }

        UploadGridIfNeeded(gl);
        if (_gridVertexCount == 0)
        {
            return;
        }

        gl.UseProgram(_gridProgram);
        Matrix4x4 matrix = viewProjection;
        gl.UniformMatrix4fv(_gridViewProjection, 1, false, (float*)&matrix.M11);
        gl.Uniform1f(_gridBias, 2e-4f);

        gl.Enable(GlBlend);
        _blendFunc?.Invoke(GlSrcAlpha, GlOneMinusSrcAlpha);
        gl.DepthMask(0);

        gl.BindBuffer(GlArrayBuffer, _gridBuffer);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, GlConsts.GL_FLOAT, 0, 16, IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 1, GlConsts.GL_FLOAT, 0, 16, (IntPtr)(3 * sizeof(float)));

        gl.DrawArrays(GlLines, 0, _gridVertexCount);

        gl.DepthMask(1);
        gl.Disable(GlBlend);
    }

    private unsafe void UploadGridIfNeeded(GlInterface gl)
    {
        if (ReferenceEquals(_uploadedGrid, _grid))
        {
            return;
        }

        _uploadedGrid = _grid;
        _gridVertexCount = _grid?.VertexCount ?? 0;
        if (_gridVertexCount == 0)
        {
            return;
        }

        gl.BindBuffer(GlArrayBuffer, _gridBuffer);
        fixed (float* data = _grid!.Vertices)
        {
            gl.BufferData(
                GlArrayBuffer,
                (IntPtr)(_gridVertexCount * 4 * sizeof(float)),
                (IntPtr)data,
                GlStaticDraw);
        }
    }

    private int CreateGridProgram(GlInterface gl)
    {
        bool isEs = gl.Version?.Contains("ES", StringComparison.OrdinalIgnoreCase) == true;
        string header = isEs ? "#version 100\nprecision highp float;\n" : "#version 120\n";

        string vertexSource = header + @"
attribute vec3 aPos;
attribute float aAlpha;
uniform mat4 uViewProj;
uniform float uBias;
varying float vAlpha;
void main()
{
    vAlpha = aAlpha;
    vec4 clip = uViewProj * vec4(aPos, 1.0);
    clip.z -= uBias * clip.w;
    gl_Position = clip;
}
";

        string fragmentSource = header + @"
varying float vAlpha;
void main()
{
    gl_FragColor = vec4(0.55, 0.78, 1.0, vAlpha);
}
";

        _gridVertexShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        string? vertexError = gl.CompileShaderAndGetError(_gridVertexShader, vertexSource);
        if (!string.IsNullOrWhiteSpace(vertexError))
        {
            throw new InvalidOperationException(vertexError);
        }

        _gridFragmentShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        string? fragmentError = gl.CompileShaderAndGetError(_gridFragmentShader, fragmentSource);
        if (!string.IsNullOrWhiteSpace(fragmentError))
        {
            throw new InvalidOperationException(fragmentError);
        }

        int program = gl.CreateProgram();
        gl.AttachShader(program, _gridVertexShader);
        gl.AttachShader(program, _gridFragmentShader);
        gl.BindAttribLocationString(program, 0, "aPos");
        gl.BindAttribLocationString(program, 1, "aAlpha");
        gl.LinkProgram(program);

        string? linkError = gl.LinkProgramAndGetError(program);
        if (!string.IsNullOrWhiteSpace(linkError))
        {
            throw new InvalidOperationException(linkError);
        }

        return program;
    }

    private unsafe void UploadMeshIfNeeded(GlInterface gl, TerrainMeshData mesh)
    {
        if (ReferenceEquals(mesh, _uploadedMesh) && _uploadedIndexCount > 0)
        {
            return;
        }

        int vertexCount = mesh.VertexCount;
        var interleaved = new float[vertexCount * 8];
        for (int i = 0; i < vertexCount; i++)
        {
            int source = i * 3;
            int target = i * 8;
            interleaved[target] = mesh.Positions[source];
            interleaved[target + 1] = mesh.Positions[source + 1];
            interleaved[target + 2] = mesh.Positions[source + 2];

            int uv = i * 2;
            interleaved[target + 3] = mesh.Uvs[uv];
            interleaved[target + 4] = mesh.Uvs[uv + 1];

            interleaved[target + 5] = mesh.Normals[source];
            interleaved[target + 6] = mesh.Normals[source + 1];
            interleaved[target + 7] = mesh.Normals[source + 2];
        }

        gl.BindVertexArray(0);
        gl.BindBuffer(GlArrayBuffer, _vertexBuffer);
        fixed (float* data = interleaved)
        {
            gl.BufferData(
                GlArrayBuffer,
                (IntPtr)(interleaved.Length * sizeof(float)),
                (IntPtr)data,
                GlStaticDraw);
        }

        gl.BindBuffer(GlElementArrayBuffer, _indexBuffer);
        fixed (int* data = mesh.Indices)
        {
            gl.BufferData(
                GlElementArrayBuffer,
                (IntPtr)(mesh.Indices.Length * sizeof(int)),
                (IntPtr)data,
                GlStaticDraw);
        }

        _uploadedIndexCount = mesh.Indices.Length;
        _uploadedMesh = mesh;
    }

    private unsafe void UploadTextureIfNeeded(GlInterface gl)
    {
        if (!_textureDirty || _scene is null)
        {
            return;
        }

        (byte[] rgba, int width, int height) = _scene.BuildAlbedoTexture(4096, 2048);

        if (_texture == 0)
        {
            _texture = gl.GenTexture();
        }

        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _texture);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlTextureMinFilter,
            _generateMipmap is not null ? GlLinearMipmapLinear : GlLinear);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlTextureMagFilter, GlLinear);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlTextureWrapS(), GlClampToEdge);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlTextureWrapT(), GlClampToEdge);

        fixed (byte* data = rgba)
        {
            gl.TexImage2D(
                GlConsts.GL_TEXTURE_2D,
                0,
                GlRgba8,
                width,
                height,
                0,
                GlConsts.GL_RGBA,
                GlConsts.GL_UNSIGNED_BYTE,
                (IntPtr)data);
        }

        if (_generateMipmap is not null)
        {
            _generateMipmap(GlConsts.GL_TEXTURE_2D);
        }

        _textureDirty = false;
    }

    private void OnMeshUpdated(object? sender, EventArgs e) => RequestFrame();

    private unsafe void CaptureFrame(GlInterface gl, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        fixed (byte* data = rgba)
        {
            _readPixels!(0, 0, width, height, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, (IntPtr)data);
        }

        int rowBytes = width * 4;
        var flipped = new byte[rgba.Length];
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(rgba, (height - 1 - y) * rowBytes, flipped, y * rowBytes, rowBytes);
        }

        FrameCaptured?.Invoke(this, new TerrainFrameCapture(width, height, flipped));
    }    private int CreateProgram(GlInterface gl)
    {
        bool isEs = gl.Version?.Contains("ES", StringComparison.OrdinalIgnoreCase) == true;
        string header = isEs
            ? "#version 100\nprecision highp float;\n"
            : "#version 120\n";

        string vertexSource = header + @"
attribute vec3 aPos;
attribute vec2 aUv;
attribute vec3 aNormal;
uniform mat4 uViewProj;
varying vec2 vUv;
varying vec3 vNormal;
varying vec3 vWorld;
void main()
{
    vUv = aUv;
    vNormal = aNormal;
    vWorld = aPos;
    gl_Position = uViewProj * vec4(aPos, 1.0);
}
";

        string fragmentSource = header + @"
uniform sampler2D uAlbedo;
uniform vec3 uSunDir;
uniform vec3 uCameraPos;
uniform vec3 uFogColor;
uniform float uFogScale;
uniform float uGlobe;
varying vec2 vUv;
varying vec3 vNormal;
varying vec3 vWorld;
void main()
{
    vec3 albedo = texture2D(uAlbedo, vUv).rgb;
    vec3 normal = normalize(vNormal);
    float diffuse = max(dot(normal, uSunDir), 0.0);
    vec3 color = albedo * (0.38 + 0.78 * diffuse);

    float water = step(vWorld.z, 1.0);
    vec3 viewDirection = normalize(uCameraPos - vWorld);
    vec3 halfDirection = normalize(uSunDir + viewDirection);
    float specular = pow(max(dot(normal, halfDirection), 0.0), 48.0) * water;
    color += vec3(0.30, 0.46, 0.62) * specular;

    // Atmosphere: a thin blue rim at the globe's silhouette.
    float rim = pow(1.0 - abs(dot(normal, viewDirection)), 3.0) * uGlobe;
    color += vec3(0.10, 0.26, 0.58) * rim * 0.85;

    float fog = clamp(length(uCameraPos - vWorld) * uFogScale, 0.0, 1.0);
    color = mix(color, uFogColor, fog * fog * 0.92);
    gl_FragColor = vec4(color, 1.0);
}
";

        _vertexShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        string? vertexError = gl.CompileShaderAndGetError(_vertexShader, vertexSource);
        if (!string.IsNullOrWhiteSpace(vertexError))
        {
            throw new InvalidOperationException(vertexError);
        }

        _fragmentShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        string? fragmentError = gl.CompileShaderAndGetError(_fragmentShader, fragmentSource);
        if (!string.IsNullOrWhiteSpace(fragmentError))
        {
            throw new InvalidOperationException(fragmentError);
        }

        int program = gl.CreateProgram();
        gl.AttachShader(program, _vertexShader);
        gl.AttachShader(program, _fragmentShader);
        gl.BindAttribLocationString(program, 0, "aPos");
        gl.BindAttribLocationString(program, 1, "aUv");
        gl.BindAttribLocationString(program, 2, "aNormal");
        gl.LinkProgram(program);

        string? linkError = gl.LinkProgramAndGetError(program);
        if (!string.IsNullOrWhiteSpace(linkError))
        {
            throw new InvalidOperationException(linkError);
        }

        return program;
    }

    private static T? CreateDelegate<T>(GlInterface gl, string name) where T : Delegate
    {
        IntPtr address = gl.GetProcAddress(name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static int GlTextureWrapS() => 0x2802;

    private static int GlTextureWrapT() => 0x2803;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Uniform3f(int location, float x, float y, float z);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Uniform1i(int location, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GenerateMipmap(int target);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReadPixels(int x, int y, int width, int height, int format, int type, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlendFunc(int source, int destination);
}

public sealed record TerrainFrameCapture(int Width, int Height, byte[] Rgba);

