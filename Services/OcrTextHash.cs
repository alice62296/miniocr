using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>Stable SHA-256 of per-page OCR text, in page order, including blanks.</summary>
public static class OcrTextHash
{
    public static string Compute(IReadOnlyList<OcrPageResult?> pages)
    {
        IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < pages.Count; i++)
        {
            OcrPageResult? page = pages[i];
            int number = page?.Page ?? (i + 1);
            string text = page?.Text ?? "";
            hash.AppendData(Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture)));
            hash.AppendData("\n"u8);
            hash.AppendData(Encoding.UTF8.GetBytes(text));
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
