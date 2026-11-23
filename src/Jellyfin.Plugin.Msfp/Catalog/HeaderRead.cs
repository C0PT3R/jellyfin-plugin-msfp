using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// A header read: every file's ffprobe output, read once before the copies
/// existed (a whole library takes long to probe), made for Overfin and
/// imported here too (the same libraries), as JSON lines, one record a file:
/// <c>{"size": 123, "mtime": 1700000000, "path": "Films/A (2001)/a.mkv",
/// "probe": {…}, "stderr": "…"}</c>, path relative to the libraries' mount,
/// mtime in seconds, probe the JSON of one run of -show_format
/// -show_streams -show_chapters -show_data, stderr never kept. Gzipped or
/// not.
/// </summary>
internal static class HeaderRead
{
    /// <summary>A record's line at most: past it, the record is dropped.</summary>
    public const int MaxLine = 32 << 20;

    /// <summary>One record of a header read.</summary>
    internal sealed record Line(long Size, long Mtime, string Path, JsonObject Probe);

    /// <summary>The read's lines (gunzipped when it is gzip); null for a line past <see cref="MaxLine"/>; empty lines skipped.</summary>
    public static IEnumerable<byte[]?> Lines(System.IO.Stream read)
    {
        var input = new BufferedStream(read, 1 << 20);
        var magic = new byte[2];
        var got = input.Read(magic, 0, 2);
        System.IO.Stream source = new ConcatStream(magic.AsSpan(0, got).ToArray(), input);
        if (got == 2 && magic[0] == 0x1f && magic[1] == 0x8b)
        {
            source = new GZipStream(source, CompressionMode.Decompress);
        }

        using (source)
        {
            var line = new MemoryStream();
            var buf = new byte[1 << 16];
            var tooLong = false;
            int n;
            while ((n = source.Read(buf, 0, buf.Length)) > 0)
            {
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] != (byte)'\n')
                    {
                        continue;
                    }

                    if (!tooLong && line.Length + (i - start) <= MaxLine)
                    {
                        line.Write(buf, start, i - start);
                    }
                    else
                    {
                        tooLong = true;
                    }

                    var done = Done(line, ref tooLong, out var skip);
                    if (!skip)
                    {
                        yield return done;
                    }

                    start = i + 1;
                }

                if (!tooLong && line.Length + (n - start) <= MaxLine)
                {
                    line.Write(buf, start, n - start);
                }
                else
                {
                    tooLong = true;
                    line.SetLength(0);
                }
            }

            if (line.Length > 0 || tooLong)
            {
                var last = Done(line, ref tooLong, out var skip);
                if (!skip)
                {
                    yield return last;
                }
            }
        }
    }

    // The line read so far, ended: null when it was too long; skip for an empty one.
    private static byte[]? Done(MemoryStream line, ref bool tooLong, out bool skip)
    {
        byte[]? out_ = tooLong ? null : line.ToArray();
        line.SetLength(0);
        skip = false;
        if (out_ is not null)
        {
            var t = Encoding.UTF8.GetString(out_).Trim();
            skip = t.Length == 0;
        }

        tooLong = false;
        return out_;
    }

    /// <summary>A line as a record; null when it isn't one.</summary>
    public static Line? Record(byte[] line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o || o["probe"] is not JsonObject probe || o["path"] is not JsonValue pv || !pv.TryGetValue<string>(out var path)
                || o["size"] is not JsonValue sv || !sv.TryGetValue<long>(out var size) || size < 0 || o["mtime"] is not JsonValue mv || !mv.TryGetValue<long>(out var mtime))
            {
                return null;
            }

            o.Remove("probe");
            return new Line(size, mtime, path, probe);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a record's path is a relative path inside its root: no root, drive or "..", no empty or "." part, no NUL.</summary>
    public static bool LocalPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal) || path.StartsWith('/') || path.StartsWith('\\') || System.IO.Path.IsPathRooted(path))
        {
            return false;
        }

        foreach (var part in path.Split('/', '\\'))
        {
            if (part.Length == 0 || part == "." || part == "..")
            {
                return false;
            }
        }

        return true;
    }

    // A stream of some bytes already read, then the rest.
    private sealed class ConcatStream(byte[] head, System.IO.Stream rest) : System.IO.Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at < head.Length)
            {
                var n = Math.Min(count, head.Length - _at);
                Array.Copy(head, _at, buffer, offset, n);
                _at += n;
                return n;
            }

            return rest.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
