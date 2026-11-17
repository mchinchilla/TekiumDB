# Fase 18 — migraciones de EF Core que cambian columnas y nombres

**Criterio de salida cumplido en macOS; las pruebas de mutación y Linux están pendientes.** Las operaciones que EF Core genera al renombrar una columna o una tabla, al cambiar el tipo o la nulabilidad de una propiedad y al quitarla se aplican con `MigrateAsync`. Dejan las mismas columnas, los mismos datos y el mismo historial que en SQLite, también hacia atrás. Lo que no cabe en las filas falla antes de tocar nada, y el costo en una tabla de un millón de filas está medido. Windows no se ha verificado.

Hecho el 2026-11-17, después de que decidieras seguir con esto en vez de la cascada en el motor, los tipos nuevos o las mediciones.

## Qué hay

### En SQL

| Sentencia | Qué hace |
| --- | --- |
| `ALTER TABLE t DROP [COLUMN] c` | Escribe cada fila otra vez sin la columna |
| `ALTER TABLE t RENAME [COLUMN] a TO b` | Escribe cada fila otra vez con el nombre nuevo; los índices conservan sus entradas |
| `ALTER TABLE t ALTER [COLUMN] c [TYPE] tipo [NOT NULL \| NULL] [DEFAULT valor]` | Escribe cada fila otra vez con el valor convertido; sin `NOT NULL` ni `NULL`, la columna queda como estaba |
| `ALTER TABLE t RENAME TO n` | Mueve las entradas del catálogo; no toca ninguna fila |
| `ALTER INDEX i ON t RENAME TO n` | Cambia el nombre del índice, también en las claves foráneas que lo usan |
| `ALTER TABLE t ADD PRIMARY KEY (c)` | Sólo se acepta si `c` ya es la clave: la clave de una tabla no cambia. Existe porque EF Core lo escribe alrededor de un `RenameTable` |

**Por qué quitar también reescribe.** Las filas se leen por el nombre de la columna. Si quitar una columna sólo registrara otra versión del esquema, el valor seguiría en cada fila. Una columna nueva con el mismo nombre lo recuperaría, y con otro tipo la API tipada no podría leerla.

**Convertir sin perder nada.** Un valor cambia de tipo sólo si, convertido de vuelta, es el que era. Así 5000000000 no se vuelve `INT`, 2.5 no se vuelve 2, `'007'` no se vuelve 7 y `'1.0'` no se vuelve 1.0. Un número se vuelve texto como lo escribe SQL. El rechazo nombra la columna, la fila y el valor.

**Todo se comprueba antes de escribir.** La conversión de cada fila, los `NULL` en una columna que pasa a `NOT NULL` (toman el `DEFAULT`, o el cambio se rechaza) y los índices únicos que se reconstruyen. Recién entonces se registra la versión nueva del esquema y se escribe cada fila.

**Los índices.** Si sus columnas sólo cambian de nombre, conserva sus entradas con la definición nueva. Si cambian de tipo o de nulabilidad, se construye otra vez. Una clave foránea sobre una columna que cambia de nulabilidad se actualiza en las dos tablas, porque cambia cómo se busca en el índice.

**Lo que no se puede:**

- Quitar la clave primaria, cambiar su tipo o hacerla anulable.
- Quitar una columna que un índice usa: primero se quita el índice, como hace EF Core.
- Cambiar el tipo de una columna con clave foránea.
- Cambiar una columna que usa la condición de un índice parcial.
- Renombrar o quitar lo que nombre una vista o un procedimiento almacenado: son texto y dejarían de funcionar.

**`RENAME TO`.** Mueve las cinco clases de entrada del catálogo de la tabla: la tabla, sus versiones de esquema, sus índices, sus restricciones y quién se refiere a ella. También cambia el nombre en las claves foráneas de las tablas a las que se refiere y de las que se refieren a ella, incluida una a sí misma. Rechaza un nombre que ya tenga otra tabla o una vista.

### En el proveedor de EF Core

- **Nuevas operaciones:** `DropColumn`, `RenameColumn`, `AlterColumn`, `RenameTable` y `RenameIndex` se traducen a esas sentencias.
- **`AlterColumn` sin cambios.** Si no cambia ni el tipo ni la nulabilidad, no escribe nada.
- **`NULL` a `NOT NULL`.** Las filas con `NULL` toman el valor por omisión de la operación o el cero del tipo. Es lo que hace el proveedor de SQLite cuando escribe la tabla otra vez (`IFNULL(columna, '')`).
- **El par `DropPrimaryKey`/`AddPrimaryKey`** que EF Core escribe alrededor de un `RenameTable` se omite. Un `DropPrimaryKey` sin su `AddPrimaryKey` se rechaza.

Lo comprobé con lo que EF Core genera de verdad, no con migraciones escritas a mano. Las migraciones de la prueba piden sus operaciones al comparador de modelos (`IMigrationsModelDiffer`) entre dos versiones del modelo, para el proveedor que las aplica, como haría `dotnet ef migrations add`.

| Cambio en el modelo | Lo que EF Core genera |
| --- | --- |
| Columna renombrada | `RenameColumn` |
| `int` a `long` | `AlterColumn` (en TekiumDB; en SQLite las dos son `INTEGER` y no genera nada) |
| Propiedad que pasa a ser requerida | `AlterColumn` |
| Tabla renombrada | `DropForeignKey`, `DropPrimaryKey`, `RenameTable`, dos `RenameIndex`, `AddPrimaryKey`, `AddForeignKey` |
| Propiedad quitada, con índice | `DropIndex`, `DropColumn` |
| Columna de una clave foránea renombrada | `DropForeignKey`, `RenameColumn`, `RenameIndex`, `AddForeignKey` |

## Mediciones

`dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- rewrite 1000000`, en macOS (arm64), con archivo. Es una tabla de un millón de filas de cinco columnas, con dos índices (uno único); el archivo ocupa 192 MB. Cada cambio corre en su transacción, con su commit.

| Cambio | Tiempo | Por fila | Memoria pedida | Memoria residente (antes → pico) | El archivo crece |
| --- | ---: | ---: | ---: | ---: | ---: |
| `RENAME COLUMN` (los índices sólo cambian de nombre) | 1,14 s | 1,14 µs | 740 MB | 230 → 671 MB | 64 MB |
| `ALTER COLUMN` de `INT` a `BIGINT`, con su índice reconstruido | 1,67 s | 1,67 µs | 1 545 MB | 668 → 914 MB | 256 MB |
| `DROP COLUMN` | 0,90 s | 0,90 µs | 744 MB | 875 → 881 MB | 64 MB |
| `ADD COLUMN` sin `DEFAULT` (no reescribe) | 0,01 s | — | 0 MB | 727 → 727 MB | 0 MB |
| `ADD COLUMN` con `DEFAULT` (Fase 17: reescribe) | 2,20 s | 2,20 µs | 4 356 MB | 727 → 1 014 MB | 0 MB |
| `RENAME TO` (no mueve filas) | 0,01 s | — | 0 MB | 976 → 976 MB | 0 MB |

Lo que dicen:

- **Reescribir cuesta alrededor de un microsegundo por fila,** menos que cargarlas (3,9 s el millón).
- **La tabla entera está en memoria mientras se reescribe:** las filas leídas, las nuevas y las páginas que la transacción cambia. En esta tabla eso llega a unos 900 MB de memoria residente. Crece con el tamaño de la tabla, así que en una de diez millones de filas no es prudente. No lo cambié: sería volcar páginas a mitad de la transacción (`SpillAsync`), que hoy sólo es seguro para la carga masiva, y eso hay que decidirlo antes.
- **El archivo crece** porque la transacción escribe páginas nuevas antes de soltar las viejas: 64 MB al reescribir filas, 256 MB si además se reconstruye un índice. Ese espacio queda libre después y se reusa.
- **El `DEFAULT` de la Fase 17 es el más caro:** escribe cada fila por el camino general, con sus índices, y pide 4,3 GB. Queda anotado: no lo optimicé, porque no estaba pedido y no tiene otra medición con qué comparar.
- **La memoria residente:** la saqué muestreando cada 5 ms, porque `Process.PeakWorkingSet64` da 0 en macOS. La medición se hizo una vez; es para dar el orden de magnitud, no para comparar variantes.

## Lo que hay que saber

- **El change feed ve cada fila como cambiada** después de reescribir una tabla, porque cada fila se escribió otra vez.
- **Un consumidor del change feed por el nombre viejo de una tabla** deja de ver sus cambios después de `RENAME TO`.
- **El tipo de fila manda.** Un tipo `[TkTable]` con el nombre o las columnas viejas sigue registrando su propio esquema. Uno con el nombre nuevo lee lo que había (está probado), pero su primera escritura quita los índices que no declara, como ya pasaba.
- **Cada cambio gasta una de las 255 versiones de esquema de la tabla.**
- **Dentro de la transacción de la migración:** si la migración falla, no queda nada de ella y no se anota en el historial.
- **Lo que nombra una vista o un procedimiento se reconoce por los nombres de su texto.** Una columna o tabla cuyo nombre aparece en una vista por otra razón también impide el cambio. Es conservador a propósito.

## Pruebas

| Qué | Prueba |
| --- | --- |
| 300 filas con valores al azar y `NULL`: renombrar, cambiar de tipo (y volver), pasar a `NOT NULL` con y sin `DEFAULT`, quitar y volver a agregar con otro tipo, comparando cada vez con un modelo; que el plan siga usando el índice renombrado o reconstruido | `SqlSchemaTests` |
| Conversiones que pierden algo, índice único que dejaría de serlo, y todo lo que no se puede, con la tabla intacta después | `SqlSchemaTests` |
| `RENAME TO` y `ALTER INDEX … RENAME TO` dentro de una transacción; claves foráneas de los dos lados y a sí misma; un tipo con el nombre nuevo lee y escribe; vistas que lo impiden | `SqlSchemaTests` |
| Procedimientos almacenados que impiden renombrar o quitar lo que nombran | `SqlSchemaTests` |
| Cinco migraciones calculadas por EF Core, aplicadas con `MigrateAsync` sobre TekiumDB y sobre SQLite: columnas (nombre y nulabilidad, leídas de cada base), filas e historial iguales; el modelo final encima; vuelta atrás y adelante | `EfSchemaChangeTests` |
| Una migración cuyo cambio no cabe en las filas falla, no se anota, no cambia nada, y sigue cuando se arreglan los datos | `EfSchemaChangeTests` |
| El SQL exacto de cada operación nueva; lo que se sigue rechazando | `EfMigrationTests` |
| Native AOT | El smoke: renombrar y quitar una columna, cambiar el tipo de otra, renombrar una tabla referida por una clave foránea |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

**En curso.** De 56 roturas preparadas se corrieron 30: 17 detectadas, 3 cuya forma no compilaba y 10 que sobreviven. Las que sobreviven: cambiar el tipo de una columna con clave foránea; cambiar una columna de la condición de un índice parcial; reescribir aunque nada cambie; pasar de la versión 255; no mover la posición de la clave al quitar una columna anterior; reconstruir todos los índices; que el tipo de fila no vuelva a mirar los índices; no actualizar la nulabilidad de una clave foránea; dejar las versiones de esquema viejas al renombrar; que la clave foránea de una tabla a sí misma conserve el nombre viejo. Cada una pide una prueba.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 907 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh` | PENDIENTE |
| Windows | — | Sin verificar |
