using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Identification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>A TMDB ID the owner picks for a title, from the operator's page.</summary>
public sealed class ChooseRequest
{
    /// <summary>The TMDB ID; "" goes back to the match.</summary>
    public string Tmdb { get; set; } = string.Empty;
}

/// <summary>
/// The plugin's identification of the shared libraries' titles, for Jellyfin
/// administrators only: what was found, what waits for the owner, and the
/// owner's choices.
/// </summary>
[ApiController]
[Route("Msfp/Identification")]
[Authorize(Policy = "RequiresElevation")]
[ApiExplorerSettings(IgnoreApi = true)]
public class IdentificationController : ControllerBase
{
    private readonly Identifier _identifier;

    public IdentificationController(Identifier identifier)
    {
        _identifier = identifier;
    }

    /// <summary>The counts, and the titles to check with their doubts and candidates.</summary>
    [HttpGet]
    public IActionResult Status()
    {
        var all = _identifier.Identities.Snapshot();
        var own = Plugin.Instance?.Configuration.TmdbKey is { Length: > 0 };
        return Ok(new
        {
            key = own ? "own" : Identifier.BuiltInTmdbKey.Length > 0 ? "plugin" : "none",
            running = _identifier.Running,
            taken = all.Values.Count(t => t.Taken is not null),
            toCheck = all.Values.Count(t => t.ToCheck),
            titles = all.Where(p => p.Value.ToCheck || p.Value.Chosen.Length > 0)
                .OrderBy(p => p.Value.Chosen.Length > 0).ThenBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new
                {
                    item = p.Key,
                    kind = p.Value.Kind,
                    name = p.Value.Name,
                    folder = p.Value.Folder,
                    file = p.Value.File,
                    match = p.Value.Tmdb,
                    chosen = p.Value.Chosen,
                    reasons = p.Value.Reasons.Select(r => r.Text),
                    candidates = p.Value.Candidates.Select(c => new { tmdb = c.Tmdb, name = c.Name, year = c.Year, originalName = c.OriginalName }),
                }),
        });
    }

    /// <summary>The owner's choice for a title: a TMDB ID, or "" to go back to the match.</summary>
    [HttpPost("{item}/Choose")]
    public async Task<IActionResult> Choose(Guid item, [FromBody] ChooseRequest request, CancellationToken ct)
    {
        try
        {
            return await _identifier.ChooseAsync(item, request?.Tmdb ?? string.Empty, ct).ConfigureAwait(false)
                ? Ok(new { ok = true })
                : BadRequest(new { error = "TMDB has no such title." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "No such title." });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = "No TMDB key: set one in the settings above." });
        }
    }
}
