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
    /// <summary>view_image enlarges a smaller picture to about this size, by a whole factor, so the vision tower (patches of 14 pixels)
    /// can read its letters.</summary>
    public const int LookMinSide = 1024;

    /// <summary>An attachment from an image file; null when it is not an image.</summary>
    public static async Task<Attachment?> MakeAsync(string file, int minSide = 0, int largest = MaxSide, double quality = JpegQuality)
    {
        byte[] data;
        try { data = await File.ReadAllBytesAsync(file).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return await MakeAsync(data, Path.GetFileName(file), minSide, largest, quality).ConfigureAwait(false);
    }

    /// <summary>A small copy for the chat (not for the model): at most 360 pixels, about 15 KB for an emulator screen.</summary>
    public static Task<Attachment?> ThumbnailAsync(string file) => MakeAsync(file, largest: 360, quality: 0.6);

    public static async Task<Attachment?> MakeAsync(byte[] image, string name, int minSide = 0, int largest = MaxSide, double quality = JpegQuality)
    {
        var jpeg = await JpegAsync(image, minSide, largest, quality).ConfigureAwait(false);
        return jpeg == null ? null : new Attachment { Filename = name, Jpeg = jpeg };
    }

    /// <summary>Longest side at most `largest` (1344), on a white ground, JPEG quality `quality` (0.85); null when it is not an image.
    /// Enlarged only below `minSide`, by a whole factor with sharp pixels.</summary>
    public static async Task<byte[]?> JpegAsync(byte[] image, int minSide = 0, int largest = MaxSide, double quality = JpegQuality)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(image.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            double longest = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            bool up = minSide > 0 && longest < minSide;
            double scale = up ? Math.Max(1, Math.Floor(minSide / longest)) : Math.Min(1, largest / longest);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = up ? BitmapInterpolationMode.NearestNeighbor : BitmapInterpolationMode.Fant,
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
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)quality, PropertyType.Single) };
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
