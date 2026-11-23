using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// How the owner side reads a library's file to describe it: the path
/// Jellyfin gives resolved to its real path, which must be under one of its
/// library's folders; a regular file, opened read-only, the one the real
/// path names. A path is never logged, kept or sent: only what the file
/// holds (its fingerprint, its facts).
/// </summary>
internal sealed class LibraryFiles
{
    private readonly List<string> _roots;

    /// <summary>The library's folders as Jellyfin lists them; those that don't resolve are left out.</summary>
    public LibraryFiles(IEnumerable<string> folders)
    {
        _roots = [];
        foreach (var f in folders)
        {
            if (string.IsNullOrEmpty(f) || !Path.IsPathFullyQualified(f) || RealPath(f) is not { } real || !Directory.Exists(real))
            {
                continue;
            }

            _roots.Add(Path.TrimEndingDirectorySeparator(real));
        }
    }

    /// <summary>No folder: every file is refused.</summary>
    public bool Empty => _roots.Count == 0;

    /// <summary>
    /// The file at path opened read-only, or null when it isn't one this
    /// side reads: its real path outside the library's folders, not a
    /// regular file, or unreadable now.
    /// </summary>
    public Opened? Open(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || RealPath(path) is not { } real)
        {
            return null;
        }

        // The deepest folder holding it, whatever order Jellyfin lists them in.
        var root = _roots.Where(r => Under(real, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root is null)
        {
            return null;
        }

        // A regular file by lstat, before it is opened (a FIFO would block
        // the open, a device or a socket isn't a file).
        var info = new FileInfo(real);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0
            || Stat(real) is not { } st || !st.Regular)
        {
            return null;
        }

        SafeFileHandle h;
        try
        {
            h = File.OpenHandle(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            // The file opened is still the one at the real path (a swap
            // between the check and the open is refused).
            var again = new FileInfo(real);
            if (!again.Exists || again.LinkTarget is not null || RealPath(real) != real || FStat(h) is not { } ft || !ft.Regular || ft.Dev != st.Dev || ft.Ino != st.Ino)
            {
                h.Dispose();
                return null;
            }

            var size = RandomAccess.GetLength(h);
            return new Opened(h, real, root, Path.GetRelativePath(root, real).Replace('\\', '/'), size, again.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            h.Dispose();
            return null;
        }
    }

    internal static bool Under(string path, string dir) =>
        dir == "/" ? path.Length > 1 : path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>A path with every link resolved (realpath(3) on Unix); null when it can't be.</summary>
    internal static string? RealPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }
        }

        var p = RealPathNative(path, IntPtr.Zero);
        if (p == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            Free(p);
        }
    }

    private readonly record struct StatInfo(ulong Dev, ulong Ino, uint Mode)
    {
        public bool Regular => (Mode & 0xF000) == 0x8000;
    }

    // struct stat: st_dev and st_ino first on Linux; st_mode after st_nlink
    // on x86-64, right after st_ino on the generic layout (arm64…). Other
    // systems: FileInfo's checks only.
    private static int ModeOffset => RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16;

    private static StatInfo? Stat(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new StatInfo(0, 0, 0x8000);
        }

        var buf = new byte[256];
        return LStatNative(path, buf) == 0 ? Read(buf) : null;
    }

    private static StatInfo? FStat(SafeFileHandle h)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new StatInfo(0, 0, 0x8000);
        }

        var buf = new byte[256];
        var added = false;
        h.DangerousAddRef(ref added);
        try
        {
            return FStatNative((int)h.DangerousGetHandle(), buf) == 0 ? Read(buf) : null;
        }
        finally
        {
            if (added)
            {
                h.DangerousRelease();
            }
        }
    }

    private static StatInfo Read(byte[] b) => new(BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8), BitConverter.ToUInt32(b, ModeOffset));

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStatNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buf);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatNative(int fd, byte[] buf);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPathNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr p);

    /// <summary>A library's file, open read-only. Real, Root and Rel are never logged, kept or sent.</summary>
    internal sealed class Opened(SafeFileHandle handle, string real, string root, string rel, long size, DateTime modified) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;

        public string Real { get; } = real;

        public string Root { get; } = root;

        public string Rel { get; } = rel;

        public long Size { get; } = size;

        /// <summary>Its modification time, UTC.</summary>
        public DateTime Modified { get; } = modified;

        /// <summary>Its modification time in whole seconds since 1970, as the header read keeps it.</summary>
        public long ModifiedUnix => new DateTimeOffset(Modified, TimeSpan.Zero).ToUnixTimeSeconds();

        public void Dispose() => Handle.Dispose();
    }
}
