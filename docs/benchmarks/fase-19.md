# Fase 19 — columnas json y jsonb

**Criterio de salida cumplido.** Un documento inválido se rechaza al escribir; json devuelve el texto exacto y jsonb el normalizado; dos jsonb iguales comparan iguales; cada lectura por ruta da lo mismo que `json_extract` de SQLite sobre los mismos documentos; un índice sobre una ruta lo mantienen igual SQL y la API tipada, y una consulta por esa ruta lo usa; un predicado LINQ por ruta da lo mismo que sobre objetos; EF Core guarda y consulta por ruta como el proveedor de SQLite. Windows no se ha verificado.

Hecho el 2026-11-17.

## Lo que decidiste

| Pregunta | Decisión |
| --- | --- |
| Qué diferencia a json de jsonb | Como PostgreSQL: json guarda el texto tal cual, jsonb una forma binaria normalizada |
| Qué se puede hacer con un documento | Leer por ruta en SQL, índices sobre una ruta, predicados en la LINQ propia y EF Core: las cuatro |
| Con qué tipo de C# | Un `TkJson` propio, inmutable |
| La versión de formato del archivo | No se sube: los tipos 22 y 23 se agregan al final, como las fases anteriores agregaron entradas al catálogo. Un build anterior da por dañada una tabla con una columna JSON en vez de leerla mal; un archivo sin JSON se sigue abriendo |

## Qué hay

### El documento: `TkJson`

- **Se comprueba al crearlo**, con el lector estricto de System.Text.Json. No acepta comentarios, comas de más, varios valores ni JSON5 (`{a:1}`), ni un escape que sea medio par sustituto.
- **Se comprueba también al leerlo de una fila.** Un documento dañado lanza `InvalidDataException`, nunca se lee mal.
- **Dos formas:**
  - `Text`: los bytes tal como se escribieron.
  - `Binary`: claves ordenadas por sus bytes y sin repetir (gana la última), cada número en una sola forma exacta, con tablas de desplazamientos.
  - Dos jsonb iguales tienen los mismos bytes.
- **`default(TkJson)` es el documento `null`**, que no es una columna sin valor (`TkJson?` con `null`).
- **La igualdad compara lo que dicen**, en cualquier forma.
- **Lectura:**
  - Por ruta: `TryGet(TkJsonPath, out TkJson)`.
  - El valor de un documento: `GetString`, `GetBoolean`, `GetInt64`, `GetDecimal`, `GetDouble` y `GetNumber` (lo más exacto que quepa).
  - Las dos de los índices: `TextAt(path)` y `NumberAt(path)`.
- **Con la aplicación:**
  - Valores de la aplicación con `Serialize`/`Deserialize` y un `JsonTypeInfo`, el del generador de System.Text.Json, que no usa reflexión.
  - `ToElement()` da un `JsonElement`.
- **Rutas:** `$`, `.nombre`, `."cualquier nombre"`, `[2]` y `[#-1]` para el último. `TkJsonPath` las escribe en forma canónica, y eso es lo que compara un índice.

### La forma binaria es formato de archivo

La describe `JsonBinary`. Empieza con un byte de versión y sigue con el valor:

- **Escalares:** null, false y true son un byte.
- **Número:** signo, dígitos en ASCII sin ceros de más y la potencia de diez.
- **Texto:** su longitud y los bytes UTF-8.
- **Arreglo:** su tamaño, la cantidad, dónde empieza cada elemento y los elementos.
- **Objeto:** igual, con dónde empieza cada clave y cada valor; las claves ordenadas.

`JsonBinary.Verify` comprueba todo antes de usarlo, y la prueba daña cada byte de un documento.

### En la API tipada

- **Las columnas:** una propiedad `TkJson` es una columna `jsonb`; con `[TkJsonText]`, `json`.
- **Rechazos del generador:**
  - Una columna JSON como clave (TK0005).
  - Un índice sobre el documento entero (TK0024).
  - `[TkJsonText]` en otra cosa (TK0025).
  - `[TkJsonIndex]` mal puesto (TK0026).
- **`[TkJsonIndex("$.ruta", Type = …, Name = …, Unique = …)]`** declara un índice sobre un valor en una ruta, de tipo TEXT o DOUBLE.

### En SQL

- **Tipos `JSON` y `JSONB`.** Al guardar, un texto se convierte en documento y se comprueba; uno que no es JSON se rechaza nombrando la columna.
- **Lectura por ruta:**
  - `->>` y `JSON_EXTRACT` dan el valor de SQL: NULL, booleano, número, texto, o el texto de un arreglo u objeto.
  - `->` da el valor como documento, y se encadena.
  - `JSON_VALUE` da sólo escalares y `JSON_QUERY` sólo arreglos y objetos.
  - También `JSON_EXISTS`, `JSON_TYPE`, `JSON(texto)` y `JSONB(texto)`, y `CAST(… AS JSONB)`.
  - Después de una flecha, un nombre o una posición sola es una ruta, como en SQLite.
- **El texto que devuelve:** el de un json no tiene espacios y conserva el orden, las claves repetidas y el texto de los números, como SQLite; el de un jsonb es su forma canónica.
- **Documentos enteros:** `=` compara lo que dicen. `ORDER BY` los ordena por su texto canónico: un orden fijo, no uno con sentido.
- **`ALTER COLUMN`** pasa de texto a json, de json a jsonb y al revés, si cada fila es un documento.
- **Índices sobre una ruta:** `CREATE INDEX i ON t (JSON_VALUE(col, '$.ruta') [AS DOUBLE])` o `(col ->> '$.ruta')`.
  - Es el mismo índice que declara `[TkJsonIndex]`, byte por byte: un campo de clase 2 con el nombre de la columna y la ruta canónica.
  - Los dos lados calculan cada entrada con la misma función, `TkJsonIndexing.Value`.
  - El planificador lo usa para `col ->> 'ruta'`, `JSON_VALUE` y `JSON_EXTRACT` comparados con un valor, de la misma columna y la misma ruta.
- **Lo que guarda cada índice:**
  - Uno TEXT guarda los textos, y el texto de arreglos y objetos, que es lo que `->>` compara.
  - Uno DOUBLE guarda los números. Un DOUBLE redondea los enteros grandes, así que sus límites se hacen inclusivos: el índice da de más y el WHERE, que se vuelve a comprobar en cada fila, decide.
  - Una fila sin valor allí, o con uno de otra clase, tiene su entrada con NULL, como una columna anulable.
- **Fases 17 y 18 con índices por ruta:**
  - Renombrar la columna lo renombra en el índice.
  - Quitarla se rechaza.
  - Dejar de ser JSON se rechaza.
  - Pasar de json a jsonb lo reconstruye.
- **Un índice creado con SQL entre dos escrituras tipadas** de la misma transacción ahora lo mantiene la segunda. El escritor no volvía a mirar los índices después de un `CREATE INDEX`, y la escritura tipada siguiente no le daba entrada. Lo encontré al revisar el diseño, con su prueba.

### En la LINQ propia

`p.Attributes.TextAt("$.sku") == "a-1"` y `p.Attributes.NumberAt("$.price") < 20` se traducen al campo del índice que declara esa ruta con ese tipo, y lo usan. Una ruta sin `[TkJsonIndex]`, o leída de la otra clase, se rechaza diciendo qué. La regla de la capa es que nunca filtra en memoria.

### En EF Core

- **Mapeo:**
  - Una propiedad `TkJson` va a `JSONB`, o a `JSON` con `HasColumnType("JSON")`.
  - Una `JsonElement` va a `JSONB`.
  - Una `string` va a `JSON` o `JSONB` si la columna lo dice.
- **Funciones:** `EF.Functions.JsonValue` y `EF.Functions.JsonExists`.
- **Funciones de la aplicación:** una función mapeada con `HasDbFunction` (por ejemplo a `JSON_EXTRACT`) funciona. El SQL de TekiumDB ahora acepta un nombre de función entre comillas, que es como EF Core lo escribe.

## Mediciones

`dotnet run -c Release --project tests/TekiumDB.Benchmarks -- json 100000`, en macOS (arm64), en memoria. El documento tiene doce claves y un arreglo de veinte objetos: 1 041 bytes de texto y 2 245 en binario. Mejor de cinco corridas.

| Qué | json (texto) | jsonb (binario) |
| --- | ---: | ---: |
| Leer `$.k03` (una clave del primer nivel) | 273 ns | 109 ns |
| Leer `$.items[18].tags[2]` (tres niveles, hacia el final) | 3 028 ns | 54 ns |
| Comprobar el documento leído de una fila | 3 446 ns | 1 817 ns |
| Consulta SQL por `$.k03` sin índice (100 000 filas, recorre todas) | 392 ms | 287 ms |
| La misma consulta con un índice sobre la ruta | 4,6 ms | 2,7 ms |

Lo que dicen:

- **Leer por ruta:** jsonb no analiza el documento, va por los desplazamientos. Una ruta profunda pasa de 3 µs a 54 ns.
- **Comprobar el documento al leerlo es lo que más cuesta,** y se hace en cada fila que se lee, en las dos formas. Por eso la consulta sin índice cuesta unos 3–4 µs por fila.
- **El índice hace que la consulta no lea las filas que no le sirven:** cien veces menos.
- **La forma binaria ocupa algo más del doble que el texto** (2 245 contra 1 041 bytes en este documento): cuatro bytes por cada desplazamiento y por cada potencia de diez. No la achiqué. Sería otro formato, y no hay una medición que diga que el espacio importe más que la lectura.
- **El camino de lectura de filas sin JSON** no cambió: sólo se agregó un caso al `switch` que salta columnas. No lo medí con `PointReadBenchmarks`.

## Lo que hay que saber

- **Comparar un texto con un número falla**, como ya pasaba en SQL: `doc ->> 'sku' = 'x'` lanza si alguna fila tiene un número en esa ruta. SQLite diría falso. Pasa igual con un booleano y un número.
- **Una clave repetida:** en json, leer por ruta da su primer valor, como SQLite; en jsonb queda el último, como PostgreSQL. Pasar de json a jsonb conserva el último.
- **`JSON_TYPE` distingue `integer` de `real`:** en json por cómo está escrito el número, como SQLite; en jsonb por su valor (`1.0` es `integer`).
- **Un número enorme o con muchos dígitos** se lee exacto como `DECIMAL` si cabe y como `DOUBLE` si no (`1e400` es infinito), como hace SQLite con los que no caben en un entero.
- **Un índice sobre una ruta es de una sola ruta;** no hay índices de varias rutas ni de una ruta y una columna.
- **Sin probar:** EF Core con índices sobre rutas desde migraciones (se crean con SQL o con la API tipada), parámetros de procedimientos almacenados y de funciones de C# de tipo JSON, y `ToJson()` de tipos poseídos, que quedó fuera del alcance.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Lo que se acepta y lo que no; las dos formas; igualdad; 400 documentos al azar leídos por cada ruta suya y otras, contra System.Text.Json; dañar cada byte de la forma binaria | `TkJsonTests` |
| Ida y vuelta de las dos columnas en el tipo que tiene todos los tipos; el formato de fila | `RoundTripTests`, `RowFormatTests` |
| Diagnósticos del generador | `TableGeneratorTests` |
| SQL: lo que guarda cada columna, igualdad, orden, rechazos, `ALTER COLUMN`; más de 500 lecturas por ruta contra SQLite; `JSON_VALUE`, `JSON_QUERY`, `JSON_EXISTS`, `JSON_TYPE` | `SqlJsonTests` |
| El índice sobre una ruta: el mismo desde SQL y desde el tipo, los dos lo mantienen, la unicidad; 700 escrituras al azar con valores de toda clase en las rutas y consultas por el índice contra un modelo; renombrar, cambiar y quitar la columna | `JsonIndexTests` |
| LINQ por ruta contra el mismo lambda sobre objetos, el plan, y los rechazos | `JsonLinqTests` |
| EF Core: consultas por ruta contra SQLite; `TkJson` y `JsonElement` de ida y vuelta; las funciones del proveedor | `EfJsonTests` |
| Native AOT | El smoke: una columna jsonb con índice por ruta, buscada desde LINQ y leída desde SQL |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

Se prepararon 63 roturas del código (una condición invertida, una comprobación quitada, un caso tomado por otro) y se corrió cada una contra las pruebas de JSON, SQL, LINQ, el generador y EF Core. Se detectan 62. La que queda es equivalente: quitar el atajo `a == b` al comparar dos documentos en SQL deja la comparación por el texto canónico, que da cero exactamente cuando los documentos son iguales.

Catorce sobrevivieron la primera vez. Trece se detectan ahora con una prueba nueva; la otra es la equivalente:

- `JsonBinary.Verify` aceptaba claves repetidas, números con un cero al final, bytes de más después del valor y cuentas que no estaban en su forma más corta. La prueba que daña cada byte no las cubría: el daño no rompe el documento, sólo lo deja en una forma que no es la canónica. Ahora hay un caso para cada una (`TkJsonTests`).
- El texto aceptaba una marca de orden de bytes al principio, un documento de texto leído de una fila no se comprobaba y un número entero salía como decimal (`TkJsonTests`).
- Una ruta guardada que no estaba en su forma canónica se usaba. Con un `DOUBLE`, los límites no se hacían inclusivos. Una constante numérica se buscaba en un índice de texto. Una ruta del otro lado de la comparación no se invertía. Con esas tres roturas el índice daba menos filas que el recorrido. Ahora `JsonIndexTests` compara cada consulta contra la misma tabla sin índices, con números que un `DOUBLE` redondea.
- Una columna con un índice por ruta podía dejar de ser JSON, y `RETURNING` devolvía un documento jsonb en la forma de texto con que llegó.

Una rotura no compilaba en su primera forma y se reescribió.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 968 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh` | 968 pasan, 3 explícitas omitidas; smoke de AOT pasa |
| Windows | — | Sin verificar |
