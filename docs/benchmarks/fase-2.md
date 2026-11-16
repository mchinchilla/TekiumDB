# Fase 2 — cifrado por página y derivación de clave

**Criterio de salida cumplido en macOS.** Los vectores del RFC 9106 pasan, la prueba de unicidad de nonce pasa y abrir con una contraseña incorrecta falla limpio.

Medido el 2026-11-16. Una sola máquina, una sola corrida por benchmark.

## Criterio de salida

| Criterio | Resultado |
| --- | --- |
| Vectores de prueba del RFC 9106 | Pasan los tres (Argon2d, Argon2i, Argon2id, con secreto y datos asociados), más nueve valores generados con argon2-cffi 25.1.0, que envuelve la implementación de referencia en C, incluido uno de 64 MiB |
| Unicidad de nonce | `No_nonce_is_ever_used_twice` observa cada página que llega al disco, con commits que fallan a medias, rollbacks y reaperturas, y exige que el par (página, transacción) no se repita |
| Contraseña incorrecta | Lanza `CryptographicException` sin escribir un byte; la contraseña correcta sigue abriendo después |

La prueba de nonce se validó rompiendo el código a propósito: sin la reserva de ids, las cuatro pruebas de nonce fallan.

## Qué cuesta el cifrado

Con la caché caliente el cifrado no interviene: las páginas están descifradas en memoria. Se paga al escribir páginas en el commit y al leerlas de disco.

| Carga | Sin cifrar | Cifrado | Costo |
| --- | ---: | ---: | ---: |
| Insertar 100 000 filas en una transacción (unas 1 600 páginas) | 25,9 ms | 32,7 ms | +26 % |
| Abrir y recorrer 1 M de filas con la caché vacía (unas 16 000 páginas) | 87,5 ms | 158,8 ms | +81 % |

El recorrido en frío es el peor caso: cada página se descifra y autentica al cargarse, a unos 4,4 µs por página de 8 KiB frente a los 0,4 µs de XxHash3. Aun así son 159 ns por fila leída de disco.

## Qué cuesta abrir una base cifrada

Una derivación Argon2id con los parámetros por defecto (64 MiB, 3 pasadas, 4 carriles, la segunda opción recomendada del RFC 9106) tarda **198 ms** y usa 67 MB mientras corre. Es deliberado: es lo que encarece probar contraseñas. Se paga una vez al abrir y corre fuera del hilo de quien llama.

Los carriles se calculan uno tras otro. Paralelizarlos bajaría el tiempo de apertura sin cambiar el resultado; queda pendiente y necesita su medición.

## Sin regresión en el resto

En la misma corrida, sin cifrar:

| Carga | SQLite | TekiumDB | Relación |
| --- | ---: | ---: | ---: |
| Lectura puntual por clave (1 M de filas) | 2 177 ns | 397 ns | 5,5× más rápido |
| Inserción secuencial, 100 000 filas en 1 transacción | 122,2 ms | 29,5 ms | 4,1× más rápido |
| Inserción secuencial, 100 000 filas en 100 transacciones | 749 ms | 957 ms | 1,28× más lento |

**Corrección a la Fase 1.** Allí se reportó que la lectura puntual había subido de 389 a 565 ns (+45 %) por el fijado de páginas. Esta corrida mide 397 ns con el mismo código de lectura. La diferencia de la Fase 1 fue en su mayor parte ruido de la máquina, no costo del pager: en aquella corrida SQLite también salió un 13 % más lento. Una sola corrida por benchmark no alcanza para afirmar cambios de ese tamaño.

## Lo que el cifrado protege y lo que no

Protege la confidencialidad del contenido de las páginas y detecta cualquier alteración de una página, de su encabezado en claro, de los slots de raíz y de los parámetros de derivación.

No oculta, por diseño: el tamaño de la base, el número de páginas, el tipo de cada página y qué transacción la escribió. Las dos páginas de cabecera van en claro.

No detecta que una página sea reemplazada por una versión anterior de esa misma página: el nonce y la etiqueta de la versión vieja siguen siendo válidos. Detectarlo exige encadenar las páginas desde la raíz, y no está en el plan.

## Una corrección al plan

El plan afirma que copy-on-write garantiza que el par (página, transacción) no se repite nunca. No es cierto en dos casos:

- **Un commit que falla o un crash después de escribir páginas.** Quedan en disco páginas cifradas con un id de transacción que nunca se confirmó, y la siguiente sesión arrancaría desde ese mismo id.
- **Dos copias del mismo archivo escritas por separado**, por ejemplo un archivo sincronizado entre equipos o un respaldo restaurado. Ambas continúan desde el mismo id con la misma clave.

Reutilizar un nonce rompe AES-GCM por completo, así que se corrigió sin cambiar la construcción del nonce:

- Un id de transacción no se emite dos veces en un proceso, ni siquiera tras un commit fallido.
- En una base cifrada, los ids se reservan en disco por bloques de un millón antes de usarse. Una sesión nunca arranca dentro de un bloque que otra reservó. Cuesta una escritura y un flush en la primera transacción de escritura tras abrir.
- Cada sesión arranca una distancia aleatoria de hasta 2^40 por delante de los ids anteriores, lo que separa las copias independientes del archivo. Es una garantía probabilística, no absoluta.

Consecuencia visible: en una base cifrada los ids de transacción no son consecutivos. El slot de raíz que escribe cada commit ya no depende de la paridad del id, sino de cuál de los dos tiene el estado vigente.

## Reproducir

```bash
dotnet test --filter-namespace "*Security*"
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*Encryption*' '*KeyDerivation*' --join
```

## Lo que queda fuera de esta fase

- `RotateFileKeyAsync`, reescribir todas las páginas con una clave de archivo nueva. El plan lo deja para cuando haga falta.
- La API pública para pasar la contraseña (`DbOptions`), que llega en la Fase 5.
- CI en Linux y Windows: `AesGcm` usa una implementación distinta en cada sistema, y sólo se probó la de macOS.
