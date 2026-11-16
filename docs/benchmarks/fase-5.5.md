# Fase 5.5 — compresión Zstandard por página

**Criterio de salida cumplido en macOS; pruebas verificadas también en Linux.** Ratio y latencia medidos contra sin comprimir; un archivo con compresión y otro sin ella abren con el mismo código.

**La conclusión de la medición es que, tal como la define el plan, la compresión no conviene:** no reduce el espacio en disco y hace más lentas la escritura y la lectura en frío. Queda implementada, apagada por defecto, y probada. La decisión de qué hacer con ella está abajo.

Medido el 2026-11-16. Una sola máquina (macOS, arm64, APFS), una corrida por benchmark.

## Criterio de salida

| Criterio | Resultado |
| --- | --- |
| Ratio medido | 3,3× con nivel 3 sobre filas de texto: las páginas guardan 10,9 MiB donde sin comprimir guardan 35,9 MiB |
| Latencia medida | Insertar 100 000 filas en un commit: 31 → 72 ms. Leer 300 000 filas con la caché vacía: 27 → 73 ms |
| Un archivo con y otro sin compresión abren con el mismo código | El archivo guarda en su cabecera si comprime. Quien lo abre no lo indica: `CompressionTests` abre un archivo comprimido con opciones que no piden compresión, y uno sin comprimir con opciones que sí la piden, y cada uno sigue siendo lo que era |

## Lo que se midió

300 000 filas de texto con forma de registro de aplicación (unos 100 bytes: nombre, ciudad, estado, saldo), clave UUID v7, páginas de 8 KiB.

### Ratio

| Archivo | Bytes guardados en las páginas | Ratio | Tamaño del archivo | Espacio real en disco |
| --- | ---: | ---: | ---: | ---: |
| Sin comprimir | 35,9 MiB | 1,00× | 64,0 MiB | 36,0 MiB |
| Zstandard nivel 1 | 10,9 MiB | 3,31× | 64,0 MiB | 36,0 MiB |
| Zstandard nivel 3 (por defecto) | 10,9 MiB | 3,30× | 64,0 MiB | 36,0 MiB |
| Zstandard nivel 9 | 10,4 MiB | 3,44× | 64,0 MiB | 36,0 MiB |
| Zstandard nivel 19 | 10,3 MiB | 3,50× | 64,0 MiB | 36,0 MiB |

Las dos últimas columnas son el hallazgo. El plan establece que cada página conserva su casilla completa en el archivo, para no romper el direccionamiento por número de página. Entonces una página que comprime a un tercio sigue ocupando su casilla entera: el archivo mide lo mismo, y el espacio real en disco (medido con `du`) también, al kilobyte.

Casi todas las páginas comprimen (4 592 de 4 599). El nivel apenas cambia el ratio: páginas de 8 KiB son poco texto para que un nivel alto encuentre más.

### Latencia

| Carga | Sin comprimir | Nivel 3 | Costo |
| --- | ---: | ---: | ---: |
| Insertar 100 000 filas en un commit | 31,1 ms | 71,9 ms | 2,3× |
| Insertar 100 000 filas en cien commits de mil | 924 ms | 1 048 ms | +13 % |
| Abrir y leer 300 000 filas con la caché vacía | 26,5 ms | 73,4 ms | 2,8× |

Con la caché caliente la compresión no interviene: las páginas están descomprimidas en memoria.

Los números con cifrado y compresión juntos se midieron antes de la última corrección (la ventana, abajo) y no se repitieron: el cifrado sumaba entre 8 y 15 ms a cada carga comprimida.

### Dos cosas que la medición corrigió

- **La ventana de Zstandard.** La primera versión usaba la ventana mínima (1 KiB). Medido en aislado sobre una página, una ventana del tamaño de la página comprime un tercio mejor (1 114 bytes contra 1 606) y descomprime en casi la mitad del tiempo (4,3 µs contra 7,6 µs). Con el cambio, el ratio pasó de 2,2× a 3,3×, insertar de 116 a 72 ms y leer en frío de 109 a 73 ms.
- **Reutilizar los contextos de Zstandard no sirve.** Se probó guardar un codificador y un fondo de decodificadores en vez de usar las llamadas de un solo paso. No cambió nada (115 ms antes y después) y se revirtió.

### Escribir menos bytes cuesta tiempo

El plan dice que la ganancia está en escribir y leer menos bytes. Para escribir menos, cada página comprimida sale en su propia escritura (lo que sigue en su casilla no se toca), y se pierde la escritura en bloque de páginas consecutivas. Se midió la alternativa de escribir casillas completas en bloque: insertar baja de 72 a 57 ms, pero entonces se escriben exactamente los mismos bytes que sin comprimir y la compresión no ahorra nada de nada. Se dejó lo que dice el plan.

Leer menos bytes no se logra: para saber cuánto mide una página comprimida hay que leer su cabecera, y dos lecturas cuestan más que una de la casilla entera.

## Qué hacer con esto

Tres caminos, de menor a mayor trabajo:

1. **Dejarla como está**, apagada por defecto. Sirve a quien le importe escribir menos bytes (desgaste de un SSD, almacenamiento en red que cobra por byte escrito) y pueda pagar la latencia. No sirve para ahorrar disco.
2. **Quitarla.** Es poco código y está aislado; lo que se pierde es una opción que hoy empeora los dos números que suelen importar.
3. **Rediseñarla para que ahorre disco.** Hace falta que una página comprimida ocupe menos sitio en el archivo: páginas de tamaño variable con una tabla de traducción de número de página a posición, o perforar huecos en el archivo, que necesita llamadas al sistema operativo que el proyecto se prohibió. Es un cambio de formato de fondo, contrario a una decisión del plan, y no entra en esta fase.

Hay una cuarta opción barata y distinta: comprimir los **valores grandes** antes de partirlos en páginas de overflow. Ahí la compresión sí ahorra páginas enteras, sin tocar el direccionamiento. No está hecha ni medida.

## Cómo quedó hecho

- La compresión va delante de lo que protege la página: se comprime y después se le pone el checksum o se cifra, nunca al revés.
- Una página comprimida se guarda como su cabecera, el cuerpo comprimido y la verificación, uno tras otro. La cabecera lleva en claro la longitud del cuerpo comprimido; como la verificación la cubre, una longitud alterada no pasa.
- No se comprimen las páginas de la free-list ni las que no encogen (datos al azar): se guardan tal cual, con longitud cero.
- En memoria una página es idéntica haya estado comprimida o no; nada por encima del pager se entera.
- Zstandard es el del runtime (`System.IO.Compression`), sin paquete adicional. Bajo Native AOT queda enlazado en el binario.

## Cómo se probó

- `CompressionTests`, sobre archivo y sobre memoria, con y sin cifrado: ida y vuelta, reapertura sin indicar compresión, bytes escritos, datos que no comprimen, free-list sin comprimir, y daño en el cuerpo, en la longitud declarada y en la verificación, que se reporta nombrando la página.
- Las pruebas de crash (`PowerLossTests`, `ProcessCrashTests`) y las de propiedades del árbol corren también sobre bases comprimidas. Con compresión el commit tiene el triple de operaciones de disco, así que esas variantes usan una carga más corta.
- Se rompió el código de tres formas y las pruebas detectaron las tres: no verificar la página antes de descomprimirla, comprimir la free-list, y no validar la longitud declarada.
- El smoke de Native AOT abre una base cifrada y comprimida.

## Qué no cubre

- **Linux**: las pruebas (401) y el smoke de AOT pasan en un contenedor, pero el ratio y la latencia sólo se midieron en macOS.
- **Windows** sin verificar.
- **Latencia en otros sistemas de archivos.** El costo de las escrituras parciales depende del sistema de archivos; sólo se midió APFS.
- **Datos reales.** El ratio depende por completo del contenido; estas filas de texto son favorables.
- **El nivel de compresión no se guarda en el archivo**: es el de quien escribe en cada sesión. Cualquier nivel lee lo que escribió cualquier otro.
