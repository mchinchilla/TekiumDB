# Fase 14 — PL/Tk: procedimientos almacenados, compilados a un programa para una máquina virtual

**Criterio de salida cumplido en macOS y en Linux.** Un procedimiento con un bucle y SQL embebido corre, y el límite de instrucciones corta un bucle infinito; las dos cosas también bajo Native AOT. Windows no se ha verificado.

Medido el 2026-11-17. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), mejor de cinco corridas, tres veces; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Qué hay

```sql
CREATE PROCEDURE archive_orders(p_before INT) RETURNS INT AS $$
DECLARE n INT := 0;
BEGIN
    FOR r IN SELECT id, total FROM orders WHERE placed < p_before LOOP
        INSERT INTO orders_archive (id, total) VALUES (r.id, r.total);
        DELETE FROM orders WHERE id = r.id;
        n := n + 1;
    END LOOP;
    RETURN n;
END $$
```

| Pieza | Qué hace |
| --- | --- |
| `CREATE [OR REPLACE] PROCEDURE`, `DROP PROCEDURE [IF EXISTS]`, `CALL` | Crean, quitan y corren un procedimiento guardado en el archivo. `CALL` busca primero entre los de C# que registró la aplicación y después entre los guardados |
| El lenguaje | `DECLARE` con tipo y valor inicial; asignación con `:=`; `IF`/`ELSIF`/`ELSE`; `WHILE … LOOP`; `FOR fila IN SELECT … LOOP`, con las columnas como `fila.columna`; `RETURN`; `RAISE`; `CALL … [INTO variable]`; y `SELECT … INTO variables`, `INSERT`, `UPDATE`, `DELETE`. Las expresiones son las de SQL, con las funciones de C# registradas |
| El compilador (`PlCompiler`) | El control de flujo se vuelve saltos entre instrucciones; cada variable, un registro; cada expresión y cada sentencia SQL, una constante |
| El programa (`PlProgram`) | Lo que va al catálogo (prefijo 9): firma, registros con su tipo, constantes y código, un arreglo plano de bytes. Diez instrucciones. Es formato de archivo |
| La máquina (`PlMachine`) | Un bucle con un `switch` sobre la instrucción. Sólo sabe calcular una expresión, saltar, correr una sentencia, recorrer las filas de una consulta, llamar a un procedimiento, devolver y fallar |
| `DbOptions.AllowStoredProcedures` | Apagado por defecto. Sin él no se crea ni corre ningún procedimiento guardado; sí se puede quitar uno |
| `DbOptions.ProcedureInstructionLimit` | Diez millones por defecto. Lo comparte toda la llamada, con los procedimientos que ella llame |
| `DbOptions.ProcedureCallDepth` | 32 por defecto, 256 a lo sumo |

Lo que está mal y se puede saber sin correr es un error al crear: una variable o una función que no existe, `RETURN` con valor sin `RETURNS`, un `SELECT` que no hace nada con sus filas, DDL, un parámetro `@x`, la fila de un bucle leída fuera de él. Y una variable que se llama igual que una columna de una tabla que su sentencia usa: no se puede saber cuál de las dos se quiso, así que se rechaza en vez de elegir.

Un procedimiento que falla (un `RAISE`, un error de SQL, un límite) deja la transacción en que corría sin poder confirmarse: sólo puede deshacerse. Llamado solo, eso es deshacer todo lo que hizo.

Viaja con el archivo: el backup, compactar y reparar lo copian; inspeccionar y verificar lo conocen.

## Seguridad

Un programa guardado es contenido ejecutable dentro de un archivo de datos. Lo que lo acota:

1. **Apagado por defecto.** Abrir un archivo ajeno no corre nada de él.
2. **La máquina no tiene con qué salir.** No hay instrucción de E/S, de red ni de archivos. Las sentencias SQL que acepta son `SELECT`, `INSERT`, `UPDATE` y `DELETE`, dentro de la transacción de la llamada; el texto guardado se vuelve a analizar al cargar, y si es otra cosa (un `DROP TABLE` escrito a mano en el archivo) el programa no arranca. De C# sólo alcanza lo que la aplicación registró.
3. **El programa se verifica antes de correr.** Cada instrucción es conocida y está entera; nombra registros y constantes que existen y de la clase que pide; cada salto cae al inicio de una instrucción. Después de eso la máquina no puede leer fuera de sus registros ni de su código.
4. **Presupuesto de instrucciones y de profundidad.** Un bucle infinito o una recursión sin fin terminan en un error, no en un proceso colgado ni en la pila agotada. La profundidad cuenta también cuando un procedimiento de C# vuelve a llamar a uno guardado.

Lo que **no** acota: el tiempo de una sola sentencia SQL (un `SELECT` sobre una tabla enorme cuenta como una instrucción), ni la memoria (el resultado de la consulta de un `FOR` se tiene entero). Y un procedimiento habilitado puede hacer con los datos todo lo que la aplicación puede: borrar todas las filas es SQL válido.

## Mediciones

En memoria. Se reproduce con `-- procedures 10000`.

| Qué | Tiempo | Por vuelta |
| --- | ---: | ---: |
| Bucle vacío: un millón de vueltas (una condición y dos asignaciones, cinco instrucciones) | 96 – 103 ms | 96 – 103 ns |
| Archivar 10 000 filas (un `INSERT` y un `DELETE` por fila): procedimiento guardado | 36,6 – 39,7 ms | 3,7 – 4,0 µs |
| Lo mismo: procedimiento de C# con las mismas sentencias | 35,8 – 36,3 ms | 3,6 µs |
| Lo mismo: sentencia por sentencia desde la aplicación, por ADO.NET | 36,6 – 37,2 ms | 3,7 µs |

- **El intérprete cuesta unos 20 ns por instrucción**, casi todo evaluar la expresión con valores en caja.
- **Sobre datos, el procedimiento guardado tarda lo mismo que el de C#** (entre 2 y 9 % más en tres corridas). Es lo que el plan esperaba: el tiempo se va en el B+Tree, no en despachar instrucciones. Una vuelta que toca dos filas cuesta 3,7 µs; el control de flujo de esa vuelta, unos 0,1 µs.
- **Para cálculo puro, C# es mucho más rápido**: un bucle vacío en C# no cuesta 96 ns por vuelta. Un procedimiento guardado es para orquestar SQL, no para calcular.

**Una corrección sobre mi primera medida.** La primera corrida daba al procedimiento guardado 2,4 veces más lento que el de C# (9,4 contra 3,9 µs por fila). No era la máquina virtual: medidas una detrás de otra, la variante que corre primero paga la base en memoria todavía creciendo, y cambiando el orden la lenta pasaba a ser la de C#. El reporte ahora alterna las tres variantes y descarta una vuelta de calentamiento.

## Diferencias con el plan

| El plan dice | Lo que hay | Por qué |
| --- | --- | --- |
| "Se guarda como bytecode en el catálogo. Nunca se vuelve a parsear" | El control de flujo sí es bytecode. Las expresiones y las sentencias SQL se guardan como texto dentro del programa y se analizan una vez por proceso, la primera vez que el procedimiento corre | Compilar expresiones a instrucciones propias sería un segundo evaluador de SQL que mantener idéntico al primero. El costo de volver a analizar es una vez por apertura de la base, no por llamada |
| "Cada sentencia SQL embebida se planifica al crear y se guarda el plan" | El plan se elige en cada ejecución, como en cualquier sentencia de la Fase 12 | El planificador de la Fase 12 elige con el estado de los árboles en el momento, y no tiene una forma guardable. Un plan guardado quedaría viejo al cambiar un índice. Medido, elegirlo cada vez no hace al guardado más lento que C# |
| Registros en un `Span<TkValue>` de 16 bytes, cero asignaciones por llamada | Registros en un arreglo de valores en caja, uno nuevo por llamada | Las expresiones las evalúa el ejecutor de SQL de la Fase 12, que trabaja con valores en caja |
| `INSERT INTO … SELECT` en el ejemplo | No existe en el SQL de la Fase 12; el ejemplo usa `INSERT … VALUES` con las columnas de la fila | — |
| Funciones escalares y tabulares en PL/Tk | Sólo procedimientos (`CALL`). No se pueden usar dentro de una expresión | Una expresión se evalúa de forma síncrona y un procedimiento con SQL es asíncrono. El criterio de salida pide un procedimiento |
| "Transacción propia o heredada" | Siempre la de la llamada | No hay transacciones anidadas ni puntos de guardado |

## Pruebas

| Qué | Prueba |
| --- | --- |
| Criterio de salida: bucle con SQL embebido, contra las filas que debían moverse; deshecho con su transacción; tras cerrar y reabrir; en un respaldo | `ProcedureTests` |
| Criterio de salida: el límite corta un bucle infinito, y lo que escribió no queda | `ProcedureTests` |
| El presupuesto es de toda la llamada (dos llamados que juntos lo exceden) y no pasa a la siguiente; la profundidad cuenta anidados, no llamados; recursión directa y pasando por C#; la mayor profundidad aceptada no agota la pila | `ProcedureTests` |
| `IF`/`ELSIF`/`ELSE` con NULL, `WHILE`, recursión, `SELECT INTO` con y sin filas, bucles anidados, dos bucles con el mismo nombre de fila, `RAISE`, funciones y procedimientos de C# desde dentro, conversión al tipo de la variable y del resultado | `ProcedureTests`, contra la misma cuenta en C# |
| Veintidós cuerpos que no deben poder crearse, cada uno con su mensaje, y que nada queda guardado después | `ProcedureTests` |
| Crear, reemplazar (también dentro de una transacción que se deshace), quitar; nombres sin distinguir mayúsculas | `ProcedureTests` |
| Apagado por defecto: ni correr lo que el archivo trae, ni crear | `ProcedureTests` |
| Un byte cambiado, de tres formas, en cada posición del programa guardado; el programa cortado en cada punto; cabecera, formato y tipos imposibles | `ProcedureTests`: se rechaza con un error de SQL o corre dentro de sus límites; nunca otra excepción ni un cuelgue |
| Inspección, verificación y copia conocen la entrada | `ProcedureTests` (verificar y respaldar) |
| Native AOT | El smoke: un procedimiento con bucle, SQL y una función de C#, y un bucle infinito cortado |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

Cincuenta roturas hechas a mano, una por una. Cuarenta y cuatro se detectaron a la primera; para cuatro agregué pruebas; una mostró que el código roto sobraba; y una, al detectarse, mostró un defecto.

| Rotura | Detectada |
| --- | --- |
| Verificación del programa: no comprobar el inicio, el formato, los registros, la clase o el índice de una constante, los saltos, los registros de un bucle | Sí |
| No validar el tipo de un registro | No al principio. Se agregó el caso de un tipo imposible, y el de una cabecera que no cuadra |
| No aplicar el límite de instrucciones | Sí: la prueba se cuelga y se mata |
| No aplicar el límite de profundidad | Sí, y de mala manera: el proceso de pruebas murió con la pila agotada. De ahí salió un defecto: `ProcedureCallDepth` aceptaba cualquier número, y uno grande convertía una recursión en un proceso muerto. Ahora tiene un máximo (256), probado |
| No bajar la profundidad al volver; tomar NULL como verdadero; no comprobar el número de argumentos; no convertir argumentos, variables ni el resultado; `SELECT INTO` sin filas que conserva un valor; no asignar el resultado de un `CALL`; un bucle que lee siempre la misma fila | Sí |
| Compilador: aceptar `RETURN` con valor sin `RETURNS`, una variable declarada dos veces, una variable desconocida, un nombre de fila reusado dentro de su bucle o con otras columnas, la fila leída después de su bucle, una fila llamada como una variable, `SELECT INTO` con otro número de variables, una columna que la fila no tiene; ignorar el valor inicial; perder el `ELSE`; un `WHILE` de una sola vuelta | Sí |
| Aceptar un agregado fuera de una consulta | No, y no cambia nada: el enlace de expresiones al crear ya lo rechaza. Se quitó la comprobación duplicada |
| Motor: correr o crear con la opción apagada; conservar el programa cargado cuando el guardado cambió; reemplazar sin `OR REPLACE`; aceptar el nombre de una rutina de C#; aceptar una variable con nombre de columna; no enlazar las expresiones al crear; `DROP` que no quita; nombres sólo exactos; dejar el presupuesto gastado para la llamada siguiente | Sí |
| Dar a cada procedimiento llamado un presupuesto propio | No al principio. Se agregó el caso de dos llamados que juntos lo exceden |
| Dejar confirmable la transacción de una llamada fallida | Sí |
| Parser: `SELECT` sin `INTO`, DDL dentro de un procedimiento, un parámetro `@x` | Sí |
| Aceptar `INTO` en la consulta de un bucle, o en una consulta fuera de un procedimiento | No al principio. Se agregaron los dos casos |
| El recorrido de mantenimiento no conoce la entrada; la copia la deja fuera | Sí |

## Límites conocidos

- **Las expresiones y sentencias se analizan otra vez en cada proceso** y el plan se elige en cada ejecución (ver arriba).
- **Sin puntos de guardado:** un procedimiento que falla no puede deshacer sólo lo suyo dentro de una transacción más grande; deja la transacción entera sin poder confirmarse.
- **El resultado de la consulta de un `FOR` se lee entero antes de la primera vuelta.** El bucle no ve lo que su cuerpo cambia, y una consulta enorme ocupa memoria.
- **El límite cuenta instrucciones, no tiempo ni memoria.**
- **Una variable no puede llamarse como una columna** de una tabla que su sentencia usa, aunque el sentido sea obvio. La comprobación se hace al crear, con las tablas que existen entonces: una columna agregada después con el nombre de una variable hace que la variable gane, sin aviso.
- **Un procedimiento creado no se vuelve a comprobar** cuando cambian las tablas que usa: una columna quitada es un error al llamarlo.
- **Sin `EXIT`, `CONTINUE`, `CASE`, cursores explícitos, excepciones propias, SQL dinámico ni DDL.** A propósito.
- **Sin funciones almacenadas** utilizables en una expresión, ni procedimientos que devuelvan filas.
- **`tkdb` no lista los procedimientos de un archivo**, ni hay forma de leer el texto con que se creó uno: se guarda el programa, no el código fuente completo.
- **El texto de error dice el fragmento, no la línea** del procedimiento.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 843 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con un procedimiento guardado y un bucle infinito cortado | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 843 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |
