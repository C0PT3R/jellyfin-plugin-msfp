using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Msfp.Describe;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// The files' header reads, kept by fingerprint in the plugin's data folder
/// (copies/probes/&lt;fingerprint&gt;.json), so that a file is probed once
/// whatever its path (a move costs one fingerprint read, no probe):
/// Describe's outputs as ffprobe gave them, without the file's name, its
/// attachments, or any string holding its library folder.
/// </summary>
internal sealed class Probes
{
    /// <summary>
    /// The probe's and the shaping's version: a probe kept with an older one
    /// is read again. Raise it when the probe or the shaping changes what a
    /// copy says.
    /// </summary>
    public const int Version = 1;

    /// <summary>The largest output kept for one ffprobe run.</summary>
    public const int MaxOutput = Prober.DefaultMaxOutput;

    private readonly string _dir;

    public Probes(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    private string PathOf(string fingerprint) => Path.Combine(_dir, fingerprint + ".json");

    /// <summary>Whether a probe of this version is kept for the fingerprint.</summary>
    public bool Has(string fingerprint) => Get(fingerprint) is not null;

    /// <summary>The probe kept for a fingerprint, of this version; null for none (or one that can't be read).</summary>
    public Probe? Get(string fingerprint)
    {
        if (!Fingerprints.Form(fingerprint))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllBytes(PathOf(fingerprint)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or DescribeException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Keeps a probe for a fingerprint (written whole, then moved in place).</summary>
    public void Put(string fingerprint, Probe probe, string libraryFolder)
    {
        if (!Fingerprints.Form(fingerprint))
        {
            throw new ArgumentException("not a fingerprint", nameof(fingerprint));
        }

        var bytes = Keepable(probe, libraryFolder);
        var path = PathOf(fingerprint);
        var tmp = path + ".new";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }

    /// <summary>
    /// A probe as kept: its media type, size and outputs, the first run's
    /// without format.filename (the descriptor ffprobe read), without
    /// attachment streams, and without any string holding the library's
    /// folder; the records' and frames' runs as they came (they hold no
    /// path).
    /// </summary>
    internal static byte[] Keepable(Probe probe, string libraryFolder)
    {
        var first = JsonNode.Parse(probe.Outputs.Streams) as JsonObject ?? throw new DescribeException(DescribeError.Probe, "not an object");
        (first["format"] as JsonObject)?.Remove("filename");
        if (first["streams"] is JsonArray list)
        {
            foreach (var s in list.ToList())
            {
                if (s is not JsonObject so || (so["codec_type"] is JsonValue t && t.TryGetValue<string>(out var ct) && ct == "attachment"))
                {
                    list.Remove(s);
                }
            }
        }

        var dir = Path.TrimEndingDirectorySeparator(libraryFolder ?? string.Empty);
        if (dir.Length > 1)
        {
            Scrub(first, dir);
        }

        var o = new JsonObject
        {
            ["version"] = Version,
            ["mediaType"] = probe.MediaType,
            ["size"] = probe.Size,
            ["streams"] = first,
            ["data"] = new JsonArray(probe.Outputs.Data.Select(d => JsonNode.Parse(d)).ToArray()),
        };
        if (probe.Outputs.Frames is { } frames)
        {
            o["frames"] = JsonNode.Parse(frames);
        }

        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    /// <summary>A kept probe read again; null when it isn't of this version.</summary>
    internal static Probe? Parse(byte[] kept)
    {
        if (JsonNode.Parse(kept) is not JsonObject o || o["version"]?.GetValue<int>() != Version || o["mediaType"]?.GetValue<string>() is not { Length: > 0 } type
            || o["size"]?.GetValue<long>() is not { } size || size < 0 || o["streams"] is not JsonObject streams)
        {
            return null;
        }

        static byte[] Raw(JsonNode n) => Encoding.UTF8.GetBytes(n.ToJsonString());
        var data = (o["data"] as JsonArray ?? []).OfType<JsonNode>().Select(Raw).ToList();
        return Probe.Parse(type, size, new Outputs(Raw(streams), data, o["frames"] is JsonNode f ? Raw(f) : null));
    }

    // Every string member or entry of a JSON tree that holds dir removed.
    private static void Scrub(JsonNode? v, string dir)
    {
        switch (v)
        {
            case JsonObject o:
                foreach (var (k, e) in o.ToList())
                {
                    if (e is JsonValue s && s.TryGetValue<string>(out var str) && str.Contains(dir, StringComparison.Ordinal))
                    {
                        o.Remove(k);
                    }
                    else
                    {
                        Scrub(e, dir);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue s && s.TryGetValue<string>(out var str) && str.Contains(dir, StringComparison.Ordinal))
                    {
                        a[i] = string.Empty;
                    }
                    else
                    {
                        Scrub(a[i], dir);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// A header read's probe (one ffprobe run of -show_format -show_streams
    /// -show_chapters -show_data, the format of the read made for Overfin) as
    /// Describe's own probe makes it: the first run without the data, the
    /// second the records of the video streams (attached pictures left out),
    /// and of the audio streams when one is AAC; format.filename dropped; the
    /// output cap applied to each. As Overfin's copies.Convert.
    /// </summary>
    public static Probe Convert(string mediaType, long size, JsonObject tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        tree = (JsonObject)tree.DeepClone();
        (tree["format"] as JsonObject)?.Remove("filename");
        var video = new JsonArray();
        var audio = new JsonArray();
        var aac = false;
        foreach (var e in tree["streams"] as JsonArray ?? [])
        {
            if (e is not JsonObject s)
            {
                continue;
            }

            var data = new JsonObject { ["index"] = s["index"]?.DeepClone() };
            foreach (var k in new[] { "extradata", "extradata_size", "extradata_hash" })
            {
                if (s[k] is { } v)
                {
                    if (k != "extradata_hash")
                    {
                        data[k] = v.DeepClone();
                    }

                    s.Remove(k);
                }
            }

            var type = s["codec_type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : string.Empty;
            if (type == "video")
            {
                var attached = s["disposition"]?["attached_pic"] is JsonValue av && av.ToJsonString() == "1";
                if (!attached)
                {
                    video.Add(data);
                }
            }
            else if (type == "audio")
            {
                audio.Add(data);
                aac = aac || (s["codec_name"] is JsonValue cv && cv.TryGetValue<string>(out var cn) && cn == "aac");
            }
        }

        var first = Encoding.UTF8.GetBytes(tree.ToJsonString());
        var runs = new List<byte[]>();
        if (video.Count > 0)
        {
            runs.Add(Encoding.UTF8.GetBytes(new JsonObject { ["streams"] = video }.ToJsonString()));
        }

        if (aac)
        {
            runs.Add(Encoding.UTF8.GetBytes(new JsonObject { ["streams"] = audio }.ToJsonString()));
        }

        if (first.Length > MaxOutput || runs.Any(r => r.Length > MaxOutput))
        {
            throw new DescribeException(DescribeError.Probe, "a probe past the output cap");
        }

        return Probe.Parse(mediaType, size, new Outputs(first, runs));
    }
}
