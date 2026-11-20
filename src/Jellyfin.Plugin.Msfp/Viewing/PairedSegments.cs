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
/// itself; the markers come with each playback's answer (MSFP video
/// section 5.3) and are saved then (<see cref="SaveAsync"/>).
/// </summary>
public sealed class PairedSegments : IMediaSegmentProvider
{
    /// <summary>The provider's name, as the libraries' settings show it.</summary>
    public const string ProviderName = "MSFP Federation";

    /// <summary>Jellyfin's ID of the provider: the MD5 of its name in lower case.</summary>
    internal static readonly string ProviderId = ProviderName.ToLowerInvariant().GetMD5().ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MediaSegmentDto>>([]);

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(PairedSources.IsStandIn(item));

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A playback's markers as the stand-in's segments, in place of those it had.</summary>
    internal static async Task SaveAsync(IMediaSegmentManager segments, BaseItem item, IReadOnlyList<global::Msfp.Segment> markers, CancellationToken ct)
    {
        var wanted = markers.Select(m => (Type: TypeOf(m.Kind), m.StartMs, m.EndMs)).Where(m => m.Type is not null && m.EndMs > m.StartMs).ToList();
        if (wanted.Count == 0 && !segments.HasSegments(item.Id))
        {
            return;
        }

        await segments.DeleteSegmentsAsync(item.Id, ct).ConfigureAwait(false);
        foreach (var m in wanted)
        {
            await segments.CreateSegmentAsync(
                new MediaSegmentDto
                {
                    ItemId = item.Id,
                    Type = m.Type!.Value,
                    StartTicks = TimeSpan.FromMilliseconds(m.StartMs).Ticks,
                    EndTicks = TimeSpan.FromMilliseconds(m.EndMs).Ticks,
                },
                ProviderId).ConfigureAwait(false);
        }
    }

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
