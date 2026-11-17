# Fase 16 — proveedor de Entity Framework Core, y el SQL que necesitaba

**Criterio de salida cumplido en macOS y en Linux.** Sobre un modelo con entidades relacionadas, `EnsureCreatedAsync`, `SaveChangesAsync` (altas, cambios y bajas) y consultas con `Where`, `OrderBy`, `Skip`/`Take`, `Include`, `Any`, `Count` y `First` dan lo mismo que el proveedor de SQLite sobre los mismos datos. Windows no se ha verificado.

Hecho el 2026-11-17. El criterio lo fijaste al empezar la fase, junto con que el proveedor sea sólo asíncrono. Esta fase no tiene criterio de rendimiento y no se midieron tiempos.

## Qué hay

### En la capa SQL

EF Core escribe SQL que la Fase 12 no tenía. Se agregó al SQL, no se esquivó en el proveedor, así que también sirve a quien use SQL directamente o Dapper.

| Qué | Cómo |
| --- | --- |
| `LEFT [OUTER] JOIN` | La fila sin pareja se queda, con NULL en las columnas de la otra tabla |
| Una consulta en el `FROM` | `FROM (SELECT …) AS t`: se corre entera y se lee como una tabla |
| `EXISTS (…)`, `x [NOT] IN (SELECT …)`, `(SELECT …)` como valor | En el `WHERE`, en la lista de columnas y en el `ORDER BY`; también sin `FROM`. Pueden leer la fila de la consulta de afuera, y unas dentro de otras |
| `CASE … WHEN … THEN … ELSE … END` | Con o sin sujeto |
| `CAST(valor AS tipo)` | Como guardar el valor en una columna del tipo; además cualquier cosa pasa a texto y un texto que es un número pasa a número |
| `STARTSWITH`, `ENDSWITH`, `CONTAINS` | Comparan caracteres: las mayúsculas cuentan |
| `RETURNING` | En `INSERT`, `UPDATE` y `DELETE`: lo que se pide de cada fila escrita |
| Clave entera que no se da | En un `INSERT`, es la siguiente a la más alta de la tabla (1 si está vacía) |
| Alias en `UPDATE` y `DELETE` | `DELETE FROM t AS x WHERE x.id = …` |

**Cómo corre una subconsulta.** Para cada fila de la consulta de afuera se corre la de adentro y su resultado queda en una posición propia de la fila; después el `WHERE` o la lista de columnas lo leen de ahí. Lo que la de adentro lee de la de afuera se le pasa como parámetro, así que si compara con una clave o con una columna indexada usa ese camino. Una que no lee nada de afuera se corre una sola vez. No se convierte en un join: con una tabla de afuera grande son muchas consultas pequeñas.

**Cuál columna es de afuera.** La que está calificada con un nombre que la subconsulta no le da a ninguna tabla suya. Una columna sin calificar es siempre de la subconsulta.

**La clave siguiente.** El árbol sólo se lee hacia adelante, así que la clave más alta se encuentra partiendo el rango por la mitad: unas decenas de búsquedas por `INSERT` que la omite.

### El proveedor

`Tekium.Embedded.EntityFrameworkCore`, con `UseTekium(cadena de conexión)`, `UseTekium(conexión)` y `UseTekium(base ya abierta)`.

| Pieza | Qué hace |
| --- | --- |
| `TkTypeMappingSource` | Qué tipo de columna guarda cada tipo de .NET, y cómo se escribe un valor de fecha dentro de una consulta |
| `TkQuerySqlGenerator` | `LIMIT … OFFSET …`, y `||` para unir textos |
| `TkMethodCallTranslatorProvider`, `TkMemberTranslatorProvider` | `StartsWith`, `EndsWith`, `Contains`, `ToLower`, `ToUpper` y `Length` |
| `TkUpdateSqlGenerator` | El de EF Core tal cual: usa `RETURNING` para leer la clave que puso la base y para saber cuántas filas cambió |
| `TkModificationCommandBatchFactory` | Una sentencia por comando |
| `TkDatabaseCreator`, `TkMigrationsSqlGenerator` | `EnsureCreatedAsync` y `EnsureDeletedAsync`: `CREATE TABLE` y `CREATE INDEX` como los lee TekiumDB |
| `TkRelationalTransactionFactory` | Transacciones sin puntos de guardado |
| `TkHistoryRepository` | Dice que no hay migraciones |

Lo demás (traducción de LINQ, seguimiento de cambios, `Include`) es de EF Core.

## Lo que funciona

Probado, cada cosa sobre TekiumDB y sobre el proveedor de SQLite con el mismo modelo y los mismos datos:

- `EnsureCreatedAsync` (crea una vez), `EnsureDeletedAsync`.
- `SaveChangesAsync`: altas con clave entera puesta por la base y con clave `Guid` puesta por EF, relaciones por navegación, cambios, bajas, el recuento de lo guardado, y `DbUpdateConcurrencyException` cuando la fila ya no está.
- `ExecuteUpdateAsync` y `ExecuteDeleteAsync`.
- Consultas: `Where`, `OrderBy`/`ThenBy`, `Skip`, `Take`, `Include` y `ThenInclude` de colecciones y referencias (en una consulta y con `AsSplitQuery`), `Any`, `All`, `Count`, `LongCount`, `First`, `FirstOrDefault`, `Single`, `Last`, `Find`, `Min`, `Max`, `Sum`, `Average`, `GroupBy` con `Count`/`Sum`/`Max` y con filtro sobre el grupo, `Distinct`, `Join`, `SelectMany`, `Contains` sobre una lista y sobre otra consulta, proyecciones con navegaciones, cuentas y condicionales, `FromSqlRaw`.
- Transacciones de la aplicación, confirmadas y deshechas.

Y contra LINQ sobre objetos, porque SQLite no sabe hacerlo: comparar, ordenar y sacar el máximo de fechas con zona (`DateTimeOffset`).

Los 16 tipos de columna y sus formas anulables se guardan, se leen iguales y se comparan, con un parámetro y con un valor escrito en la consulta.

## Lo que no hay

| Qué | Qué pasa |
| --- | --- |
| Las APIs síncronas de EF (`ToList()`, `SaveChanges()`, `EnsureCreated()`…) | `NotSupportedException`, diciendo que se use la asíncrona. Fue tu decisión: TekiumDB no hace E/S síncrona. `SaveChanges()` sin nada que guardar no lanza, porque no toca la base |
| Migraciones | `MigrateAsync` lanza `NotSupportedException`. Las tablas se crean con `EnsureCreatedAsync`; un cambio de modelo sobre una base con datos no tiene camino |
| Claves de varias columnas | `EnsureCreatedAsync` lanza `NotSupportedException` |
| Claves foráneas en la base | **No se crean**, porque las tablas hechas con SQL no las tienen. EF sigue relacionando y borrando en cascada lo que tiene cargado en memoria, pero nada impide guardar una referencia a una fila que no existe, y borrar una fila no borra las que dependen de ella si no están cargadas. En SQLite las dos cosas las hace la base. Es la diferencia de comportamiento más importante |
| Puntos de guardado | Un `SaveChangesAsync` que falla dentro de una transacción de la aplicación no puede deshacer sólo lo suyo |
| `Union`, `Concat`, `Intersect`, `Except` | Error de SQL al ejecutar: "UNION is not supported" |
| Lo que EF no sabe traducir aquí (`Trim`, `Substring`, funciones de fecha, `Math`…) | `InvalidOperationException` de EF, que dice qué expresión |
| Native AOT | Este paquete no es compatible: EF Core arma sus consultas con reflexión y código generado en ejecución. Es el único de `/src` |

Diferencias que no son faltas pero conviene saber:

- **Texto:** `StartsWith`, `EndsWith` y `Contains` distinguen mayúsculas, como en .NET. En SQLite, con `LIKE`, no.
- **`EnsureDeletedAsync`** quita las tablas del modelo; el archivo queda.
- **La existencia de las tablas** se averigua preguntando por cada una; no hay un catálogo consultable desde SQL.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Criterio de salida | `EfCoreTests`: crear, guardar un grafo de 35 entidades, veintitantas consultas, cambios y bajas, concurrencia y una transacción deshecha, cada paso sobre las dos bases |
| Más de LINQ: texto, aritmética, agrupaciones, agregados, joins, subconsultas, `ExecuteUpdate`/`ExecuteDelete` | `EfCoreTests`, contra SQLite; fechas con zona, contra LINQ sobre objetos |
| Los tipos | `EfCoreTests`: ida y vuelta, y 34 filtros, cada uno contra el mismo filtro sobre objetos; y lo que EF escribió, leído con SQL |
| Lo que no hay | `EfCoreTests`: cada llamada síncrona, migraciones, `Union`, una traducción que falta, clave de varias columnas, y la referencia a una fila que no existe, que se guarda |
| Cadena de conexión y conexión propia | `EfCoreTests`: sobre un archivo cifrado, cerrando y reabriendo |
| El SQL nuevo | `SqlTests`: `LEFT JOIN`, consultas en `FROM`, subconsultas de todas las formas, `CASE`, `CAST` y las funciones de texto contra LINQ sobre objetos; `RETURNING` y 300 operaciones al azar de claves automáticas contra un modelo |
| Native AOT | El smoke corre el SQL nuevo. El proveedor de EF Core no entra en el smoke |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

Cincuenta roturas hechas a mano, una por una. Cuarenta y tres se detectaron a la primera; para tres agregué pruebas; dos hubo que repetirlas porque la primera forma no compilaba; una se repitió porque apuntaba a un archivo equivocado; y una no cambia nada.

| Rotura | Detectada |
| --- | --- |
| `LEFT JOIN` que pierde las filas sin pareja; `INNER JOIN` que las conserva; `LEFT JOIN` leído como `INNER` | Sí |
| `LEFT JOIN` que repite las filas con pareja | Sí (repetida: la primera forma no compilaba) |
| Subconsulta que lee la fila calculada una sola vez; con varias filas o varias columnas aceptada como valor; `EXISTS` verdadero sin filas; valor sin filas que no es NULL; `IN` que dice verdadero cuando no está; `NOT IN (consulta)` leído como `IN`; `EXISTS` leído como valor; las de la lista de columnas sin calcular | Sí |
| `NOT IN` con un NULL entre los valores que afirma | Sí (repetida) |
| Tomar por de afuera una columna de una tabla propia de la subconsulta | No al principio. Se agregó una subconsulta que llama a su tabla igual que la de afuera |
| Dar otro alias a la consulta del `FROM` | No, y no cambia nada: también se la encuentra por su nombre, que es el mismo |
| `RETURNING`: filas afectadas sin informar; `UPDATE` que devuelve las filas como estaban; `*` que no da nada | Sí |
| Claves automáticas: no asignar una `INT`; la misma para todas las filas de una sentencia; empezar en 0; dar la más alta en vez de la siguiente; buscar en la mitad equivocada; no notar el tope del tipo | Sí |
| La búsqueda de la clave más alta que no termina | Sí: la prueba se cuelga y se mata |
| Ignorar el alias de un `UPDATE` o de un `DELETE` | Sí |
| `CASE` que toma la rama falsa, o que ignora su sujeto, o que sin `ELSE` da el último valor; `STARTSWITH` y `CONTAINS` sin distinguir mayúsculas; `CAST` que no lee un número de un texto, o que sólo pasa a texto lo que ya lo era | Sí |
| Proveedor: `OFFSET` sin escribir | Sí (repetida: apuntaba al archivo equivocado) |
| Proveedor: `Skip` sin `Take` que no devuelve nada | No al principio. Se agregó el caso |
| Proveedor: anunciar puntos de guardado; `NOT NULL` al revés; tablas que siempre parecen faltar; `EnsureDeleted` que no quita; clave de varias columnas creada con la primera; `EnsureCreated` síncrono que no se niega | Sí |
| Proveedor: unir textos con `+`; `ToUpper` como `LOWER`; `EndsWith` como `STARTSWITH`; `Length` sin traducir; una fecha con zona escrita sólo con su año; un día escrito como su mes | Sí |
| Proveedor: una hora escrita en la consulta sin sus segundos | No al principio. Se agregó una hora con segundos |

## Límites conocidos

- **Las claves foráneas no existen en la base**, como se dijo arriba.
- **Una subconsulta se corre una vez por fila.** `Where(b => b.Posts.Any(…))` sobre una tabla grande son tantas búsquedas como filas; con índice sobre la columna de la relación (EF lo crea) cada una es corta.
- **Un `Include` con `Take` corre la consulta de adentro entera** y la guarda en memoria antes de unirla.
- **Las subconsultas no se admiten** en consultas con `GROUP BY` (fuera del `WHERE`), ni en `UPDATE`, `DELETE` o `INSERT`. EF no las genera ahí para lo probado.
- **`ExecuteUpdateAsync` y `ExecuteDeleteAsync` se probaron con filtros sobre la propia tabla.** Con filtros que crucen una navegación EF escribe una subconsulta dentro del `UPDATE` o el `DELETE`, que este SQL no admite: no se probó, y lo esperable es que falle diciéndolo.
- **La clave entera automática** es la más alta más uno: una clave borrada del final se vuelve a dar. SQLite con `AUTOINCREMENT` no la repite.
- **Sin compatibilidad de esquema con otras bases:** las tablas las crea este proveedor, con sus tipos.
- **No se probó** `DbContext` en un pool, consultas compiladas, propiedades sombra complejas, tipos poseídos, herencia (TPH/TPT) ni conversores de valor propios. Es probable que lo que sólo dependa de EF funcione; no se afirma.
- **No se midió nada.** EF Core sobre TekiumDB pasa por el ejecutor de SQL de la Fase 12, que no se optimizó para esto.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 885 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con el SQL nuevo | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 885 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |
