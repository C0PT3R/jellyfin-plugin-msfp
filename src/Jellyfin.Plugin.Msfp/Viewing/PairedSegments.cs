using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// The skip markers of paired servers' titles (intro, credits...), as
/// Jellyfin's media segments of the stand-ins. Jellyfin lists only the
/// segments of a provider it knows, which an owner can turn off per
/// library like any other: this provider is that name. It finds nothing
/// on disk; the markers are the copies' (MSFP video section 3.4), saved at
/// each pass of the stand-ins (<see cref="SaveAsync"/>).
/// </summary>
public sealed class PairedSegments : IMediaSegmentProvider
{
    /// <summary>The provider's name, as the libraries' settings show it.</summary>
    public const string ProviderName = "MSFP Federation";

    /// <summary>Jellyfin's ID of the provider: the MD5 of its name in lower case.</summary>
    internal static readonly string ProviderId = ProviderName.ToLowerInvariant().GetMD5().ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string Name => ProviderName;

    private readonly IServiceProvider _services;

    // The stand-ins' keeper taken when asked: it needs Jellyfin's segment
    // manager, which needs this provider.
    public PairedSegments(IServiceProvider services) => _services = services;

    /// <inheritdoc />
    // Asked by Jellyfin's segment scan: the best copy's markers, as each pass saves them.
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(_services.GetService(typeof(StandIns)) is StandIns standIns ? standIns.SegmentsOf(request.ItemId) : (IReadOnlyList<MediaSegmentDto>)[]);

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(PairedSources.IsStandIn(item));

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// A copy's markers as the stand-in's segments, in place of those it had
    /// when they differ (each pass): a kind Jellyfin doesn't know as its
    /// Unknown, which players show as a plain skip (MSFP video section 3.4).
    /// </summary>
    internal static async Task<bool> SaveAsync(IMediaSegmentManager segments, BaseItem item, MediaBrowser.Model.Configuration.LibraryOptions options, IReadOnlyList<global::Msfp.Protocol.CopySegment> markers, CancellationToken ct)
    {
        var wanted = Wanted(markers);
        var had = (await segments.GetSegmentsAsync(item, null, options, false).ConfigureAwait(false))
            .Select(s => (s.Type, s.StartTicks, s.EndTicks)).OrderBy(s => s.StartTicks).ThenBy(s => s.Type).ToList();
        if (had.SequenceEqual(wanted))
        {
            return wanted.Count > 0;
        }

        await segments.DeleteSegmentsAsync(item.Id, ct).ConfigureAwait(false);
        foreach (var m in wanted)
        {
            await segments.CreateSegmentAsync(
                new MediaSegmentDto { ItemId = item.Id, Type = m.Type, StartTicks = m.StartTicks, EndTicks = m.EndTicks },
                ProviderId).ConfigureAwait(false);
        }

        return wanted.Count > 0;
    }

    /// <summary>A copy's markers as Jellyfin's segments, in order: an unknown kind as Unknown.</summary>
    internal static List<(MediaSegmentType Type, long StartTicks, long EndTicks)> Wanted(IReadOnlyList<global::Msfp.Protocol.CopySegment> markers) =>
        markers.Where(m => m.EndMs > m.StartMs)
            .Select(m => (TypeOf(m.Kind) ?? MediaSegmentType.Unknown, TimeSpan.FromMilliseconds(m.StartMs).Ticks, TimeSpan.FromMilliseconds(m.EndMs).Ticks))
            .OrderBy(s => s.Item2).ThenBy(s => s.Item1).ToList();

    /// <summary>An MSFP marker's kind as Jellyfin's segment type; null for another.</summary>
    internal static MediaSegmentType? TypeOf(string kind) => kind switch
    {
        "intro" => MediaSegmentType.Intro,
        "outro" => MediaSegmentType.Outro,
        "recap" => MediaSegmentType.Recap,
        "preview" => MediaSegmentType.Preview,
        "commercial" => MediaSegmentType.Commercial,
        _ => null,
    };

    /// <summary>Jellyfin's segment type as an MSFP marker's kind; null for Unknown.</summary>
    internal static string? KindOf(MediaSegmentType type) => type switch
    {
        MediaSegmentType.Intro => "intro",
        MediaSegmentType.Outro => "outro",
        MediaSegmentType.Recap => "recap",
        MediaSegmentType.Preview => "preview",
        MediaSegmentType.Commercial => "commercial",
        _ => null,
    };
}
