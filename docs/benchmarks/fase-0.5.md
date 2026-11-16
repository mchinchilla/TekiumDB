# Fase 0.5 — spike de validación contra SQLite

**Veredicto: se sigue.** El criterio de salida era quedar dentro de 2× de SQLite en lectura puntual, rango de fechas e inserción secuencial. El motor mínimo queda por delante en cuatro de las cinco mediciones y empatado en la quinta.

Medido el 2026-11-16 con el código de este commit. Una sola máquina, una sola corrida: son números para decidir si el proyecto es viable, no cifras de producto.

## Resultados

| Carga | SQLite | TekiumDB | Relación | Asignado por operación (SQLite / TekiumDB) |
| --- | ---: | ---: | ---: | ---: |
| Lectura puntual por clave (1 M de filas) | 2 402 ns | 389 ns | 6,2× más rápido | 800 B / 0 B |
| Rango de fechas, 1 000 filas | 906 µs | 23,8 µs | 38× más rápido | 338 KB / 584 B |
| Rango de fechas, 100 000 filas | 87,4 ms | 2,22 ms | 39× más rápido | 33,6 MB / 584 B |
| Inserción secuencial, 100 000 filas en 1 transacción | 130,7 ms | 26,9 ms | 4,9× más rápido | 35,2 MB / 12,6 MB |
| Inserción secuencial, 100 000 filas en 100 transacciones | 906 ms | 946 ms | 1,05× (empate) | 35,3 MB / 15,0 MB |

Desviación estándar por debajo del 4 % en todo salvo en la última fila, donde SQLite tiene ±47 ms y TekiumDB ±15 ms: la diferencia de 40 ms cae dentro del ruido.

## Cómo leer estos números

Tres advertencias, en orden de importancia.

**1. La comparación es contra `Microsoft.Data.Sqlite`, no contra el núcleo en C de SQLite.** Es la forma en que una aplicación .NET usa SQLite, y es la comparación que el plan pide. Pero una parte grande de la ventaja en lecturas es el costo del proveedor, no del motor: cada fila cruza P/Invoke varias veces y `GetBytes` asigna una copia del blob (336 bytes por fila en el rango). SQLite llamado directamente desde C recorre un rango varias veces más rápido que lo que aquí aparece. La ventaja real de motor a motor en el rango es bastante menor que 38×; lo que estos números prueban es que *desde .NET* el motor administrado no paga ese peaje.

**2. El spike hace menos trabajo del que hará el motor real.** No verifica checksums ni descifra páginas al leerlas, no desaloja la caché (todo queda en memoria), no tiene free-list y no reutiliza espacio. Cada una de esas piezas agrega costo en las Fases 1 y 2. El margen actual es el colchón para pagarlas.

**3. Todo se midió con la caché caliente.** Las dos bases caben en memoria y se recorren enteras antes de medir. No hay E/S en las lecturas. El comportamiento en frío y con una base más grande que la caché no se midió.

## Qué dice cada carga

- **Lectura puntual.** 389 ns para bajar tres niveles de árbol sobre 125 MB de páginas, sin una sola asignación: el camino rápido síncrono devuelve una `ValueTask` ya completada.
- **Rango de fechas.** 22 a 24 ns por fila. Las entradas apuntan a la página inmutable, así que el escaneo no copia ni asigna por fila; los 584 bytes son el enumerador y su camino.
- **Inserción en una transacción.** Mide la CPU del árbol. El split 100/0 deja las hojas llenas y cada página se copia una sola vez por transacción.
- **Inserción en 100 transacciones.** Aquí manda el disco: unos 9 ms por commit en ambos motores. TekiumDB hace dos flush por commit (páginas, luego slot de raíz) y aun así empata. Es la carga a vigilar: con commits pequeños, el costo es el flush, no el árbol.

## Condiciones

| | |
| --- | --- |
| Máquina | Apple M2 Max, 12 núcleos, macOS 27.0.1 |
| Runtime | .NET 11.0.0, Arm64 RyuJIT, JIT (no AOT) |
| Herramienta | BenchmarkDotNet 0.16.0-preview.2, `MemoryDiagnoser` |
| SQLite | `Microsoft.Data.Sqlite` 11.0.0, API síncrona, comandos preparados |
| Durabilidad en SQLite | `journal_mode=WAL`, `synchronous=FULL`, `fullfsync=ON`, `cache_size` de 256 MiB |
| Durabilidad en TekiumDB | Dos `FlushToDisk` por commit |
| Página de TekiumDB | 8 KiB |
| Datos | Clave de 16 bytes (UUID v7) u 8 bytes (fecha), valor de 100 bytes |
| Esquema en SQLite | `WITHOUT ROWID` con clave `BLOB` para UUID; `INTEGER PRIMARY KEY` (rowid) para la fecha |

Lecturas: 3 iteraciones de calentamiento y 10 medidas. Inserciones: archivo nuevo en cada iteración, 1 de calentamiento y 7 medidas.

No se verificó de forma independiente que `FlushToDisk` emita `F_FULLFSYNC` en macOS. Los ~4,7 ms por flush son coherentes con que sí lo hace, y `fullfsync=ON` se activó en SQLite para que ambos paguen lo mismo.

## Reproducir

```bash
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*' --join
```

Tarda unos dos minutos. Los informes completos quedan en `BenchmarkDotNet.Artifacts/results/`.

## Lo que el spike deja pendiente

Decisiones que este código tomó para poder medir y que las fases siguientes reemplazan:

- Sin free-list: el archivo sólo crece (Fase 1).
- Caché sin límite ni desalojo, sobre `byte[]` en vez de buffers alquilados (Fase 1).
- Sin checksum por página; el slot de raíz se valida con FNV-1a en vez de un MAC (Fases 1 y 2).
- Sin delete, merge, páginas de overflow, compresión de prefijos ni zone maps (Fases 3 y 7).
- Un escritor y lectores sobre snapshots, pero sin retención de páginas por lector, que hoy no hace falta porque nada se libera (Fase 4).
