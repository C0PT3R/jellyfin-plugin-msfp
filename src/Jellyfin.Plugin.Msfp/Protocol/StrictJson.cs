using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>
/// Federation bodies as MSFP section 2.1 reads them: I-JSON (RFC 7493).
/// UTF-8 without a byte order mark; no surrogate code points, escaped or
/// not, and no noncharacters; no object with the same member name twice,
/// names compared after their escapes are decoded. Integers have one form.
/// </summary>
public static class StrictJson
{
    /// <summary>The largest integer MSFP allows, 2^53 − 1.</summary>
    public const long MaxInteger = 9_007_199_254_740_991;

    /// <summary>Parses a body, or refuses it when it isn't I-JSON.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out JsonDocument? document)
    {
        document = null;
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            return false;
        }

        if (!System.Text.Unicode.Utf8.IsValid(utf8))
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var names = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        names.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        names.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        names.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!;
                        if (!IsCleanText(name) || names.Peek() is not { } seen || !seen.Add(name))
                        {
                            return false;
                        }

                        break;
                    case JsonTokenType.String:
                        if (!IsCleanText(reader.GetString()!))
                        {
                            return false;
                        }

                        break;
                }
            }

            document = JsonDocument.Parse(utf8.ToArray());
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            document?.Dispose();
            document = null;
            return false;
        }
    }

    /// <summary>
    /// An integer in its one form: a JSON number with no fraction, no
    /// exponent, not -0, within ±(2^53 − 1).
    /// </summary>
    public static bool TryGetInteger(JsonElement e, out long value)
    {
        value = 0;
        if (e.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var text = e.GetRawText();
        var digits = text.StartsWith('-') ? text[1..] : text;
        if (digits.Length == 0 || digits.Length > 16 || text == "-0" || (digits.Length > 1 && digits[0] == '0'))
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
            || value > MaxInteger || value < -MaxInteger)
        {
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>Text with no lone surrogates and no noncharacters.</summary>
    internal static bool IsCleanText(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }

            if (cp is >= 0xFDD0 and <= 0xFDEF || (cp & 0xFFFE) == 0xFFFE)
            {
                return false;
            }
        }

        return true;
    }
}
