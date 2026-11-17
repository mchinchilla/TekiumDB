# Fase 10.2 — backup en caliente desde un snapshot fijado

**Criterio de salida cumplido en macOS; pruebas verificadas también en Linux.** La copia es consistente con el escritor activo, y la latencia de escritura no se mueve durante el respaldo: la mediana del commit durable es la misma con backups corriendo uno tras otro (13,0 ms) que sin ellos (13,1 ms). La cola sí sube algo: el p95 pasa de 16,1 a 19,1 ms. Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Qué hay

```csharp
var report = await db.BackupAsync("respaldo.tkdb");        // con la base abierta y en uso
// report.TransactionId: el commit que refleja la copia; todo hasta él, nada después
```

- **Consistente:** la copia tiene exactamente lo que dejó el último commit al empezar. Lo que se escriba mientras corre no entra, y nada entra a medias.
- **Sin bloquear:** fija un snapshot, como cualquier lector, y copia desde él. El escritor no espera.
- **Sin tocar la caché:** lee las páginas directo del archivo. Un backup lee cada página una vez; pasándolas por la caché desalojaría lo que la aplicación está usando.
- **Compacta:** es la misma copia de la Fase 10 (cada árbol reconstruido a páginas llenas, nada libre), con su propia clave de archivo.
- **Cifrado:** si la base está cifrada, el backup pide una contraseña propia (`TkBackupOptions.Password`) o que se diga explícitamente que va sin cifrar (`Unencrypted`). Nunca se escribe sin cifrar por omisión. La base abierta no conserva su contraseña, así que no puede reutilizarla.
- **Si falla o se cancela:** no queda archivo, y el snapshot se suelta.

## Mediciones

Un millón de filas con dos índices (127 MiB en uso), sobre archivo. Un escritor hace commits durables de 100 filas actualizadas al azar, 400 por tramo. En el tramo del medio, otra tarea hace backups uno tras otro a archivos nuevos.

| Tramo | Mediana | p95 | p99 | Backups terminados | Archivo al final |
| --- | ---: | ---: | ---: | ---: | ---: |
| Sin backup | 13,05 ms | 16,14 ms | 21,02 ms | — | 171 MiB |
| Con backups corriendo | 13,02 ms | 19,05 ms | 26,94 ms | 16 | 171 MiB |
| Sin backup, después | 13,90 ms | 15,93 ms | 24,78 ms | — | 171 MiB |

Cada backup tardó 0,35 s en promedio y pesa 127 MiB.

- **El umbral lo había pensado antes de medir pero no lo dejé escrito:** mediana dentro del 10 %. Se cumple con holgura. El plan dice "no se mueve", sin número.
- **La cola sube** (p95 +18 %). El backup escribe 127 MiB al mismo disco cada 0,35 s mientras el escritor vacía sus commits: comparten el disco, no un candado. El p99 varía tanto entre los dos tramos sin backup (21 y 25 ms) que no dice nada.
- **El archivo no creció** durante dieciséis backups. El plan pedía medir la amplificación: aquí es cero, porque cada backup retiene el estado sólo 0,35 s y las páginas que el escritor libera en ese lapso caben en lo que ya había libre. Crecería con un backup más largo (una base mucho más grande, un disco lento) o un escritor más rápido: mientras el backup corre, ninguna página de su estado se reutiliza, igual que con un lector que no termina.

## Lo que no es como dice el plan

El plan describe el respaldo como "copiar las páginas alcanzables desde una raíz". Lo hecho copia las *entradas* y reconstruye los árboles, que es lo que ya hacía la compactación de la Fase 10. Copiar páginas tal cual pediría además traducir cada número de página (las del snapshot están salpicadas por el archivo) o copiar también las libres. A cambio, el backup sale compacto, y cuesta decodificar y volver a armar cada página en vez de copiar bytes.

## Pruebas

| Qué | Prueba |
| --- | --- |
| La copia es el estado del inicio, aunque después se reescriba todo cinco veces y se borre la mitad | `BackupTests`: con el snapshot ya fijado y antes de copiar, el escritor hace seis commits; la copia tiene las filas de antes, sus índices responden, y no pierde páginas |
| Con un escritor que no para | Backups uno tras otro mientras otra tarea confirma sin pausa: cada copia tiene todas las filas de una misma ronda, la del commit que dice reflejar |
| Lo retenido se suelta | Tras el backup, y tras uno fallado o cancelado, el archivo crece igual que el de una base idéntica sin backups |
| No pasa por la caché | En una base más grande que la caché, lo que la aplicación tenía caliente sigue caliente después del backup |
| Cifrado y compresión | Base cifrada sin decir nada: se rechaza; con contraseña propia; sin cifrar a pedido. El backup de una base comprimida queda comprimido |
| Por ruta | El destino no debe existir; uno cancelado no deja archivo |
| Índices vectoriales | El backup lleva el índice como estaba, aunque después crezca y pierda vectores (`Tekium.Embedded.Vectors.Tests`) |

### Las pruebas detectan el código roto

Diez roturas hechas a mano. Seis se detectaron a la primera, para una agregué una prueba, y tres no cambian nada tal como están escritas.

| Rotura | Detectada |
| --- | --- |
| Soltar el snapshot antes de copiar | Sí |
| Leer por la caché | Sí |
| Respaldar sin cifrar una base cifrada sin que se pida; aceptar contraseña y "sin cifrar" a la vez | Sí |
| Dejar el archivo de un backup fallido | Sí |
| Leer directo sin comprobar la página | Sí |
| Perder la marca de compresión | No al principio. Se agregó la prueba |
| Tomar la raíz actual en vez de la del snapshot; informar la transacción actual | No: en ese punto del código son la misma. Lo que las pruebas sí detectan es soltar el snapshot, que es lo que las haría distintas |
| No comprobar que el número de página esté en el archivo al leer directo | No: quien recorre los árboles ya lo comprueba antes |

## Límites conocidos

- **Retiene espacio mientras corre.** No medí un caso donde eso haga crecer el archivo; existe (ver arriba).
- **No hay backup incremental** ni a un flujo: es una copia completa a un archivo nuevo.
- **El historial del change feed no se copia,** como en la compactación: los números de transacción del respaldo empiezan de nuevo.
- **Una página dañada detiene el backup** (no hay respaldo de una base dañada). Para eso está `tkdb repair`.
- **Sin comando en `tkdb`:** la herramienta trabaja sobre archivos cerrados, y sobre uno cerrado `compact` da la misma copia.
- **La cola de latencia sube** mientras el backup escribe al mismo disco.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 727 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT: backup de una base cifrada abierta, a un archivo con otra contraseña, e inspección del resultado | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 727 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Las mediciones sólo se hicieron en macOS.
