# Fase 20 — preparar una tabla tipada sin escribir filas

**Criterio de salida cumplido.** Una tabla preparada y nunca escrita la ve SQL, y su catálogo es el mismo que deja SQL al crearla con sus índices y claves foráneas. Sobre filas de una versión anterior, SQL ve las columnas nuevas y lee las filas viejas como la API tipada, sin reescribir ninguna. Preparar dos veces no gasta otra versión ni escribe nada. Una restricción nueva que las filas no cumplen aborta la transacción. Las herramientas (inspector, copiar, verificar, backup) aceptan la tabla vacía. Windows no se ha verificado.

## De dónde sale

La aplicación de punto de venta (sobre la API tipada, con reportes en SQL) encontró dos huecos:

- Una tabla tipada no existe en el archivo hasta su primera escritura. Antes de eso, una consulta SQL sobre ella falla con "There is no table or view".
- SQL conoce cada tabla con las columnas de la última escritura de su tipo. Sobre un archivo viejo, una columna nueva del tipo no existe para SQL hasta que se escriba otra fila.

Los dos tienen la misma causa. La versión del esquema, los índices y las restricciones de un tipo se registran cuando ese tipo escribe. SQL no conoce los tipos, que viven en la aplicación, y leer no registra nada: una lectura no escribe páginas.

## Lo que decidiste

Una llamada explícita por tipo, `Table<T>.EnsureAsync(transaction)` (2026-11-17). La aplicación la llama al arrancar; nada se escribe a sus espaldas. Se descartó que el generador emitiera la lista de todos los tipos para prepararlos de una vez.

## Qué hay

```csharp
await using var writer = await database.BeginWriteAsync(ct);
await database.Table<Invoice>().EnsureAsync(writer, ct);
await database.Table<Customer>().EnsureAsync(writer, ct);
await writer.CommitAsync(ct);
```

`EnsureAsync` hace lo que hace la primera escritura de un tipo, sin la fila, y por el mismo código (`TkWriteTransaction.TableForWriteAsync`):

- crea la tabla si no existe;
- registra otra versión del esquema si el tipo tiene otras columnas que la última (las filas no se reescriben; cada una sigue diciendo con qué versión se escribió);
- construye, reconstruye o elimina los índices y zone maps según lo que el tipo declara, y escribe sus restricciones, que se comprueban contra las filas que hay.

Si la tabla ya está como la declara el tipo, no escribe nada: el catálogo queda en la misma raíz y no se gasta ninguna de las 255 versiones. Una restricción que las filas no cumplen lanza `TkConstraintException` y la transacción sólo puede deshacerse, como en la primera escritura. Una tabla que se refiere a otra que todavía no existe se puede preparar antes o después de ella, con el mismo resultado.

SQL ya leía las filas de versiones anteriores por el nombre de cada columna (renombrada con `[TkColumn]`, quitada, en otro orden), así que no hizo falta cambiar nada en la capa SQL.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Una tabla preparada y vacía: su catálogo (columnas, versión, restricciones, quién se refiere a ella, índices) contra el de la misma hecha con SQL; SQL escribe en ella comprobando sus claves foráneas | `EnsureTests` |
| Filas de la versión 1, el tipo de la versión 2 preparado: otra versión, la raíz de la tabla sin cambiar, y SQL lee cada fila como la API tipada | `EnsureTests` |
| Preparar dos veces, en la misma transacción y en otra, no mueve el catálogo; preparar y escribir en la misma transacción | `EnsureTests` |
| Una restricción que las filas no cumplen aborta la transacción; corregida la fila, los índices nuevos se construyen con una entrada por fila | `EnsureTests` |
| Preparar la tabla que se refiere antes de la referida | `EnsureTests` |
| Verificar, backup, inspector y copiar sobre una tabla vacía | `EnsureTests` |
| Native AOT | El smoke: una tabla preparada y sin filas, con su índice por ruta usado desde SQL |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

Cuatro roturas: no preparar nada, aceptar una transacción de otra base, sólo buscar la tabla sin registrarla, y registrar una versión nueva cada vez. Las cuatro se detectan. La segunda no compilaba en su primera forma (quitar toda la comprobación, que el analizador exige) y se reescribió.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 982 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh` | 982 pasan, 3 explícitas omitidas; smoke de AOT pasa |
| Windows | — | Sin verificar |
