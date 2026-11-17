// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tekium.Embedded.Tests.Json;

/// <summary>
/// Los documentos JSON: qué se acepta, qué guarda cada forma (el texto tal cual, o la binaria normalizada),
/// y que leer por una ruta dé lo mismo que navegar el documento con System.Text.Json.
/// </summary>
public sealed class TkJsonTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("-0.5e-3")]
    [InlineData("\"é\\n\\u00e9\"")]
    [InlineData(" { \"a\" : [ 1 , 2 ] } ")]
    [InlineData("{\"a\":1,\"a\":2}")]
    public void A_valid_document_is_kept_as_it_was_written_and_its_binary_form_reads_the_same(string text)
    {
        var json = TkJson.Parse(text);
        Assert.Equal(TkJsonFormat.Text, json.Format);
        Assert.Equal(text, json.ToString());

        var binary = json.ToBinary();
        Assert.Equal(TkJsonFormat.Binary, binary.Format);
        Assert.Equal(binary, TkJson.Parse(binary.ToString()).ToBinary());
        Assert.Equal(binary.ToBinary(), binary);
        Assert.Equal(binary, TkJson.FromBinary(binary.GetBinary()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{a:1}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("// note\n1")]
    [InlineData("'x'")]
    [InlineData("{\"a\":1} {}")]
    [InlineData("NaN")]
    [InlineData("01")]
    [InlineData("\"\\ud800\"")]
    public void What_is_not_strict_json_is_refused_saying_so(string text)
    {
        var failure = Assert.Throws<FormatException>(() => TkJson.Parse(text));
        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
        Assert.False(TkJson.TryParse(text, out _));
    }

    [Fact]
    public void The_binary_form_sorts_the_keys_keeps_the_last_of_a_repeated_one_and_writes_each_number_once()
    {
        Assert.Equal("""{"a":3,"b":1}""", TkJson.Parse("""{"b":1,"a":2,"a":3}""").ToBinary().ToString());
        Assert.Equal("""[1.5,100,0,12345678901234567890,1e+400,-2.5e-7,0.001]""", TkJson.Parse("[1.50, 1e2, -0, 12345678901234567890, 1e400, -25e-8, 0.0010]").ToBinary().ToString());
        Assert.Equal("""{"x":"é\n\"\u0001"}""".Replace("\u0001", "\\u0001", StringComparison.Ordinal), TkJson.Parse("""{"x":"\u00e9\n\"\u0001"}""").ToBinary().ToString());

        // Iguales lo que dicen lo mismo, en cualquier forma; distintos los que no.
        Assert.Equal(TkJson.Parse("""{"a":[1,{"c":null}],"b":true}"""), TkJson.Parse(""" { "b" : true, "a" : [ 1.0, { "c" : null } ] } """));
        Assert.NotEqual(TkJson.Parse("[1,2]"), TkJson.Parse("[2,1]"));
        Assert.NotEqual(TkJson.Parse("1"), TkJson.Parse("\"1\""));
        Assert.Equal(TkJson.Parse("null"), default);
        Assert.Equal("null", default(TkJson).ToString());
        Assert.Equal(JsonValueKind.Null, default(TkJson).ValueKind);
    }

    [Fact]
    public void Reading_by_a_path_gives_what_navigating_the_document_gives()
    {
        var random = new Random(31);
        for (var round = 0; round < 400; round++)
        {
            var node = RandomNode(random, 0);
            var text = node?.ToJsonString() ?? "null";
            var forms = new[] { TkJson.Parse(text), TkJson.Parse(text).ToBinary() };
            foreach (var path in Paths(node, random))
            {
                var expected = Navigate(node, path, out var found);
                foreach (var json in forms)
                {
                    Assert.Equal(found, json.TryGet(TkJsonPath.Parse(path), out var value));
                    if (found)
                    {
                        Assert.Equal(expected?.GetValueKind() ?? JsonValueKind.Null, value.ValueKind);
                        Assert.Equal(TkJson.Parse(expected?.ToJsonString() ?? "null"), value);
                    }
                }
            }
        }
    }

    [Fact]
    public void A_scalar_is_read_as_the_value_it_is()
    {
        var json = TkJson.Parse("""{"n":12,"big":12345678901234567890,"f":1.50,"huge":1e400,"s":"hé","t":true,"z":null,"o":{"p":[7]}}""");
        foreach (var form in new[] { json, json.ToBinary() })
        {
            Assert.Equal(12L, Get(form, "$.n").GetInt64());
            Assert.Equal(12345678901234567890m, Get(form, "$.big").GetDecimal());
            Assert.Equal(1.50m, Get(form, "$.f").GetDecimal());
            Assert.Equal(double.PositiveInfinity, Get(form, "$.huge").GetDouble());
            Assert.Equal("hé", Get(form, "$.s").GetString());
            Assert.True(Get(form, "$.t").GetBoolean());
            Assert.Equal(JsonValueKind.Null, Get(form, "$.z").ValueKind);
            Assert.Equal(7L, Get(form, "$.o.p[0]").GetInt64());
            Assert.Equal(7L, Get(form, "$.o.p[#-1]").GetInt64());
            Assert.False(form.TryGet(TkJsonPath.Parse("$.o.p[1]"), out _));
            Assert.False(form.TryGet(TkJsonPath.Parse("$.s.x"), out _));
            Assert.Throws<InvalidOperationException>(() => Get(form, "$.s").GetInt64());
            Assert.Throws<OverflowException>(() => Get(form, "$.big").GetInt64());
        }

        // En el texto, una clave repetida da la primera, como SQLite; en la forma binaria queda la última, como PostgreSQL.
        var repeated = TkJson.Parse("""{"a":1,"a":2}""");
        Assert.Equal(1L, Get(repeated, "$.a").GetInt64());
        Assert.Equal(2L, Get(repeated.ToBinary(), "$.a").GetInt64());

        // El texto de un valor dentro de un documento de texto es el que tenía.
        Assert.Equal("1.50", Get(json, "$.f").ToString());
        Assert.Equal("1.5", Get(json.ToBinary(), "$.f").ToString());
    }

    [Fact]
    public void Paths_are_canonical_and_what_is_not_a_path_says_where()
    {
        Assert.Equal("$.a.\"b c\"[2][#-1].\"x.y\".\"q\\\"\"", TkJsonPath.Parse("$.\"a\".\"b c\"[2][#-1].\"x.y\".\"q\\\"\"").Text);
        Assert.Equal(TkJsonPath.Parse("$.a"), TkJsonPath.Parse("$.\"a\""));
        Assert.Equal("$", TkJsonPath.Root.Text);
        foreach (var bad in new[] { "", "a", "$.", "$..a", "$[", "$[x]", "$[-1]", "$[#-0]", "$a", "$.\"open" })
        {
            Assert.Contains("is not a JSON path", Assert.Throws<FormatException>(() => TkJsonPath.Parse(bad)).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_value_of_the_application_goes_in_and_comes_out_through_its_json_type_info()
    {
        var product = new Product("p-1", 12.5m, ["red", "big"]);
        var json = TkJson.Serialize(product, ProductContext.Default.Product);
        Assert.Equal("p-1", Get(json, "$.Sku").GetString());
        Assert.Equal(product.Tags, json.Deserialize(ProductContext.Default.Product)!.Tags);
        Assert.Equal(product with { Tags = product.Tags }, json.ToBinary().Deserialize(ProductContext.Default.Product)! with { Tags = product.Tags });
        Assert.Equal("big", json.ToElement().GetProperty("Tags")[1].GetString());
    }

    [Fact]
    public void Damaged_binary_bytes_are_reported_as_damage_and_never_misread_beyond_them()
    {
        var binary = TkJson.Parse("""{"name":"x","list":[1,2.5,"three",{"deep":[true,null]}],"n":-12345678901234567890}""").ToBinary().GetBinary().ToArray();
        for (var i = 0; i < binary.Length; i++)
        {
            foreach (var change in new byte[] { 0x00, 0xFF, 0x01, 0x80 })
            {
                var damaged = (byte[])binary.Clone();
                if (damaged[i] == change)
                {
                    continue;
                }

                damaged[i] = change;
                try
                {
                    var json = TkJson.FromBinary(damaged);

                    // Si pasó la comprobación es un documento entero: se lee de punta a punta sin fallar.
                    _ = json.ToString();
                    _ = json.TryGet(TkJsonPath.Parse("$.list[3].deep[1]"), out _);
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        Assert.Throws<InvalidDataException>(() => TkJson.FromBinary(binary.AsSpan(0, binary.Length - 1)));
        Assert.Throws<InvalidDataException>(() => TkJson.FromBinary([]));
    }

    private static TkJson Get(TkJson json, string path) => json.TryGet(TkJsonPath.Parse(path), out var value) ? value : throw new KeyNotFoundException(path);

    private static JsonNode? RandomNode(Random random, int depth)
    {
        var kind = depth > 3 ? random.Next(5) : random.Next(7);
        return kind switch
        {
            0 => null,
            1 => JsonValue.Create(random.Next(2) == 0),
            2 => JsonValue.Create(random.Next(-1000, 1000)),
            3 => JsonValue.Create(Math.Round(random.NextDouble() * 1000, 3)),
            4 => JsonValue.Create(random.Next(4) == 0 ? "a\"b\\c\n" : $"s{random.Next(50)}"),
            5 => new JsonArray([.. Enumerable.Range(0, random.Next(5)).Select(_ => RandomNode(random, depth + 1))]),
            _ => Object(random, depth),
        };

        static JsonObject Object(Random random, int depth)
        {
            var result = new JsonObject();
            foreach (var name in Enumerable.Range(0, random.Next(5)).Select(i => i == 3 ? "with space" : $"k{random.Next(8)}").Distinct())
            {
                result[name] = RandomNode(random, depth + 1);
            }

            return result;
        }
    }

    /// <summary>Paths to every value of the document, and some that lead nowhere.</summary>
    private static IEnumerable<string> Paths(JsonNode? node, Random random)
    {
        var found = new List<string> { "$" };
        Walk(node, "$");
        return found.Concat(["$.nothing", "$[9]", "$.k1.k2.k3", "$[#-9]"]);

        void Walk(JsonNode? current, string path)
        {
            switch (current)
            {
                case JsonObject value:
                    foreach (var (name, child) in value)
                    {
                        var step = name.Contains(' ', StringComparison.Ordinal) ? $"{path}.\"{name}\"" : $"{path}.{name}";
                        found.Add(step);
                        Walk(child, step);
                    }

                    break;
                case JsonArray value:
                    for (var i = 0; i < value.Count; i++)
                    {
                        found.Add($"{path}[{i}]");
                        found.Add($"{path}[#-{value.Count - i}]");
                        Walk(value[i], $"{path}[{i}]");
                    }

                    break;
            }
        }
    }

    private static JsonNode? Navigate(JsonNode? node, string path, out bool found)
    {
        found = true;
        var parsed = TkJsonPath.Parse(path);
        var current = node;
        foreach (var segment in parsed.Text.Length == 1 ? [] : Split(parsed.Text[1..]))
        {
            switch (current)
            {
                case JsonObject value when segment.Name is { } name && value.TryGetPropertyValue(name, out var child):
                    current = child;
                    break;
                case JsonArray value when segment.Name is null && (segment.FromEnd ? value.Count - segment.Index : segment.Index) is var at && at >= 0 && at < value.Count:
                    current = value[at];
                    break;
                default:
                    found = false;
                    return null;
            }
        }

        return current;

        static IEnumerable<(string? Name, int Index, bool FromEnd)> Split(string rest)
        {
            var i = 0;
            while (i < rest.Length)
            {
                if (rest[i] == '[')
                {
                    var close = rest.IndexOf(']', i);
                    var inside = rest[(i + 1)..close];
                    yield return inside.StartsWith('#')
                        ? (null, int.Parse(inside[2..], CultureInfo.InvariantCulture), true)
                        : (null, int.Parse(inside, CultureInfo.InvariantCulture), false);
                    i = close + 1;
                }
                else if (rest[i + 1] == '"')
                {
                    var close = rest.IndexOf('"', i + 2);
                    yield return (rest[(i + 2)..close], 0, false);
                    i = close + 1;
                }
                else
                {
                    var end = i + 1;
                    while (end < rest.Length && rest[end] is not ('.' or '['))
                    {
                        end++;
                    }

                    yield return (rest[(i + 1)..end], 0, false);
                    i = end;
                }
            }
        }
    }
}

public sealed record Product(string Sku, decimal Price, string[] Tags);

[JsonSerializable(typeof(Product))]
public sealed partial class ProductContext : JsonSerializerContext;
