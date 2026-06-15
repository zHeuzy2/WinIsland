using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace WinIsland.Services;

[ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

/// <summary>
/// Live webcam preview using MediaCapture + MediaFrameReader. Frames are copied
/// into a reusable SKBitmap (guarded by AppState.CameraLock) for the renderer.
/// </summary>
public sealed class CameraService
{
    private readonly AppState _state;
    private readonly Action _notify;

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private bool _active;

    public CameraService(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
    }

    public void SetActive(bool active)
    {
        if (active == _active) return;
        _active = active;
        if (active) _ = StartAsync();
        else _ = StopAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            _state.CameraError = null;

            var groups = await MediaFrameSourceGroup.FindAllAsync();
            MediaFrameSourceGroup? group = null;
            MediaFrameSourceInfo? info = null;
            foreach (var g in groups)
            {
                foreach (var si in g.SourceInfos)
                {
                    if (si.SourceKind == MediaFrameSourceKind.Color &&
                        (si.MediaStreamType == MediaStreamType.VideoPreview ||
                         si.MediaStreamType == MediaStreamType.VideoRecord))
                    {
                        group = g; info = si; break;
                    }
                }
                if (group != null) break;
            }

            if (group == null || info == null)
            {
                _state.CameraError = "Nenhuma câmera encontrada";
                _notify();
                return;
            }

            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            var source = _capture.FrameSources[info.Id];

            // Create the reader using the camera's native output format and convert
            // to Bgra8 per-frame in OnFrameArrived. Forcing Bgra8 here makes
            // StartAsync silently return a non-Success status on cameras that only
            // expose NV12/MJPG/YUY2, which leaves the preview stuck on "starting".
            _reader = await _capture.CreateFrameReaderAsync(source);
            _reader.FrameArrived += OnFrameArrived;

            var status = await _reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                _state.CameraError = $"Falha ao iniciar câmera ({status})";
                _notify();
                await StopAsync();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            _state.CameraError = "Acesso à câmera negado (verifique as permissões do Windows)";
            _notify();
            await StopAsync();
        }
        catch (Exception ex)
        {
            _state.CameraError = "Câmera indisponível: " + ex.Message;
            _notify();
            await StopAsync();
        }
    }

    private unsafe void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap == null) return;

        SoftwareBitmap converted = bitmap;
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        try
        {
            int w = converted.PixelWidth;
            int h = converted.PixelHeight;

            using var buffer = converted.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = buffer.CreateReference();
            var plane = buffer.GetPlaneDescription(0);
            int srcStride = plane.Stride;

            ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* src, out uint capacity);

            lock (_state.CameraLock)
            {
                if (_state.CameraBitmap == null ||
                    _state.CameraBitmap.Width != w || _state.CameraBitmap.Height != h)
                {
                    _state.CameraBitmap?.Dispose();
                    _state.CameraBitmap = new SKBitmap(
                        new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
                }

                byte* dst = (byte*)_state.CameraBitmap.GetPixels();
                int dstStride = _state.CameraBitmap.RowBytes;
                int rowBytes = Math.Min(srcStride, dstStride);
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, rowBytes);

                _state.HasCameraFrame = true;
            }

            _notify();
        }
        finally
        {
            if (!ReferenceEquals(converted, bitmap)) converted.Dispose();
        }
    }

    private async Task StopAsync()
    {
        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrameArrived;
                await _reader.StopAsync();
                _reader.Dispose();
                _reader = null;
            }
            _capture?.Dispose();
            _capture = null;
        }
        catch { }

        lock (_state.CameraLock)
        {
            _state.HasCameraFrame = false;
            _state.CameraBitmap?.Dispose();
            _state.CameraBitmap = null;
        }
        _notify();
    }
}
