using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// Play pressed on a title every paired server holding it is too busy
// for: Jellyfin's rate-limit answer, which clients say in the viewer's
// language; one the paired server refused, "no compatible stream"; no
// media source either way. Any other error is left as it is.
public class BusyFilterTests
{
    private static ExceptionContext Context(Exception e) =>
        new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = e };

    [Theory]
    [InlineData(true, PlaybackErrorCode.RateLimitExceeded)]
    [InlineData(false, PlaybackErrorCode.NoCompatibleStream)]
    public void APairedServersAnswerAsJellyfinsOwn(bool busy, PlaybackErrorCode want)
    {
        var c = Context(busy ? new PairedBusyException("busy") : new PairedRefusedException("refused"));
        new BusyFilter().OnException(c);
        Assert.True(c.ExceptionHandled);
        var answer = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(c.Result).Value);
        Assert.Equal(want, answer.ErrorCode);
        Assert.Empty(answer.MediaSources);
    }

    [Fact]
    public void OtherErrorsAreLeftAsTheyAre()
    {
        foreach (var e in new Exception[] { new InvalidOperationException("x"), new ArgumentException("y") })
        {
            var c = Context(e);
            new BusyFilter().OnException(c);
            Assert.False(c.ExceptionHandled);
            Assert.Null(c.Result);
        }
    }

    [Fact]
    public void AddedToJellyfinsApi()
    {
        var options = new MvcOptions();
        new BusyFilterSetup().Configure(options);
        Assert.Contains(options.Filters, f => f is TypeFilterAttribute t && t.ImplementationType == typeof(BusyFilter));
    }
}
