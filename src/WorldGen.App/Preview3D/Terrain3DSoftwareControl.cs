using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using WorldGen;

namespace WorldGen.App.Preview3D;

/// <summary>
/// Displays the software rasteriser's output. Rendering happens on a worker at reduced resolution and is
/// blitted into a <see cref="WriteableBitmap"/> on the UI thread.
/// </summary>
public sealed class Terrain3DSoftwareControl : Control
{
    private readonly Terrain3DSoftwareRenderer _renderer = new();
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = Array.Empty<byte>();
    private int _pixelWidth;
    private int _pixelHeight;
    private int _running;
    private int _dirty;
    private double _targetWidth;
    private double _targetHeight;
    private TerrainScene? _scene;
    private IPreviewCamera _camera = new OrbitCamera();

    public Terrain3DSoftwareControl()
    {
        ClipToBounds = true;
    }

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
            _scene = value;
            RequestFrame();
        }
    }

    /// <summary>Sea-level reference grid; drawn by the software rasteriser.</summary>
    public GridGeometry? Grid { get; set; }

    public bool ShowGrid { get; set; } = true;

    /// <summary>When set, the control rasterises this mesh instead of the regional camera mesh.</summary>
    public TerrainMeshData? FixedMesh { get; set; }

    public bool GlobeMode { get; set; }

    /// <summary>Render resolution relative to the control size; the software path is the slow path.</summary>
    public float RenderScale { get; set; } = 0.62f;

    public void RequestFrame()
    {
        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RenderLoop);
    }

    private void RenderLoop()
    {
        try
        {
            while (Volatile.Read(ref _dirty) != 0)
            {
                Volatile.Write(ref _dirty, 0);

                TerrainScene? scene = _scene;
                double width = _targetWidth;
                double height = _targetHeight;
                if (scene is null || width < 2 || height < 2)
                {
                    break;
                }

                int pixelWidth = Math.Max(48, (int)(width * RenderScale));
                int pixelHeight = Math.Max(32, (int)(height * RenderScale));

                if (_pixels.Length != pixelWidth * pixelHeight * 4)
                {
                    _pixels = new byte[pixelWidth * pixelHeight * 4];
                }

                var snapshot = _camera.Snapshot();

                try
                {
                    _renderer.Render(scene, snapshot, _pixels, pixelWidth, pixelHeight, FixedMesh, GlobeMode, ShowGrid ? Grid : null);
                }
                catch (Exception exception)
                {
                    RenderFailed?.Invoke(this, exception.Message);
                    break;
                }

                _pixelWidth = pixelWidth;
                _pixelHeight = pixelHeight;

                Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            if (Volatile.Read(ref _dirty) != 0)
            {
                RequestFrame();
            }
        }
    }

    public event EventHandler<string>? RenderFailed;

    private void Present()
    {
        if (_pixelWidth <= 0 || _pixelHeight <= 0)
        {
            return;
        }

        if (_bitmap is null ||
            _bitmap.PixelSize.Width != _pixelWidth ||
            _bitmap.PixelSize.Height != _pixelHeight)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(_pixelWidth, _pixelHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        using (ILockedFramebuffer buffer = _bitmap.Lock())
        {
            int rowBytes = _pixelWidth * 4;
            for (int y = 0; y < _pixelHeight; y++)
            {
                Marshal.Copy(
                    _pixels,
                    y * rowBytes,
                    buffer.Address + y * buffer.RowBytes,
                    rowBytes);
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_bitmap is null)
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#0B0E13")), new Rect(Bounds.Size));
            RequestFrame();
            return;
        }

        context.DrawImage(_bitmap, new Rect(Bounds.Size));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            _targetWidth = Bounds.Width;
            _targetHeight = Bounds.Height;
            RequestFrame();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _targetWidth = finalSize.Width;
        _targetHeight = finalSize.Height;
        Size size = base.ArrangeOverride(finalSize);
        RequestFrame();
        return size;
    }
}
