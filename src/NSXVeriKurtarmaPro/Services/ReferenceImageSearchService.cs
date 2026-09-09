using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Referans fotograf ile bulunan fotograflarin kucuk onizlemeleri arasinda
/// yapisal benzerlik hesabi yapar. Disk tarama motorundan tamamen bagimsizdir;
/// yalniz decode edilmis onizleme piksellerini kullanir.
/// </summary>
public sealed record ReferenceImageSignature(
    ulong DifferenceHash,
    ulong AverageHash,
    float[] LumaGrid,
    float[] ColorGrid,
    double AspectRatio);

public static class ReferenceImageSearchService
{
    private const int WorkingSize = 32;
    private const int LumaGridSize = 16;
    private const int ColorGridSize = 4;

    public static ReferenceImageSignature CreateSignature(ImageSource imageSource)
    {
        if (imageSource is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            throw new InvalidDataException("Referans goruntu piksel verisine donusturulemedi.");

        BitmapSource bitmap = NormalizeBitmap(source, WorkingSize, WorkingSize);
        int stride = checked(WorkingSize * 4);
        byte[] pixels = new byte[checked(stride * WorkingSize)];
        bitmap.CopyPixels(pixels, stride, 0);

        float[] luma = BuildLumaGrid(pixels, stride);
        float[] color = BuildColorGrid(pixels, stride);
        ulong differenceHash = BuildDifferenceHash(pixels, stride);
        ulong averageHash = BuildAverageHash(pixels, stride);
        double aspect = source.PixelHeight > 0
            ? source.PixelWidth / (double)source.PixelHeight
            : 1d;

        return new ReferenceImageSignature(differenceHash, averageHash, luma, color, aspect);
    }

    public static int CalculateSimilarity(ReferenceImageSignature reference, ImageSource candidate)
    {
        ReferenceImageSignature signature = CreateSignature(candidate);
        return CalculateSimilarity(reference, signature);
    }

    public static int CalculateSimilarity(ReferenceImageSignature reference, ReferenceImageSignature candidate)
    {
        double dHashScore = 1d - BitOperations.PopCount(reference.DifferenceHash ^ candidate.DifferenceHash) / 64d;
        double aHashScore = 1d - BitOperations.PopCount(reference.AverageHash ^ candidate.AverageHash) / 64d;
        double structureScore = CalculateNormalizedCorrelation(reference.LumaGrid, candidate.LumaGrid);
        double colorScore = CalculateVectorSimilarity(reference.ColorGrid, candidate.ColorGrid);
        double aspectScore = CalculateAspectSimilarity(reference.AspectRatio, candidate.AspectRatio);

        // Ayni fotograf farkli JPEG kalite/olceklerinde araniyor. Bu nedenle yapisal
        // korelasyon ve gradient hash agirlikli; renk/aspect yalniz destek sinyalidir.
        double combined =
            (structureScore * 0.45d) +
            (dHashScore * 0.25d) +
            (aHashScore * 0.15d) +
            (colorScore * 0.10d) +
            (aspectScore * 0.05d);

        return Math.Clamp((int)Math.Round(combined * 100d, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static BitmapSource NormalizeBitmap(BitmapSource source, int width, int height)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0d);

        double scaleX = width / (double)Math.Max(1, converted.PixelWidth);
        double scaleY = height / (double)Math.Max(1, converted.PixelHeight);
        var transformed = new TransformedBitmap(converted, new ScaleTransform(scaleX, scaleY));
        transformed.Freeze();
        return transformed;
    }

    private static float[] BuildLumaGrid(byte[] pixels, int stride)
    {
        var values = new float[LumaGridSize * LumaGridSize];
        int cell = WorkingSize / LumaGridSize;

        for (int gy = 0; gy < LumaGridSize; gy++)
        {
            for (int gx = 0; gx < LumaGridSize; gx++)
            {
                double sum = 0d;
                int count = 0;
                for (int y = gy * cell; y < (gy + 1) * cell; y++)
                {
                    int row = y * stride;
                    for (int x = gx * cell; x < (gx + 1) * cell; x++)
                    {
                        int offset = row + (x * 4);
                        byte b = pixels[offset];
                        byte g = pixels[offset + 1];
                        byte r = pixels[offset + 2];
                        sum += ToLuma(r, g, b);
                        count++;
                    }
                }

                values[(gy * LumaGridSize) + gx] = count > 0 ? (float)(sum / count / 255d) : 0f;
            }
        }

        return values;
    }

    private static float[] BuildColorGrid(byte[] pixels, int stride)
    {
        var values = new float[ColorGridSize * ColorGridSize * 3];
        int cell = WorkingSize / ColorGridSize;

        for (int gy = 0; gy < ColorGridSize; gy++)
        {
            for (int gx = 0; gx < ColorGridSize; gx++)
            {
                double r = 0d;
                double g = 0d;
                double b = 0d;
                int count = 0;

                for (int y = gy * cell; y < (gy + 1) * cell; y++)
                {
                    int row = y * stride;
                    for (int x = gx * cell; x < (gx + 1) * cell; x++)
                    {
                        int offset = row + (x * 4);
                        b += pixels[offset];
                        g += pixels[offset + 1];
                        r += pixels[offset + 2];
                        count++;
                    }
                }

                int index = ((gy * ColorGridSize) + gx) * 3;
                double divisor = Math.Max(1, count) * 255d;
                values[index] = (float)(r / divisor);
                values[index + 1] = (float)(g / divisor);
                values[index + 2] = (float)(b / divisor);
            }
        }

        return values;
    }

    private static ulong BuildDifferenceHash(byte[] pixels, int stride)
    {
        ulong hash = 0UL;
        int bit = 0;

        for (int y = 0; y < 8; y++)
        {
            int sampleY = Math.Min(WorkingSize - 1, (int)Math.Round(y * (WorkingSize - 1) / 7d));
            for (int x = 0; x < 8; x++)
            {
                int leftX = Math.Min(WorkingSize - 2, (int)Math.Round(x * (WorkingSize - 2) / 7d));
                double left = ReadLuma(pixels, stride, leftX, sampleY);
                double right = ReadLuma(pixels, stride, leftX + 1, sampleY);
                if (left > right)
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    private static ulong BuildAverageHash(byte[] pixels, int stride)
    {
        Span<double> samples = stackalloc double[64];
        double total = 0d;
        int index = 0;

        for (int y = 0; y < 8; y++)
        {
            int sampleY = Math.Min(WorkingSize - 1, (int)Math.Round((y + 0.5d) * WorkingSize / 8d - 0.5d));
            for (int x = 0; x < 8; x++)
            {
                int sampleX = Math.Min(WorkingSize - 1, (int)Math.Round((x + 0.5d) * WorkingSize / 8d - 0.5d));
                double value = ReadLuma(pixels, stride, sampleX, sampleY);
                samples[index++] = value;
                total += value;
            }
        }

        double average = total / samples.Length;
        ulong hash = 0UL;
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i] >= average)
                hash |= 1UL << i;
        }

        return hash;
    }

    private static double CalculateNormalizedCorrelation(float[] a, float[] b)
    {
        int count = Math.Min(a.Length, b.Length);
        if (count == 0)
            return 0d;

        double meanA = 0d;
        double meanB = 0d;
        for (int i = 0; i < count; i++)
        {
            meanA += a[i];
            meanB += b[i];
        }
        meanA /= count;
        meanB /= count;

        double numerator = 0d;
        double energyA = 0d;
        double energyB = 0d;
        for (int i = 0; i < count; i++)
        {
            double da = a[i] - meanA;
            double db = b[i] - meanB;
            numerator += da * db;
            energyA += da * da;
            energyB += db * db;
        }

        double denominator = Math.Sqrt(energyA * energyB);
        if (denominator < 1e-9)
            return CalculateVectorSimilarity(a, b);

        double correlation = numerator / denominator;
        return Math.Clamp((correlation + 1d) / 2d, 0d, 1d);
    }

    private static double CalculateVectorSimilarity(float[] a, float[] b)
    {
        int count = Math.Min(a.Length, b.Length);
        if (count == 0)
            return 0d;

        double difference = 0d;
        for (int i = 0; i < count; i++)
            difference += Math.Abs(a[i] - b[i]);

        return Math.Clamp(1d - (difference / count), 0d, 1d);
    }

    private static double CalculateAspectSimilarity(double a, double b)
    {
        if (a <= 0d || b <= 0d)
            return 0d;

        double ratio = Math.Min(a, b) / Math.Max(a, b);
        return Math.Clamp(ratio, 0d, 1d);
    }

    private static double ReadLuma(byte[] pixels, int stride, int x, int y)
    {
        int offset = (y * stride) + (x * 4);
        return ToLuma(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static double ToLuma(byte r, byte g, byte b) =>
        (0.2126d * r) + (0.7152d * g) + (0.0722d * b);
}
