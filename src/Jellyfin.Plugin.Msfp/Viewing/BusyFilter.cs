using System;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>Every paired server holding a title refused it as busy: a viewer's "try later".</summary>
public sealed class PairedBusyException : InvalidOperationException
{
    public PairedBusyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Pressing Play on a title every paired server holding it is too busy for
/// gives Jellyfin's own answer for that, rate limits
/// (<see cref="PlaybackErrorCode.RateLimitExceeded"/>), which clients show
/// in the viewer's language ("cannot be played at this time"), instead of
/// a server error. Only <see cref="PairedBusyException"/> is touched.
/// </summary>
public sealed class BusyFilter : IExceptionFilter
{
    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is PairedBusyException)
        {
            context.Result = new OkObjectResult(new PlaybackInfoResponse
            {
                MediaSources = [],
                ErrorCode = PlaybackErrorCode.RateLimitExceeded,
            });
            context.ExceptionHandled = true;
        }
    }
}

/// <summary>Adds <see cref="BusyFilter"/> to Jellyfin's API.</summary>
public sealed class BusyFilterSetup : IConfigureOptions<MvcOptions>
{
    /// <inheritdoc />
    public void Configure(MvcOptions options) => options.Filters.Add<BusyFilter>();
}
