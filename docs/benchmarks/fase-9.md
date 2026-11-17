# Fase 9 — índice vectorial HNSW en `Tekium.Embedded.Vectors`

**Criterio de salida cumplido en macOS; pruebas verificadas también en Linux.** Sobre un millón de vectores, el recall@10 contra fuerza bruta es 0,98 con los parámetros por defecto (el criterio pide más de 0,95). Vale para vectores con estructura, como los embeddings; con vectores sin ninguna estructura el recall es mucho más bajo, y está medido abajo. Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## El criterio, tal como se midió

El plan dice "Recall@10 por encima de 0,95 contra fuerza bruta en un conjunto de un millón de vectores", sin decir qué vectores ni con qué parámetros. Lo fijé así antes de medir:

> Un millón de vectores de 128 dimensiones agrupados alrededor de mil centros, métrica coseno, parámetros por defecto (`M` 16, `efConstruction` 200, `efSearch` 64). Mil consultas sacadas de la misma mezcla que los datos. Recall@10: de los diez vecinos verdaderos de cada consulta (comparándola contra el millón), cuántos devuelve el índice.

Si prefieres otro conjunto (uno público como SIFT1M, por ejemplo), el programa de medición sólo necesita los vectores.

## Mediciones

Sobre archivo, páginas de 8 KiB, caché de 4 GiB (todo el índice cabe), commits de 10 000 vectores. Latencia de una consulta a la vez, con la caché caliente.

### Un millón de vectores, agrupados

Construcción: 1 092 s (916 vectores por segundo, en un solo hilo). En uso: 689 MiB dentro de un archivo de 1 024 MiB.

| `efSearch` | Recall@10 | Latencia media | p99 |
| ---: | ---: | ---: | ---: |
| 16 | 0,787 | 0,39 ms | 0,62 ms |
| 32 | 0,919 | 0,56 ms | 0,95 ms |
| **64 (por defecto)** | **0,980** | **0,75 ms** | **1,36 ms** |
| 128 | 0,996 | 0,96 ms | 1,68 ms |
| 256 | 0,999 | 1,19 ms | 2,02 ms |

Fuerza bruta sobre el mismo millón: 3,8 ms por consulta usando todos los núcleos.

### Cien mil vectores

| Conjunto | Construcción | En uso | Recall@10 con `efSearch` 16 / 64 / 256 | Latencia media con 64 |
| --- | ---: | ---: | ---: | ---: |
| Agrupados | 104 s | 69 MiB | 0,990 / 1,000 / 1,000 | 0,29 ms |
| Agrupados, cuantizado a `int8` | 106 s | 83 MiB | 0,967 / 1,000 / 1,000 | 0,30 ms |
| Sin estructura | 181 s | — | 0,078 / 0,234 / 0,563 | 0,81 ms |
| Sin estructura, `M` 48 | 532 s | 96 MiB | 0,250 / 0,566 / 0,916 | 2,00 ms |

- **Sin estructura** son vectores con cada componente al azar e independiente. En 128 dimensiones todos quedan casi a la misma distancia unos de otros y no hay vecindario que el grafo pueda seguir: es el peor caso conocido de cualquier índice aproximado, no un caso de uso. Lo que hay que saber es que el recall depende de los datos, y que las palancas son `efSearch` y `M`.
- **Cuantizado** guarda cada componente del grafo en un byte. Encuentra lo mismo, porque los candidatos finales se reordenan con los vectores originales; ocupa más en total, porque los originales también se guardan. Lo que se reduce a la cuarta parte es la parte que la búsqueda recorre.

## Cómo quedó hecho

**El grafo vive en B+Trees del mismo archivo.** Cuatro árboles por índice, con sus raíces en una entrada del catálogo:

| Árbol | Clave | Valor |
| --- | --- | --- |
| Nodos | id | marcas, nivel, el vector como lo compara el grafo (`float` o `int8`), y la clave del usuario |
| Vecinos | id y capa | los ids de los vecinos en esa capa |
| Claves | clave del usuario | id |
| Originales (sólo cuantizado) | id | el vector tal como se dio |

Con eso el índice hereda, sin código propio, todo lo que el motor ya garantiza: copy-on-write, snapshots (un lector sigue viendo su grafo aunque otro lo cambie), cifrado, y commit atómico junto con las filas. El núcleo sólo ganó una entrada de catálogo nueva y un punto donde lo que participa en una transacción escribe su estado al confirmar.

**El algoritmo** es HNSW como lo describe el artículo: el nivel de cada nodo sale de su id (así es reproducible), se baja capa por capa hasta el vecino más cercano, y en cada capa del nodo nuevo se eligen sus vecinos con la regla de diversidad (se descarta al candidato que está más cerca de un vecino ya elegido que del nodo). Los lugares que sobran se llenan con los más cercanos de los descartados: medido sobre 6 000 vectores con consultas que no se parecen a los datos, eso sube el recall con producto interno de 0,80 a 0,94 (`efSearch` 64).

**Las distancias** se calculan con `TensorPrimitives` (SIMD), sin intrínsecos a mano. Coseno normaliza al guardar y después es un producto interno.

**Borrar** marca el nodo: se sigue atravesando, pero no se devuelve ni se elige como vecino, y va saliendo de las listas a medida que se rehacen. Reemplazar un vector es borrar e insertar.

## Lo que no es como dice el plan

| El plan | Lo hecho | Por qué |
| --- | --- | --- |
| Un tipo de página "nodo HNSW" | El grafo en B+Trees | Una página propia direccionada por número no sobrevive al copy-on-write sin una tabla de traducción, que es lo que el árbol ya es. Cuesta una búsqueda en árbol por cada nodo visitado |
| `docs.SearchVectorAsync(x => x.Embedding, …)` | `db.VectorIndex<TKey>(nombre, opciones)`, que se escribe junto a la fila en la misma transacción | La lambda pide árboles de expresión (reflexión, que el plan prohíbe). Y que la tabla mantenga el vector sola pide un tipo de columna nuevo y que el núcleo conozca al paquete de vectores, que tiene prohibido |
| "El grafo se carga perezosamente por capas al abrir" | No se carga nada al abrir: cada búsqueda lee las páginas que toca, por la caché | Es lo que da el estar en el pager |
| Cuantización que "reduce la memoria del grafo cuatro veces" | Reduce cuatro veces los vectores que el grafo recorre; los originales se guardan aparte para el reordenamiento exacto, como pide el mismo párrafo | — |
| `IVectorIndex`, para que DiskANN entre después | Existe, y `HnswIndex` la implementa | — |

## Pruebas

`HnswIndexTests`, sobre archivo y sobre memoria (24 pruebas en cada uno):

| Qué | Cómo |
| --- | --- |
| Lo que encuentra | Con una búsqueda ancha sobre 300 vectores, exactamente los diez de fuerza bruta y sus distancias, en las tres métricas. Recall@10 por encima de 0,95 sobre 6 000 vectores con `efSearch` 64, en las tres métricas y cuantizado |
| El caso difícil | Consultas que no se parecen a lo guardado, con producto interno: se recupera ensanchando la búsqueda |
| Transacciones | Lo escrito se encuentra dentro de la transacción y no fuera hasta confirmar; un lector anterior sigue viendo lo anterior; sin commit no queda rastro |
| Persistencia | Mismos resultados después de cerrar y abrir |
| Cambios | 4 800 reemplazos y borrados al azar contra un modelo: nada borrado aparece, nada sale dos veces, el recall se mantiene |
| Corte de energía | En cada operación de un commit, con lo no vaciado perdido entero, a medias o conservado: el índice abre como antes o como después |
| Fallo a medias | Una lectura que falla durante una inserción deja la transacción sin poder confirmarse |
| Forma del grafo | Niveles repartidos como deben, ninguna lista por encima de su tope, la capa de abajo con el doble, sin enlaces a sí mismo ni repetidos ni a capas que el vecino no tiene, nadie suelto |
| Cuantización | La parte de nodos ocupa menos de un tercio; la distancia devuelta es la exacta; rango no centrado y valores fuera de rango; los originales de lo borrado se van |
| Lo demás | Claves de los cuatro tipos; vectores de 1 536 dimensiones; lo que no cuadra con el índice se rechaza antes de tocar nada; `DropAsync` devuelve las páginas y libera el nombre |

Todas las pruebas que escriben comprueban además que ninguna página se pierde.

### Las pruebas detectan el código roto

Treinta y cuatro roturas hechas a mano, una por una. Veintisiete se detectaron a la primera; para cuatro agregué pruebas; una hubo que repetirla porque la primera forma no compilaba; y dos no cambian ningún resultado, sólo cuánto se tarda.

| Rotura | Detectada |
| --- | --- |
| Sin la regla de diversidad al elegir vecinos; sin llenar los lugares sobrantes | Sí |
| Sin enlaces de vuelta; listas que nunca se recortan; la capa de abajo sin el doble | Sí |
| Todos los nodos en la capa cero; la entrada del grafo nunca sube | Sí (forma del grafo) |
| Devolver nodos borrados, o la entrada borrada | Sí |
| El conjunto de resultados sin tope | Sí |
| Elegir como vecinos a nodos borrados | No al principio. Se agregó la prueba de que los enlaces a lo borrado van desapareciendo |
| Cuantización: decodificar sin el desplazamiento; codificar sin recortar al rango | No al principio: las pruebas usaban rangos centrados en cero y datos dentro del rango. Se agregó la prueba del rango no centrado con valores fuera |
| Dejar los vectores originales de lo borrado | No al principio. Se agregó la comprobación |
| Coseno sin normalizar; sin reordenar con los originales | Sí |
| Borrar sin quitar la clave, sin marcar el nodo o sin bajar la cuenta; reemplazar sin quitar el anterior | Sí |
| No escribir la cabecera al confirmar; el núcleo sin llamar a los participantes | Sí |
| Un fallo a medias que no envenena la transacción | Sí |
| El escritor buscando o contando sobre lo confirmado en vez de lo suyo | Sí |
| No comprobar la declaración del índice; ignorar las dimensiones en ella | Sí (la segunda, repetida de otra forma: la primera no compilaba) |
| `DropAsync` dejando un árbol o la entrada del catálogo | Sí |
| No comprobar las dimensiones del vector; ignorar `topK` | Sí |
| Cortar la búsqueda de una capa demasiado pronto | No, y no cambia los resultados de las pruebas: la condición sólo importa con el conjunto lleno |
| Saltarse el descenso por las capas altas | No: en conjuntos chicos la capa de abajo llega igual. Es rendimiento a escala, no corrección |

## Límites conocidos

- **El recall depende de los datos.** Con vectores sin estructura es bajo con los parámetros por defecto (tabla de arriba).
- **Construir es lento:** del orden de mil vectores por segundo con 128 dimensiones, en un solo hilo. No hay construcción en paralelo ni carga masiva del grafo.
- **Un escritor a la vez,** como todo en la base. Las búsquedas sí corren en paralelo.
- **Borrar no devuelve espacio.** El nodo queda en el grafo; sólo `DropAsync` libera las páginas, y no hay reconstrucción en el sitio. Un índice con muchos borrados se rehace creando otro.
- **Producto interno sobre vectores sin normalizar** no es una distancia de verdad y el grafo lo sirve peor que las otras dos métricas; con consultas distintas a los datos necesita `efSearch` más alto.
- **La cuantización usa un rango fijo,** el mismo para todas las componentes (`QuantizationMin`/`Max`; por defecto −1 a 1, que es lo que da coseno). Lo que se sale cuenta como el extremo. No se ajusta sola a los datos.
- **Vectores de más de unos 500 `float`** no caben en una hoja y salen a páginas de overflow: funcionan (hay prueba con 1 536 dimensiones) pero cada visita lee más páginas. No está medido a escala.
- **Hasta unos cuatro mil millones de vectores insertados por índice** en toda su vida, contando reemplazos: los ids no se reutilizan.
- **Sin filtro por condición** ("los más cercanos entre los de este cliente"): se filtra después, pidiendo más resultados.
- **Los tiempos son de una sola máquina** y de una corrida.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 652 pasan (604 del núcleo y 48 del índice vectorial), 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con dos índices vectoriales (uno cuantizado) sobre una base cifrada | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 652 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Las mediciones sólo se hicieron en macOS. El millón se midió dos veces: la primera, con otras pruebas corriendo en la máquina, dio el mismo recall (0,980) y 1 131 s de construcción; la tabla es la segunda, sin nada más corriendo. La fila "sin estructura" con `M` 16 se midió antes de que el programa informara el espacio en uso.
