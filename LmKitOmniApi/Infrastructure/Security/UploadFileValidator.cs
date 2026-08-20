namespace LmKitOmniApi.Infrastructure.Security;

public static class UploadFileValidator
{
    public static async Task<bool> HasExpectedSignatureAsync(IFormFile file, string extension, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[16];
        var read = await stream.ReadAsync(header, ct);
        var bytes = header.AsSpan(0, read);

        return extension.ToLowerInvariant() switch
        {
            ".pdf" => StartsWith(bytes, "%PDF"u8),
            ".doc" or ".xls" or ".ppt" => StartsWith(bytes, new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }),
            ".docx" or ".xlsx" or ".pptx" => StartsWith(bytes, new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
            ".png" => StartsWith(bytes, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => StartsWith(bytes, new byte[] { 0xFF, 0xD8, 0xFF }),
            ".gif" => StartsWith(bytes, "GIF87a"u8) || StartsWith(bytes, "GIF89a"u8),
            ".bmp" => StartsWith(bytes, "BM"u8),
            ".webp" => bytes.Length >= 12 && StartsWith(bytes, "RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
            ".tif" or ".tiff" => StartsWith(bytes, new byte[] { 0x49, 0x49, 0x2A, 0x00 })
                || StartsWith(bytes, new byte[] { 0x4D, 0x4D, 0x00, 0x2A }),
            ".txt" or ".md" or ".csv" or ".json" or ".xml" => !bytes.Contains((byte)0),
            _ => false
        };
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);
}
