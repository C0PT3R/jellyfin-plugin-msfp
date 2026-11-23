using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msfp.Describe;
using Msfp.Protocol;
using Video = MediaBrowser.Controller.Entities.Video;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// Keeps every shared film's, episode's and extra's copies (MSFP video
/// sections 3.2 to 3.9): each version's file read by this plugin, once,
/// from its headers (Describe, with Jellyfin's own ffprobe, hardened), the
/// read kept by the file's fingerprint; until it is, the copy Jellyfin's
/// data describes. A pass lists every version, checks each file (size and
/// time; a fingerprint read when they changed), shapes its copy, and
/// re-reads its skip markers and previews; then reads, one at a time, the
/// files not read yet. When a pass changes what a title carries, the
/// catalog is told (<see cref="Changed"/>): the engine notes the change and
/// peers' next readings send the title again.
/// </summary>
public sealed class CopyKeeper : IHostedService, IDisposable
{
    /// <summary>A title's copies at most (the default version, then its alternates).</summary>
    public const int MaxCopies = 16;

    /// <summary>How often a pass runs when nothing wakes it.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    /// <summary>While files are read: the catalog is told at least this often.</summary>
    public static readonly TimeSpan PublishEvery = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly IMediaSegmentManager _segments;
    private readonly ITaskManager _tasks;
    private readonly Playback.Previews _previews;
    private readonly ILogger<CopyKeeper> _log;
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<string, bool> _failed = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private string? _dir;
    private byte[]? _key;
    private Probes? _probes;
    private State _state = State.Empty;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public CopyKeeper(ILibraryManager library, IMediaEncoder encoder, IMediaSegmentManager segments, ITrickplayManager trickplay, ITaskManager tasks, ILogger<CopyKeeper> log)
    {
        _library = library;
        _encoder = encoder;
        _segments = segments;
        _tasks = tasks;
        _previews = new Playback.Previews(trickplay, library);
        _log = log;
    }

    /// <summary>A pass changed what some title carries.</summary>
    public event EventHandler? Changed;

    /// <summary>What the keeper holds now, for the operator: counts only.</summary>
    public KeeperCounts Counts
    {
        get
        {
            var s = Volatile.Read(ref _state);
            return new KeeperCounts(s.Versions.Count, s.Versions.Values.Count(v => v.Minimal), s.Pending, s.Passes, _import);
        }
    }

    private ImportCounts? _import;

    /// <summary>An item's copies, in order (its default version first); empty for none known.</summary>
    public IReadOnlyList<Copy> CopiesOf(Guid item)
    {
        var s = Volatile.Read(ref _state);
        if (!s.Titles.TryGetValue(item, out var versions) || _key is not { } key)
        {
            return [];
        }

        var copies = new List<Copy>();
        foreach (var v in versions)
        {
            if (s.Versions.TryGetValue(v, out var d))
            {
                copies.Add(d.Copy with { Id = v.ToString("N"), ETag = Fingerprints.ETag(key, d.Fingerprint) });
            }
        }

        return copies;
    }

    /// <summary>The map of a version's track IDs to Jellyfin's stream indexes; null for a version not described.</summary>
    public IReadOnlyDictionary<string, int>? TracksOf(Guid version) =>
        Volatile.Read(ref _state).Versions.TryGetValue(version, out var d) ? d.Tracks : null;

    /// <summary>Starts a pass now rather than at the next one; a pass under way is followed by another.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Task.CompletedTask;
        }

        _dir = Path.Combine(plugin.DataFolderPath, "copies");
        Directory.CreateDirectory(_dir);
        _key = Fingerprints.Key(plugin.DataFolderPath);
        _probes = new Probes(Path.Combine(_dir, "probes"));
        // What the last run described, so that peers see the same copies
        // from the start, before the first pass checks them.
        _state = Load(Path.Combine(_dir, "described.json")) ?? State.Empty;
        _tasks.TaskCompleted += TaskCompleted;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _tasks.TaskCompleted -= TaskCompleted;
        if (_stop is { } stop)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop?.Dispose();
        _pass.Dispose();
        _wake.Dispose();
    }

    // Jellyfin's trickplay and segment tasks change the previews and skip
    // markers a copy carries: a pass after them.
    private void TaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        var key = e.Task?.ScheduledTask?.Key ?? string.Empty;
        var name = e.Task?.Name ?? string.Empty;
        if (key.Contains("Trickplay", StringComparison.OrdinalIgnoreCase) || key.Contains("Segment", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Trickplay", StringComparison.OrdinalIgnoreCase) || name.Contains("Segment", StringComparison.OrdinalIgnoreCase))
        {
            Wake();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Jellyfin's start first.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PassAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "MSFP: describing the copies failed");
            }

            try
            {
                await _wake.WaitAsync(Every, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass: every version described from what is known, then the files not read yet read.</summary>
    internal async Task PassAsync(CancellationToken ct)
    {
        if (_probes is null || _key is null)
        {
            return;
        }

        await _pass.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var old = Volatile.Read(ref _state);
            var versions = new Dictionary<Guid, Described>();
            var titles = new Dictionary<Guid, List<Guid>>();
            var queue = new List<Job>();
            foreach (var (lib, files) in Libraries())
            {
                foreach (var (title, list) in Listed(lib))
                {
                    ct.ThrowIfCancellationRequested();
                    var kept = new List<Guid>();
                    foreach (var v in list)
                    {
                        if (kept.Count == MaxCopies || kept.Contains(v.Id))
                        {
                            continue;
                        }

                        if (versions.TryGetValue(v.Id, out var done) || (done = await DescribeAsync(v, files, old, queue, ct).ConfigureAwait(false)) is not null)
                        {
                            versions[v.Id] = done;
                            kept.Add(v.Id);
                        }
                    }

                    if (kept.Count > 0)
                    {
                        titles[title] = kept;
                    }
                }
            }

            Publish(new State(versions, titles, queue.Count, old.Passes + 1), old);

            // The files not read yet, one at a time, at the lowest priority.
            var last = Stopwatch.StartNew();
            var dirty = false;
            for (var i = 0; i < queue.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var job = queue[i];
                if (await ReadAsync(job, ct).ConfigureAwait(false) is { } d)
                {
                    var now = Volatile.Read(ref _state);
                    var vs = new Dictionary<Guid, Described>(now.Versions) { [job.Version.Id] = d };
                    var next = now with { Versions = vs, Pending = queue.Count - i - 1 };
                    Volatile.Write(ref _state, next);
                    dirty = true;
                }

                if (dirty && last.Elapsed >= PublishEvery)
                {
                    Publish(Volatile.Read(ref _state), null);
                    dirty = false;
                    last.Restart();
                }
            }

            var end = Volatile.Read(ref _state) with { Pending = 0 };
            Volatile.Write(ref _state, end);
            if (dirty || queue.Count > 0)
            {
                Publish(end, null);
            }
        }
        finally
        {
            _pass.Release();
        }
    }

    // Sets the state, saves it, and tells the catalog when it differs
    // from before (old null: it was changed bit by bit, always differs).
    private void Publish(State next, State? old)
    {
        Volatile.Write(ref _state, next);
        var changed = old is null || !Same(old, next);
        if (_dir is { } dir && changed)
        {
            try
            {
                Save(Path.Combine(dir, "described.json"), next);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("MSFP: the copies couldn't be saved: {Error}", e.GetType().Name);
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool Same(State a, State b)
    {
        if (a.Titles.Count != b.Titles.Count || a.Versions.Count != b.Versions.Count)
        {
            return false;
        }

        foreach (var (t, l) in b.Titles)
        {
            if (!a.Titles.TryGetValue(t, out var o) || !o.SequenceEqual(l))
            {
                return false;
            }
        }

        foreach (var (v, d) in b.Versions)
        {
            if (!a.Versions.TryGetValue(v, out var o) || o.Fingerprint != d.Fingerprint || o.Json != d.Json)
            {
                return false;
            }
        }

        return true;
    }

    // The libraries this server shares (the plugin's own two left out), each with its folders.
    private IEnumerable<(VirtualFolderInfo Library, LibraryFiles Files)> Libraries() =>
        _library.GetVirtualFolders()
            .Where(f => Guid.TryParse(f.ItemId, out _) && !Viewing.StandIns.IsOurs(f))
            .Select(f => (f, new LibraryFiles(f.Locations ?? [])));

    // A library's titles that carry copies: each film and episode with its
    // versions (Jellyfin's default first, as its media sources list them),
    // and each extra of a film, a series or a season, alone.
    private IEnumerable<(Guid Title, List<BaseItem> Versions)> Listed(VirtualFolderInfo lib)
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series, BaseItemKind.Season],
            Recursive = true,
            IsVirtualItem = false,
            AncestorIds = [Guid.Parse(lib.ItemId)],
        });
        foreach (var item in items.Where(i => !Viewing.PairedSources.IsStandIn(i)))
        {
            if (item is Video v && v.PrimaryVersionId is null)
            {
                yield return (item.Id, VersionsOf(v));
            }

            foreach (var x in ExtrasOf(item))
            {
                yield return (x.Id, [x]);
            }
        }
    }

    // A film's or an episode's versions as its media sources list them.
    private List<BaseItem> VersionsOf(Video v)
    {
        var list = new List<BaseItem>();
        try
        {
            foreach (var s in v.GetMediaSources(false))
            {
                if (Guid.TryParse(s.Id, out var id) && (id == v.Id ? v : _library.GetItemById(id)) is Video version && !list.Contains(version))
                {
                    list.Add(version);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
        {
        }

        if (list.Count == 0)
        {
            list.Add(v);
        }

        return list;
    }

    /// <summary>An item's extras of the types MSFP names (video section 3.9).</summary>
    internal IEnumerable<BaseItem> ExtrasOf(BaseItem item)
    {
        if (item is not (Movie or Series or Season))
        {
            return [];
        }

        try
        {
            return item.GetExtras(null!).Where(x => ExtraTypeToken(x.ExtraType) is not null).ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            return [];
        }
    }

    /// <summary>Jellyfin's extra type as MSFP's token; null for one MSFP doesn't name.</summary>
    internal static string? ExtraTypeToken(ExtraType? type) => type switch
    {
        ExtraType.Trailer => "trailer",
        ExtraType.Featurette => "featurette",
        ExtraType.BehindTheScenes => "behindthescenes",
        ExtraType.DeletedScene => "deletedscene",
        ExtraType.Interview => "interview",
        ExtraType.Scene => "scene",
        ExtraType.Short => "short",
        ExtraType.Clip => "clip",
        ExtraType.Sample => "sample",
        ExtraType.ThemeSong => "themesong",
        ExtraType.ThemeVideo => "themevideo",
        _ => null,
    };

    /// <summary>
    /// Whether an item's file can be a copy this release: a single file
    /// (not a stack of parts, a disc folder or an image, a placeholder or a
    /// shortcut) on the file system.
    /// </summary>
    internal static bool OneFile(BaseItem item) =>
        !string.IsNullOrEmpty(item.Path) && System.IO.Path.IsPathFullyQualified(item.Path) && !item.Path.Contains("://", StringComparison.Ordinal) && !item.IsShortcut
        && (item is not Video v || (v.VideoType == VideoType.VideoFile && !v.IsPlaceHolder && (v.AdditionalParts?.Length ?? 0) == 0 && !v.IsStacked));

    // A version described: from the last pass when its file and Jellyfin's
    // streams are the same, else from the probe kept by its fingerprint,
    // else Jellyfin's data (and the file queued to be read). Its skip
    // markers and previews read again. Null: not a copy (not one file,
    // outside its library's folders, unreadable).
    private async Task<Described?> DescribeAsync(BaseItem v, LibraryFiles files, State old, List<Job> queue, CancellationToken ct)
    {
        if (!OneFile(v) || files.Empty)
        {
            return null;
        }

        using var f = files.Open(v.Path);
        if (f is null)
        {
            return null;
        }

        var streams = v.GetMediaStreams();
        var inputs = InputsOf(streams);
        old.Versions.TryGetValue(v.Id, out var before);
        string fingerprint;
        if (before is not null && before.Size == f.Size && before.Modified == f.ModifiedUnix)
        {
            fingerprint = before.Fingerprint;
        }
        else
        {
            try
            {
                fingerprint = Fingerprints.Of(f.Handle, f.Size);
            }
            catch (IOException)
            {
                return null;
            }
        }

        Copy copy;
        Dictionary<string, int> tracks;
        bool minimal;
        var frames = false;
        if (before is not null && before.Fingerprint == fingerprint && before.Inputs == inputs && !before.Minimal)
        {
            (copy, tracks, minimal, frames) = (before.Copy, before.Tracks, false, before.Frames);
        }
        else if (_probes!.Get(fingerprint) is { } probe)
        {
            (copy, tracks) = CopyFacts.Shape(probe, streams);
            minimal = false;
            frames = probe.NeedsFrames();
        }
        else
        {
            var head = new byte[MediaTypes.SniffSize];
            var n = RandomAccess.Read(f.Handle, head, 0);
            var sniffed = MediaTypes.Sniff(head.AsSpan(0, n));
            (copy, tracks) = CopyFacts.Minimal(sniffed, (v as Video)?.Container ?? Path.GetExtension(v.Path).TrimStart('.'), v.RunTimeTicks, streams);
            minimal = true;
            if (!_failed.ContainsKey(fingerprint))
            {
                queue.Add(new Job(v, f.Real, f.Root, fingerprint, false));
            }
        }

        if (frames && !_failed.ContainsKey("frames " + fingerprint))
        {
            queue.Add(new Job(v, f.Real, f.Root, fingerprint, true));
        }

        copy = copy with
        {
            Size = f.Size,
            Segments = await SegmentsAsync(v).ConfigureAwait(false),
            PreviewLayout = v is Video video ? await LayoutAsync(video).ConfigureAwait(false) : null,
        };
        return Described.Of(fingerprint, f.Size, f.ModifiedUnix, inputs, minimal, copy, tracks) with { Frames = frames };
    }

    // What of Jellyfin's streams the copy is shaped from: the subtitle files
    // beside it, and the counts it maps tracks with.
    internal static string InputsOf(IReadOnlyList<MediaStream> streams)
    {
        var sb = new StringBuilder();
        foreach (var s in streams.OrderBy(s => s.Index))
        {
            sb.Append(s.Index).Append('|').Append(s.Type).Append('|').Append(s.IsExternal ? 1 : 0);
            if (s.IsExternal)
            {
                sb.Append('|').Append(s.Codec).Append('|').Append(s.Language).Append('|').Append(s.IsDefault).Append(s.IsForced).Append(s.IsHearingImpaired);
            }

            sb.Append('\n');
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    // Reads a file not read yet (or its HDR frames), keeps the read by its
    // fingerprint, and shapes its copy; null when the read failed (left as
    // it was until the plugin starts again).
    private async Task<Described?> ReadAsync(Job job, CancellationToken ct)
    {
        var prober = new Prober
        {
            FFprobe = _encoder.ProbePath ?? string.Empty,
            Frames = true,
            Nice = 19,
        };
        Probe probe;
        try
        {
            if (job.Frames)
            {
                probe = _probes!.Get(job.Fingerprint) ?? throw new DescribeException(DescribeError.Io, "the kept probe is gone");
                await prober.ReadFramesAsync(job.Real, probe, ct).ConfigureAwait(false);
            }
            else
            {
                probe = await prober.ProbeAsync(job.Real, ct).ConfigureAwait(false);
            }
        }
        catch (DescribeException e)
        {
            _failed[(job.Frames ? "frames " : string.Empty) + job.Fingerprint] = true;
            // Never the path: the item's ID only.
            _log.LogWarning("MSFP: the file of item {Item} couldn't be read ({Kind}): {Error}", job.Version.Id.ToString("N"), e.Kind, e.Message);
            return null;
        }

        // The file must still be the one fingerprinted.
        if (probe.Size <= 0 || !Fingerprints.Form(job.Fingerprint))
        {
            return null;
        }

        try
        {
            _probes!.Put(job.Fingerprint, probe, job.Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("MSFP: a file's read couldn't be kept: {Error}", e.GetType().Name);
        }

        var now = Volatile.Read(ref _state);
        if (!now.Versions.TryGetValue(job.Version.Id, out var d) || d.Fingerprint != job.Fingerprint)
        {
            return null;
        }

        var (copy, tracks) = CopyFacts.Shape(probe, job.Version.GetMediaStreams());
        copy = copy with { Size = d.Size, Segments = d.Copy.Segments, PreviewLayout = d.Copy.PreviewLayout };
        return Described.Of(d.Fingerprint, d.Size, d.Modified, d.Inputs, false, copy, tracks) with { Frames = probe.NeedsFrames() };
    }

    private async Task<IReadOnlyList<CopySegment>> SegmentsAsync(BaseItem item)
    {
        try
        {
            var found = await _segments.GetSegmentsAsync(item, null, _library.GetLibraryOptions(item), true).ConfigureAwait(false);
            return Segments(found.Select(s => (s.Type, s.StartTicks, s.EndTicks)), item.RunTimeTicks);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Jellyfin's skip markers in MSFP's kinds (video section 5.3), in
    /// order, each within the copy and of some length; kinds MSFP doesn't
    /// name are left out.
    /// </summary>
    internal static List<CopySegment> Segments(IEnumerable<(MediaSegmentType Type, long StartTicks, long EndTicks)> found, long? runTimeTicks)
    {
        var end = runTimeTicks is > 0 ? runTimeTicks.Value / TimeSpan.TicksPerMillisecond : long.MaxValue;
        return found
            .Select(s => (Kind: Viewing.PairedSegments.KindOf(s.Type), Start: s.StartTicks / TimeSpan.TicksPerMillisecond, End: s.EndTicks / TimeSpan.TicksPerMillisecond))
            .Where(s => s.Kind is not null && s.Start >= 0 && s.End > s.Start && s.Start < end)
            .Select(s => new CopySegment(s.Kind!, s.Start, Math.Min(s.End, end)))
            .OrderBy(s => s.StartMs)
            .ThenBy(s => s.EndMs)
            .ToList();
    }

    private async Task<PreviewLayout?> LayoutAsync(Video video)
    {
        try
        {
            return await _previews.ForAsync(video).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    // ---- The header read ---------------------------------------------------

    /// <summary>
    /// Imports a header read (the one made for Overfin: one JSON line a
    /// file, its size, mtime in seconds, path relative to the libraries'
    /// mount, and one ffprobe run's JSON), so that the files aren't probed
    /// again: a record is used only if its file is one this server lists,
    /// inside its library's folders, a regular file, of the same size and
    /// time; its probe converted as Describe's own, fingerprinted, kept
    /// without its path. Then a pass shapes the copies. Counts only, never
    /// a path.
    /// </summary>
    public async Task<ImportCounts> ImportAsync(System.IO.Stream read, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (_probes is null || !Path.IsPathFullyQualified(root ?? string.Empty))
        {
            throw new ArgumentException("the root must be an absolute path", nameof(root));
        }

        // Every version this server lists, by its path, with its library's folders.
        var byPath = new Dictionary<string, (BaseItem Item, LibraryFiles Files)>(StringComparer.Ordinal);
        foreach (var (lib, files) in Libraries())
        {
            foreach (var (_, versions) in Listed(lib))
            {
                foreach (var v in versions.Where(OneFile))
                {
                    byPath.TryAdd(Path.GetFullPath(v.Path), (v, files));
                }
            }
        }

        var counts = await Task.Run(() => Import(_probes, read, root!, byPath, ct), ct).ConfigureAwait(false);
        _import = counts;
        _log.LogInformation("MSFP: header read imported: {Counts}", counts.ToString());
        Wake();
        return counts;
    }

    internal static ImportCounts Import(Probes probes, System.IO.Stream read, string root, IReadOnlyDictionary<string, (BaseItem Item, LibraryFiles Files)> byPath, CancellationToken ct)
    {
        var c = new ImportCounts();
        foreach (var line in HeaderRead.Lines(read))
        {
            ct.ThrowIfCancellationRequested();
            c.Records++;
            if (line is null || HeaderRead.Record(line) is not { } rec)
            {
                c.Bad++;
                continue;
            }

            if (!HeaderRead.LocalPath(rec.Path))
            {
                c.Refused++;
                continue;
            }

            if (!byPath.TryGetValue(Path.GetFullPath(Path.Combine(root, rec.Path)), out var listed))
            {
                c.Unlisted++;
                continue;
            }

            using var f = listed.Files.Open(listed.Item.Path);
            if (f is null)
            {
                c.Refused++;
                continue;
            }

            if (f.Size != rec.Size || f.ModifiedUnix != rec.Mtime)
            {
                c.Changed++;
                continue;
            }

            string fingerprint;
            try
            {
                fingerprint = Fingerprints.Of(f.Handle, f.Size);
            }
            catch (IOException)
            {
                c.Gone++;
                continue;
            }

            if (probes.Has(fingerprint))
            {
                c.Already++;
                continue;
            }

            var head = new byte[MediaTypes.SniffSize];
            var n = RandomAccess.Read(f.Handle, head, 0);
            var mediaType = MediaTypes.Sniff(head.AsSpan(0, n));
            if (MediaTypes.Demuxer(mediaType).Length == 0)
            {
                c.Unknown++;
                continue;
            }

            Probe probe;
            try
            {
                probe = Probes.Convert(mediaType, f.Size, rec.Probe);
                probes.Put(fingerprint, probe, f.Root);
            }
            catch (Exception e) when (e is DescribeException or JsonException or InvalidOperationException or FormatException)
            {
                c.Bad++;
                continue;
            }

            c.Imported++;
            if (probe.NeedsFrames())
            {
                c.Frames++;
            }
        }

        return c;
    }

    // ---- State ---------------------------------------------------------------

    private sealed record Job(BaseItem Version, string Real, string Root, string Fingerprint, bool Frames);

    private sealed record State(IReadOnlyDictionary<Guid, Described> Versions, IReadOnlyDictionary<Guid, List<Guid>> Titles, int Pending, int Passes)
    {
        public static readonly State Empty = new(new Dictionary<Guid, Described>(), new Dictionary<Guid, List<Guid>>(), 0, 0);
    }

    /// <summary>A version as described: its file (fingerprint, size, time), the inputs from Jellyfin, its copy and track map.</summary>
    internal sealed record Described(string Fingerprint, long Size, long Modified, string Inputs, bool Minimal, Copy Copy, Dictionary<string, int> Tracks, string Json)
    {
        /// <summary>Its HDR picture's frame read is still to make.</summary>
        public bool Frames { get; init; }

        public static Described Of(string fingerprint, long size, long modified, string inputs, bool minimal, Copy copy, Dictionary<string, int> tracks)
        {
            // The copy written with a placeholder ID and etag: the etag is
            // the key's, computed when the copy leaves.
            var json = Encoding.UTF8.GetString(Copies.Write(copy with { Id = "v", ETag = "e" }));
            return new Described(fingerprint, size, modified, inputs, minimal, copy, tracks, json);
        }
    }

    private static void Save(string path, State s)
    {
        var versions = new JsonObject();
        foreach (var (id, d) in s.Versions)
        {
            var tracks = new JsonObject();
            foreach (var (k, i) in d.Tracks)
            {
                tracks[k] = i;
            }

            versions[id.ToString("N")] = new JsonObject
            {
                ["fingerprint"] = d.Fingerprint,
                ["size"] = d.Size,
                ["mtime"] = d.Modified,
                ["inputs"] = d.Inputs,
                ["minimal"] = d.Minimal,
                ["frames"] = d.Frames,
                ["copy"] = JsonNode.Parse(d.Json),
                ["tracks"] = tracks,
            };
        }

        var titles = new JsonObject();
        foreach (var (id, l) in s.Titles)
        {
            titles[id.ToString("N")] = new JsonArray(l.Select(v => (JsonNode)JsonValue.Create(v.ToString("N"))!).ToArray());
        }

        var tmp = path + ".new";
        File.WriteAllText(tmp, new JsonObject { ["version"] = Probes.Version, ["versions"] = versions, ["titles"] = titles }.ToJsonString());
        File.Move(tmp, path, true);
    }

    private State? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject o || o["version"]?.GetValue<int>() != Probes.Version)
            {
                return null;
            }

            var versions = new Dictionary<Guid, Described>();
            foreach (var (k, n) in o["versions"] as JsonObject ?? [])
            {
                if (!Guid.TryParseExact(k, "N", out var id) || n is not JsonObject d || Copies.Read(d["copy"]) is not { } copy
                    || d["fingerprint"]?.GetValue<string>() is not { } fp || !Fingerprints.Form(fp))
                {
                    continue;
                }

                var tracks = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (t, i) in d["tracks"] as JsonObject ?? [])
                {
                    if (i is JsonValue v && v.TryGetValue<int>(out var index))
                    {
                        tracks[t] = index;
                    }
                }

                versions[id] = Described.Of(fp, d["size"]?.GetValue<long>() ?? 0, d["mtime"]?.GetValue<long>() ?? 0, d["inputs"]?.GetValue<string>() ?? string.Empty,
                    d["minimal"]?.GetValue<bool>() ?? true, copy, tracks) with { Frames = d["frames"]?.GetValue<bool>() ?? false };
            }

            var titles = new Dictionary<Guid, List<Guid>>();
            foreach (var (k, n) in o["titles"] as JsonObject ?? [])
            {
                if (Guid.TryParseExact(k, "N", out var id) && n is JsonArray a)
                {
                    var l = a.Select(x => Guid.TryParseExact(x?.GetValue<string>(), "N", out var g) ? g : Guid.Empty).Where(g => versions.ContainsKey(g)).ToList();
                    if (l.Count > 0)
                    {
                        titles[id] = l;
                    }
                }
            }

            return new State(versions, titles, 0, 0);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException)
        {
            _log.LogWarning("MSFP: the copies saved couldn't be read: {Error}", e.GetType().Name);
            return null;
        }
    }
}

/// <summary>What the copy keeper holds, for the operator: counts only.</summary>
public sealed record KeeperCounts(int Versions, int NotRead, int Pending, int Passes, ImportCounts? Import);

/// <summary>What an import did with the records it read. Never a path: counts only.</summary>
public sealed class ImportCounts
{
    public int Records { get; set; }

    public int Imported { get; set; }

    public int Already { get; set; }

    public int Frames { get; set; }

    public int Changed { get; set; }

    public int Gone { get; set; }

    public int Refused { get; set; }

    public int Unlisted { get; set; }

    public int Unknown { get; set; }

    public int Bad { get; set; }

    public override string ToString() =>
        $"records {Records}, imported {Imported} (frame read to make: {Frames}), already kept {Already}, changed since {Changed}, gone {Gone}, " +
        $"refused {Refused}, not listed by Jellyfin {Unlisted}, unknown type {Unknown}, unreadable {Bad}";
}
