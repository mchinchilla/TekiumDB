# Páginas de overflow — valores que no caben en una hoja

Trabajo hecho entre la Fase 5 y la 5.5, porque una base cuyas filas no pueden pasar de 2 KiB no sirve para un uso real. El plan lo describe en "Valores grandes"; no es una fase con criterio de salida propio.

Medido el 2026-11-16 en macOS (arm64), y verificado en un contenedor Linux.

## Qué cambia para quien usa la librería

Nada que tenga que hacer: una fila puede medir hasta 256 MiB y una tabla puede tener más columnas de las que caben en una página. Lo que antes lanzaba `TkRowTooLargeException` ahora se guarda.

## Cómo quedó hecho

- Una entrada (clave más valor) que pasa de un cuarto de página saca su valor a una cadena de páginas de tipo `Overflow`. Cada una empieza con el número de la siguiente. La hoja guarda 8 bytes: la longitud total y la primera página.
- Una cadena nunca se modifica en su sitio. Reemplazar un valor grande escribe una cadena nueva y libera la anterior; así un lector que la estaba viendo la sigue viendo entera, igual que con cualquier otra página.
- Al leer por clave, el valor se copia al destino siguiendo la cadena. Al recorrer, se arma en un buffer que vale hasta que el recorrido avanza.
- El catálogo usa el mismo árbol, así que los esquemas grandes salen a overflow sin código propio.

**Diferencia con el plan:** el plan dice que la hoja guarda los primeros bytes del valor en línea además del puntero. Aquí la hoja guarda sólo la referencia. Hoy toda lectura arma la fila entera, así que un prefijo en línea no ahorraría nada y quitaría sitio a las demás entradas de la hoja. Si un índice o un filtro llegan a necesitar el principio del valor sin leer la cadena, se agrega entonces.

## Cómo se probó

| Prueba | Qué comprueba |
| --- | --- |
| `BTreeOverflowTests` | Valores de 0 a 300 000 bytes, con los tamaños justo antes y después de cada frontera (el límite de la hoja, una página de overflow exacta, dos); reemplazos chico↔grande y borrados sin perder una página; un lector que conserva el valor grande que vio mientras el escritor lo reemplaza veinte veces; una cadena que lleva a una página que no es de overflow se reporta como corrupción |
| `BTreePropertyTests` | Las secuencias al azar contra el modelo ahora mezclan valores grandes: uno de cada doce en las cortas, uno de cada cuarenta en la de un millón de operaciones |
| `PowerLossTests`, `ProcessCrashTests` | La carga de las pruebas de crash lleva un valor grande de cada ocho, así que los commits que se interrumpen incluyen cadenas de overflow |
| `TableTests` | Una fila de 820 KiB por la API pública, con y sin cifrado: insertar, leer, recorrer, reemplazar por una chica y al revés, borrar; y una tabla de 600 columnas cuyo esquema no cabe en una página |

El verificador de las pruebas cuenta ahora las páginas de overflow de cada valor y exige que sean exactamente las que su longitud pide, todas de ese tipo y ninguna compartida.

Se rompió el código de cuatro formas y las pruebas detectaron las cuatro: no liberar la cadena al reemplazar, no liberarla al borrar, y perder la marca de overflow al partir una hoja o al unir dos.

## Qué cuesta

En el camino común, nada medible:

| Lectura por clave, caché caliente | Antes | Después |
| --- | ---: | ---: |
| API tipada, 100 000 filas de seis columnas | 336 ns, 326 B | 338 ns, 326 B |
| Árbol crudo, 1 M de filas | 397 ns, 0 B | 437 ns, 0 B |

La lectura cruda subió un 10 % entre corridas, pero SQLite subió un 7 % en la misma corrida (2 177 → 2 321 ns), así que la mayor parte es ruido de la máquina: la relación entre ambos pasó de 0,18 a 0,19. La primera versión de este cambio sí hacía más lenta la lectura tipada (360 ns), porque alquilaba dos buffers por lectura; se corrigió y se volvió a medir.

No se midió cuánto tarda leer o escribir una fila grande.

## Qué no cubre

- **Reescribir una fila grande cuesta sitio.** Copy-on-write necesita espacio para la copia nueva mientras las dos anteriores siguen reservadas (una por si el slot de raíz nuevo no llega al disco). Una fila de 820 KiB reescrita una y otra vez estabiliza el archivo en unas tres veces su tamaño; no crece más, pero tampoco menos.
- **Una fila grande se lee entera.** No hay lectura parcial ni por flujo: 256 MiB de fila son 256 MiB de memoria, más de una vez mientras se escribe.
- **La escritura mantiene en memoria todas las páginas de la transacción**, también las de overflow, hasta el commit. Era así antes y los valores grandes lo hacen más visible.
- **Leer una fila grande por clave baja dos veces por el árbol**: la primera para saber cuánto mide.
- **El límite de 256 MiB no tiene prueba a través de la API tipada**, sólo en el árbol.
- **Windows** sin verificar.
