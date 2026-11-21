# Fase 4 — MVCC, transacciones y commit atómico

**Criterio de salida cumplido en macOS.** Un proceso que se mata en cada punto del commit deja siempre una base que abre en un estado consistente. Falta confirmarlo en Linux y Windows, donde corre en CI.

Medido el 2026-11-16. Una sola máquina.

## Criterio de salida

La carga son ocho transacciones de cuarenta operaciones (inserciones, reemplazos y borrados) sobre un B+Tree de páginas de 4 KiB, con la base creada dentro de la misma corrida. Hace 57 operaciones sobre el archivo sin cifrar y 59 cifrada: escrituras, flushes y cambios de longitud.

| Prueba | Qué hace | Casos |
| --- | --- | ---: |
| `ProcessCrashTests` | Un proceso hijo ejecuta la carga sobre un archivo real y se mata (`SIGKILL` / `TerminateProcess`) antes de cada operación y, si es una escritura, también a medio hacerla | 93 muertes sin cifrar, 96 cifrada |
| `PowerLossTests` | El dispositivo deja de funcionar antes de cada operación; de lo escrito desde el último flush sobrevive nada, todo, o una mezcla al azar por escritura o por sector de 512 bytes | 57 y 59 puntos, 8 imágenes cada uno, sobre archivo y sobre memoria |

En cada caso se exige lo mismo (`CrashRecovery`):

1. La base abre, salvo que el proceso haya muerto antes de que la creación se confirmara; entonces puede fallar, pero con "no es una base de datos", no con datos dañados.
2. Su contenido es exactamente el del último commit confirmado al llamador o el del commit que estaba en vuelo. Ningún otro.
3. El árbol pasa el verificador de estructura y ninguna página se perdió.
4. Se puede seguir escribiendo: la carga se retoma desde ese estado y termina con el contenido final correcto.

Matar el proceso no pierde escrituras, porque el sistema operativo las conserva; por eso existe la segunda prueba, que es más dura: simula lo que el disco pudo no haber recibido.

### Las pruebas detectan un commit roto

Se rompió el commit a propósito de tres formas y se restauró después:

| Rotura | Pruebas que fallan |
| --- | --- |
| No hacer flush de las páginas antes de escribir el slot de raíz | Las cuatro de corte de energía |
| Escribir el slot de raíz antes que las páginas | Las cuatro de corte de energía y las dos de proceso muerto |
| El escritor ignora a los lectores vivos al reutilizar páginas | Las de concurrencia y las de transacción de lectura |

## Un defecto encontrado en esta fase

El commit de las fases anteriores tenía un hueco que no necesitaba un crash para abrirse. Si la escritura del slot de raíz o su flush fallaban, o si el llamador cancelaba justo ahí, el motor seguía trabajando desde el estado anterior, pero el slot nuevo podía haber llegado al disco. La siguiente transacción reutilizaba páginas que ese slot alcanzaba; un crash antes de su propio commit dejaba en el disco un slot válido apuntando a páginas sobrescritas.

Ahora:

- Pasado el flush de las páginas, el commit no observa la cancelación. Son una escritura de 64 bytes y un flush.
- Si la escritura del slot o su flush fallan, el resultado es incierto y el motor lo dice: lanza un error que pide reabrir la base y no acepta más escrituras. Los lectores siguen en el estado anterior, que está intacto. Al reabrir, la base está en uno de los dos estados, completo.
- Un fallo antes del slot (escribir páginas, disco lleno, cancelación) sigue siendo un rollback limpio y el escritor continúa.

## Concurrencia

`ConcurrencyTests`: un escritor y cuatro lectores en hilos distintos, con una caché de 24 páginas. Cada transacción reescribe la base entera con su número de generación; cada lector recorre todo dentro de una transacción de lectura y exige que todas las filas sean de la misma generación, que las lecturas puntuales posteriores dentro de la misma transacción sigan viéndola, y que nunca retroceda. Uno de los lectores retiene su snapshot mientras pasan muchos commits.

CI corre dos segundos por dispositivo. La versión de tres minutos que pide el plan es explícita:

```
dotnet test -c Release --filter-method "*for_several_minutes*" --explicit only
```

Pasó sobre archivo y sobre memoria, tres minutos cada una. La prueba no reporta cuántas generaciones ni cuántas rondas de lectura hizo en ese tiempo.

## Amplificación de espacio

El plan pide medirla desde esta fase. 100 000 filas (clave UUID v7, valor de 100 bytes: 11,1 MiB de datos), y después 2 000 transacciones de 50 actualizaciones al azar. Páginas de 8 KiB en TekiumDB y de 4 KiB en SQLite.

| Base | Tras la carga inicial | Tras las actualizaciones | Veces los datos |
| --- | ---: | ---: | ---: |
| TekiumDB | 11,9 MiB | 12,8 MiB | 1,16× |
| SQLite (WAL, tras checkpoint) | 13,5 MiB | 13,5 MiB | 1,22× |
| TekiumDB con un lector retenido todo el tiempo | 11,9 MiB | 928,4 MiB | 83,9× |

Sin lectores retenidos, copy-on-write cuesta 120 páginas libres en circulación: el archivo queda por debajo del de SQLite. Con una transacción de lectura olvidada, cada commit necesita páginas nuevas y el archivo crece sin límite: 84 veces los datos en esta carga. Es el riesgo que el plan ya nombraba, ahora con número. El espacio se recupera para reutilizarlo al cerrar el lector, pero el archivo no se encoge hasta que exista `CompactAsync`.

Se reproduce con `dotnet run -c Release --project tests/TekiumDB.Benchmarks -- space`.

## Qué no cubre

- **Sectores a medio escribir.** El corte de energía simulado deja cada sector de 512 bytes entero, viejo o nuevo. No modela un sector con basura. El slot de raíz lleva checksum (o HMAC si la base está cifrada) y ante uno dañado se usa el otro, pero la reserva de ids de transacción comparte sector con el slot de su página meta: en un disco que no escriba sectores de forma atómica, una reserva interrumpida podría costar el último commit. No está probado ni resuelto.
- **Tiempo de escritura con lectores activos.** No se midió si los lectores concurrentes frenan al escritor; no hay benchmark de concurrencia todavía.
- **Aviso de snapshots vivos demasiado tiempo.** El plan lo propone como mitigación del lector olvidado. No está: corresponde a los contadores de diagnóstico de la Fase 10.4.
- **Linux y Windows.** `fsync`, `FlushFileBuffers` y la forma de matar un proceso difieren; todo lo anterior se verificó sólo en macOS.
