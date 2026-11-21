### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TK0001 | TekiumDB | Error | A table type must be partial
TK0002 | TekiumDB | Error | A table needs a key
TK0003 | TekiumDB | Error | A table has one key
TK0004 | TekiumDB | Error | A property has a type that cannot be stored
TK0005 | TekiumDB | Error | A property has a type that cannot be a key
TK0006 | TekiumDB | Error | A table type needs a parameterless constructor
TK0007 | TekiumDB | Error | This kind of type cannot be a table
TK0008 | TekiumDB | Error | Two properties are stored under the same name
TK0009 | TekiumDB | Error | A table needs columns
TK0010 | TekiumDB | Error | An index names a property that cannot be indexed
TK0011 | TekiumDB | Error | Two indexes have the same name
TK0012 | TekiumDB | Error | The condition of a partial index cannot be understood
TK0013 | TekiumDB | Error | An index declared on the class must name its properties
TK0014 | TekiumDB | Error | A table type cannot have a member named Fields
TK0015 | TekiumDB | Error | A CHECK constraint must be a bool property with a body
TK0016 | TekiumDB | Error | A FOREIGN KEY must be a column of the type of the key it refers to
TK0017 | TekiumDB | Error | Two constraints have the same name
TK0018 | TekiumDB | Error | A zone map must be on a date column
TK0019 | TekiumDB | Error | A SQL routine must be a static method generated code can call
TK0020 | TekiumDB | Error | A SQL function uses a type SQL has no value for
TK0021 | TekiumDB | Error | A vectorized function takes spans in and a span out
TK0022 | TekiumDB | Error | A procedure takes a TkContext first and returns a task
TK0023 | TekiumDB | Error | Two SQL routines have the same name
TK0024 | TekiumDB | Error | A JSON document is not indexed whole
TK0025 | TekiumDB | Error | [TkJsonText] goes on a TkJson property
TK0026 | TekiumDB | Error | [TkJsonIndex] indexes a TEXT or DOUBLE value at a path of a TkJson property
