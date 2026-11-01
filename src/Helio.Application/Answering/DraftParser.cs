using System.Text.Json;
using System.Text.Json.Serialization;
using Helio.Domain;

namespace Helio.Application.Answering;

/// <summary>Parses and validates the model's JSON reply into a <see cref="ModelDraft"/>. Tolerates code fences and leading prose.</summary>
public static class DraftParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModelDraft? TryParse(string raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "empty_response";
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            error = "no_json_object";
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<DraftDto>(json, Options);
            if (dto is null)
            {
                error = "null_object";
                return null;
            }

            if (dto.Answer is null)
            {
                error = "missing_answer";
                return null;
            }

            if (dto.Confidence is < 0 or > 1 || double.IsNaN(dto.Confidence))
            {
                error = "confidence_out_of_range";
                return null;
            }

            var citations = (dto.Citations ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToArray();

            return new ModelDraft(dto.Answer.Trim(), citations, dto.Confidence, dto.Abstained);
        }
        catch (JsonException ex)
        {
            error = "invalid_json: " + ex.Message;
            return null;
        }
    }

    /// <summary>Finds the first balanced JSON object in the text, ignoring braces inside strings.</summary>
    internal static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
                default:
                    break;
            }
        }

        return null;
    }

    private sealed record DraftDto(
        [property: JsonPropertyName("answer")] string? Answer,
        [property: JsonPropertyName("citations")] string[]? Citations,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("abstained")] bool Abstained);
}
