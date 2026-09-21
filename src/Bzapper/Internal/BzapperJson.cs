using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Bzapper.Internal;

internal static class BzapperJson
{
    /// <summary>
    /// Nomes vêm dos <c>[JsonPropertyName]</c> dos modelos; campos desconhecidos na resposta vão para
    /// <see cref="BzapperObject.AdditionalFields"/> (a API ganha campos sem aviso). Nulos não são enviados.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// Datas ISO 8601: lê qualquer fuso (string vazia = <c>null</c>) e escreve em UTC com <c>Z</c>
/// (<c>2026-09-21T12:00:00Z</c>, fração só quando houver).
/// </summary>
internal sealed class UtcDateTimeConverter : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("data esperada como string ISO 8601");
        }

        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return value;
        }

        throw new JsonException($"data inválida: '{raw}'");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(Query.FormatDate(value.Value));
    }
}
