using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WinAIMagic.Chat;

/// <summary>Images for the model: the vision tower takes at most ~1024 image tokens, so larger pictures only cost prompt time.</summary>
public static class ImageScaler
{
    public const int MaxSide = 1344;
    public const double JpegQuality = 0.85;

    /// <summary>An attachment from an image file; null when it is not an image.</summary>
    public static async Task<Attachment?> MakeAsync(string file)
    {
        byte[] data;
        try { data = await File.ReadAllBytesAsync(file).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return await MakeAsync(data, Path.GetFileName(file)).ConfigureAwait(false);
    }

    public static async Task<Attachment?> MakeAsync(byte[] image, string name)
    {
        var jpeg = await JpegAsync(image).ConfigureAwait(false);
        return jpeg == null ? null : new Attachment { Filename = name, Jpeg = jpeg };
    }

    /// <summary>Longest side at most 1344 (never enlarged), on a white ground, JPEG quality 0.85; null when it is not an image.</summary>
    public static async Task<byte[]?> JpegAsync(byte[] image)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(image.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            double scale = Math.Min(1, (double)MaxSide / Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var pixels = new byte[4 * bitmap.PixelWidth * bitmap.PixelHeight];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            // premultiplied: what is transparent lets the white through
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int rest = 255 - pixels[i + 3];
                pixels[i] = (byte)Math.Min(255, pixels[i] + rest);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + rest);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + rest);
                pixels[i + 3] = 255;
            }
            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)JpegQuality, PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            var result = new byte[output.Size];
            output.Seek(0);
            await output.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None);
            return result;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }   // WIC reports an unknown format with its own HRESULTs
    }
}
