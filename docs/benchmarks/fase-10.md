# Fase 10 — herramientas: inspección, reparación, cambio de contraseña y compactación

**Criterio de salida cumplido en macOS y en Linux.** Un archivo corrupto a mano se diagnostica sin abrir la base: se daña un byte de cada página del archivo, una por una, y el inspector nombra siempre la página dañada y a qué pertenece, sin lanzar nunca. Windows no se ha verificado.

Hecho el 2026-11-16. Esta fase no tiene criterio de rendimiento y no se midieron tiempos.

## Qué hay

| Pieza | Dónde | Qué hace |
| --- | --- | --- |
| `TkInspector.InspectAsync` | Núcleo | Examina un archivo sin abrirlo y devuelve lo que encontró: cabecera, registros de raíz, árboles con sus entradas y páginas, y una lista de hallazgos con severidad, página y a qué pertenece |
| `TkMaintenance.CompactAsync` | Núcleo | Escribe una copia sin espacio libre, con cada árbol reconstruido a páginas llenas. Se niega si el original está dañado |
| `TkMaintenance.RepairAsync` | Núcleo | Escribe una copia con lo que se puede leer, e informa qué estaba mal y qué dejó fuera |
| `tkdb` | `TekiumDB.Cli` | Lo anterior desde la línea de comandos (`info`, `check`, `compact`, `repair`) y `passwd`, que cambia la contraseña en el sitio. Se instala como herramienta de .NET o se publica como ejecutable nativo de 2,8 MB |

Ninguna cambia el archivo que recibe, salvo `passwd`, que reescribe sólo la cabecera (ya existía como `ChangePasswordAsync`).

## Qué revisa el inspector

1. **La cabecera**, que está escrita dos veces. Si la primera copia está dañada lee la segunda y lo dice; el motor sólo lee la primera, así que ese archivo no abre aunque sus datos estén enteros.
2. **Los dos registros de raíz** (dónde está la base). Dice cuál pasa su comprobación y con cuál abriría; si el más nuevo está dañado, avisa que un commit pudo perderse.
3. **Que el archivo no esté cortado**: que tenga las páginas que la base dice tener.
4. **Cada página en uso**: su suma de verificación (o su etiqueta de autenticación, si está cifrada), que sea la página que debe ser, y que el árbol esté bien formado: claves en orden y dentro del rango que da el padre, hojas a la misma profundidad, ninguna página alcanzada dos veces ni fuera del archivo, valores grandes con todas sus páginas.
5. **La lista de páginas libres**: que se pueda leer, que no ofrezca una página en uso, y que no falte ninguna (páginas que nadie usa ni ofrece).

Recorre el catálogo y, desde él, cada tabla, cada índice y cada árbol de cada índice vectorial. El núcleo no sabe qué guarda un índice vectorial, sólo dónde están sus árboles: por eso su entrada del catálogo cambió de forma en esta fase y ahora empieza por las raíces.

**Con una base cifrada y sin contraseña** sólo se lee la cabecera, y se dice. Con la contraseña equivocada, también se dice.

## Qué deja fuera la reparación, y por qué

Reparar copia todo lo que se puede leer, con una regla: la copia nunca responde mal.

- **Filas de una página dañada:** se pierden. El informe nombra la página y la tabla.
- **Un índice dañado, o cualquier índice de una tabla que perdió filas:** no se copia. Las consultas recorren la tabla hasta la primera escritura en ella, que lo construye de nuevo (es lo que el motor ya hace con un índice declarado que no encuentra).
- **Un índice vectorial con cualquier daño:** no se copia. Un grafo al que le faltan nodos no se distingue de uno entero; hay que volver a llenarlo.
- **El historial del change feed:** nunca se copia, tampoco al compactar. Apunta a estados del archivo original. Quien consume el change feed tiene que empezar de nuevo sobre la copia, y los números de transacción de la copia empiezan otra vez.
- **Lo que no se revisa:** que las claves foráneas sigan cumpliéndose después de perder filas. Una fila puede quedar apuntando a otra que se perdió.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Criterio de salida: un byte dañado en cada página, una por una | `InspectorTests`: el inspector nunca lanza; nombra la página y a qué pertenece; una página libre dañada no es un hallazgo. De paso comprueba que la base, abierta normalmente, falla al llegar a esa página (y que a veces no llega: un índice que no se consulta puede estar dañado sin que la aplicación lo note) |
| Daño que la suma de verificación no ve | Páginas bien selladas con contenido imposible: claves fuera de orden, dos punteros a la misma página, un puntero fuera del archivo, tipos de página cambiados, la lista de libres ofreciendo una página en uso u olvidando una |
| Cabecera y raíces | Primera copia dañada; versión de formato desconocida; registro de raíz más nuevo dañado; los dos dañados; archivo cortado; lo que no es una base |
| Cifrado | Sin contraseña, con la equivocada, con la correcta, y una página cifrada alterada |
| Compactar | Todo sigue ahí (filas, cinco índices, valores grandes), los índices se usan, no queda nada libre ni perdido, el archivo queda en menos de la mitad; se niega ante un archivo dañado; restricciones copiadas byte por byte y vigentes; otra contraseña o ninguna |
| Reparar | Una hoja dañada: se pierden sus filas, las demás son idénticas, los índices de esa tabla se reconstruyen en la primera escritura. Un índice dañado: sólo ése queda fuera |
| Índices vectoriales | Se inspeccionan, se compactan y responden igual; uno dañado queda fuera de la reparación (`TekiumDB.Vectors.Tests`) |
| `tkdb` | Cada comando, llamado como desde una terminal: salida, código de salida, contraseñas por variable, archivo y entrada estándar, y que una contraseña escrita como argumento se rechaza |

Todas sobre archivo y sobre memoria, salvo las de `tkdb`, que son sobre archivos de verdad.

### Las pruebas detectan el código roto

Cuarenta y cinco roturas hechas a mano, una por una. Treinta y tres se detectaron a la primera; para ocho agregué pruebas; dos hubo que repetirlas porque la primera forma no compilaba; una no cambia nada; y una sobrevive.

| Rotura | Detectada |
| --- | --- |
| No comprobar el orden de las claves, páginas alcanzadas dos veces, tipo de página de árbol o de valor grande | Sí |
| No comprobar que las claves estén en el rango que da el padre | No al principio. Se agregó el caso de dos hijos cambiados de lugar |
| No comprobar que un número de página esté dentro del archivo | Sí (repetida: la primera forma no compilaba) |
| No comprobar que las hojas estén a la misma profundidad | **No.** En todos los casos que pude construir, la comprobación del rango salta antes. Queda como segunda barrera sin prueba propia |
| Dejar pasar un fallo de lectura del dispositivo como excepción | No al principio: el archivo cortado se detectaba antes de leer. Se agregó una lectura que falla a mitad de la inspección |
| No contar las páginas dañadas; no leer enteros los valores grandes | Sí |
| Contar como leída una entrada cuyo valor grande se perdió | No al principio. Se agregó la comprobación |
| No probar la segunda copia de la cabecera; elegir el registro de raíz más viejo; no avisar del registro dañado, del archivo cortado ni de la cabecera cortada | Sí |
| Avisar de un registro de raíz que nunca se escribió | No al principio, y al escribir la prueba apareció un defecto: con un solo commit, un registro de raíz dañado no se avisaba. Corregido y con prueba |
| No avisar de la segunda copia de la cabecera dañada | No al principio. Se agregó la prueba |
| Contraseña equivocada sin ser error | Sí (repetida de otra forma) |
| Lista de libres: página en uso ofrecida, páginas sin contar, daño sin su número de página | Sí |
| No seguir los árboles de los índices o de los índices vectoriales; no avisar de una entrada desconocida | Sí |
| Reparar: conservar el índice de una tabla dañada, un índice dañado o un índice vectorial dañado; no liberar las páginas de lo descartado | Sí |
| Compactar con una entrada desconocida; no copiar las restricciones; perder la definición de un índice; no reescribir las raíces del índice vectorial | Sí |
| Reiniciar la versión del esquema de una tabla | No al principio: el motor se recupera solo en la siguiente escritura. Se agregó la comprobación directa |
| Copiar el historial del change feed | No, y no cambia nada: esa entrada no se copia por otro camino |
| Compactar un archivo dañado; dejar el archivo de una copia fallida; ignorar la contraseña nueva | Sí |
| Perder la marca de compresión en la copia | No al principio. Se agregó la prueba |
| `tkdb`: salir con 0 habiendo problemas (`check`, `repair`); ignorar `--no-password`; leer más de la primera línea del archivo de contraseña | Sí |
| `tkdb info` saliendo con 0 ante lo que no es una base | No al principio. Se agregó la comprobación |

## Límites conocidos

- **Sólo archivos cerrados.** Las herramientas no se coordinan con un proceso que tenga la base abierta: inspeccionar mientras otro escribe puede dar hallazgos falsos, y compactar puede copiar un estado a medias. La verificación sobre una base abierta es la Fase 10.4.
- **El historial retenido del change feed no se revisa.** El inspector recorre el estado actual. Una página dañada que sólo pertenezca a un estado anterior retenido no se detecta.
- **Reparar no revisa claves foráneas ni vuelve a comprobar `CHECK`.**
- **Compactar necesita espacio para la copia** y no reemplaza el original: lo hace quien la corre.
- **El archivo compactado puede medir una página o dos más que sus datos.**
- **Las contraseñas de `tkdb` pasan por memoria administrada** (cadenas de .NET) y no se borran al terminar.
- **Sin rotación de la clave de archivo en el sitio.** `compact` da a la copia una clave nueva; `passwd` sólo vuelve a envolver la que hay.
- **No se midió cuánto tardan** en archivos grandes. Compactar y reparar leen cada página una vez y escriben cada página una vez.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 711 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT: inspeccionar, compactar a un archivo sin cifrar, dañar un byte, diagnosticar y reparar | Pasa |
| macOS (arm64) | `tkdb` publicado como ejecutable nativo (2,8 MB), corrido a mano sobre un archivo real con un byte cambiado: `check`, `compact` (se niega) y `repair` | Funciona; la salida está en el README |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 711 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

El ejecutable nativo de `tkdb` sólo se publicó y corrió en macOS.
