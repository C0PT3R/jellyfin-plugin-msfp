using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// The copying out of an embedded PGS track by Jellyfin's ffmpeg, bounded
/// as the probe is (SECURITY.md rule 8): no shell, an empty environment,
/// the <c>file</c> protocol only on the file opened by the path checks, the
/// lowest priority, a wall-clock limit and an output cap; a run past
/// either is killed and gives nothing.
/// </summary>
internal static class PgsExtraction
{
    /// <summary>
    /// A run's wall-clock limit. Copying a track out reads the whole
    /// container: a 80 GB 4K remux at the lowest priority on a busy disk
    /// takes minutes, so the limit is wide; past it the run is stuck, not
    /// slow.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The largest track kept, in bytes (also for an external .sup). A full
    /// film's PGS track is usually 20 to 60 MiB; dense or 4K ones come near
    /// 100 MiB. Past it, it isn't a subtitle track worth holding in memory.
    /// </summary>
    public const int MaxOutput = 128 << 20;

    /// <summary>The ffmpeg child's niceness: the lowest priority.</summary>
    public const int Nice = 19;

    /// <summary>Why a run gave nothing.</summary>
    internal enum Failure
    {
        None,
        Start,
        Time,
        Size,
        Exit,
    }

    /// <summary>The arguments copying out the file's own stream number <paramref name="stream"/> of <paramref name="input"/> as a .sup on stdout.</summary>
    internal static IReadOnlyList<string> Arguments(string input, int stream) =>
    [
        "-nostdin", "-hide_banner", "-v", "error",
        "-protocol_whitelist", "file", "-copyts", "-i", "file:" + input,
        "-map", "0:" + stream.ToString(CultureInfo.InvariantCulture), "-c:s", "copy", "-f", "sup", "pipe:1",
    ];

    /// <summary>The path a child opens the handle's very file by (Linux), else the real path.</summary>
    internal static string ChildPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string real) =>
        OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd")
            ? "/proc/" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "/fd/" + handle.DangerousGetHandle().ToString()
            : real;

    /// <summary>
    /// One run of <paramref name="program"/>: its stdout when it exits 0
    /// within <paramref name="timeout"/> and <paramref name="maxOutput"/>
    /// bytes; otherwise it's killed (its whole tree) and the failure given.
    /// Its stderr is read and dropped (it names the file).
    /// </summary>
    internal static async Task<(byte[]? Data, Failure Failed)> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, int maxOutput, int nice)
    {
        var psi = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.Environment.Clear();
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, Failure.Start);
        }

        using (p)
        {
            if (nice > 0 && OperatingSystem.IsLinux())
            {
                _ = SetPriority(0, p.Id, Math.Min(nice, 19));
            }

            p.StandardInput.Close();
            using var cts = new CancellationTokenSource(timeout);
            var over = false;
            var stdout = new MemoryStream();
            var read = Task.Run(
                async () =>
                {
                    var buf = new byte[65536];
                    int k;
                    while ((k = await p.StandardOutput.BaseStream.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        if (stdout.Length + k > maxOutput)
                        {
                            over = true;
                            await cts.CancelAsync().ConfigureAwait(false);
                            return;
                        }

                        stdout.Write(buf, 0, k);
                    }
                },
                CancellationToken.None);
            var err = Task.Run(
                async () =>
                {
                    var buf = new char[4096];
                    while (await p.StandardError.ReadAsync(buf, cts.Token).ConfigureAwait(false) > 0)
                    {
                    }
                },
                CancellationToken.None);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                await read.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(true);
                    p.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                }

                return (null, over ? Failure.Size : Failure.Time);
            }
            finally
            {
                try
                {
                    await err.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (over)
            {
                return (null, Failure.Size);
            }

            return p.ExitCode == 0 ? (stdout.ToArray(), Failure.None) : (null, Failure.Exit);
        }
    }

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriority(int which, int who, int prio);
}

/// <summary>One run per key at a time: asks while one runs wait for that one.</summary>
internal sealed class SingleRuns<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _runs = new(StringComparer.Ordinal);

    /// <summary>The run under key: the one running, else a new one from start.</summary>
    public Task<T> Run(string key, Func<Task<T>> start)
    {
        Lazy<Task<T>>? mine = null;
        mine = new Lazy<Task<T>>(() => Go(key, mine!, start));
        return _runs.GetOrAdd(key, mine).Value;
    }

    private async Task<T> Go(string key, Lazy<Task<T>> self, Func<Task<T>> start)
    {
        try
        {
            await Task.Yield();
            return await start().ConfigureAwait(false);
        }
        finally
        {
            _runs.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, self));
        }
    }
}
