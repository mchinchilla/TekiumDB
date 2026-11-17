# Fase 12 — capa SQL y proveedor ADO.NET

**Criterio de salida cumplido en macOS y en Linux**, sobre un subconjunto de SQL más chico que el que describe el plan (abajo dice qué falta). Dapper corre contra el proveedor sin adaptador; una vista anidada en otra se resuelve y un ciclo se rechaza al crearla; y `EXPLAIN` imprime el plan elegido con las filas estimadas contra las reales. Windows no se ha verificado.

Hecho el 2026-11-16. No se midieron tiempos.

## Qué hay

Un paquete nuevo, `Tekium.Embedded.Sql`:

```csharp
await using var connection = new TkConnection("Data Source=app.tkdb;Password=…");
await connection.OpenAsync();

// Con Dapper, sin adaptador:
await connection.ExecuteAsync("INSERT INTO orders (id, customer, total) VALUES (@Id, @Customer, @Total)", orders);
var big = await connection.QueryAsync<Order>("SELECT * FROM orders WHERE customer = @c AND total > @t ORDER BY total DESC", new { c, t });
```

| Sentencia | Lo que admite |
| --- | --- |
| `SELECT` | Columnas, expresiones y alias; `*` y `tabla.*`; `DISTINCT`; `FROM` una tabla o vista; `INNER JOIN … ON`; `WHERE`; `GROUP BY` y `HAVING`; `ORDER BY` (expresión, alias o posición, `ASC`/`DESC`); `LIMIT` y `OFFSET` |
| Expresiones | Comparaciones, `AND`/`OR`/`NOT` con la lógica de tres valores de SQL, aritmética, `||`, `IS [NOT] NULL`, `[NOT] IN (lista)`, `[NOT] BETWEEN`, `[NOT] LIKE`, parámetros `@nombre`; `LOWER`, `UPPER`, `LENGTH`, `ABS`, `COALESCE` |
| Agregados | `COUNT(*)`, `COUNT(x)`, `COUNT(DISTINCT x)`, `SUM`, `MIN`, `MAX`, `AVG` |
| `INSERT` | Con o sin lista de columnas, varias filas |
| `UPDATE`, `DELETE` | Con `WHERE`; devuelven cuántas filas tocaron |
| `CREATE TABLE` | Tipos enteros, `DECIMAL`, `REAL`/`DOUBLE`, `TEXT`/`VARCHAR`, `BOOLEAN`, `UUID`, `TIMESTAMP`, `DATETIME`, `DATE`, `TIME`, `INTERVAL`, `BLOB`; `NOT NULL`, `UNIQUE`, una columna `PRIMARY KEY`; `IF NOT EXISTS` |
| `CREATE [UNIQUE] INDEX`, `DROP TABLE`, `DROP INDEX … ON` | Sobre columnas |
| `CREATE [OR REPLACE] VIEW`, `DROP VIEW` | Una vista es una consulta guardada, sólo de lectura |
| `EXPLAIN SELECT …` | El plan, con filas estimadas y reales |

**Las mismas tablas.** SQL lee cualquier tabla del archivo, también las que declara un tipo con `[TkTable]`, y lo que escribe es, byte por byte, lo que escribiría la API tipada: las filas, y las entradas de cada índice. Una tabla creada con SQL y una creada por un tipo son lo mismo en el catálogo. Dentro de una transacción de ADO.NET se pueden mezclar las dos (`TkDbTransaction.Writer`).

**Lo que SQL no escribe.** Una tabla con algo que sólo su tipo de fila sabe calcular (un índice sobre una expresión, un `CHECK`, una clave foránea, un zone map) se lee con SQL pero no se escribe: se rechaza diciendo por qué. Escribirla sin poder mantener eso la dejaría inconsistente.

## El planificador

Para la primera tabla de la consulta, mira las condiciones del `WHERE` que comparan una columna con un valor y arma los caminos posibles: la fila por su clave, un rango de la clave, cada índice cuyas primeras columnas quedan fijadas (más un rango en la siguiente), o recorrer la tabla. Estima las filas de cada uno y elige el menor.

**La estimación sale de la forma del árbol,** no de estadísticas guardadas: baja hasta el inicio del rango y, por cuántos hijos tiene cada nodo del camino y dónde cae el rango entre ellos, calcula cuántas entradas hay. Lee unas pocas páginas, no el rango, y no hay nada que mantener al escribir.

Cada tabla unida se busca por clave o por índice si el `ON` iguala una columna suya con algo de las tablas anteriores; si no, se lee una vez y se empareja en memoria por valor.

Elija lo que elija, cada condición se vuelve a comprobar sobre cada fila: el plan sólo acota lo que se lee.

```
EXPLAIN SELECT owner, COUNT(*) FROM items i JOIN owners o ON o.name = i.owner
        WHERE i.id < 50 GROUP BY owner ORDER BY 2 DESC LIMIT 3

plan                                                              estimated_rows  actual_rows
Limit 3                                                                                     3
  Sort by 2 DESC                                                                            8
    Group by owner                                                                          8
      Filter: i.id < 50                                                                    50
        Join: index name_unique of owners on o.name = i.owner                              50
          Key range of items (Id < 50)                                           50        50
```

(La forma del plan y las filas reales son las de la prueba; la estimación del rango de la clave en esa prueba no la fijé a un número, sólo a un intervalo.)

Las sentencias ya analizadas se guardan por su texto: una que se repite no se vuelve a analizar. El camino a los datos sí se decide cada vez, porque depende de los valores de los parámetros.

## El proveedor ADO.NET

`TkConnection`, `TkCommand`, `TkParameter`, `TkDataReader` y `TkDbTransaction`, sobre las clases base de `System.Data.Common`. La conexión abre un archivo por su cadena de conexión, o usa un `TekiumDb` que la aplicación ya tiene abierto.

- **Sólo asíncrono.** `OpenAsync`, `ExecuteReaderAsync`, `ExecuteNonQueryAsync`, `ExecuteScalarAsync`, `BeginTransactionAsync`, `CommitAsync`. Los métodos síncronos de ADO.NET lanzan `NotSupportedException` diciendo cuál usar: el motor no tiene E/S síncrona. Dapper tiene variantes asíncronas de todo (`QueryAsync`, `ExecuteAsync`…), y son las que sirven.
- **Sin transacción,** una sentencia que escribe confirma sola y una que lee ve el último commit.
- **El lector tiene las filas en memoria** cuando se entrega: leerlo no hace E/S.

## Lo que el plan pide y no está

| El plan | Estado |
| --- | --- |
| Joins internos y agregados | Hechos |
| `ORDER BY VECTOR_DISTANCE(col, @v) LIMIT k` | **No.** La búsqueda vectorial sigue siendo sólo por su API |
| Sintaxis de rango temporal | **No.** Los rangos de fechas se escriben con `>=`, `<` y `BETWEEN`, y usan los índices; los zone maps no se usan desde SQL |
| Estadísticas por índice | Estimación desde la forma del árbol, calculada al planificar; no hay estadísticas guardadas |
| Caché de planes con la consulta normalizada como clave | Caché de sentencias analizadas, por su texto tal cual (sin normalizar espacios ni mayúsculas) |
| Una vista "que el planificador inlinea" | Una vista se ejecuta entera y después se filtra: una condición sobre la vista no baja a sus tablas |
| Índices sobre expresiones con `INCLUDE` desde SQL | No |

Además: sin subconsultas, sin `UNION`, sin joins externos ni cruzados, sin `ALTER TABLE`, una sola sentencia por comando, clave primaria de una sola columna (como en todo el motor), sin `DEFAULT` ni claves foráneas desde SQL. Cada una se rechaza al analizar, diciendo qué no se admite.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Criterio: Dapper sin adaptador | `DapperTests`, sobre un archivo abierto por cadena de conexión: listas de objetos como parámetros, objetos de vuelta (con `Guid`, `decimal`, `DateTimeOffset`, `bool`, enumeraciones, nulos, `byte[]`), `IN` con una lista, escalares, filas sin tipo, tuplas, un join armado en dos objetos, `UPDATE`/`DELETE` con su cuenta, transacciones confirmadas y deshechas, y `EXPLAIN` |
| Criterio: vistas anidadas y ciclos | `SqlTests`: una vista sobre otra sobre otra da lo que da la consulta escrita entera, y se une como una tabla. Reemplazar la primera por una que usa la tercera se rechaza al crearla, nombrando el ciclo, y la vista queda como estaba |
| Criterio: `EXPLAIN` con estimado y real | Búsqueda por clave, rango de clave, índice, índice compuesto con rango, índice único, recorrido: el camino esperado, las filas reales exactas y la estimación dentro de un factor de tres. Un plan completo de seis pasos, línea por línea |
| Lo que devuelve cada consulta | Veintiocho condiciones, y además parámetros, orden, límites, `DISTINCT`, agregados, grupos, `HAVING` y cinco joins, cada uno contra hacer lo mismo con LINQ sobre los objetos |
| Las mismas tablas | `SqlTableTests`: seis rondas de inserciones, cambios y borrados al azar hechos sin el tipo de fila; el tipo generado lee las mismas filas, sus índices las encuentran, cada índice tiene una entrada por fila y no se pierde ninguna página. Y al revés |
| Lo que se rechaza | Clave repetida, `UNIQUE`, `NOT NULL`, tipo equivocado; veintidós errores de sintaxis o de cosas no admitidas, con su mensaje; escribir una tabla que sólo su tipo puede mantener; los métodos síncronos |
| Transacciones | Lo escrito no se ve desde otra conexión hasta confirmar; deshacer y soltar sin confirmar no dejan nada; SQL y la API tipada en la misma transacción; el mismo texto se analiza una vez |
| Herramientas | Tras DDL y escrituras por SQL la base se verifica sana; un respaldo lleva las tablas hechas con SQL y admite las mismas vistas |
| Native AOT | El smoke crea tablas, escribe con parámetros en una transacción, consulta una vista con join y agregados, y lee un plan |

### Las pruebas detectan el código roto

Cincuenta y cinco roturas hechas a mano. Treinta y cuatro se detectaron a la primera (una de ellas como bloqueo: la prueba no termina); para diecisiete agregué pruebas; tres hubo que repetirlas porque no compilaban, y una de ésas no compiló tampoco la segunda vez; una no cambia ningún resultado.

**La primera corrida entera no valió.** El filtro con que el guion elegía las pruebas estaba mal escrito, el ejecutor de pruebas salía con error sin correr nada, y el guion lo contaba como "detectado". Lo noté por el código de salida, lo corregí (ahora distingue una corrida inválida de una detección) y repetí las cincuenta y cinco.

| Rotura | Detectada |
| --- | --- |
| Insertar sin mirar si la clave existe; no comprobar `UNIQUE`, o comprobarlo con NULL; no comprobar `NOT NULL` | Sí |
| Dejar la entrada vieja de un índice al actualizar; no tocar los índices al borrar; no marcar la tabla como cambiada | Sí |
| Escribir una tabla de sólo lectura | Sí |
| Ignorar la condición de un índice parcial al escribir; usar un índice parcial para leer como si tuviera todas las filas | No al principio: ninguna tabla de las pruebas de SQL tenía uno. Se agregó una con su índice parcial sobre columnas |
| Leer las filas de un esquema viejo como si fueran del actual | No al principio. Al escribir la prueba apareció un defecto: una columna que no existía al escribir la fila se leía como NULL desde SQL y como cero desde la API tipada. Corregido |
| Buscar nombres sólo exactos; estimación siempre cero; elegir el peor camino | Sí |
| Extremos de un rango de la clave o de un índice mal incluidos o excluidos; incluir los NULL en un rango de índice | Uno a la primera; para los otros tres se agregaron condiciones con esos extremos y las filas leídas por el camino en `EXPLAIN` |
| No volver a comprobar el `WHERE` tras el camino | Sí (repetida: no compilaba) |
| Usar para acotar la búsqueda un valor que no sobrevive a la conversión (1,5 contra una columna entera) | No al principio. Se agregó el caso |
| No volver a comprobar el `ON`, dejando pasar lo que queda sin saberse | No al principio. Se agregó un join con una condición sobre una columna nula |
| Conservar las claves NULL al emparejar un join en memoria | No, y no cambia nada: un valor NULL del otro lado nunca busca |
| `HAVING`, `DISTINCT`, `DESC`, `OFFSET` ignorados; sin grupo para un agregado sobre nada; `COUNT(DISTINCT)` contando repetidos; columna ambigua tomando la primera | Sí |
| `COUNT(x)` contando los NULL | No compiló ninguna de las dos formas de romperlo. La prueba compara `COUNT(*)` con `COUNT(code)` sobre una columna con nulos |
| No detectar el ciclo de vistas | No al principio: lo paraba el tope de profundidad, con un mensaje de treinta y dos vistas. La prueba ahora exige la cadena exacta |
| `AND` con desconocido dando verdadero; `NOT` de desconocido dando verdadero; `BETWEEN` sin su extremo; `_` de `LIKE`; división | Sí |
| `OR` con desconocido dando falso; `NOT IN` con un NULL en la lista; texto a la izquierda de una comparación con una fecha; `AND` y `OR` con la precedencia cambiada | No al principio. Se agregaron las cuatro condiciones |
| `UPDATE` que cambia la clave tratado como si no la cambiara | No al principio: la prueba buscaba la fila por otra columna. Ahora la busca por la clave nueva y por la vieja |
| Índice único fallido a medio construir; vista inválida dejada en el catálogo; vista reemplazada sin restaurar | No al principio: fuera de una transacción el error deshace todo y no se notaba. Se agregó la prueba dentro de una transacción que sigue y confirma |
| `CREATE TABLE` sobre una existente; vista con nombre de tabla | Sí |
| `DROP TABLE` dejando las filas | Sí |
| `DROP TABLE` dejando sus índices | No al principio. Se agregó la comprobación de que no queda ningún árbol |
| Analizar la sentencia cada vez; no confirmar una sentencia fuera de transacción; `Rollback` que confirma; soltar sin confirmar que confirma | Sí |
| El comando ignorando la transacción de la conexión | Sí: la prueba se bloquea esperando al escritor |
| Comilla doblada sin desescapar; texto sobrante tras la sentencia | Sí |

## Límites conocidos

- **El subconjunto de SQL** es el de la tabla de arriba; lo demás se rechaza.
- **Todo resultado se arma en memoria** antes de entregarse, y `ORDER BY` siempre ordena en memoria aunque un índice ya diera ese orden. Una consulta sin `ORDER BY` ni `DISTINCT` con `LIMIT` sí deja de leer al llegar al límite.
- **Los índices parciales no se usan para leer** desde SQL (sí se mantienen al escribir).
- **El orden de los joins es el escrito:** no se reordenan.
- **Dapper sin abrir la conexión** la abre y la cierra en cada llamada, y cerrar una conexión que abrió el archivo lo cierra de verdad: conviene abrirla una vez.
- **Los nombres** de tablas y columnas se buscan sin distinguir mayúsculas; si dos tablas difieren sólo en eso, gana la que coincide exacta.
- **El texto se compara y ordena por punto de código,** como en todo el motor; `LIKE` distingue mayúsculas.
- **Crear un índice o una vista** recorre lo que hay: el índice inserta una entrada por fila (no usa la carga masiva), y la vista se ejecuta una vez para validarla.
- **Dapper no es compatible con Native AOT** (genera código en ejecución); el proveedor sí lo es.
- **Sin `DbProviderFactory`** ni `GetSchema`.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 799 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con SQL por ADO.NET sobre una base cifrada; sin advertencias de AOT ni de recorte | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 799 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Dapper (2.1.66, licencia Apache-2.0) es dependencia sólo de las pruebas.
