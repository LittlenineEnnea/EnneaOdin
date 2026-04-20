using ICSharpCode.SharpZipLib.Tar;

namespace OdinCore.Tar;

public record TarFileEntry(string Filename, long FileSize);

/// <summary>
/// Reads Samsung firmware .tar / .tar.md5 packages without extraction.
/// tar.md5 files are ordinary tarballs with an MD5 hash line appended;
/// SharpZipLib ignores the trailing garbage safely.
/// </summary>
public static class TarInspector
{
    /// <summary>List every file contained in the archive.</summary>
    public static List<TarFileEntry> List(string path)
    {
        var result = new List<TarFileEntry>();
        if (!File.Exists(path)) return result;

        try
        {
            using var fs  = File.OpenRead(path);
            using var tar = new TarInputStream(fs, System.Text.Encoding.UTF8);
            ICSharpCode.SharpZipLib.Tar.TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (!entry.IsDirectory && entry.Size > 0)
                    result.Add(new TarFileEntry(
                        Path.GetFileName(entry.Name), entry.Size));
            }
        }
        catch { /* corrupt / not a tar – return what we have */ }

        return result;
    }

    /// <summary>
    /// Try to estimate the decompressed size of an lz4 entry inside the tar.
    /// Falls back to compressed size × 3 if the header cannot be read.
    /// </summary>
    public static long EstimateLz4Size(string tarPath, string lz4Filename)
    {
        try
        {
            using var fs  = File.OpenRead(tarPath);
            using var tar = new TarInputStream(fs, System.Text.Encoding.UTF8);
            ICSharpCode.SharpZipLib.Tar.TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (entry.IsDirectory) continue;
                if (!string.Equals(Path.GetFileName(entry.Name), lz4Filename,
                        StringComparison.OrdinalIgnoreCase)) continue;

                // Read the first 15 bytes of the lz4 frame
                var hdr = new byte[15];
                int n = tar.Read(hdr, 0, hdr.Length);

                // LZ4 frame magic: 04 22 4D 18
                if (n >= 11 && hdr[0] == 0x04 && hdr[1] == 0x22
                            && hdr[2] == 0x4D && hdr[3] == 0x18)
                {
                    bool hasContentSize = (hdr[4] & 0x08) != 0;
                    if (hasContentSize && n >= 15)
                        return BitConverter.ToInt64(hdr, 6);   // little-endian
                }
                return entry.Size * 3;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Returns true if the archive contains at least one .pit file.</summary>
    public static bool ContainsPit(string path) =>
        List(path).Any(e => e.Filename.EndsWith(".pit", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the name of the first .pit file found, or null.</summary>
    public static string? FindPitName(string path) =>
        List(path).FirstOrDefault(e =>
            e.Filename.EndsWith(".pit", StringComparison.OrdinalIgnoreCase))?.Filename;
}
