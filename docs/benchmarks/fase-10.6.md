# Fase 10.6 — viaje en el tiempo: leer la base como estaba en una transacción pasada

**Criterio de salida cumplido en macOS y en Linux.** `AsOfAsync` devuelve el estado de una transacción anterior, y el horizonte configurado libera las raíces que lo exceden: con cualquier horizonte, el archivo deja de crecer aunque se siga reescribiendo todo. Windows no se ha verificado.

Hecho el 2026-11-16. No se midieron tiempos: leer el pasado es leer por un snapshot, como cualquier otra lectura.

## Qué hay

```csharp
var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", ChangeRetention = 100 });

await using var past = await db.AsOfAsync(transactionId);
var before = await orders.GetAsync(past, id);                       // la fila como estaba entonces
await foreach (var order in orders.QueryAsync(past, filter)) { … }  // consultas e índices, también
```

- **Qué devuelve:** una transacción de lectura sobre el estado que dejó esa transacción. Sirve para todo lo que acepta una transacción: `GetAsync`, `ScanAsync`, `QueryAsync` (con los índices como estaban entonces) y la búsqueda en índices vectoriales.
- **Qué número:** el de `TkWriteTransaction.TransactionId`, el de `LastTransactionId`, o el que traen los cambios del change feed. Si ese número no confirmó nada (una transacción deshecha), el estado es el del último commit anterior; `past.TransactionId` dice cuál fue.
- **Hasta dónde:** el estado actual y los `ChangeRetention` commits anteriores. Más atrás, `TkStateUnavailableException`. Con `ChangeRetention = 0` (por defecto) sólo existe el presente.
- **Antes del primer commit** la base estaba vacía, y así se lee (si el historial llega hasta la creación).
- **Mientras se lee,** ese estado no se suelta aunque el escritor lo deje muy atrás del horizonte. Al cerrar la transacción, su espacio vuelve a usarse.

Con el change feed y el backup forma un conjunto: el change feed dice qué cambió, esto deja ver cómo estaba, y los tres son el mismo mecanismo de retener un estado.

## El horizonte libera lo que lo excede

Doscientas filas reescritas enteras en cada commit, páginas de 4 KiB:

| Horizonte (`ChangeRetention`) | Tras 1 commit | Tras 30 | Tras 60 | Tras 200 |
| ---: | ---: | ---: | ---: | ---: |
| 0 (sin pasado) | 25 páginas | 64 | 64 | 64 |
| 3 | 25 | 102 | 102 | 102 |
| 12 | 25 | 273 | 273 | 273 |
| 50 | 25 | 547 | 952 | 995 |

Los datos vivos ocupan unas 28 páginas. Con horizonte 50, a los 60 commits el archivo aún estaba llenándose (hacen falta más de 50 para que salga el primero); a los 200 ya no crece.

El archivo crece hasta un tamaño que depende del horizonte y ahí se queda: cada commit que entra saca del historial al más viejo, y sus páginas se reutilizan.

## Lo que no es como dice el plan

| El plan | Lo hecho | Por qué |
| --- | --- | --- |
| Un anillo de N raíces en la cabecera, reservado desde la Fase 1 | El historial de commits vive en el catálogo: cada commit anota dónde quedó el estado anterior (Fase 6.7) | Ya existía para el change feed y es lo mismo: una raíz por commit retenido. En el catálogo no tiene tope fijo ni pide reservar espacio en la cabecera; la cabecera sigue con sus dos slots |
| `past.Table<Order>().FindAsync(id)` | `orders.GetAsync(past, id)` | Las tablas de esta API no cuelgan de la transacción: la reciben |
| Horizonte "por número de transacciones o por tiempo" | Por número de commits (`ChangeRetention`, la misma opción del change feed) | Por tiempo pide guardar la hora de cada commit en el historial, que es un cambio de formato. No lo hice; tampoco hay `AsOfAsync` por fecha |
| "Apagado por defecto" | Sí: `ChangeRetention` es 0 por defecto | — |

## Pruebas

| Qué | Prueba |
| --- | --- |
| Criterio: cada transacción retenida se lee como estaba | `TimeTravelTests`: doce commits de inserciones, cambios y borrados al azar contra un modelo guardado en cada uno; cada estado se lee completo, por clave y por índice. Los números de las transacciones deshechas dan el estado del commit anterior |
| Criterio: el horizonte libera lo que lo excede | Con horizonte 0, 3 y 12: treinta reescrituras completas más no hacen crecer el archivo, y a más horizonte, más grande queda |
| Fuera del horizonte, o en el futuro | Se rechazan; sin horizonte sólo el presente |
| Antes del primer commit | Vacía |
| Un lector del pasado retiene su estado | Abierto dentro del horizonte, sigue leyendo lo mismo diecisiete commits después, cuando ese estado ya no se puede pedir; cuesta espacio mientras vive, y al cerrarlo (dos veces no hace daño) el espacio vuelve y leer por él se rechaza |
| Tras cerrar y abrir | El pasado sigue ahí |
| Índices vectoriales | Una búsqueda en el pasado encuentra lo que el índice tenía entonces (`TekiumDB.Vectors.Tests`) |

### Las pruebas detectan el código roto

Doce roturas hechas a mano. Seis se detectaron a la primera; tres no cambian nada porque hay una segunda barrera; dos señalaron una rama que nunca se alcanza, que quité; y una no compilaba y tampoco cambia nada.

| Rotura | Detectada |
| --- | --- |
| Aceptar una transacción que no ha ocurrido | Sí |
| No retener el estado pasado | Sí |
| No soltarlo al cerrar | Sí |
| Leer el presente en vez del pasado | Sí |
| Informar la transacción pedida en vez del commit leído | Sí |
| Buscar el estado actual en el historial en vez de devolverlo directamente | Sí |
| Sin horizonte, no rechazar el pasado (dos formas); fuera del horizonte, no rechazar | No, y no cambia nada: el pager se niega a retener un estado por debajo de su piso, así que se rechaza igual. Son dos barreras para lo mismo |
| "Antes del primer commit" leyendo el presente, o rechazado | No: esa rama no se alcanzaba nunca. El estado vacío de antes del primer commit está en el historial como cualquier otro. La quité |
| Soltar el estado dos veces | No compilaba la rotura; y soltar dos veces no hace nada, el pager lo ignora |

## Límites conocidos

- **Sólo por número de transacción.** No hay consulta por fecha ni horizonte por tiempo.
- **Sólo lectura.** No se puede escribir sobre un estado pasado ni volver la base a él; para "deshacer" hay que leer del pasado y escribir en el presente.
- **El horizonte es el del change feed:** una sola opción para las dos cosas.
- **Compactar, reparar y respaldar no llevan el pasado:** la copia empieza sin historial.
- **Cuesta espacio,** más cuanto más largo el horizonte y más se reescriba; y un lector del pasado que no se cierra lo retiene sin límite, igual que cualquier lector.
- **Los tipos son los de ahora.** Las filas viejas se leen con el tipo actual (la evolución de esquema lo permite); un índice que entonces no existía, o era otro, no se usa y la consulta recorre la tabla.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 761 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT: borrar una fila, leerla en el pasado y devolverla al presente | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 761 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Los tamaños de archivo por horizonte se midieron en macOS, sobre memoria, en una corrida.
