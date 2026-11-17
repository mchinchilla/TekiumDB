// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Tests.Json;

/// <summary>Productos con sus atributos en un documento, indexado por dos rutas: una de texto, única, y una de número.</summary>
[TkTable("goods")]
public sealed partial record Good
{
    [TkKey]
    public int Id { get; set; }

    [TkJsonIndex("$.sku", Name = "by_sku", Unique = true)]
    [TkJsonIndex("$.\"price\"", Type = TkColumnType.F64, Name = "by_price")]
    public TkJson Doc { get; set; }

    public string Name { get; set; } = string.Empty;
}
