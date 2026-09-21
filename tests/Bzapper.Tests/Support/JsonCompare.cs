using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bzapper.Tests.Support;

internal static class JsonCompare
{
    private static readonly Regex IsoDateTime = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}", RegexOptions.Compiled);

    /// <summary>
    /// Diferenças entre <paramref name="expected"/> e <paramref name="actual"/> (igualdade profunda; mesmo conjunto de
    /// chaves). Com <paramref name="lenient"/> (resultados re-serializados dos modelos tipados): uma chave esperada com
    /// valor <c>null</c> equivale a chave ausente (o modelo não re-emite nulos) e datas ISO no mesmo instante são iguais.
    /// </summary>
    public static List<string> Diff(JsonElement expected, JsonElement actual, bool lenient)
    {
        var diffs = new List<string>();
        Walk(expected, actual, "$", lenient, diffs);
        return diffs;
    }

    private static void Walk(JsonElement expected, JsonElement actual, string path, bool lenient, List<string> diffs)
    {
        if (expected.ValueKind != actual.ValueKind && !(IsBool(expected) && IsBool(actual) && expected.GetBoolean() == actual.GetBoolean()))
        {
            diffs.Add($"{path}: esperado {Show(expected)}, veio {Show(actual)}");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    if (!actual.TryGetProperty(property.Name, out var value))
                    {
                        if (!(lenient && property.Value.ValueKind == JsonValueKind.Null))
                        {
                            diffs.Add($"{path}.{property.Name}: ausente (esperado {Show(property.Value)})");
                        }

                        continue;
                    }

                    Walk(property.Value, value, $"{path}.{property.Name}", lenient, diffs);
                }

                foreach (var property in actual.EnumerateObject())
                {
                    if (!expected.TryGetProperty(property.Name, out _))
                    {
                        diffs.Add($"{path}.{property.Name}: não esperado (veio {Show(property.Value)})");
                    }
                }

                break;

            case JsonValueKind.Array:
                var e = expected.EnumerateArray().ToList();
                var a = actual.EnumerateArray().ToList();
                if (e.Count != a.Count)
                {
                    diffs.Add($"{path}: esperado {e.Count} itens, vieram {a.Count}");
                    return;
                }

                for (var i = 0; i < e.Count; i++)
                {
                    Walk(e[i], a[i], $"{path}[{i}]", lenient, diffs);
                }

                break;

            case JsonValueKind.String:
                var es = expected.GetString()!;
                var @as = actual.GetString()!;
                if (es == @as)
                {
                    return;
                }

                if (lenient && IsoDateTime.IsMatch(es) && IsoDateTime.IsMatch(@as)
                    && DateTimeOffset.TryParse(es, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ed)
                    && DateTimeOffset.TryParse(@as, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ad)
                    && ed == ad)
                {
                    return;
                }

                diffs.Add($"{path}: esperado {Show(expected)}, veio {Show(actual)}");
                break;

            case JsonValueKind.Number:
                var equal = expected.TryGetDecimal(out var ex) && actual.TryGetDecimal(out var ac)
                    ? ex == ac
                    : expected.GetDouble().Equals(actual.GetDouble());
                if (!equal)
                {
                    diffs.Add($"{path}: esperado {Show(expected)}, veio {Show(actual)}");
                }

                break;
        }
    }

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string Show(JsonElement e)
    {
        var raw = e.GetRawText();
        return raw.Length > 120 ? raw[..120] + "…" : raw;
    }
}
