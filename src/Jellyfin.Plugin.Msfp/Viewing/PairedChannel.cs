using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// An empty channel the stand-ins name as theirs. Jellyfin takes an item
/// of an installed channel for a remote one, which clients offer to play;
/// an item with no file of its own would be virtual, like a missing episode,
/// with no Play button (DESIGN-NOTES, the spike). The titles themselves
/// are in the plugin's two libraries, not in the channel. Their sources
/// are shown as a local file's (versions, video, audio and subtitle
/// tracks): since migration phase 5 they are the copies' facts.
/// </summary>
public sealed class PairedChannel : IChannel
{
    /// <summary>The channel's name; its ID, which the stand-ins carry, comes from it.</summary>
    public const string ChannelName = "Paired servers";

    /// <inheritdoc />
    public string Name => ChannelName;

    /// <inheritdoc />
    public string Description => "Titles from the servers this one is paired with (MSFP). They are in the libraries the plugin made.";

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video],
        ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
    };

    /// <summary>
    /// False for every user: the channel stays out of channel lists, so no
    /// empty tile in My Media. Jellyfin asks it only for those lists; its
    /// items' access and playback go by the channel's own visibility (user
    /// policy), which this leaves alone.
    /// </summary>
    public bool IsEnabledFor(string userId) => false;

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new ChannelItemResult());

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
}
