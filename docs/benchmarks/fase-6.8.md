# Fase 6.8 — carga masiva: el B+Tree construido de abajo hacia arriba

**El criterio de salida no se cumple tal como está escrito.** Pide que importar diez millones de filas ordenadas sea un orden de magnitud más rápido que insertarlas una por una. Medido: es 3,1 veces más rápido. La carga masiva funciona, está probada en macOS y en Linux, y usa la novena parte de la memoria; pero el orden de magnitud sólo aparece contra insertar con un commit cada mil filas (58 veces). Windows no se ha verificado.

La razón es que insertar en orden fila por fila ya era rápido en este motor (1,7 millones de filas por segundo), y el piso de cualquier carga es generar las filas y escribir 700 MB. Qué hacer con el criterio es decisión tuya; las opciones están al final.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida por caso, cada caso en su propio proceso; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Lo que se midió

Diez millones de facturas (clave tipo UUID v7 creciente, cliente, fecha, total `decimal`, pagada, un texto corto: unos 70 bytes por fila), páginas de 8 KiB, todo en una transacción salvo donde se indica. El tiempo incluye abrir la base, generar las filas (0,36 s) y el commit durable.

### Filas en orden de clave, tabla sin índices

| | Tiempo | Filas por segundo | Memoria máxima | Archivo |
| --- | ---: | ---: | ---: | ---: |
| Carga masiva | 1,85 s | 5,4 M | 100 MiB | 768 MiB |
| Fila por fila, una transacción | 5,81 s | 1,7 M | 875 MiB | 768 MiB |
| Fila por fila, un commit cada mil filas | 107,7 s | 0,09 M | 348 MiB | 768 MiB |
| SQLite, fila por fila, una transacción | 14,0 s | 0,7 M | 388 MiB | 559 MiB |

- Contra fila por fila en una transacción: **3,1 veces más rápida**, con el 11 % de la memoria.
- Contra un commit cada mil filas: **58 veces más rápida**. Ahí el tiempo es de los diez mil fsync.
- Contra SQLite: 7,6 veces más rápida.

### Los otros casos

| | Carga masiva | Fila por fila, una transacción | Ventaja |
| --- | ---: | ---: | ---: |
| Filas desordenadas, sin índices | 7,05 s, 482 MiB | 11,97 s, 1 235 MiB | 1,7× en tiempo, 2,6× en memoria |
| Filas en orden, dos índices secundarios | 10,32 s, 480 MiB | 23,20 s, 1 903 MiB | 2,2× en tiempo, 4,0× en memoria |

En estos dos casos la carga masiva tiene que ordenar (las filas, o las entradas de los índices), y el archivo queda más grande: 1 536 MiB y 1 984 MiB contra 1 088 MiB y 1 792 MiB. Las tandas del ordenamiento ocupan páginas del propio archivo, que al confirmar quedan libres para reutilizarse pero no achican el archivo.

La memoria de la carga masiva en estos casos incluye la caché de páginas (256 MiB por defecto), que se llena al releer las tandas, y el buffer de ordenamiento (64 MiB).

## Por qué no llega al orden de magnitud

- **La referencia ya era rápida.** Insertar en orden fila por fila cuesta 0,58 µs por fila: el árbol reconoce la inserción al final y llena las hojas sin partirlas por la mitad (Fase 3), y dentro de una transacción las páginas se modifican en memoria.
- **El piso es alto.** De los 1,85 s de la carga masiva, 0,36 s son generar las filas, y escribir y vaciar 700 MB al disco no baja de unas décimas de segundo. Aunque el resto costara cero, la ventaja no pasaría de unas 7 veces.
- **No se optimizó más.** El camino por fila se hizo síncrono y dejó de alquilar un buffer por fila (de 0,76 a 0,68 s sobre dos millones de filas, en memoria). No se perfiló más allá de eso.

Lo que la carga masiva sí cambia de fondo es la memoria: fila por fila, una transacción tiene en memoria todas las páginas que tocó hasta el commit (875 MiB para diez millones de filas, 1,9 GiB con dos índices) y crece sin límite con el tamaño de la carga. La carga masiva no.

## Cómo quedó hecho

Tres piezas, cada una probada contra un modelo:

- **Volcado antes del commit** (`WriteTransaction.SpillAsync`). Una transacción puede escribir al archivo las páginas que ya terminó y soltarlas de memoria. Nada las alcanza hasta el cambio de raíz, así que no cambia lo que nadie ve; un rollback o un corte de energía las deja como espacio sin usar. El commit las vacía a disco antes de escribir la raíz.
- **Constructor de abajo hacia arriba** (`BTreeBuilder`). Recibe entradas en orden, llena cada hoja al 100 % y la escribe una vez; después el nivel de arriba, hasta la raíz. No busca, no parte ni copia. El árbol resultante ocupa lo que ocupan sus entradas (menos de un 3 % de más).
- **Ordenador externo** (`RunSorter`). Para las entradas de los índices, que no llegan en su orden, y para filas desordenadas. Ordena en memoria hasta un presupuesto, escribe la tanda y al final las mezcla. Las tandas van a páginas temporales del propio archivo, escritas por el pager: en una base cifrada quedan cifradas, y una base en memoria no necesita disco.

`BulkInsertAsync` las une: las filas que llegan en orden a una tabla vacía van directo al árbol; si alguna rompe el orden, lo construido queda como tabla y el resto se ordena y se mezcla con él. Una tabla que ya tenía filas se reescribe entera mezclada con las nuevas. Cada índice se reconstruye mezclando sus entradas con las nuevas ya ordenadas, y ahí mismo se comprueba la unicidad. `CHECK` y `FOREIGN KEY` se comprueban fila por fila al entrar.

### Las pruebas detectan el código roto

Veinte roturas hechas a mano. Dieciséis se detectaron a la primera, para tres se agregaron pruebas, y una no cambia el comportamiento.

| Rotura | Detectada |
| --- | --- |
| El commit no vacía a disco lo volcado cuando no tiene más que escribir | No al principio. Se agregó la prueba |
| El volcado deja en la caché la página vieja de un número reutilizado | Sí |
| El último nodo de un nivel se queda con un solo hijo | Sí |
| El nodo lleno se escribe sin esperar a ver si el siguiente necesita tomarle uno | Sí |
| El separador entre dos hojas es la clave equivocada | Sí |
| El constructor acepta claves fuera de orden | Sí |
| El ordenador cruza entradas de igual clave | Sí |
| El ordenador pierde lo que quedó en memoria | Sí |
| Las páginas temporales no se devuelven | Sí |
| Clave repetida contra una fila existente, aceptada | Sí |
| Clave repetida entre filas desordenadas, aceptada | Sí |
| Unicidad no comprobada en la carga | Sí |
| Filas sin valor chocan en un índice único | Sí |
| La tabla anterior no se libera tras la mezcla | Sí |
| El índice anterior no se libera | Sí |
| Un fallo a media carga no impide confirmar | Sí |
| Las restricciones se saltan en la carga | Sí |
| No volcar antes de empezar a leer lo que la transacción ya había escrito | No al principio: dependía de qué buffer se reutilizaba. Las pruebas ahora borran en memoria cada página volcada, y lo detectan |
| No volcar lo construido antes de mezclarlo con el resto | No al principio. Lo mismo |
| Clave repetida entre filas en orden, aceptada en el camino rápido | No: la detecta igual el camino de la mezcla |

## Límites conocidos

- **Una tabla que ya tiene filas se reescribe entera.** Agregar mil filas a una tabla de diez millones por este camino cuesta reescribir las diez millones.
- **El archivo no se achica.** Lo que el ordenamiento usó de espacio temporal, y la tabla anterior cuando se reescribe, quedan como páginas libres.
- **No admite tablas con `FOREIGN KEY` a sí mismas**: una fila puede referirse a otra que todavía no llegó.
- **`FOREIGN KEY` a otra tabla se comprueba fila por fila**, con una búsqueda en la otra tabla por cada fila.
- **Un fallo a media carga deja la transacción sin poder confirmarse**, también si es una clave repetida, que en la inserción normal no la aborta.
- **El volcado sólo lo usa la carga masiva.** Una transacción normal que inserta millones de filas una por una sigue teniendo todas sus páginas en memoria hasta el commit.
- **El change feed informa una tabla reescrita comparándola entera**: los dos estados no comparten páginas.

## Decisión que te toca

1. **Aceptar la fase con el criterio sin cumplir**, anotado como está aquí. Es lo que yo haría: lo que el criterio buscaba —que cargar una tabla no sea insertar fila por fila— está, y la ventaja de fondo resultó ser la memoria.
2. **Cambiar el criterio** por uno que se pueda medir contra esta referencia: por ejemplo, "tres veces más rápido y con memoria acotada", o "un orden de magnitud contra commits por lotes".
3. **Seguir optimizando.** El techo es de unas 7 veces, por el costo de generar las filas y escribir el archivo; llegar a 10 no es posible contra una inserción en orden que ya es así de rápida.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 572 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con una carga de filas desordenadas a una tabla con índices y restricciones | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 572 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Las mediciones sólo se hicieron en macOS.

Además de las pruebas contra modelos: un corte de energía simulado antes de cada operación de una transacción que vuelca, tras el cual la base abre en el estado anterior o en el nuevo; y una carga en una base cifrada, comprobando que nada del ordenamiento llega en claro al archivo.
