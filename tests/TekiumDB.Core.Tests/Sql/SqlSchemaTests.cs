// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using TekiumDB.Catalog;
using TekiumDB.Sql;
using TekiumDB.Sql.Storage;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;
using static TekiumDB.Tests.Sql.SqlTests;

namespace TekiumDB.Tests.Sql;

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
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE teams SPLIT Name", "Expected ADD, DROP, RENAME or ALTER");
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
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE wide RENAME COLUMN c2 TO d2", "all 255 schema versions");
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Dropping_renaming_and_changing_columns_agrees_with_a_model_of_the_rows_after_every_change()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE stock (id INT PRIMARY KEY, code TEXT NOT NULL, qty INT, price DECIMAL, note TEXT)");
        await RunAsync(connection, "CREATE UNIQUE INDEX by_code ON stock (code)");
        await RunAsync(connection, "CREATE INDEX by_qty ON stock (qty, id)");
        var random = new Random(23);

        // El modelo: los nombres y tipos de las columnas, y cada fila como texto por columna.
        var columns = new List<(string Name, string Type)> { ("id", "INT"), ("code", "TEXT"), ("qty", "INT"), ("price", "DECIMAL"), ("note", "TEXT") };
        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i <= 300; i++)
        {
            var qty = random.Next(4) == 0 ? null : (int?)random.Next(-50, 500);
            var price = random.Next(5) == 0 ? null : (decimal?)(random.Next(100_000) / 100m);
            var note = random.Next(3) == 0 ? null : $"n{random.Next(20)}";
            await RunAsync(connection, "INSERT INTO stock (id, code, qty, price, note) VALUES (@id, @code, @qty, @price, @note)", ("id", i), ("code", $"c{i:D4}"), ("qty", qty), ("price", price), ("note", note));
            rows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = $"{i}", ["code"] = $"c{i:D4}", ["qty"] = Show(qty), ["price"] = Show(price), ["note"] = note ?? string.Empty,
            });
        }

        await SameAsync();

        // Renombrar: nada cambia en las filas ni en los índices, sólo los nombres; los índices siguen sirviendo.
        await RunAsync(connection, "ALTER TABLE stock RENAME COLUMN qty TO amount");
        Rename("qty", "amount");
        await SameAsync();
        Assert.Contains(await PlanAsync("SELECT id FROM stock WHERE amount = 7"), static line => line.Contains("by_qty", StringComparison.Ordinal));

        // Cambiar el tipo: INT a BIGINT, DECIMAL a DOUBLE y vuelta, INT a TEXT. Cada valor se conserva.
        await RunAsync(connection, "ALTER TABLE stock ALTER COLUMN amount TYPE BIGINT");
        Retype("amount", "BIGINT");
        await SameAsync();
        Assert.Contains(await PlanAsync("SELECT id FROM stock WHERE amount > 400"), static line => line.Contains("by_qty", StringComparison.Ordinal));
        await RunAsync(connection, "ALTER TABLE stock ALTER COLUMN price TYPE DECIMAL");
        await RunAsync(connection, "ALTER TABLE stock ALTER COLUMN id TYPE INT");
        await SameAsync();

        // A NOT NULL: los NULL toman el valor dado, o se rechaza si no se da ninguno.
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock ALTER COLUMN amount BIGINT NOT NULL", "has NULL in");
        await RunAsync(connection, "ALTER TABLE stock ALTER COLUMN amount BIGINT NOT NULL DEFAULT -1");
        foreach (var row in rows.Where(static row => row["amount"].Length == 0))
        {
            row["amount"] = "-1";
        }

        await SameAsync();
        await RefusedAsync<TkSqlException>(connection, "INSERT INTO stock (id, code) VALUES (999, 'x')", "cannot be NULL");
        await RunAsync(connection, "ALTER TABLE stock ALTER COLUMN amount BIGINT NULL");
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO stock (id, code) VALUES (999, 'x')"));
        rows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["id"] = "999", ["code"] = "x", ["amount"] = string.Empty, ["price"] = string.Empty, ["note"] = string.Empty });
        await SameAsync();

        // Quitar: el valor sale de cada fila; una columna nueva con el mismo nombre no lo recupera.
        await RunAsync(connection, "ALTER TABLE stock DROP COLUMN note");
        columns.RemoveAll(static column => column.Name == "note");
        await SameAsync();
        await RunAsync(connection, "ALTER TABLE stock ADD COLUMN note INT");
        columns.Add(("note", "INT"));
        foreach (var row in rows)
        {
            row["note"] = string.Empty;
        }

        await SameAsync();

        // Un valor que el tipo nuevo no guarda tal cual se rechaza, y la tabla queda como estaba.
        await RunAsync(connection, "UPDATE stock SET amount = 5000000000 WHERE id = 3");
        rows.Single(static row => row["id"] == "3")["amount"] = "5000000000";
        var tooBig = await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock ALTER COLUMN amount INT", "the row with key 3");
        Assert.Contains("5000000000", tooBig, StringComparison.Ordinal);
        var fraction = rows.First(static row => row["price"].Contains('.', StringComparison.Ordinal));
        var lost = await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock ALTER COLUMN price INT", "cannot become an INT");
        Assert.Contains($"the row with key {fraction["id"]} has {fraction["price"]}", lost, StringComparison.Ordinal);
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock ALTER COLUMN code INT", "c0001");
        await SameAsync();

        // Un texto que cambia al volverse número no se convierte: '1.0' sería 1, y de vuelta '1'.
        await RunAsync(connection, "CREATE TABLE codes (id INT PRIMARY KEY, label TEXT UNIQUE)");
        await RunAsync(connection, "INSERT INTO codes (id, label) VALUES (1, '1.0'), (2, NULL), (3, NULL)");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE codes ALTER COLUMN label DOUBLE", "the row with key 1 has '1.0' in it");

        // Un índice único que dejaría de serlo se rechaza antes de tocar nada: los NULL no se repiten, el valor que los reemplaza sí.
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE codes ALTER COLUMN label TEXT NOT NULL DEFAULT 'none'", "unique index 'label_unique'");
        Assert.Equal(["1|1.0", "2|", "3|"], await QueryAsync(connection, "SELECT id, label FROM codes ORDER BY id"));
        await RunAsync(connection, "UPDATE codes SET label = '2.5' WHERE id = 2");
        await RunAsync(connection, "DELETE FROM codes WHERE id IN (1, 3)");
        await RunAsync(connection, "ALTER TABLE codes ALTER COLUMN label DECIMAL NOT NULL");
        Assert.Equal(["2.5"], await QueryAsync(connection, "SELECT label FROM codes WHERE label > 2"));
        Assert.Equal("label_unique", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO codes (id, label) VALUES (9, 2.5)"))).Constraint);

        // Lo que no se puede: la clave, una columna que un índice usa, una que no existe, dos con el mismo nombre.
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock DROP COLUMN id", "primary key");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock DROP COLUMN code", "index 'by_code'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock DROP COLUMN nothing", "no column 'nothing'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock RENAME COLUMN code TO PRICE", "already has a column 'PRICE'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE stock ALTER COLUMN id TEXT", "primary key");
        await RunAsync(connection, "DROP INDEX by_code ON stock");
        await RunAsync(connection, "ALTER TABLE stock DROP COLUMN code");
        columns.RemoveAll(static column => column.Name == "code");
        await SameAsync();
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);

        void Rename(string from, string to)
        {
            columns[columns.FindIndex(column => column.Name == from)] = (to, columns.Single(column => column.Name == from).Type);
            foreach (var row in rows)
            {
                row[to] = row[from];
                row.Remove(from);
            }
        }

        void Retype(string name, string type) => columns[columns.FindIndex(column => column.Name == name)] = (name, type);

        async Task SameAsync()
        {
            var names = string.Join(", ", columns.Select(static column => column.Name));
            Assert.Equal(
                rows.OrderBy(static row => int.Parse(row["id"], CultureInfo.InvariantCulture)).Select(row => string.Join('|', columns.Select(column => row.GetValueOrDefault(column.Name, string.Empty)))),
                await QueryAsync(connection, $"SELECT {names} FROM stock ORDER BY id"));
            await using var reader = database.BeginRead();
            var stock = (await SqlTable.OpenAsync(reader, "stock", Ct))!;
            Assert.Equal(columns.Select(static column => column.Name), stock.Columns.Select(static column => column.Name));
            Assert.Null(stock.ReadOnlyReason);
        }

        async Task<List<string>> PlanAsync(string sql) => await QueryAsync(connection, "EXPLAIN " + sql);

        static string Show(object? value) => value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!,
        };
    }

    [Fact]
    public async Task A_renamed_table_keeps_its_rows_indexes_and_foreign_keys_and_a_row_type_reads_it_under_the_new_name()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Team>().InsertAsync(writer, new Team { Id = 1, Name = "one" }, Ct);
            await database.Table<Team>().InsertAsync(writer, new Team { Id = 2, Name = "two" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await RunAsync(connection, "CREATE TABLE fans (id INT PRIMARY KEY, team INT REFERENCES teams, rival INT, name TEXT NOT NULL)");
        await RunAsync(connection, "ALTER TABLE fans ADD CONSTRAINT rival FOREIGN KEY (rival) REFERENCES fans");
        await RunAsync(connection, "CREATE INDEX by_name ON teams (Name)");
        await RunAsync(connection, "INSERT INTO fans (id, team, rival, name) VALUES (1, 1, NULL, 'ana'), (2, 2, 1, 'bea')");

        // Una vista o un procedimiento que nombra la tabla impide renombrarla: dejaría de funcionar.
        await RunAsync(connection, "CREATE VIEW loyal AS SELECT name FROM fans WHERE team = 1");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE fans RENAME TO supporters", "view 'loyal'");
        await RunAsync(connection, "DROP VIEW loyal");

        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "ALTER TABLE teams RENAME TO clubs");
            await RunAsync(connection, "ALTER TABLE clubs RENAME COLUMN Name TO Title");
            await RunAsync(connection, "ALTER TABLE fans RENAME TO supporters");
            await RunAsync(connection, "ALTER INDEX by_name ON clubs RENAME TO by_title");

            // Dentro de la misma transacción ya se ve con los nombres nuevos, y el viejo no existe.
            Assert.Equal(["1|one", "2|two"], await QueryAsync(connection, "SELECT Id, Title FROM clubs ORDER BY Id"));
            await RefusedAsync<TkSqlException>(connection, "SELECT 1 FROM teams", "no table or view 'teams'");
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(["ana|one", "bea|two"], await QueryAsync(connection, "SELECT s.name, c.Title FROM supporters s JOIN clubs c ON c.Id = s.team ORDER BY s.id"));
        Assert.Contains(await QueryAsync(connection, "EXPLAIN SELECT Id FROM clubs WHERE Title = 'one'"), static line => line.Contains("by_title", StringComparison.Ordinal));

        // Las claves foráneas siguen, con el nombre nuevo de cada lado, también la de la tabla a sí misma.
        var missing = await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO supporters (id, team, name) VALUES (3, 9, 'cora')"));
        Assert.Contains("no row of 'clubs'", missing.Message, StringComparison.Ordinal);
        var referred = await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM clubs WHERE Id = 1"));
        Assert.Equal("supporters", referred.Table);
        Assert.Equal("rival", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM supporters WHERE id = 1"))).Constraint);
        await RefusedAsync<TkSqlException>(connection, "DROP TABLE clubs", "rows of 'supporters' refer to it");
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO supporters (id, team, rival, name) VALUES (4, 2, 1, 'dora')"));
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM supporters WHERE id = 4"));
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE supporters ALTER COLUMN team BIGINT", "FOREIGN KEY 'fk_team' is on it");

        // Nada del nombre viejo queda en el catálogo.
        await using (var reader = database.BeginRead())
        {
            foreach (var old in new[] { "teams", "fans" })
            {
                var key = CatalogStore.TableKey(old);
                for (var version = 1; version <= 255; version++)
                {
                    Assert.Empty(await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.SchemaKey(key, (byte)version), Ct));
                }

                Assert.Empty(await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.ConstraintsKey(key), Ct));
                Assert.Empty(await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.ReferrersKey(key), Ct));
            }
        }

        await RefusedAsync<TkSqlException>(connection, "ALTER INDEX by_title ON clubs RENAME TO by_title", "already has an index 'by_title'");

        // Un tipo de fila con el nombre y las columnas nuevas lee y escribe lo que había. Como no declara
        // el índice, su primera escritura lo quita: el tipo manda sobre los índices de su tabla.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            Assert.Equal(new Club { Id = 2, Title = "two" }, await database.Table<Club>().GetAsync(writer, 2, Ct));
            await database.Table<Club>().InsertAsync(writer, new Club { Id = 3, Title = "three" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await RefusedAsync<TkSqlException>(connection, "ALTER INDEX by_title ON clubs RENAME TO x", "no index 'by_title'");
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO supporters (id, team, name) VALUES (3, 3, 'cora')"));

        // Lo que no se puede: un nombre que ya existe, una tabla que no existe, un índice que no existe.
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE clubs RENAME TO supporters", "already a table 'supporters'");
        await RunAsync(connection, "CREATE VIEW all_clubs AS SELECT Id FROM clubs");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE supporters RENAME TO all_clubs", "already a view 'all_clubs'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE nowhere RENAME TO x", "no table 'nowhere'");
        await RefusedAsync<TkSqlException>(connection, "ALTER INDEX nothing ON clubs RENAME TO x", "no index 'nothing'");

        // Un índice renombrado que usa una clave foránea sigue siendo el de la clave.
        await using (var reader = database.BeginRead())
        {
            var supporters = (await SqlTable.OpenAsync(reader, "supporters", Ct))!;
            var name = supporters.ForeignKeys.Single(static key => key.Info.Name == "fk_team").Info.Index;
            await using var again = new TkConnection(database);
            await RunAsync(again, $"ALTER INDEX \"{name}\" ON supporters RENAME TO by_team");
        }

        await using (var reader = database.BeginRead())
        {
            var supporters = (await SqlTable.OpenAsync(reader, "supporters", Ct))!;
            Assert.Equal("by_team", supporters.ForeignKeys.Single(static key => key.Info.Name == "fk_team").Info.Index);
        }

        Assert.Equal("fk_team", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM clubs WHERE Id = 3"))).Constraint);
        await RefusedAsync<TkSqlException>(connection, "DROP INDEX by_team ON supporters", "FOREIGN KEY 'fk_team'");

        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_table_or_column_a_stored_procedure_names_is_not_renamed_or_dropped()
    {
        var device = NewDevice();
        await using var database = await TekiumDb.OpenAsync(_ => device, new DbOptions { InMemory = true, PageSize = Options.PageSize, CacheSizeMb = Options.CacheSizeMb, KeyDerivation = Options.KeyDerivation, AllowStoredProcedures = true }, Ct);
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE orders (id BIGINT PRIMARY KEY, total DECIMAL NOT NULL, placed INT NOT NULL, note TEXT)");
        await RunAsync(connection, "CREATE PROCEDURE close_orders(p_before INT) AS $$ BEGIN DELETE FROM orders WHERE placed < p_before; END $$");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE orders RENAME TO sales", "procedure 'close_orders'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE orders RENAME COLUMN placed TO day", "procedure 'close_orders'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE orders DROP COLUMN placed", "procedure 'close_orders'");

        // Lo que el procedimiento no nombra se cambia.
        await RunAsync(connection, "ALTER TABLE orders RENAME COLUMN note TO memo");
        await RunAsync(connection, "ALTER TABLE orders DROP COLUMN memo");
        await RunAsync(connection, "ALTER TABLE orders ALTER COLUMN total DOUBLE NOT NULL");
        await RunAsync(connection, "DROP PROCEDURE close_orders");
        await RunAsync(connection, "ALTER TABLE orders RENAME TO sales");
        Assert.Empty(await QueryAsync(connection, "SELECT id FROM sales"));
    }

    [Fact]
    public async Task What_a_rewrite_must_keep_the_key_the_foreign_keys_and_the_bindings_of_a_row_type()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);

        // Una clave que no es la primera columna: quitar una de antes la mueve de lugar.
        await RunAsync(connection, "CREATE TABLE late (a INT, id INT PRIMARY KEY, b TEXT)");
        await RunAsync(connection, "INSERT INTO late (a, id, b) VALUES (1, 10, 'x'), (2, 20, 'y')");
        await RunAsync(connection, "ALTER TABLE late DROP COLUMN a");
        Assert.Equal(["20|y"], await QueryAsync(connection, "SELECT id, b FROM late WHERE id = 20"));
        await Assert.ThrowsAsync<TkDuplicateKeyException>(async () => await RunAsync(connection, "INSERT INTO late (id, b) VALUES (10, 'again')"));
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO late (id, b) VALUES (30, 'z')"));
        Assert.Equal(["10|x", "20|y", "30|z"], await QueryAsync(connection, "SELECT id, b FROM late ORDER BY id"));
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE late ADD PRIMARY KEY (b)", "does not change");
        await RunAsync(connection, "ALTER TABLE late ADD PRIMARY KEY (id)");

        // Una tabla cambiada y renombrada en la misma transacción no reaparece con el nombre viejo al confirmar.
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "INSERT INTO late (id, b) VALUES (40, 'w')");
            await RunAsync(connection, "ALTER TABLE late RENAME TO later");
            await transaction.CommitAsync(Ct);
        }

        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM late", "no table or view 'late'");
        Assert.Equal(["10", "20", "30", "40"], await QueryAsync(connection, "SELECT id FROM later ORDER BY id"));
        await RunAsync(connection, "ALTER TABLE later RENAME TO late");

        // Renombrar a su propio nombre no hace nada; con otras mayúsculas, es otro nombre.
        await RunAsync(connection, "ALTER TABLE late RENAME TO late");
        Assert.Equal(["10", "20", "30", "40"], await QueryAsync(connection, "SELECT id FROM late ORDER BY id"));

        // Un índice quitado antes de renombrar, en la misma transacción, no queda colgado del nombre viejo.
        await RunAsync(connection, "CREATE INDEX by_b ON late (b)");
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "DROP INDEX by_b ON late");
            await RunAsync(connection, "ALTER TABLE late RENAME TO later");
            await transaction.CommitAsync(Ct);
        }

        await using (var reader = database.BeginRead())
        {
            Assert.Empty(await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.IndexKey(CatalogStore.TableKey("late"), "by_b"), Ct));
        }

        await RunAsync(connection, "CREATE TABLE late (id INT PRIMARY KEY)");
        await using (var reader = database.BeginRead())
        {
            Assert.Empty((await SqlTable.OpenAsync(reader, "late", Ct))!.Indexes);
        }

        await RunAsync(connection, "DROP TABLE late");
        await RunAsync(connection, "ALTER TABLE later RENAME TO late");

        // Un cambio que no cambia nada no gasta una versión ni escribe filas.
        await using (var reader = database.BeginRead())
        {
            Assert.Equal(2, (await SqlTable.OpenAsync(reader, "late", Ct))!.Version);
        }

        await RunAsync(connection, "ALTER TABLE late ALTER COLUMN b TEXT");
        await using (var reader = database.BeginRead())
        {
            Assert.Equal(2, (await SqlTable.OpenAsync(reader, "late", Ct))!.Version);
        }

        // Una clave foránea sobre una columna que deja de admitir NULL se sigue buscando bien al borrar.
        await RunAsync(connection, "CREATE TABLE owners (id INT PRIMARY KEY)");
        await RunAsync(connection, "CREATE TABLE pets (id INT PRIMARY KEY, owner INT REFERENCES owners)");
        await RunAsync(connection, "INSERT INTO owners (id) VALUES (1), (2)");
        await RunAsync(connection, "INSERT INTO pets (id, owner) VALUES (1, 1)");
        await RunAsync(connection, "ALTER TABLE pets ALTER COLUMN owner INT NOT NULL");
        Assert.Equal("fk_owner", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM owners WHERE id = 1"))).Constraint);
        await RunAsync(connection, "ALTER TABLE pets ALTER COLUMN owner INT NULL");
        Assert.Equal("fk_owner", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM owners WHERE id = 1"))).Constraint);
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM owners WHERE id = 2"));

        // Un índice parcial está escrito para su columna como es: esa columna no cambia.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Chore>().InsertAsync(writer, new Chore { Id = 1, Due = 3, Title = "one" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE chores ALTER COLUMN Done BOOLEAN NULL", "partial index 'pending'");
        await RunAsync(connection, "ALTER TABLE chores ALTER COLUMN Due BIGINT");
        Assert.Equal(["1"], await QueryAsync(connection, "SELECT Id FROM chores WHERE Due = 3 AND NOT Done"));

        // Renombrar la columna de la condición sí se puede: el índice parcial sigue siendo de SQL.
        await RunAsync(connection, "ALTER TABLE chores RENAME COLUMN Done TO Finished");
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO chores (Id, Due, Finished, Title) VALUES (2, 3, FALSE, 'two')"));
        Assert.Equal(["1", "2"], await QueryAsync(connection, "SELECT Id FROM chores WHERE Due = 3 AND NOT Finished ORDER BY Id"));
        await RunAsync(connection, "ALTER TABLE chores RENAME COLUMN Finished TO Done");

        // Un tipo de fila que ya escribió en la transacción vuelve a mirar los índices después de una reescritura.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Team>().InsertAsync(writer, new Team { Id = 1, Name = "one" }, Ct);
            await database.Table<Team>().InsertAsync(writer, new Team { Id = 2, Name = "two" }, Ct);

            // SQL sólo ve una tabla que el tipo creó cuando ya está confirmada.
            await database.Table<Player>().InsertAsync(writer, new Player { Id = 99, Name = "nobody's" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var players = database.Table<Player>();
            await players.InsertAsync(writer, new Player { Id = 100, Team = 2, Name = "first" }, Ct);
            var sql = new TkContext(database, writer);
            await sql.ExecuteAsync("ALTER TABLE players ALTER COLUMN Name TEXT NULL", cancellationToken: Ct);
            await sql.ExecuteAsync("ALTER TABLE players ALTER COLUMN Name TEXT NOT NULL", cancellationToken: Ct);
            await players.InsertAsync(writer, new Player { Id = 101, Team = 1, Name = "second" }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal("fk_Team", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "DELETE FROM teams WHERE Id = 1"))).Constraint);
        Assert.Equal(["101"], await QueryAsync(connection, "SELECT Id FROM players WHERE Team = 1"));
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
