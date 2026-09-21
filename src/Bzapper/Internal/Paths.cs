using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Bzapper.Internal;

/// <summary>Parâmetros de caminho: percent-encoding por segmento (<c>abc 1</c> → <c>abc%201</c>, <c>/</c> → <c>%2F</c>).</summary>
internal static class PathSegment
{
    internal static string Encode(string value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (value.Length == 0)
        {
            throw new ArgumentException($"'{paramName}' não pode ser vazio.", paramName);
        }

        if (value == "." || value == "..")
        {
            throw new ArgumentException($"'{paramName}' não pode ser '.' nem '..'.", paramName);
        }

        // JIDs (5511999990000@s.whatsapp.net, 120363…@g.us) passam como vieram, só codificados.
        return Uri.EscapeDataString(value);
    }
}

/// <summary>
/// Query string: omite o que não foi informado; booleanos <c>true</c>/<c>false</c>; datas ISO 8601 UTC com <c>Z</c>;
/// listas como CSV (<c>style: form, explode: false</c> da spec).
/// </summary>
internal sealed class Query
{
    private readonly List<KeyValuePair<string, string>> _pairs = new List<KeyValuePair<string, string>>();

    internal Query Add(string name, string? value)
    {
        if (value is not null)
        {
            _pairs.Add(new KeyValuePair<string, string>(name, value));
        }

        return this;
    }

    internal Query Add(string name, int? value) =>
        value is null ? this : Add(name, value.Value.ToString(CultureInfo.InvariantCulture));

    internal Query Add(string name, long? value) =>
        value is null ? this : Add(name, value.Value.ToString(CultureInfo.InvariantCulture));

    internal Query Add(string name, double? value) =>
        value is null ? this : Add(name, value.Value.ToString("R", CultureInfo.InvariantCulture));

    internal Query Add(string name, bool? value) =>
        value is null ? this : Add(name, value.Value ? "true" : "false");

    internal Query Add(string name, DateTimeOffset? value) =>
        value is null ? this : Add(name, FormatDate(value.Value));

    internal Query Add(string name, IEnumerable<string>? values)
    {
        if (values is null)
        {
            return this;
        }

        var items = new List<string>();
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
            {
                items.Add(v);
            }
        }

        return items.Count == 0 ? this : Add(name, string.Join(",", items));
    }

    /// <summary><c>2026-09-01T03:00:00Z</c> (fração de segundo só quando houver).</summary>
    internal static string FormatDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    public override string ToString()
    {
        if (_pairs.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var pair in _pairs)
        {
            sb.Append(sb.Length == 0 ? '?' : '&')
              .Append(Uri.EscapeDataString(pair.Key))
              .Append('=')
              .Append(Uri.EscapeDataString(pair.Value));
        }

        return sb.ToString();
    }
}
