# Fase 17 — claves foráneas desde SQL y migraciones de EF Core

**Criterio de salida cumplido en macOS; Linux está pendiente y las pruebas de mutación van por la mitad.** SQL crea y comprueba las mismas claves foráneas que la API tipada, y una cadena de migraciones de EF Core aplicada con `MigrateAsync` deja el mismo esquema, los mismos datos y el mismo historial que en SQLite. Windows no se ha verificado.

Hecho el 2026-11-17. Esta fase no tiene criterio de rendimiento y no se midieron tiempos.

## Lo que decidiste al empezar

| Pregunta | Decisión | Lo que cuesta |
| --- | --- | --- |
| Qué hace una clave foránea al borrar la fila referida | **Sólo impedir.** Sin cascada ni `SET NULL` en el motor | Donde SQLite borraría las filas hijas, TekiumDB hace fallar el borrado. El formato de archivo no cambió |
| Qué migraciones | **Las que no reescriben filas** | Quitar, renombrar o cambiar una columna, y renombrar una tabla, se rechazan. Un modelo que cambie así no tiene camino con migraciones |

## Qué hay

### En SQL

- **Claves foráneas.** `REFERENCES otra` en una columna, o `[CONSTRAINT nombre] FOREIGN KEY (columna) REFERENCES otra [(clave)]`, en `CREATE TABLE` y en `ALTER TABLE … ADD`. `ALTER TABLE … DROP CONSTRAINT [IF EXISTS] nombre` la quita.
  - Es una columna que se refiere a la clave primaria de una tabla, del mismo tipo.
  - Al escribir una fila, la referida tiene que existir (o la columna no tener valor). Al borrar una fila, nadie puede referirse a ella. Una fila puede referirse a sí misma.
  - Una violación aborta la transacción entera, igual que en la API tipada.
  - Una restricción nueva se comprueba contra las filas que ya hay, antes de escribir nada.
  - `ON DELETE NO ACTION` y `RESTRICT` se aceptan, porque es lo que hace. `CASCADE`, `SET NULL` y `SET DEFAULT` se rechazan al declarar.
- **Es la misma restricción que declara `[TkForeignKey]`.** Una base cuyas tablas crea SQL y otra cuyas tablas crea el tipo de fila quedan con el mismo catálogo, byte por byte: la entrada de restricciones, la nota en la tabla referida y la definición del índice. Lo que escribe un lado lo comprueba el otro.
- **El índice.** Borrar una fila pregunta quién se refiere a ella, y eso necesita un índice que empiece por la columna. Se usa el que haya; si no hay, se hace uno con el nombre de la restricción. Al quitar la restricción el índice se queda: puede ser uno que ya estaba.
- **`ALTER TABLE t ADD COLUMN c TIPO [NOT NULL] [DEFAULT valor] [UNIQUE]`.** Registra otra versión del esquema y no reescribe ninguna fila. Las filas que había leen la columna como `NULL`, o como el cero de su tipo si no puede ser `NULL`: lo mismo que lee la API tipada. Sólo un `DEFAULT` distinto de eso escribe las filas otra vez.
- **Cambio de regla:** una tabla con claves foráneas, o a la que otra se refiere, antes era de sólo lectura para SQL. Ya no. Sigue siéndolo la que tiene `CHECK`, índices sobre expresiones o zone maps.
- **Palabras reservadas nuevas:** `ALTER`, `CONSTRAINT`, `FOREIGN` y `REFERENCES`. Una columna o tabla que se llame así tiene que ir entre comillas.

### En el proveedor de EF Core

- **Las claves foráneas del modelo se crean en la base**, con `EnsureCreatedAsync` y con migraciones. Usan el índice que EF crea para cada una, así que no hay dos índices sobre la misma columna.
- **`MigrateAsync`**, con la tabla `__EFMigrationsHistory`, migraciones pendientes y aplicadas, ir a una migración dada (también hacia atrás) y cada corrida en una transacción.
- **Operaciones:** crear y borrar tabla, agregar columna, crear y borrar índice, agregar y quitar clave foránea, agregar y quitar restricción de unicidad (es un índice único), datos sembrados y SQL a mano.
- **`EnsureDeletedAsync`** quita primero las claves foráneas, después las tablas y el historial.

## Lo que no hay, y falla diciendo qué

| Qué | Qué pasa |
| --- | --- |
| Borrado en cascada | La base nunca borra filas hijas. El `OnDelete` del modelo no se escribe en la base. EF Core sigue borrando los dependientes que tiene cargados |
| Quitar, renombrar o cambiar una columna; renombrar una tabla o un índice; cambiar la clave primaria | `NotSupportedException` con la operación, la columna y la tabla |
| `CHECK` desde SQL o desde una migración | Se rechaza: SQL no tiene `CHECK` |
| Claves foráneas de varias columnas, o a una columna que no es la clave primaria | Se rechazan |
| Valor por omisión guardado en la columna | No existe. El `DEFAULT` de `ADD COLUMN` es para las filas que había; una inserción posterior tiene que dar el valor. Una columna con `HasDefaultValue` en un `CreateTable` se rechaza |
| Índices con filtro o en orden descendente; secuencias; esquemas | Se rechazan |
| Scripts idempotentes (`GenerateScript` con `Idempotent`) | Se rechazan: el SQL de TekiumDB no tiene con qué decidir |
| `Database.Migrate()` y `dotnet ef database update` | Son síncronos: lanzan, por la decisión de la Fase 16 |

## Diferencias con SQLite que conviene conocer

- **Con dependientes sin cargar, el borrado falla.** Con la relación por omisión de EF Core (cascada), `ExecuteDeleteAsync` sobre un blog con entradas las borra en SQLite; en TekiumDB lanza `TkConstraintException`. Hay que borrar las hijas primero, o cargarlas.
- **Una violación aborta la transacción.** Dentro de una transacción de la aplicación, después de una violación no se puede seguir ni confirmar: sólo deshacer. En SQLite la transacción sigue.
- **Se comprueba fila por fila, no al final de la sentencia.** Un `DELETE` de varias filas que se refieren entre sí puede fallar según el orden en que salgan.
- **Al revés:** SQLite no sabe agregar ni quitar una clave foránea de una tabla ya hecha; EF Core tiene que escribir la tabla otra vez. En TekiumDB es una operación de la migración. En las pruebas, la migración que hace eso lleva para SQLite la reconstrucción a mano.

## Desviaciones y límites

- **El valor por omisión no se guarda.** Guardarlo sería cambiar el formato del esquema de una tabla. Lo dije al preguntar por el alcance como "agregar columna (anulable o con valor por omisión)"; lo que hay es que el valor se aplica a las filas existentes y nada más.
- **Un `DEFAULT` distinto del cero del tipo reescribe todas las filas de la tabla dentro de la transacción**, cargándolas en memoria. En una tabla grande eso cuesta, y no está medido.
- **Una tabla admite 255 versiones de esquema**, así que 254 `ADD COLUMN` a lo largo de su vida. Se dice cuando se acaban.
- **El tipo de fila manda sobre el catálogo.** Si una tabla hecha con SQL se escribe después con un tipo `[TkTable]` que no declara la clave foránea, la primera escritura con ese tipo la quita, como ya pasaba con los índices.
- **Las herramientas `dotnet ef`** (`migrations add`, `script`) no se probaron contra este proveedor. La generación de un script por la API (`IMigrator.GenerateScript`) sí.
- **Sin probar:** migraciones generadas por el andamiaje de EF Core (las de las pruebas están escritas a mano), herencia, tipos poseídos y relaciones de muchos a muchos.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Una base hecha con SQL y otra hecha con el tipo de fila tienen el mismo catálogo; cada lado comprueba lo que escribió el otro, y el tipo no cambia nada al escribir | `SqlSchemaTests` |
| Cientos de sentencias al azar sobre tres tablas con tres claves foráneas (una a la propia tabla, una de texto), contra un modelo que dice cuáles deben fallar | `SqlSchemaTests` |
| Restricción nueva sobre filas que la violan; índice existente o propio; índice parcial que no sirve; lo que no se puede declarar, borrar o quitar | `SqlSchemaTests` |
| `ADD COLUMN`: sin reescribir (se mira la raíz del árbol), con valor por omisión, única, dentro de una transacción que se deshace, con el tipo de fila leyendo y escribiendo, y las 255 versiones | `SqlSchemaTests` |
| Una tabla a la que otra se refiere se escribe sin su tipo, y no se le borra una fila referida | `SqlTableTests` |
| Cinco migraciones aplicadas por pasos, todas juntas, otra vez, una hacia atrás y de nuevo hacia adelante: columnas, filas, historial y lo que las restricciones impiden, igual que en SQLite | `EfMigrationTests` |
| `EnsureCreatedAsync` crea las claves foráneas con el índice de EF; una violación dentro de una transacción; `EnsureDeletedAsync` | `EfMigrationTests` |
| Dieciocho operaciones que se rechazan, cada una con su texto; el SQL exacto de las que sí; una cadena cuya segunda migración no se puede aplicar no deja nada | `EfMigrationTests` |
| La referencia a una fila que no existe no se guarda; la base no borra en cascada, EF sí lo que tiene cargado | `EfCoreTests` |
| Native AOT | El smoke: clave foránea agregada con `ALTER TABLE`, violada al insertar y al borrar, y dos columnas nuevas |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

**En curso.** De 73 roturas preparadas se han corrido 39: 36 detectadas, una que sobrevive (buscar una clave de texto sin escapar en el índice de quien se refiere a ella) y dos cuya forma no compilaba. Faltan 34, repetir las dos, y decidir qué prueba pide la que sobrevive.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 897 pasan, 3 explícitas omitidas (antes de agregar los últimos casos a las pruebas; las clases tocadas se volvieron a correr y pasan) |
| macOS (arm64) | Smoke de Native AOT | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh` | PENDIENTE |
| Windows | — | Sin verificar |
