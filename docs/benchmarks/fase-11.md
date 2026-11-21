# Fase 11 — capa LINQ

**Criterio de salida cumplido en macOS y en Linux.** Los predicados soportados se traducen a filtros del motor y dan las mismas filas que el mismo predicado sobre objetos en memoria. Los no soportados fallan: al construir la consulta, con un mensaje que dice qué parte y por qué, o directamente en compilación. Nada se evalúa del lado del cliente sin que quien escribe la consulta lo pida. Windows no se ha verificado.

Hecho el 2026-11-16. No se midieron tiempos: una consulta traducida es la misma consulta que se escribiría a mano con `Fields`.

## Qué hay

Un paquete nuevo, `TekiumDB.Linq`:

```csharp
using TekiumDB.Linq;

var recent = await orders
    .Where(o => o.Customer == customerId && o.IssuedAt >= since && !o.Paid)
    .Take(50)
    .ToListAsync();

await foreach (var order in orders.Where(o => o.Total > 1000m)) { … }

var titles = from o in orders.Query() where o.Customer == customerId select o.Notes;   // sintaxis de consulta
```

- **Se traduce, no se compila.** El árbol de expresión del predicado se lee y se convierte en el mismo `TkFilter` que se escribiría a mano; el planificador elige el índice igual. No hay `Expression.Compile()` (está prohibido en el build) y funciona bajo Native AOT.
- **Sin reflexión para llegar a los campos.** El generador emite, por cada tipo, cómo encontrar un campo por el nombre de su propiedad; el núcleo ganó eso y una forma de usar un campo sin conocer su tipo de valor. La capa LINQ sí usa reflexión para una cosa: leer los valores capturados por el predicado (variables, propiedades, llamadas), que el compilador deja como metadatos.

### Lo que se puede escribir

| En el predicado | Ejemplo |
| --- | --- |
| Comparar una propiedad guardada (o una expresión indexada) con un valor | `o.Total >= 100m`, `o.Customer == id`, `100 < o.Total` |
| Unir condiciones con `&&` | `o.Customer == id && o.IssuedAt >= since` |
| Preguntar si hay valor | `o.PaidAt == null`, `o.PaidAt != null`, `o.PaidAt.HasValue`, `!o.PaidAt.HasValue` |
| Una propiedad `bool` sola o negada | `o.Paid`, `!o.Paid` |
| Enumeraciones | `o.State == OrderState.Open` |
| El valor: cualquier cosa que no dependa de la fila | constantes, variables, propiedades, `DateTime.UtcNow.AddDays(-7)`. Se calcula una vez, al construir la consulta |

Sobre la consulta: `Where` (varias veces), `Skip`, `Take`, `Select`, `ToListAsync`, `FirstOrDefaultAsync`, `AnyAsync`, `CountAsync`, `ExplainAsync`, `await foreach`, y `Query(transaction)` para leer por una transacción (de escritura, o del pasado).

### Lo que no, y cómo falla

| Qué | Cómo falla |
| --- | --- |
| `||`, `!=`, negar una condición, un método sobre una propiedad (`StartsWith`, `Contains`), aritmética con una propiedad, comparar dos propiedades, una propiedad que no se guarda | `NotSupportedException` al construir la consulta, antes de leer nada: *"The predicate … cannot be translated: in 'o.Owner != "a"', it uses != and the engine has no "not equal" condition…"* |
| Ordenar, agrupar, unir, `Distinct`, agregados | **No compilan.** Una consulta no es un `IAsyncEnumerable<T>` ni un `IQueryable<T>`, así que esos operadores no existen sobre ella |
| Enumerarla de forma síncrona (`foreach`) | No compila |

Para hacer en memoria lo que el motor no hace, hay que decirlo: `query.AsAsyncEnumerable()` devuelve una secuencia corriente, y a partir de ahí lo que se le aplique corre sobre las filas ya leídas.

`Select` recibe una función corriente y corre en memoria sobre las filas que el motor devolvió: cambia la forma de cada fila, nunca cuáles son.

## Lo que no es como dice el plan

El plan dibuja la capa como "`IQueryable` tipado". No lo es, a propósito, por dos razones:

- **`IQueryable<T>` se enumera de forma síncrona,** y aquí toda la E/S es asíncrona, sin excepciones.
- **Con `IQueryable<T>` o `IAsyncEnumerable<T>`, todo operador de LINQ compila.** Los que el proveedor no traduce lanzan al ejecutar, o peor, se resuelven en memoria por los operadores estándar sin que nadie lo note. Con un tipo propio que sólo tiene lo que el motor hace, el compilador rechaza el resto: es la forma más fuerte de cumplir "nunca degradan a evaluación en cliente en silencio".

La sintaxis de consulta de C# (`from … where … select`) funciona igual, porque el compilador sólo pide que existan `Where` y `Select`.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Criterio: lo soportado se traduce | `LinqTests`: veintiocho predicados, cada uno escrito dos veces (como árbol de expresión y como función); las filas que da la consulta son las que da la función sobre los objetos. Cubre los seis operadores, los dos lados, `&&` anidado, nulos, `HasValue`, `.Value`, banderas, enumeraciones, `decimal`, fechas, expresiones indexadas, y valores constantes, capturados, de instancia, estáticos y anidados |
| Mismo filtro, mismo índice | El plan de la consulta traducida es el del filtro escrito a mano: búsqueda por clave, rango de clave, índice, índice parcial, recorrido, y "nada" para rangos que se contradicen |
| Criterio: lo no soportado lanza claro | Quince predicados, cada uno rechazado al construir la consulta con un mensaje que nombra la parte |
| Criterio: lo no soportado no compila | Se compilan fragmentos con Roslyn dentro de la prueba: ordenar, agrupar, unir, `Distinct`, sumar, `orderby`, `foreach` síncrono y convertir a `IQueryable` o `IAsyncEnumerable` dan error de compilación (y por el motivo debido); `Where`, `Take` y `AsAsyncEnumerable().OrderBy(…)` compilan |
| Valores | Una llamada del lado del valor se evalúa una vez, al construir la consulta, no por fila ni por ejecución |
| Componer | `Where` encadenado, `Skip`, `Take`, `Select`, contar, primero, alguno, sintaxis de consulta; dentro de una transacción de escritura ve lo suyo; en el pasado, lo de entonces |
| Native AOT | El smoke hace una consulta con una variable capturada y una llamada del lado del valor, y comprueba que un `!=` se rechaza |

### Las pruebas detectan el código roto

Veintinueve roturas hechas a mano. Veinticuatro se detectaron a la primera; para dos afiné o agregué pruebas; dos no cambian nada porque hay una segunda barrera (y una de ellas me hizo quitar una comprobación repetida); y una estaba mal planteada y no rompía nada.

| Rotura | Detectada |
| --- | --- |
| No dar vuelta la comparación cuando la propiedad está a la derecha | Sí |
| `&&` quedándose sólo con un lado; `||` tomado como `&&` | Sí |
| Cada operador cambiado por su vecino (`<` por `<=`, `>=` por `>`, y al revés) | Sí, los cuatro |
| Bandera negada leída como verdadera; bandera sola leída como falsa | Sí |
| `HasValue`, `!HasValue` y `== null` leídos al revés | Sí |
| No ver a través de `.Value` ni de las conversiones que agrega el compilador | Sí |
| Tomar por columna la propiedad de cualquier objeto | Sí |
| `!=` no rechazado | No al principio: se rechazaba igual más adelante, con otro mensaje, y la prueba sólo buscaba "!=" (que está en el texto del predicado). Ahora busca el motivo |
| `!=` tomado como `==` | No: no se alcanza, el `!=` se rechaza antes |
| Comparar dos propiedades sin la primera comprobación | No: había dos comprobaciones para lo mismo. Quité la repetida |
| `Take` y `Skip` ignorados; `Where` después de `Take` aceptado | Sí |
| El segundo `Take` gana aunque sea mayor | No al principio. Se agregó la prueba |
| La transacción ignorada, con y sin filtro; un segundo `Where` que reemplaza al primero | Sí |
| Contar de más; plan equivocado sin filtro; la proyección perdiendo el `Skip` | Sí |

## Límites conocidos

- **Sin `||`, sin `!=`, sin `Contains` ni prefijos de texto, sin ordenar ni agrupar.** Son los límites del motor (Fase 6), no de la traducción: llegarán cuando el motor los tenga.
- **Sin comparación ordenada de texto.** C# no tiene `<` para cadenas y `CompareTo` usa la cultura, que no es el orden del motor (por punto de código); se rechaza. Para rangos de texto está la API de `Fields`.
- **`CountAsync` lee las filas:** el motor no guarda cuentas.
- **Los valores se calculan por reflexión** (leer un campo capturado, invocar un método). Es una vez por consulta, pero no es gratis: en un bucle muy caliente conviene construir el filtro una vez con `TkLinq.ToFilter` y reutilizarlo.
- **Condicionales y aritmética de enteros del lado del valor** (`x ? 1 : 2`, `a + b` con enteros) no se evalúan: hay que calcular el valor en una variable antes. El mensaje lo dice.
- **Un valor fuera del rango de la columna** (comparar una columna `int` con un `long` enorme) se rechaza en vez de responder "ninguna".

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 773 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con una consulta LINQ y un predicado rechazado; sin advertencias de AOT ni de recorte | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 773 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |
