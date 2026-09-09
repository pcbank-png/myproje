using System.Buffers.Binary;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// BMP RAW carving için yalnızca "BM" imzasına güvenmez. Dosya başlığı, DIB yapısı,
/// piksel ofseti, renk tablosu/maskeler, satır hizası ve bildirilen dosya boyutunun
/// birbirleriyle tutarlı olmasını zorunlu kılar.
/// </summary>
internal static class BmpStructureValidator
{
    private const uint BitmapCoreHeader = 12;
    private const uint BitmapInfoHeader = 40;
    private const uint BitmapV2InfoHeader = 52;
    private const uint BitmapV3InfoHeader = 56;
    private const uint Os2BitmapArrayHeader = 64;
    private const uint BitmapV4Header = 108;
    private const uint BitmapV5Header = 124;

    public static bool LooksLikePrefix(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M')
            return false;

        // Standart BITMAPFILEHEADER alanları. Reserved alanlarının sıfır olması gerekir.
        if (data[6] != 0 || data[7] != 0 || data[8] != 0 || data[9] != 0)
            return false;

        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(2, 4));
        uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(10, 4));
        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));

        if (fileSize < 26 || pixelOffset < 26 || pixelOffset >= fileSize)
            return false;

        return IsSupportedDibHeader(dibSize);
    }

    public static bool TryValidate(ReadOnlySpan<byte> data, long availableBytes, out long declaredSize)
    {
        declaredSize = 0;
        if (!LooksLikePrefix(data))
            return false;

        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(2, 4));
        uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(10, 4));
        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));

        if (availableBytes > 0 && fileSize > (ulong)availableBytes)
            return false;

        ulong requiredHeaderBytes = 14UL + dibSize;
        if ((ulong)data.Length < requiredHeaderBytes)
            return false;

        bool valid = dibSize == BitmapCoreHeader
            ? ValidateCoreHeader(data, fileSize, pixelOffset)
            : ValidateInfoHeader(data, fileSize, pixelOffset, dibSize);

        if (!valid)
            return false;

        declaredSize = fileSize;
        return true;
    }

    private static bool ValidateCoreHeader(ReadOnlySpan<byte> data, uint fileSize, uint pixelOffset)
    {
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(18, 2));
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(20, 2));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(22, 2));
        ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(24, 2));

        if (width == 0 || height == 0 || planes != 1 ||
            bitsPerPixel is not (1 or 4 or 8 or 24))
            return false;

        ulong paletteEntries = bitsPerPixel <= 8 ? 1UL << bitsPerPixel : 0UL;
        ulong minimumPixelOffset = 14UL + BitmapCoreHeader + paletteEntries * 3UL;
        if (pixelOffset < minimumPixelOffset)
            return false;

        if (!TryComputeUncompressedPixelBytes(width, height, bitsPerPixel, out ulong pixelBytes))
            return false;

        ulong expectedEnd = (ulong)pixelOffset + pixelBytes;
        return expectedEnd == fileSize;
    }

    private static bool ValidateInfoHeader(
        ReadOnlySpan<byte> data,
        uint fileSize,
        uint pixelOffset,
        uint dibSize)
    {
        if (dibSize < BitmapInfoHeader || data.Length < 54)
            return false;

        int width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(18, 4));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22, 4));
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2));
        ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(30, 4));
        uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(34, 4));
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(46, 4));

        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue || planes != 1)
            return false;

        // RAW carving'de güvenilir biçimde sınırı hesaplanabilen BMP türlerini kabul et.
        // BI_RGB, BI_BITFIELDS ve BI_ALPHABITFIELDS dosya geometrisini kesin verir.
        if (compression is not (0u or 3u or 6u))
            return false;

        if (compression == 0)
        {
            if (bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
                return false;
        }
        else
        {
            if (bitsPerPixel is not (16 or 32))
                return false;
        }

        if (signedHeight < 0 && compression is not (0u or 3u or 6u))
            return false;

        ulong minimumPixelOffset = 14UL + dibSize;

        if (bitsPerPixel <= 8)
        {
            uint maxColors = 1u << bitsPerPixel;
            if (colorsUsed > maxColors)
                return false;

            ulong paletteEntries = colorsUsed == 0 ? maxColors : colorsUsed;
            minimumPixelOffset += paletteEntries * 4UL;
        }

        if (dibSize == BitmapInfoHeader && compression is 3u or 6u)
            minimumPixelOffset += compression == 6u ? 16UL : 12UL;

        if (pixelOffset < minimumPixelOffset || pixelOffset >= fileSize)
            return false;

        if (compression is 3u or 6u && !ValidateBitMasks(data, dibSize, compression, bitsPerPixel))
            return false;

        ulong height = (ulong)Math.Abs((long)signedHeight);
        if (!TryComputeUncompressedPixelBytes((ulong)width, height, bitsPerPixel, out ulong pixelBytes))
            return false;

        if (imageSize != 0 && imageSize != pixelBytes)
            return false;

        ulong pixelEnd = (ulong)pixelOffset + pixelBytes;
        if (pixelEnd > fileSize)
            return false;

        ulong expectedEnd = pixelEnd;

        // BITMAPV5HEADER renk profili taşıyabilir. Varsa dosya sonu profile kadar uzanabilir.
        if (dibSize == BitmapV5Header)
        {
            uint profileData = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14 + 112, 4));
            uint profileSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14 + 116, 4));

            if ((profileData == 0) != (profileSize == 0))
                return false;

            if (profileData != 0)
            {
                ulong profileStart = 14UL + profileData;
                ulong profileEnd = profileStart + profileSize;
                if (profileStart < 14UL + BitmapV5Header || profileEnd > fileSize)
                    return false;

                expectedEnd = Math.Max(expectedEnd, profileEnd);
            }
        }

        // bfSize rastgele bir sayı olamaz: hesaplanan piksel/profil sınırıyla eşleşmelidir.
        return expectedEnd == fileSize;
    }

    private static bool ValidateBitMasks(
        ReadOnlySpan<byte> data,
        uint dibSize,
        uint compression,
        ushort bitsPerPixel)
    {
        int maskOffset;
        bool requireAlpha = compression == 6u;

        if (dibSize == BitmapInfoHeader)
        {
            maskOffset = 14 + (int)BitmapInfoHeader;
            int required = requireAlpha ? 16 : 12;
            if (data.Length < maskOffset + required)
                return false;
        }
        else
        {
            if (dibSize < BitmapV2InfoHeader)
                return false;

            maskOffset = 14 + 40;
            if (data.Length < maskOffset + 12)
                return false;

            if (requireAlpha && dibSize < BitmapV3InfoHeader)
                return false;
        }

        uint red = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(maskOffset, 4));
        uint green = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(maskOffset + 4, 4));
        uint blue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(maskOffset + 8, 4));
        uint alpha = 0;

        if (red == 0 || green == 0 || blue == 0 ||
            (red & green) != 0 || (red & blue) != 0 || (green & blue) != 0)
            return false;

        if (requireAlpha)
        {
            if (data.Length < maskOffset + 16)
                return false;

            alpha = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(maskOffset + 12, 4));
            if (alpha == 0 || (alpha & red) != 0 || (alpha & green) != 0 || (alpha & blue) != 0)
                return false;
        }

        uint allowedBits = bitsPerPixel == 32 ? uint.MaxValue : (1u << bitsPerPixel) - 1u;
        return ((red | green | blue | alpha) & ~allowedBits) == 0;
    }

    private static bool TryComputeUncompressedPixelBytes(
        ulong width,
        ulong height,
        ushort bitsPerPixel,
        out ulong bytes)
    {
        bytes = 0;
        try
        {
            ulong rowBits = checked(width * bitsPerPixel);
            ulong rowStride = checked(((rowBits + 31UL) / 32UL) * 4UL);
            bytes = checked(rowStride * height);
            return bytes > 0 && bytes <= uint.MaxValue;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool IsSupportedDibHeader(uint size) =>
        size is BitmapCoreHeader or
            BitmapInfoHeader or
            BitmapV2InfoHeader or
            BitmapV3InfoHeader or
            Os2BitmapArrayHeader or
            BitmapV4Header or
            BitmapV5Header;
}
