// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Sql;
using Tekium.Embedded.Sql.Storage;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;
using static Tekium.Embedded.Tests.Sql.SqlTests;

namespace Tekium.Embedded.Tests.Sql;

/// <summary>
/// Lo que SQL cambia de una tabla después de crearla: claves foráneas (las mismas que declara un tipo de
/// fila, en el archivo y al comprobarlas) y columnas nuevas.
/// </summary>
public abstract class SqlSchemaTests : DatabaseTests
{
    [Fact]
    public async Task Foreign_keys_made_by_sql_and_by_a_row_type_are_the_same_in_the_file_and_each_side_checks_the_other()
    {
        // Una base donde las tablas las crea el tipo, y otra donde las crea SQL con las mismas restricciones.
        await using var typed = await OpenAsync(NewDevice());
        await using (var writer = await typed.BeginWriteAsync(Ct))
        {
            await typed.Table<Team>().InsertAsync(writer, new Team { Id = 1, Name = "one" }, Ct);
            await typed.Table<Player>().InsertAsync(writer, new Player { Id = 1, Team = 1, Name = "ana" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using var made = await OpenAsync(NewDevice());
        await using (var sql = new TkConnection(made))
        {
            await RunAsync(sql, "CREATE TABLE teams (Id INT PRIMARY KEY, Name TEXT NOT NULL)");
            await RunAsync(sql, """
                CREATE TABLE players (
                    Id BIGINT PRIMARY KEY,
                    Team INT REFERENCES teams,
                    Captain BIGINT,
                    Name TEXT NOT NULL,
                    CONSTRAINT captain FOREIGN KEY (Captain) REFERENCES players (Id))
                """);
            await RunAsync(sql, "INSERT INTO teams (Id, Name) VALUES (1, 'one')");
            await RunAsync(sql, "INSERT INTO players (Id, Team, Name) VALUES (1, 1, 'ana')");
        }

        // Lo que quedó en el catálogo es lo mismo, byte por byte.
        foreach (var table in new[] { "teams", "players" })
        {
            Assert.Equal(await CatalogAsync(typed, table), await CatalogAsync(made, table));
        }

        foreach (var database in new[] { typed, made })
        {
            await using var connection = new TkConnection(database);

            // SQL comprueba al escribir...
            var missing = await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO players (Id, Team, Name) VALUES (2, 9, 'bea')"));
            Assert.Equal("fk_Team", missing.Constraint);
            Assert.Equal("players", missing.Table);
            Assert.Contains("no row of 'teams' has the key it refers to", missing.Message, StringComparison.Ordinal);
            Assert.Contains("key 2", missing.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "UPDATE players SET Team = 7 WHERE Id = 1"));
            Assert.Equal("captain", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO players (Id, Captain, Name) VALUES (2, 99, 'bea')"))).Constraint);

            // ...y al borrar: nadie puede quedar refiriéndose a una fila que no está.
            var referred = await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM teams WHERE Id = 1"));
            Assert.Equal("fk_Team", referred.Constraint);
            Assert.Contains("cannot be deleted while rows of 'players' refer to it", referred.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "UPDATE teams SET Id = 5 WHERE Id = 1"));

            // Sin valor no hay referencia; una fila puede referirse a sí misma, y eso no impide borrarla.
            Assert.Equal(1, await RunAsync(connection, "INSERT INTO players (Id, Captain, Name) VALUES (2, 2, 'bea')"));
            Assert.Equal(1, await RunAsync(connection, "INSERT INTO players (Id, Captain, Name) VALUES (3, 2, 'cora')"));
            Assert.Equal("captain", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM players WHERE Id = 2"))).Constraint);
            Assert.Equal(1, await RunAsync(connection, "DELETE FROM players WHERE Id = 3"));
            Assert.Equal(1, await RunAsync(connection, "DELETE FROM players WHERE Id = 2"));

            // Una violación aborta la transacción entera, como en la API tipada.
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                await RunAsync(connection, "INSERT INTO teams (Id, Name) VALUES (2, 'two')");
                await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM teams WHERE Id = 1"));
                await Assert.ThrowsAnyAsync<Exception>(async () => await transaction.CommitAsync(Ct));
            }

            Assert.Equal(["1|one"], await QueryAsync(connection, "SELECT Id, Name FROM teams"));

            // El tipo comprueba lo que SQL dejó, y no cambia nada del catálogo al escribir.
            var before = await CatalogAsync(database, "players");
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                await database.Table<Player>().InsertAsync(writer, new Player { Id = 10, Team = 1, Captain = 1, Name = "dana" }, Ct);
                await writer.CommitAsync(Ct);
            }

            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                var refused = await Assert.ThrowsAsync<TkConstraintException>(async () => await database.Table<Player>().InsertAsync(writer, new Player { Id = 11, Team = 42, Name = "eva" }, Ct));
                Assert.Equal("fk_Team", refused.Constraint);
            }

            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                await Assert.ThrowsAsync<TkConstraintException>(async () => await database.Table<Team>().DeleteAsync(writer, 1, Ct));
            }

            Assert.Equal(before, await CatalogAsync(database, "players"));
            Assert.Equal(["1|1||ana", "10|1|1|dana"], await QueryAsync(connection, "SELECT Id, Team, Captain, Name FROM players ORDER BY Id"));
            Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
            await AssertNoLeaksAsync(database);
        }

        static async Task<string> CatalogAsync(TekiumDb database, string table)
        {
            await using var reader = database.BeginRead();
            var key = CatalogStore.TableKey(table);
            var constraints = await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.ConstraintsKey(key), Ct);
            var referrers = await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.ReferrersKey(key), Ct);
            var opened = (await SqlTable.OpenAsync(reader, table, Ct))!;
            Assert.Null(opened.ReadOnlyReason);
            return $"{Convert.ToHexString(constraints)} {Convert.ToHexString(referrers)} {string.Join(',', opened.Indexes.Select(static index => $"{index.Name}={Convert.ToHexString(index.Definition)}"))}";
        }
    }

    [Fact]
    public async Task Foreign_keys_refuse_exactly_what_a_model_with_the_same_rules_refuses()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE folders (id INT PRIMARY KEY, parent INT, label TEXT NOT NULL)");
        await RunAsync(connection, "CREATE TABLE files (id BIGINT PRIMARY KEY, folder INT NOT NULL, tag TEXT)");
        await RunAsync(connection, "CREATE TABLE tags (name TEXT PRIMARY KEY)");
        await RunAsync(connection, "CREATE INDEX by_folder ON files (folder, tag)");

        // Con filas ya puestas: una restricción nueva se comprueba contra ellas.
        await RunAsync(connection, "INSERT INTO folders (id, parent, label) VALUES (1, NULL, 'root'), (2, 1, 'a')");
        await RunAsync(connection, "INSERT INTO files (id, folder, tag) VALUES (1, 1, 'x'), (2, 3, NULL)");
        await RunAsync(connection, "INSERT INTO tags (name) VALUES ('x')");
        var orphan = await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT in_folder FOREIGN KEY (folder) REFERENCES folders (id)", "the row with key 2");
        Assert.Contains("in_folder", orphan, StringComparison.Ordinal);
        await RunAsync(connection, "DELETE FROM files WHERE id = 2");
        await RunAsync(connection, "ALTER TABLE files ADD CONSTRAINT in_folder FOREIGN KEY (folder) REFERENCES folders (id)");
        await RunAsync(connection, "ALTER TABLE files ADD FOREIGN KEY (tag) REFERENCES tags ON DELETE NO ACTION ON UPDATE RESTRICT");
        await RunAsync(connection, "ALTER TABLE folders ADD CONSTRAINT inside FOREIGN KEY (parent) REFERENCES folders");

        // La primera usó el índice que ya empezaba por su columna; las otras hicieron el suyo, con su nombre.
        await using (var reader = database.BeginRead())
        {
            Assert.Equal(["by_folder", "fk_tag"], (await SqlTable.OpenAsync(reader, "files", Ct))!.Indexes.Select(static index => index.Name));
            Assert.Equal(["inside"], (await SqlTable.OpenAsync(reader, "folders", Ct))!.Indexes.Select(static index => index.Name));
        }

        // Un texto que no puede ser clave no se refiere a nada; y una violación al escribir también aborta la transacción.
        await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO files (id, folder, tag) VALUES (900, 1, '')"));
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "INSERT INTO tags (name) VALUES ('never')");
            await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO files (id, folder, tag) VALUES (901, 44, NULL)"));
            await Assert.ThrowsAnyAsync<Exception>(async () => await transaction.CommitAsync(Ct));
        }

        Assert.Equal(["x"], await QueryAsync(connection, "SELECT name FROM tags"));

        // Una clave de texto que es el comienzo de otra no es la otra: quitar 'ab' no pregunta por quien se refiere a 'abc'.
        await RunAsync(connection, "INSERT INTO tags (name) VALUES ('ab'), ('abc')");
        await RunAsync(connection, "INSERT INTO files (id, folder, tag) VALUES (950, 1, 'abc')");
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM tags WHERE name = 'ab'"));
        await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM tags WHERE name = 'abc'"));
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM files WHERE id = 950"));
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM tags WHERE name = 'abc'"));

        var folders = new Dictionary<int, int?> { [1] = null, [2] = 1 };
        var files = new Dictionary<long, (int Folder, string? Tag)> { [1] = (1, "x") };
        var tags = new HashSet<string> { "x" };
        var random = new Random(17);
        var refusals = 0;
        var done = 0;
        for (var step = 0; step < 900; step++)
        {
            var folder = random.Next(1, 9);
            var file = (long)random.Next(1, 12);
            var tag = random.Next(3) == 0 ? null : $"t{random.Next(4)}";
            var parent = random.Next(3) == 0 ? (int?)null : random.Next(1, 9);
            var parentText = parent?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
            var tagText = tag is null ? "NULL" : $"'{tag}'";
            bool exists;
            bool allowed;
            string statement;
            Action apply;
            switch (random.Next(9))
            {
                case 0:
                    statement = $"INSERT INTO folders (id, parent, label) VALUES ({folder}, {parentText}, 'f')";
                    exists = !folders.ContainsKey(folder);
                    allowed = parent is null || parent == folder || folders.ContainsKey(parent.Value);
                    apply = () => folders[folder] = parent;
                    break;
                case 1:
                    statement = $"UPDATE folders SET parent = {parentText} WHERE id = {folder}";
                    exists = folders.ContainsKey(folder);
                    allowed = parent is null || folders.ContainsKey(parent.Value);
                    apply = () => folders[folder] = parent;
                    break;
                case 2:
                    statement = $"DELETE FROM folders WHERE id = {folder}";
                    exists = folders.ContainsKey(folder);
                    allowed = !files.Values.Any(entry => entry.Folder == folder) && !folders.Any(entry => entry.Value == folder && entry.Key != folder);
                    apply = () => folders.Remove(folder);
                    break;
                case 3 or 4:
                    statement = $"INSERT INTO files (id, folder, tag) VALUES ({file}, {folder}, {tagText})";
                    exists = !files.ContainsKey(file);
                    allowed = folders.ContainsKey(folder) && (tag is null || tags.Contains(tag));
                    apply = () => files[file] = (folder, tag);
                    break;
                case 5:
                    statement = $"UPDATE files SET folder = {folder}, tag = {tagText} WHERE id = {file}";
                    exists = files.ContainsKey(file);
                    allowed = folders.ContainsKey(folder) && (tag is null || tags.Contains(tag));
                    apply = () => files[file] = (folder, tag);
                    break;
                case 6:
                    statement = $"DELETE FROM files WHERE id = {file}";
                    exists = files.ContainsKey(file);
                    allowed = true;
                    apply = () => files.Remove(file);
                    break;
                case 7:
                    var name = $"t{random.Next(4)}";
                    statement = $"INSERT INTO tags (name) VALUES ('{name}')";
                    exists = !tags.Contains(name);
                    allowed = true;
                    apply = () => tags.Add(name);
                    break;
                default:
                    var gone = $"t{random.Next(4)}";
                    statement = $"DELETE FROM tags WHERE name = '{gone}'";
                    exists = tags.Contains(gone);
                    allowed = !files.Values.Any(entry => entry.Tag == gone);
                    apply = () => tags.Remove(gone);
                    break;
            }

            if (!exists)
            {
                continue;
            }

            if (allowed)
            {
                done++;
                Assert.Equal(1, await RunAsync(connection, statement));
                apply();
            }
            else
            {
                refusals++;
                await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, statement));
            }
        }

        Assert.True(refusals > 50 && done > 50, $"{refusals} refused, {done} done");
        Assert.Equal(folders.OrderBy(static entry => entry.Key).Select(static entry => $"{entry.Key}|{entry.Value}"), await QueryAsync(connection, "SELECT id, parent FROM folders ORDER BY id"));
        Assert.Equal(files.OrderBy(static entry => entry.Key).Select(static entry => $"{entry.Key}|{entry.Value.Folder}|{entry.Value.Tag}"), await QueryAsync(connection, "SELECT id, folder, tag FROM files ORDER BY id"));
        Assert.Equal(tags.Order(StringComparer.Ordinal), await QueryAsync(connection, "SELECT name FROM tags ORDER BY name"));
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);

        // Lo que una clave foránea necesita no se puede quitar mientras está.
        await RefusedAsync<TkSqlException>(connection, "DROP TABLE folders", "refer to it");
        await RefusedAsync<TkSqlException>(connection, "DROP TABLE tags", "refer to it");
        await RefusedAsync<TkSqlException>(connection, "DROP INDEX by_folder ON files", "FOREIGN KEY 'in_folder'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files DROP CONSTRAINT nothing", "no constraint 'nothing'");
        await RunAsync(connection, "ALTER TABLE files DROP CONSTRAINT IF EXISTS nothing");

        // Lo que no se puede declarar dice por qué.
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT in_folder FOREIGN KEY (folder) REFERENCES folders", "already has a constraint 'in_folder'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT x FOREIGN KEY (folder) REFERENCES nowhere", "no table 'nowhere'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT x FOREIGN KEY (nothing) REFERENCES folders", "no column 'nothing'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT x FOREIGN KEY (folder) REFERENCES folders (label)", "only to its primary key");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT x FOREIGN KEY (id) REFERENCES folders", "is a BIGINT and the key of 'folders' is an INT");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT x FOREIGN KEY (folder) REFERENCES folders ON DELETE CASCADE", "ON DELETE CASCADE is not supported");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT x FOREIGN KEY (folder) REFERENCES folders ON UPDATE SET NULL", "ON UPDATE SET NULL is not supported");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD CONSTRAINT x FOREIGN KEY (folder, id) REFERENCES folders", "a foreign key is one column");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE files ADD COLUMN owner INT REFERENCES folders", "cannot be added with REFERENCES");
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE bad (id INT PRIMARY KEY, f INT REFERENCES nowhere)", "no table 'nowhere'");
        await RefusedAsync<TkSqlException>(connection, "SELECT 1 FROM bad", "bad");

        // Un índice parcial no tiene todas las filas: la clave foránea no lo usa, y hace el suyo con el nombre de la columna como está en la tabla.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Chore>().InsertAsync(writer, new Chore { Id = 1, Due = 3, Done = true, Title = "done" }, Ct);
            await database.Table<Chore>().InsertAsync(writer, new Chore { Id = 2, Due = 4, Title = "pending" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await RunAsync(connection, "CREATE TABLE days (n INT PRIMARY KEY)");
        await RunAsync(connection, "INSERT INTO days (n) VALUES (3), (4), (5)");
        await RunAsync(connection, "ALTER TABLE chores ADD FOREIGN KEY (DUE) REFERENCES days");
        await using (var reader = database.BeginRead())
        {
            var chores = (await SqlTable.OpenAsync(reader, "chores", Ct))!;
            Assert.Equal(["fk_Due", "pending"], chores.Indexes.Select(static index => index.Name));
            Assert.Equal([("fk_Due", "fk_Due")], chores.ForeignKeys.Select(static key => (key.Info.Name, key.Info.Index)));
        }

        Assert.Equal("fk_Due", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM days WHERE n = 3"))).Constraint);
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM days WHERE n = 5"));

        // Una tabla que SQL no puede escribir tampoco se cambia con SQL.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Ticket>().InsertAsync(writer, new Ticket { Id = 1, Owner = "a", Title = "One" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE tickets ADD COLUMN extra INT", "cannot be changed through SQL");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE tickets ADD FOREIGN KEY (id) REFERENCES days", "cannot be changed through SQL");

        // Quitada la restricción, la tabla se puede borrar y la referida queda libre.
        await RunAsync(connection, "ALTER TABLE files DROP CONSTRAINT in_folder");
        await RunAsync(connection, "DROP INDEX by_folder ON files");
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO files (id, folder) VALUES (500, 77)"));
        await RunAsync(connection, "DROP TABLE files");
        Assert.Equal(tags.Count, await RunAsync(connection, "DELETE FROM tags"));
        await RunAsync(connection, "DROP TABLE tags");
        await RunAsync(connection, "DROP TABLE folders");
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_column_added_to_a_table_reads_as_its_default_in_the_rows_there_were_and_the_row_type_agrees()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Team>().InsertAsync(writer, new Team { Id = 1, Name = "one" }, Ct);
            for (var i = 1; i <= 40; i++)
            {
                await database.Table<Player>().InsertAsync(writer, new Player { Id = i, Team = i % 2 == 0 ? 1 : null, Name = $"p{i}" }, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        // Sin valor por omisión no se reescribe ninguna fila: la que no puede ser NULL lee el cero de su tipo.
        uint root;
        await using (var reader = database.BeginRead())
        {
            root = (await SqlTable.OpenAsync(reader, "players", Ct))!.Root.Value;
        }

        Assert.Equal(-1, await RunAsync(connection, "ALTER TABLE players ADD COLUMN Number INT NOT NULL"));
        Assert.Equal(-1, await RunAsync(connection, "ALTER TABLE players ADD Nick TEXT NULL"));
        await using (var reader = database.BeginRead())
        {
            var players = (await SqlTable.OpenAsync(reader, "players", Ct))!;
            Assert.Equal(root, players.Root.Value);
            Assert.Equal(3, players.Version);
            Assert.Equal(["Id", "Team", "Captain", "Name", "Number", "Nick"], players.Columns.Select(static column => column.Name));
        }

        Assert.Equal(["1||p1|0|", "2|1|p2|0|"], await QueryAsync(connection, "SELECT Id, Team, Name, Number, Nick FROM players WHERE Id < 3 ORDER BY Id"));
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO players (Id, Name, Number, Nick) VALUES (41, 'p41', 7, 'seven')"));
        Assert.Equal(20, await RunAsync(connection, "UPDATE players SET Number = Id WHERE Team = 1"));
        await RunAsync(connection, "CREATE INDEX by_number ON players (Number)");
        Assert.Equal(["40"], await QueryAsync(connection, "SELECT Id FROM players WHERE Number = 40"));
        await RunAsync(connection, "DROP INDEX by_number ON players");

        // El tipo que declara las mismas columnas lee y escribe la tabla sin registrar otra versión.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var table = database.Table<PlayerWithNumber>();
            Assert.Equal(new PlayerWithNumber { Id = 1, Name = "p1" }, await table.GetAsync(writer, 1L, Ct));
            Assert.Equal(new PlayerWithNumber { Id = 41, Name = "p41", Number = 7, Nick = "seven" }, await table.GetAsync(writer, 41L, Ct));
            await table.InsertAsync(writer, new PlayerWithNumber { Id = 42, Team = 1, Name = "p42", Number = 9 }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using (var reader = database.BeginRead())
        {
            Assert.Equal(3, (await SqlTable.OpenAsync(reader, "players", Ct))!.Version);
        }

        // Un valor por omisión que es lo que la fila vieja ya leería tampoco reescribe nada.
        uint teams;
        await using (var reader = database.BeginRead())
        {
            teams = (await SqlTable.OpenAsync(reader, "teams", Ct))!.Root.Value;
        }

        await RunAsync(connection, "ALTER TABLE teams ADD COLUMN Level INT NOT NULL DEFAULT 0");

        // El valor por omisión es para las filas que había: no se guarda, y una fila nueva tiene que dar el suyo.
        await RefusedAsync<TkSqlException>(connection, "INSERT INTO teams (Id, Name) VALUES (9, 'nine')", "Column 'Level' of table 'teams' cannot be NULL");
        await using (var reader = database.BeginRead())
        {
            Assert.Equal(teams, (await SqlTable.OpenAsync(reader, "teams", Ct))!.Root.Value);
        }

        // Con valor por omisión, las filas que había lo toman; las nuevas llevan el suyo.
        await RunAsync(connection, "ALTER TABLE teams ADD COLUMN Stars INT NOT NULL DEFAULT 3");
        await RunAsync(connection, "ALTER TABLE teams ADD COLUMN City TEXT DEFAULT 'none'");
        await RunAsync(connection, "ALTER TABLE teams ADD COLUMN Founded DATE NULL DEFAULT NULL");
        await RunAsync(connection, "ALTER TABLE teams ADD COLUMN Score DOUBLE NOT NULL DEFAULT -1.5");
        await RunAsync(connection, "ALTER TABLE teams ADD COLUMN Open BOOLEAN NOT NULL DEFAULT FALSE UNIQUE");
        await RunAsync(connection, "INSERT INTO teams (Id, Name, Level, Stars, Score, Open) VALUES (2, 'two', 0, 0, 0, TRUE)");
        Assert.Equal(["1|one|3|none||-1.5|False", "2|two|0|||0|True"], await QueryAsync(connection, "SELECT Id, Name, Stars, City, Founded, Score, Open FROM teams ORDER BY Id"));

        Assert.Equal("Open_unique", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO teams (Id, Name, Level, Stars, Score, Open) VALUES (3, 'three', 0, 0, 0, TRUE)"))).Constraint);

        // Una columna que no puede ser única no deja nada de sí, tampoco dentro de una transacción que sigue.
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams ADD COLUMN Twin INT NOT NULL UNIQUE", "cannot be unique");
            await RefusedAsync<TkSqlException>(connection, "SELECT Twin FROM teams", "Twin");
            Assert.Equal(1, await RunAsync(connection, "UPDATE teams SET Stars = 9 WHERE Id = 1"));
            await transaction.RollbackAsync(Ct);
        }

        // Dentro de una transacción que se deshace, no queda nada de la columna.
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "ALTER TABLE teams ADD COLUMN Gone INT DEFAULT 5");
            Assert.Equal(["5", "5"], await QueryAsync(connection, "SELECT Gone FROM teams"));
            await transaction.RollbackAsync(Ct);
        }

        await RefusedAsync<TkSqlException>(connection, "SELECT Gone FROM teams", "Gone");

        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams ADD COLUMN name INT", "already has a column 'name'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams ADD COLUMN other INT PRIMARY KEY", "primary key");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams ADD COLUMN other INT NOT NULL DEFAULT NULL", "cannot be NULL");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams ADD COLUMN other INT DEFAULT 'abc'", "other");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams ADD COLUMN other INT DEFAULT Id + 1", "Expected a value");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE nowhere ADD COLUMN other INT", "no table 'nowhere'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams DROP COLUMN Name", "Expected CONSTRAINT");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams RENAME TO clubs", "Expected ADD or DROP");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams ADD COLUMN Dup BOOLEAN NOT NULL UNIQUE", "cannot be unique");
        await RefusedAsync<TkSqlException>(connection, "SELECT Dup FROM teams", "Dup");

        // En una misma transacción, el tipo que ya había escrito vuelve a mirar las columnas después del ALTER:
        // como no declara la nueva, registra otra versión en vez de escribir con la que no es suya.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var table = database.Table<PlayerWithNumber>();
            await table.InsertAsync(writer, new PlayerWithNumber { Id = 50, Name = "p50", Number = 1 }, Ct);
            await new TkContext(database, writer).ExecuteAsync("ALTER TABLE players ADD COLUMN Extra INT", cancellationToken: Ct);
            await table.InsertAsync(writer, new PlayerWithNumber { Id = 51, Name = "p51", Number = 2 }, Ct);
            Assert.Equal(new PlayerWithNumber { Id = 51, Name = "p51", Number = 2 }, await table.GetAsync(writer, 51L, Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(["50||p50|1", "51||p51|2"], await QueryAsync(connection, "SELECT Id, Team, Name, Number FROM players WHERE Id >= 50 ORDER BY Id"));
        await using (var reader = database.BeginRead())
        {
            Assert.Equal(5, (await SqlTable.OpenAsync(reader, "players", Ct))!.Version);
        }

        // Única y sin valor en ninguna fila sí puede ser: los NULL no se repiten. Con un valor por omisión, no, y no queda nada.
        await RunAsync(connection, "ALTER TABLE players ADD COLUMN Code TEXT UNIQUE");
        Assert.Equal(1, await RunAsync(connection, "UPDATE players SET Code = 'c1' WHERE Id = 1"));
        await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "UPDATE players SET Code = 'c1' WHERE Id = 2"));
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RefusedAsync<TkSqlException>(connection, "ALTER TABLE players ADD COLUMN Badge TEXT DEFAULT 'x' UNIQUE", "cannot be unique");
            await RefusedAsync<TkSqlException>(connection, "SELECT Badge FROM players", "Badge");
            await transaction.RollbackAsync(Ct);
        }

        // Una tabla tiene 255 versiones de esquema, y se dice cuando se acaban.
        await RunAsync(connection, "CREATE TABLE wide (id INT PRIMARY KEY)");
        for (var i = 2; i <= 255; i++)
        {
            await RunAsync(connection, $"ALTER TABLE wide ADD COLUMN c{i} INT");
        }

        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE wide ADD COLUMN last INT", "all 255 schema versions");
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);
    }
}

public sealed class SqlSchemaTests_File : SqlSchemaTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class SqlSchemaTests_Memory : SqlSchemaTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
