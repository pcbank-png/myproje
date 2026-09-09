using ImageMagick;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Uygulamadaki thumbnail, büyük önizleme ve resim onarma işlemlerinin tamamı için
/// tek codec kapısıdır. WPF'nin kurulu Windows codec'lerine bağlı sınırlı decoder'ı
/// yerine, paketle birlikte gelen ImageMagick motorunu kullanır.
/// </summary>
public static class GlobalImageCodec
{
    private const ulong Megabyte = 1024UL * 1024UL;

    private static readonly IReadOnlyDictionary<string, MagickFormat> FormatByExtension =
        new Dictionary<string, MagickFormat>(StringComparer.OrdinalIgnoreCase)
        {
            ["JPG"] = MagickFormat.Jpeg,
            ["JPEG"] = MagickFormat.Jpeg,
            ["JPE"] = MagickFormat.Jpeg,
            ["JFIF"] = MagickFormat.Jpeg,
            ["PNG"] = MagickFormat.Png,
            ["APNG"] = MagickFormat.APng,
            ["WEBP"] = MagickFormat.WebP,
            ["GIF"] = MagickFormat.Gif,
            ["BMP"] = MagickFormat.Bmp,
            ["DIB"] = MagickFormat.Dib,
            ["TIF"] = MagickFormat.Tiff,
            ["TIFF"] = MagickFormat.Tiff,
            ["AVIF"] = MagickFormat.Avif,
            ["HEIC"] = MagickFormat.Heic,
            ["HEIF"] = MagickFormat.Heic,
            ["ICO"] = MagickFormat.Ico,
            ["CUR"] = MagickFormat.Cur,
            ["TGA"] = MagickFormat.Tga,
            ["PCX"] = MagickFormat.Pcx,
            ["PSD"] = MagickFormat.Psd,
            ["PSB"] = MagickFormat.Psb,
            ["JP2"] = MagickFormat.Jp2,
            ["J2K"] = MagickFormat.J2k,
            ["JPF"] = MagickFormat.Jp2,
            ["JPX"] = MagickFormat.Jp2,
            ["JPM"] = MagickFormat.Jpm,
            ["JXL"] = MagickFormat.Jxl,
            ["MNG"] = MagickFormat.Mng,
            ["EXR"] = MagickFormat.Exr,
            ["HDR"] = MagickFormat.Hdr,
            ["DDS"] = MagickFormat.Dds,
            ["PBM"] = MagickFormat.Pbm,
            ["PGM"] = MagickFormat.Pgm,
            ["PPM"] = MagickFormat.Ppm,
            ["PNM"] = MagickFormat.Pnm,
            ["PAM"] = MagickFormat.Pam,
            ["SVG"] = MagickFormat.Svg,
            ["SVGZ"] = MagickFormat.Svgz,
            ["WMF"] = MagickFormat.Wmf,
            ["EMF"] = MagickFormat.Emf,
            ["DCM"] = MagickFormat.Dcm,
            ["DICOM"] = MagickFormat.Dcm,
            ["3FR"] = MagickFormat.ThreeFr,
            ["ARW"] = MagickFormat.Arw,
            ["CR2"] = MagickFormat.Cr2,
            ["CR3"] = MagickFormat.Cr3,
            ["CRW"] = MagickFormat.Crw,
            ["DNG"] = MagickFormat.Dng,
            ["ERF"] = MagickFormat.Erf,
            ["IIQ"] = MagickFormat.Iiq,
            ["KDC"] = MagickFormat.Kdc,
            ["MEF"] = MagickFormat.Mef,
            ["MOS"] = MagickFormat.Mos,
            ["MRW"] = MagickFormat.Mrw,
            ["NEF"] = MagickFormat.Nef,
            ["NRW"] = MagickFormat.Nrw,
            ["ORF"] = MagickFormat.Orf,
            ["PEF"] = MagickFormat.Pef,
            ["RAF"] = MagickFormat.Raf,
            ["RAW"] = MagickFormat.Raw,
            ["RW2"] = MagickFormat.Rw2,
            ["RWL"] = MagickFormat.Dng,
            ["SR2"] = MagickFormat.Sr2,
            ["SRF"] = MagickFormat.Srf,
            ["SRW"] = MagickFormat.Srw,
            ["X3F"] = MagickFormat.X3f
        };

    static GlobalImageCodec()
    {
        // Kurtarılan dosyalar güvenilir olmayabilir. Bozuk/saldırgan görsellerin
        // belleği veya diski sınırsız tüketmesini engelleyen süreç-geneli sınırlar.
        ResourceLimits.Memory = 512UL * Megabyte;
        ResourceLimits.Disk = 2UL * 1024UL * Megabyte;
        ResourceLimits.MaxMemoryRequest = 256UL * Megabyte;
        ResourceLimits.MaxProfileSize = 64UL * Megabyte;
        ResourceLimits.Width = 100_000;
        ResourceLimits.Height = 100_000;
        ResourceLimits.ListLength = 512;
        ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount, 1, 4);
    }

    public static bool CanDecode(string extension)
    {
        if (!TryGetFormat(extension, out MagickFormat format))
            return false;

        return GetFormatInfo(format)?.SupportsReading == true;
    }

    public static string GetRepairExtension(string extension)
    {
        string normalized = Models.FileTypeHelper.Normalize(extension);
        if (TryGetFormat(normalized, out MagickFormat format) &&
            GetFormatInfo(format)?.SupportsWriting == true)
        {
            return normalized switch
            {
                "JPEG" or "JPE" or "JFIF" => "jpg",
                "TIF" => "tiff",
                "HEIF" => "heic",
                "JPF" or "JPX" => "jp2",
                _ => normalized.ToLowerInvariant()
            };
        }

        // Kamera RAW ve salt-okunur HEIF türleri gerçekten decode edilir; temiz,
        // kayıpsız bir PNG'ye dönüştürülerek onarılır ve uzantı da buna göre değişir.
        return "png";
    }

    public static ImageSource? CreateThumbnail(byte[] data, int maxPixels)
    {
        (ImageSource? image, _, _) = CreateThumbnailWithDimensions(data, maxPixels);
        return image;
    }

    /// <summary>
    /// Thumbnail ve gerçek piksel ölçüsünü tek decode geçişinde üretir. Tarama/önizleme
    /// hattında aynı fotoğrafı ikinci kez decode ederek CPU veya disk I/O yükü oluşturmaz.
    /// </summary>
    public static (ImageSource? Image, uint Width, uint Height) CreateThumbnailWithDimensions(byte[] data, int maxPixels)
    {
        if (data.Length == 0 || maxPixels <= 0)
            return (null, 0, 0);

        using var image = new MagickImage(data);
        ValidateDimensions(image.Width, image.Height);
        image.AutoOrient();
        ValidateDimensions(image.Width, image.Height);

        uint width = image.Width;
        uint height = image.Height;
        image.Thumbnail((uint)maxPixels, (uint)maxPixels);
        return (ToFrozenBitmapSource(image), width, height);
    }

    public static ImageSource LoadPreview(string path, int maxPixels)
    {
        using var image = new MagickImage(path);
        ValidateDimensions(image.Width, image.Height);
        image.AutoOrient();

        if (image.Width > maxPixels || image.Height > maxPixels)
            image.Resize((uint)maxPixels, (uint)maxPixels);

        return ToFrozenBitmapSource(image)
            ?? throw new InvalidDataException("Görüntü WPF önizlemesine dönüştürülemedi.");
    }

    public static (uint Width, uint Height) GetDimensions(string path)
    {
        using var image = new MagickImage(path);
        ValidateDimensions(image.Width, image.Height);
        image.AutoOrient();
        ValidateDimensions(image.Width, image.Height);
        return (image.Width, image.Height);
    }

    public static void ValidateFile(string path, string extension)
    {
        if (!CanDecode(extension))
            throw new NotSupportedException($"{Models.FileTypeHelper.Normalize(extension)} görüntü kod çözücüsü kullanılamıyor.");

        using var frames = new MagickImageCollection(path);
        if (frames.Count == 0)
            throw new InvalidDataException("Görüntü çözümlenemedi.");

        foreach (IMagickImage<byte> frame in frames)
            ValidateDimensions(frame.Width, frame.Height);
    }

    public static (long Bytes, uint Width, uint Height, int Frames, string Extension) Repair(
        string sourcePath,
        string destinationPath,
        string requestedExtension,
        Action<string>? progress = null)
    {
        progress?.Invoke("Global resim codec motoru dosyayı çözümlüyor...");
        using var frames = new MagickImageCollection(sourcePath);
        if (frames.Count == 0)
            throw new InvalidDataException("Görüntü karesi çözümlenemedi.");

        IMagickImage<byte> first = frames[0];
        ValidateDimensions(first.Width, first.Height);
        uint originalWidth = first.Width;
        uint originalHeight = first.Height;

        string outputExtension = GetRepairExtension(requestedExtension);
        if (!TryGetFormat(outputExtension, out MagickFormat outputFormat))
            outputFormat = MagickFormat.Png;

        IMagickFormatInfo? outputInfo = GetFormatInfo(outputFormat);
        if (outputInfo?.SupportsWriting != true)
            throw new NotSupportedException($"{outputExtension.ToUpperInvariant()} kodlayıcısı kullanılamıyor.");

        progress?.Invoke($"{frames.Count:N0} görüntü karesi temiz dosya yapısına hazırlanıyor...");
        foreach (IMagickImage<byte> frame in frames)
        {
            ValidateDimensions(frame.Width, frame.Height);
            frame.AutoOrient();
            frame.Strip();
            frame.Format = outputFormat;
            if (outputFormat is MagickFormat.Jpeg or MagickFormat.WebP or MagickFormat.Avif)
                frame.Quality = 95;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        progress?.Invoke($"Temiz {outputExtension.ToUpperInvariant()} dosyası kodlanıyor...");

        int expectedFrames;
        if (frames.Count > 1 && outputInfo.SupportsMultipleFrames)
        {
            frames.Write(destinationPath, outputFormat);
            expectedFrames = frames.Count;
        }
        else
        {
            frames[0].Write(destinationPath, outputFormat);
            expectedFrames = 1;
        }

        progress?.Invoke("Onarılan resim bağımsız olarak yeniden açılıp doğrulanıyor...");
        using var verification = new MagickImageCollection(destinationPath);
        if (verification.Count != expectedFrames)
            throw new InvalidDataException("Onarılan görüntünün kare sayısı doğrulanamadı.");
        if (verification[0].Width != originalWidth || verification[0].Height != originalHeight)
            throw new InvalidDataException("Onarılan görüntünün piksel boyutu değişti.");

        long bytes = new FileInfo(destinationPath).Length;
        if (bytes <= 0)
            throw new InvalidDataException("Onarılan resim boş oluştu.");

        return (bytes, originalWidth, originalHeight, expectedFrames, outputExtension);
    }

    private static ImageSource? ToFrozenBitmapSource(IMagickImage<byte> image)
    {
        using var png = new MemoryStream();
        image.Write(png, MagickFormat.Png);
        png.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = png;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static bool TryGetFormat(string extension, out MagickFormat format)
    {
        string normalized = Models.FileTypeHelper.Normalize(extension);
        if (FormatByExtension.TryGetValue(normalized, out format))
            return true;

        // Magick.NET yeni format eklediğinde uygulamanın sabit bir uzantı listesine
        // mahkum kalmaması için enum adı uzantıyla eşleşiyorsa otomatik kullan.
        return Enum.TryParse(normalized, ignoreCase: true, out format) &&
               GetFormatInfo(format) is not null;
    }

    private static IMagickFormatInfo? GetFormatInfo(MagickFormat format) =>
        MagickNET.SupportedFormats.FirstOrDefault(info => info.Format == format);

    private static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0)
            throw new InvalidDataException("Geçerli görüntü boyutu bulunamadı.");
        if (width > ResourceLimits.Width || height > ResourceLimits.Height)
            throw new InvalidDataException("Görüntü güvenli piksel sınırını aşıyor.");
    }
}
