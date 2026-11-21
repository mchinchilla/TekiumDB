# Fase 13 — funciones y procedimientos en C#, registrados por el generador de código

**Criterio de salida cumplido en macOS y en Linux.** Una función escalar y una vectorizada, escritas en C#, corren desde SQL bajo `PublishAot`, sin reflexión ni `Expression.Compile`; también un procedimiento. Windows no se ha verificado.

Medido el 2026-11-17. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), mejor de cinco corridas; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Qué hay

| Pieza | Dónde | Qué hace |
| --- | --- | --- |
| `[TkFunction]`, `[TkProcedure]` | `TekiumDB.Sql` | Marcan un método estático como función o procedimiento de SQL |
| `RoutineGenerator` | `TekiumDB.Generators` | Por cada método marcado escribe, al compilar, un método que convierte los argumentos y lo llama por su nombre, y una tabla `TkRoutines.All` con todos los del ensamblado |
| `database.UseRoutines(TkRoutines.All)` | `TekiumDB.Sql` | Registra las rutinas para esa base. Viven en la aplicación, no en el archivo |
| `CALL nombre(…)` | SQL | Corre un procedimiento dentro de la transacción de la sentencia |
| `TkContext` | `TekiumDB.Sql` | Lo que recibe un procedimiento: la base, la transacción, y `ExecuteAsync`/`QueryAsync` para correr SQL dentro de ella |

Tres clases de rutina:

- **Función escalar.** Toma y devuelve `bool`, `int`, `long`, `double`, `decimal`, `string`, `Guid`, `DateTime`, `DateTimeOffset` o `byte[]`, y puede tomar `ReadOnlySpan<char>`. Con NULL en un parámetro que no lo admite no se la llama y el resultado es NULL; un parámetro anulable lo recibe.
- **Función vectorizada** (`Vectorized = true`). Toma un `ReadOnlySpan<T>` por argumento y un `Span<T>` para los resultados, con `T` uno de `double`, `float`, `long` o `int`. Se la llama una vez por bloque de hasta 1 024 filas cuando está en la lista de columnas, en una clave de grupo o dentro de un agregado; en otro lugar (el `WHERE`, un `ON`), una vez por fila. Una fila con un argumento NULL da NULL.
- **Procedimiento.** Su primer parámetro es un `TkContext`; puede terminar en un `CancellationToken`; devuelve `ValueTask` o `Task`, con o sin valor. Lo que escribe, con la API tipada o con SQL, se confirma o se deshace con la transacción que lo llamó.

`Deterministic = true` hace dos cosas: una llamada con argumentos constantes se calcula una vez por sentencia, y con argumentos de la fila se reutiliza el resultado ya calculado para los mismos argumentos.

Una función que la aplicación no registró es un error al preparar la consulta, antes de leer una fila; lo mismo una vista que la nombra, cuando la abre otra aplicación.

Lo que no puede ser una rutina es un error de compilación: `TK0019` (método no estático, genérico, privado o sin nombre), `TK0020` (un tipo para el que SQL no tiene valor), `TK0021` (forma de una vectorizada), `TK0022` (forma de un procedimiento), `TK0023` (dos rutinas con el mismo nombre).

## Mediciones

Un millón de filas en memoria (`id`, dos `DOUBLE`, un `INT`). Cada consulta suma el resultado sobre toda la tabla. Se reproduce con `-- routines 1000000`.

| Consulta | Tiempo | Sobre sólo recorrer, por fila |
| --- | ---: | ---: |
| Sólo recorrer: `SUM(a)` | 144 ms | — |
| Dos columnas: `SUM(a - b)` | 154 ms | 10 ns |
| Escrita en SQL: `SUM(ABS(a - b))` | 172 ms | 28 ns |
| Función escalar | 173 ms | 29 ns |
| Función escalar determinista, argumentos todos distintos | 171 ms | 27 ns |
| Función vectorizada (con `TensorPrimitives`) | 183 ms | 39 ns |
| Vectorizada llamada fila por fila (en el `WHERE`) | 231 ms | 87 ns |
| Función cara (200 raíces cuadradas), 100 argumentos distintos | 335 ms | 191 ns |
| La misma, marcada determinista | 196 ms | 51 ns |

Entre corridas estos números se mueven unos 10 ns por fila.

### Lo que dicen

- **Llamar a una función de C# cuesta lo mismo que la expresión escrita en SQL**: unos 30 ns por fila. De esos, unos 10 son leer la segunda columna.
- **La vectorizada no es más rápida que la escalar; es algo más lenta** (39 contra 29 ns). El ejecutor de SQL trabaja fila por fila con valores sueltos: para llamar por bloques hay que juntar los argumentos de mil filas en arreglos y repartir después los resultados, y eso cuesta más que las mil llamadas que ahorra. La cuenta en sí (`TensorPrimitives` sobre mil `double`) es una fracción mínima del total. Ganaría con un ejecutor que ya tuviera las columnas en bloques, que no existe ni está en el plan (el almacenamiento columnar está fuera de alcance). Queda porque el plan y el criterio de salida la piden y porque funciona; no se presenta como una mejora de rendimiento.
- **Fuera de la lista de columnas y de los agregados, la vectorizada es claramente peor** (87 ns): cada fila arma un bloque de uno.
- **`Deterministic` paga cuando la función es cara y los argumentos se repiten**: 191 → 51 ns.
- **Recordar resultados cuesta cuando los argumentos no se repiten.** La primera versión recordaba siempre y la escalar determinista con argumentos todos distintos pasaba de 21 a 84 ns por fila. Ahora deja de recordar al llegar a 1 024 argumentos distintos habiendo acertado menos veces que eso; medido después: 27 ns, igual que sin la marca.

## Diferencias con el plan

| El plan dice | Lo que hay | Por qué |
| --- | --- | --- |
| "Cero boxing, cero asignaciones. La llamada cuesta lo mismo que un método estático normal" | Los argumentos llegan como los tiene el ejecutor de SQL, que son valores en caja; cada llamada asigna el arreglo de argumentos y la caja del resultado | El ejecutor de la Fase 12 trabaja así. La llamada no usa reflexión, pero no es gratis: unos 20–30 ns por fila |
| Un `switch` sobre un id entero | Una tabla de delegados a métodos estáticos generados | Hace lo mismo bajo AOT y no obliga a repartir ids entre ensamblados |
| `int Slugify(ReadOnlySpan<char> src, Span<char> dst)` | Una función de texto devuelve `string` | No hay forma de saber de antemano cuánto mide el destino |
| "El catálogo guarda la firma esperada" | El archivo no guarda nada de las rutinas | Lo único guardado que puede nombrar una función es el texto de una vista, que se vuelve a enlazar cada vez que se usa: el error sale igual al preparar la consulta. Guardar firmas serviría si un índice o un `CHECK` pudieran llamar a una función, y no pueden |
| Funciones agregadas (`Init`/`Accumulate`/`Merge`/`Finalize`) y tabulares | No están | El criterio de salida pide una escalar y una vectorizada |

## Pruebas

| Qué | Prueba |
| --- | --- |
| Funciones escalares de todos los tipos, en la lista de columnas, el `WHERE`, dentro de un agregado y en una vista; NULL en parámetros que lo admiten y que no | `RoutineTests`, contra el mismo método llamado desde C# |
| Cuántas veces se llama a cada función: una por sentencia con argumentos constantes, una por argumento distinto, una por fila sin la marca, y dónde deja de recordar | `RoutineTests`, con funciones que cuentan sus llamadas |
| Vectorizadas: 2 500 filas en tres llamadas; los cuatro tipos de elemento; una dentro de otra; dentro de un agregado y como clave de grupo; con `LIMIT` un solo bloque | `RoutineTests` |
| Procedimientos: con la API tipada y con SQL por dentro, confirmados y deshechos con su transacción; argumentos equivocados; el token de cancelación | `RoutineTests` |
| Función sin registrar: error antes de leer el archivo; desde otra aplicación que abre la misma base | `RoutineTests` |
| El generador: lo generado compila sin advertencias y no menciona reflexión; veinte formas de método que no pueden ser rutina, cada una con su diagnóstico | `RoutineGeneratorTests` |
| Criterio de salida | El smoke de AOT: una escalar, una vectorizada (3 000 filas en tres llamadas) y un procedimiento que borra con SQL |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

Treinta y tres roturas hechas a mano, una por una. Treinta se detectaron a la primera; para dos agregué pruebas; una hubo que repetirla porque la primera forma no compilaba.

| Rotura | Detectada |
| --- | --- |
| Aceptar un procedimiento como función; no comprobar el número de argumentos de una función o de un procedimiento; no envolver el fallo de una función | Sí |
| Ignorar `Deterministic`; no calcular una vez la llamada constante; no guardar ese resultado para la sentencia; recordar resultados de una función sin la marca | Sí |
| No dejar nunca de recordar | Sí |
| Dejar de recordar al llegar al límite aunque los argumentos se repitan | No al principio. Se agregó el caso de 1 250 valores que llegan cuatro veces cada uno |
| Vectorizada nunca llamada por bloques; bloques de una fila; no guardar los resultados del bloque; no preparar el bloque (en la lista de columnas y en los grupos); perder el último bloque incompleto (en los dos) | Sí |
| `LIMIT` que no deja de leer | Sí |
| No marcar el argumento NULL; devolver el resultado de una fila con NULL | Sí |
| Aceptar una segunda rutina con un nombre usado; rechazar registrar la misma dos veces; nombres sensibles a mayúsculas | Sí |
| Generador: no generar la comprobación de NULL; no pasar NULL a un parámetro anulable; no pasar `Deterministic` | Sí |
| Generador: no avisar de nombres repetidos, de un método inalcanzable, de un parámetro `ref`, de un resultado de sólo lectura en una vectorizada, de un nombre vacío | Sí |
| Generador: aceptar un procedimiento que no devuelve una tarea | Sí (repetida: la primera forma no compilaba) |
| Generador: no pasar el token de cancelación al procedimiento | No al principio. Se agregó un procedimiento que dice si su token puede cancelarse |

## Límites conocidos

- **La vectorizada no acelera nada hoy**, como se midió arriba.
- **Las rutinas se registran por base y por proceso**, cada vez que se abre. Dos aplicaciones sobre el mismo archivo pueden tener funciones distintas, y una vista que usa una función sólo sirve donde esté registrada.
- **Nada comprueba que `Deterministic` sea verdad.** Una función marcada así que no lo sea da resultados que dependen de cuántas veces se la llame.
- **Una función no puede usarse en un índice ni en un `CHECK`.** Esos siguen siendo expresiones de C# de la clase de la tabla.
- **Una función escalar no recibe la transacción** ni puede leer la base; un procedimiento sí.
- **Sin sobrecargas**: un nombre, una rutina, un número de argumentos. Los nombres propios de SQL (`LOWER`, `ABS`, `COUNT`…) ganan sobre una rutina que se llame igual.
- **Tipos de parámetro**: no hay `float` ni enteros de 8 o 16 bits en las escalares, ni `decimal` en las vectorizadas.
- **Una vectorizada convierte sus argumentos** al tipo de elemento que declara: un `DECIMAL` pasado a una de `double` pierde precisión sin avisar.
- **Las rutinas de un ensamblado referenciado** se registran con su propio `TkRoutines.All`; el generador sólo ve el ensamblado que compila.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 831 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con una escalar, una vectorizada y un procedimiento | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 831 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |
