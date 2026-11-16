# Fase 1 — pager con caché acotada, checksums y free-list

**Criterio de salida cumplido en macOS.** Un millón de páginas escritas y leídas en un archivo real, y cero asignaciones en el camino caliente.

Medido el 2026-11-16. Una sola máquina, una sola corrida por benchmark.

## Criterio de salida

| Criterio | Resultado |
| --- | --- |
| Escribir y leer un millón de páginas | Pasa en 8,1 s: archivo real de 4 GiB (páginas de 4 KiB), caché de 4 096 páginas, lectura en orden pseudoaleatorio con verificación de cada página |
| Cero asignaciones en el camino caliente | 0 B por operación: tomar un snapshot y fijar una página en caché cuesta 15,9 ns |

La prueba del millón es `One_million_pages_are_written_and_read_back_from_a_real_file`. Está marcada como explícita por su tamaño y no corre en CI:

```bash
dotnet test -c Release --filter-method "*One_million*" --explicit only
```

En CI corre la misma lógica con 20 000 páginas sobre memoria y una caché de 128, más una prueba que mide las asignaciones del camino caliente con `GC.GetAllocatedBytesForCurrentThread` y exige cero.

## Qué costó el pager real

> **Corrección posterior.** La corrida de la [Fase 2](fase-2.md), con el mismo código de lectura, midió 397 ns en la lectura puntual. El +45 % de la tabla de abajo fue en su mayor parte ruido de la máquina, no costo del pager. Los números se dejan como se midieron.

El spike de la Fase 0.5 no verificaba checksums, no desalojaba la caché y no reutilizaba espacio. Estas son las mismas cargas con el pager de la Fase 1:

| Carga | SQLite | TekiumDB Fase 0.5 | TekiumDB Fase 1 | Cambio | Relación con SQLite |
| --- | ---: | ---: | ---: | ---: | ---: |
| Lectura puntual por clave (1 M de filas) | 2 651 ns | 389 ns | 565 ns | +45 % | 4,7× más rápido |
| Rango de fechas, 1 000 filas | 1 096 µs | 23,8 µs | 29,1 µs | +22 % | 38× más rápido |
| Rango de fechas, 100 000 filas | 88,4 ms | 2,22 ms | 2,72 ms | +23 % | 33× más rápido |
| Inserción secuencial, 100 000 filas en 1 transacción | 126,6 ms | 26,9 ms | 26,7 ms | sin cambio | 4,7× más rápido |
| Inserción secuencial, 100 000 filas en 100 transacciones | 790 ms | 946 ms | 1 007 ms | +6 % | 1,28× más lento |

La columna de SQLite es la de esta corrida. Entre las dos corridas SQLite se movió hasta un 13 % sin que su código cambiara, así que diferencias de un dígito en la columna "Cambio" son ruido de la máquina.

Lo que explica cada cambio:

- **Lectura puntual, +45 %.** Es el costo de fijar y soltar cada página del camino (tres niveles), registrar el snapshot del lector y buscar el marco en un diccionario concurrente en vez de un arreglo. Es el precio de poder desalojar páginas. Sigue sin asignar memoria.
- **Rango, +22 %.** El mismo fijado, pero una vez por hoja en vez de por fila. Las asignaciones por escaneo subieron de 584 a 912 bytes, fijas, sin importar cuántas filas se recorran.
- **Inserción en una transacción, sin cambio.** El checksum de cada página escrita no se nota frente al resto del commit.
- **Inserción en 100 transacciones, 1,28× más lento que SQLite.** Antes era empate. Cada commit ahora escribe además las páginas de la free-list, y como reutiliza páginas libres, las escrituras dejan de ser contiguas. Sigue dentro del margen de 2×, pero es la carga donde menos colchón queda y la que hay que vigilar cuando llegue el cifrado.

Valen las mismas advertencias que en la [Fase 0.5](fase-0.5.md): la comparación es contra `Microsoft.Data.Sqlite`, no contra SQLite en C, y todo se midió con la caché caliente.

## Condiciones

Las mismas de la Fase 0.5: Apple M2 Max, macOS 27.0.1, .NET 11.0.0 Arm64 con JIT, BenchmarkDotNet 0.16.0-preview.2, páginas de 8 KiB, caché de 256 MiB en ambos motores, SQLite con `journal_mode=WAL`, `synchronous=FULL` y `fullfsync=ON`.

```bash
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*' --join
```

## Lo que falta para cerrar la fase

- **CI en Linux y Windows.** Todo lo anterior se verificó sólo en macOS. El plan pide probar el comportamiento del flush a disco en los tres sistemas desde esta fase, y eso sólo lo da el CI.
- **`F_FULLFSYNC` en macOS.** No se verificó de forma independiente que `RandomAccess.FlushToDisk` lo emita. Los tiempos de commit (unos 5 ms por flush) son coherentes con que sí.

## Lo que queda fuera de esta fase

- Una transacción de escritura guarda todas sus páginas nuevas en memoria hasta el commit. Una transacción más grande que la RAM no cabe.
- El slot de raíz se valida con XxHash3; el MAC llega con el cifrado (Fase 2).
- El lector retiene su snapshot con un contador, sin diagnóstico de snapshots viejos (Fase 4).
- Delete, merge y páginas de overflow en el B+Tree (Fase 3).
