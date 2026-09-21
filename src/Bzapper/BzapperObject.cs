using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bzapper.Internal;

namespace Bzapper;

/// <summary>
/// Base de todo modelo da SDK (corpos de requisição e respostas).
/// <list type="bullet">
/// <item>Propriedade <c>null</c> (o padrão) = <b>omitida</b>: o campo não vai no corpo.</item>
/// <item>Para enviar <c>null</c> explícito (limpar um campo num PATCH/PUT), adicione o campo em
/// <see cref="ClearFields"/> — pelo nome C# (<c>nameof</c>) ou pelo nome JSON. Se o campo tiver valor, vale o valor.</item>
/// <item>Campos que a API devolver e esta versão não conhece ficam em <see cref="AdditionalFields"/> (nunca é erro);
/// o que você puser lá também é enviado.</item>
/// </list>
/// </summary>
/// <example>
/// <code>
/// // Troca o nome e LIMPA o e-mail do contato; o resto fica como está.
/// await bz.UpdateContactAsync(id, new UpdateContact
/// {
///     Name = "Ana Souza",
///     ClearFields = { nameof(UpdateContact.Email) },
/// });
/// </code>
/// </example>
public abstract class BzapperObject
{
    private ClearFieldSet? _clearFields;

    /// <summary>Campos que a spec não declara: os que a API devolveu a mais, e os extras que você quer enviar.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }

    /// <summary>
    /// Campos a enviar como <c>null</c> explícito (limpar). Aceita o nome da propriedade
    /// (<c>nameof(UpdateContact.Email)</c>) ou o nome JSON (<c>"email"</c>); nome desconhecido lança
    /// <see cref="ArgumentException"/> na hora.
    /// </summary>
    [JsonIgnore]
    public ICollection<string> ClearFields => _clearFields ??= new ClearFieldSet(GetType());

    internal JsonObject ToJsonObject()
    {
        var node = JsonSerializer.SerializeToNode(this, GetType(), BzapperJson.Options) as JsonObject ?? new JsonObject();
        ApplyClears(this, node);
        return node;
    }

    internal string ToJsonString() => ToJsonObject().ToJsonString(BzapperJson.Options);

    private static void ApplyClears(BzapperObject value, JsonObject node)
    {
        if (value._clearFields is { Count: > 0 } clears)
        {
            foreach (var wire in clears)
            {
                if (!node.ContainsKey(wire))
                {
                    node[wire] = null;
                }
            }
        }

        foreach (var field in FieldMap.For(value.GetType()).Fields)
        {
            if (field.Property.GetValue(value) is BzapperObject child && node[field.WireName] is JsonObject childNode)
            {
                ApplyClears(child, childNode);
            }
        }
    }
}

internal sealed class FieldMap
{
    private static readonly ConcurrentDictionary<Type, FieldMap> Cache = new ConcurrentDictionary<Type, FieldMap>();
    private readonly Dictionary<string, Field> _byName = new Dictionary<string, Field>(StringComparer.Ordinal);
    private readonly Type _type;

    private FieldMap(Type type)
    {
        _type = type;
        var fields = new List<Field>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (name is null)
            {
                continue;
            }

            var field = new Field(property, name.Name);
            fields.Add(field);
            _byName[field.WireName] = field;
            _byName[property.Name] = field;
        }

        Fields = fields;
    }

    internal IReadOnlyList<Field> Fields { get; }

    internal static FieldMap For(Type type) => Cache.GetOrAdd(type, static t => new FieldMap(t));

    internal bool TryResolve(string name, out Field field) => _byName.TryGetValue(name ?? string.Empty, out field!);

    internal Field Resolve(string name)
    {
        if (!TryResolve(name, out var field))
        {
            var known = string.Join(", ", Fields.Select(f => f.WireName));
            throw new ArgumentException($"'{name}' não é um campo de {_type.Name}. Campos: {known}.", nameof(name));
        }

        return field;
    }

    internal sealed class Field
    {
        internal Field(PropertyInfo property, string wireName)
        {
            Property = property;
            WireName = wireName;
        }

        internal PropertyInfo Property { get; }

        internal string WireName { get; }
    }
}

/// <summary>Conjunto de campos a limpar, normalizado para o nome JSON e validado ao adicionar.</summary>
internal sealed class ClearFieldSet : ICollection<string>
{
    private readonly Type _owner;
    private readonly HashSet<string> _wireNames = new HashSet<string>(StringComparer.Ordinal);

    internal ClearFieldSet(Type owner) => _owner = owner;

    public int Count => _wireNames.Count;

    public bool IsReadOnly => false;

    public void Add(string item) => _wireNames.Add(FieldMap.For(_owner).Resolve(item).WireName);

    public bool Contains(string item) => FieldMap.For(_owner).TryResolve(item, out var field) && _wireNames.Contains(field.WireName);

    public bool Remove(string item) => FieldMap.For(_owner).TryResolve(item, out var field) && _wireNames.Remove(field.WireName);

    public void Clear() => _wireNames.Clear();

    public void CopyTo(string[] array, int arrayIndex) => _wireNames.CopyTo(array, arrayIndex);

    public IEnumerator<string> GetEnumerator() => _wireNames.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
