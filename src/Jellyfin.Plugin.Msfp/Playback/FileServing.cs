using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>A file served as it is, whole or the one byte range asked (MSFP media section 2.1).</summary>
internal static class FileServing
{
    public static async Task ServeAsync(string path, ByteRange? range, string contentType, IResponseSink output, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        await using (file.ConfigureAwait(false))
        {
            var size = file.Length;
            var (first, last) = (0L, size - 1);
            var (status, contentRange) = (200, string.Empty);
            if (range is not null)
            {
                if (range.Suffix > 0)
                {
                    (first, last) = (Math.Max(size - range.Suffix, 0), size - 1);
                }
                else
                {
                    (first, last) = (range.First, range.Last < 0 ? size - 1 : Math.Min(range.Last, size - 1));
                }

                if (first >= size)
                {
                    throw new MsfpException(MsfpError.RangeNotSatisfiable);
                }

                (status, contentRange) = (206, $"bytes {first}-{last}/{size}");
            }

            output.Start(status, contentType, last - first + 1, contentRange);
            file.Position = first;
            var buffer = new byte[256 * 1024];
            for (var left = last - first + 1; left > 0;)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                left -= read;
            }
        }
    }
}
