using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Viewing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>
/// The paired servers' titles in this server's libraries, for Jellyfin
/// administrators only: the two libraries the plugin made, and how many
/// titles are in them.
/// </summary>
[ApiController]
[Route("Msfp/Paired")]
[Authorize(Policy = "RequiresElevation")]
[ApiExplorerSettings(IgnoreApi = true)]
public class PairedController : ControllerBase
{
    private readonly StandIns _standIns;

    public PairedController(StandIns standIns)
    {
        _standIns = standIns;
    }

    /// <summary>The libraries ("ready", "making", "removed") and their counts.</summary>
    [HttpGet]
    public IActionResult Status() => Ok(_standIns.Status);

    /// <summary>Makes the libraries again that their owner removed, and fills them.</summary>
    [HttpPost("MakeAgain")]
    public async Task<IActionResult> MakeAgain(CancellationToken ct)
    {
        await _standIns.MakeAgainAsync(ct).ConfigureAwait(false);
        return Ok(_standIns.Status);
    }

    /// <summary>Keeps the titles in step now, rather than in the next hour.</summary>
    [HttpPost("Keep")]
    public async Task<IActionResult> Keep(CancellationToken ct)
    {
        await _standIns.KeepAsync(ct).ConfigureAwait(false);
        return Ok(_standIns.Status);
    }
}
