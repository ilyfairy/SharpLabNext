using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactStore;

internal sealed class ArtifactStoreDatabase
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateCanonicalSerializerOptions();
    private readonly string _connectionString;

    public ArtifactStoreDatabase(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            _ = await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            var currentVersion = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (currentVersion > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Artifact Store database schema {currentVersion} is newer than supported schema {SchemaVersion}.");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS contents (
                id TEXT PRIMARY KEY,
                size INTEGER NOT NULL CHECK(size >= 0),
                relative_path TEXT NOT NULL UNIQUE,
                created_utc INTEGER NOT NULL,
                last_access_utc INTEGER NOT NULL,
                expires_utc INTEGER NOT NULL,
                ref_count INTEGER NOT NULL DEFAULT 0 CHECK(ref_count >= 0)
            );

            CREATE TABLE IF NOT EXISTS artifacts (
                id TEXT PRIMARY KEY,
                descriptor_json TEXT NOT NULL,
                relative_path TEXT NOT NULL UNIQUE,
                total_size INTEGER NOT NULL CHECK(total_size >= 0),
                created_utc INTEGER NOT NULL,
                last_access_utc INTEGER NOT NULL,
                expires_utc INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS artifact_entries (
                artifact_id TEXT NOT NULL REFERENCES artifacts(id) ON DELETE CASCADE,
                path TEXT NOT NULL,
                role TEXT NOT NULL,
                size INTEGER NOT NULL CHECK(size >= 0),
                digest TEXT NOT NULL,
                content_id TEXT NOT NULL REFERENCES contents(id) ON DELETE RESTRICT,
                PRIMARY KEY(artifact_id, path)
            );

            CREATE TABLE IF NOT EXISTS leases (
                token_hash TEXT PRIMARY KEY,
                artifact_id TEXT NOT NULL REFERENCES artifacts(id) ON DELETE CASCADE,
                owner TEXT NOT NULL,
                created_utc INTEGER NOT NULL,
                expires_utc INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_artifacts_expiry ON artifacts(expires_utc);
            CREATE INDEX IF NOT EXISTS ix_contents_expiry_refcount ON contents(expires_utc, ref_count);
            CREATE INDEX IF NOT EXISTS ix_leases_artifact_expiry ON leases(artifact_id, expires_utc);
            PRAGMA user_version = 1;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertContentAsync(
        PublishedContent content,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await UpsertContentCoreAsync(connection, transaction, content, now, expiresAt, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<bool> CommitArtifactAsync(
        ArtifactBundleDescriptor descriptor,
        string artifactRelativePath,
        IReadOnlyList<PublishedContent> contents,
        long totalSize,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var artifactId = descriptor.Manifest.ArtifactId.Value;

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT descriptor_json, relative_path, total_size FROM artifacts WHERE id = $id;";
            existing.Parameters.AddWithValue("$id", artifactId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existingDescriptor = reader.GetString(0);
                var existingPath = reader.GetString(1);
                var existingSize = reader.GetInt64(2);
                var incomingDescriptor = JsonSerializer.Serialize(descriptor, JsonOptions);
                if (!string.Equals(existingDescriptor, incomingDescriptor, StringComparison.Ordinal)
                    || !string.Equals(existingPath, artifactRelativePath, StringComparison.Ordinal)
                    || existingSize != totalSize)
                {
                    throw new ArtifactCorruptedException($"Artifact metadata for '{artifactId}' does not match its content address.");
                }

                await reader.DisposeAsync().ConfigureAwait(false);
                await ExtendArtifactExpiryAsync(connection, transaction, artifactId, now, expiresAt, cancellationToken)
                    .ConfigureAwait(false);
                transaction.Commit();
                return false;
            }
        }

        foreach (var content in contents)
        {
            await UpsertContentCoreAsync(connection, transaction, content, now, expiresAt, cancellationToken)
                .ConfigureAwait(false);
        }

        await using (var insertArtifact = connection.CreateCommand())
        {
            insertArtifact.Transaction = transaction;
            insertArtifact.CommandText = """
                INSERT INTO artifacts(id, descriptor_json, relative_path, total_size, created_utc, last_access_utc, expires_utc)
                VALUES($id, $descriptor, $path, $size, $now, $now, $expires);
                """;
            insertArtifact.Parameters.AddWithValue("$id", artifactId);
            insertArtifact.Parameters.AddWithValue("$descriptor", JsonSerializer.Serialize(descriptor, JsonOptions));
            insertArtifact.Parameters.AddWithValue("$path", artifactRelativePath);
            insertArtifact.Parameters.AddWithValue("$size", totalSize);
            insertArtifact.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            insertArtifact.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expiresAt));
            _ = await insertArtifact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in descriptor.Entries)
        {
            await using var insertEntry = connection.CreateCommand();
            insertEntry.Transaction = transaction;
            insertEntry.CommandText = """
                INSERT INTO artifact_entries(artifact_id, path, role, size, digest, content_id)
                VALUES($artifact, $path, $role, $size, $digest, $content);
                UPDATE contents SET ref_count = ref_count + 1 WHERE id = $content;
                """;
            insertEntry.Parameters.AddWithValue("$artifact", artifactId);
            insertEntry.Parameters.AddWithValue("$path", entry.Path);
            insertEntry.Parameters.AddWithValue("$role", entry.Role);
            insertEntry.Parameters.AddWithValue("$size", entry.Size);
            insertEntry.Parameters.AddWithValue("$digest", entry.Digest);
            insertEntry.Parameters.AddWithValue("$content", entry.ContentRef.Value);
            _ = await insertEntry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return true;
    }

    public async Task<StoredArtifactMetadata?> GetArtifactAsync(
        ArtifactRef artifactRef,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT descriptor_json, relative_path, total_size, expires_utc
            FROM artifacts
            WHERE id = $id
              AND (expires_utc > $now OR EXISTS(
                    SELECT 1 FROM leases WHERE artifact_id = artifacts.id AND expires_utc > $now));
            """;
        command.Parameters.AddWithValue("$id", artifactRef.Value);
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var descriptor = JsonSerializer.Deserialize<ArtifactBundleDescriptor>(reader.GetString(0), JsonOptions)
            ?? throw new ArtifactCorruptedException($"Artifact '{artifactRef}' has an empty descriptor.");
        var result = new StoredArtifactMetadata(
            descriptor,
            reader.GetString(1),
            reader.GetInt64(2),
            FromUnixMilliseconds(reader.GetInt64(3)));
        await reader.DisposeAsync().ConfigureAwait(false);
        await TouchArtifactAsync(connection, artifactRef.Value, now, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<(ContentRef ContentRef, long Size, string RelativePath)?> GetContentAsync(
        ContentRef contentRef,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT size, relative_path
            FROM contents
            WHERE id = $id AND (expires_utc > $now OR ref_count > 0);
            """;
        command.Parameters.AddWithValue("$id", contentRef.Value);
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var result = (contentRef, reader.GetInt64(0), reader.GetString(1));
        await reader.DisposeAsync().ConfigureAwait(false);
        await TouchContentAsync(connection, contentRef.Value, now, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<(ContentRef ContentRef, long Size, string RelativePath)?> GetArtifactEntryAsync(
        ArtifactRef artifactRef,
        string path,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.content_id, c.size, c.relative_path
            FROM artifact_entries e
            INNER JOIN artifacts a ON a.id = e.artifact_id
            INNER JOIN contents c ON c.id = e.content_id
            WHERE e.artifact_id = $artifact AND e.path = $path
              AND (a.expires_utc > $now OR EXISTS(
                    SELECT 1 FROM leases WHERE artifact_id = a.id AND expires_utc > $now));
            """;
        command.Parameters.AddWithValue("$artifact", artifactRef.Value);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var result = (ArtifactStoreProtocol.ParseContentRef(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2));
        await reader.DisposeAsync().ConfigureAwait(false);
        await TouchArtifactAsync(connection, artifactRef.Value, now, cancellationToken).ConfigureAwait(false);
        await TouchContentAsync(connection, result.Item1.Value, now, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> CreateLeaseAsync(
        ArtifactRef artifactRef,
        string tokenHash,
        string owner,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO leases(token_hash, artifact_id, owner, created_utc, expires_utc)
            SELECT $token, id, $owner, $now, $expires
            FROM artifacts
            WHERE id = $artifact AND expires_utc > $now;
            """;
        command.Parameters.AddWithValue("$token", tokenHash);
        command.Parameters.AddWithValue("$artifact", artifactRef.Value);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        command.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expiresAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<(ArtifactRef ArtifactRef, string Owner)?> RenewLeaseAsync(
        string tokenHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT artifact_id, owner FROM leases WHERE token_hash = $token AND expires_utc > $now;";
        select.Parameters.AddWithValue("$token", tokenHash);
        select.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var artifactRef = ArtifactStoreProtocol.ParseArtifactRef(reader.GetString(0));
        var owner = reader.GetString(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE leases SET expires_utc = $expires WHERE token_hash = $token;";
        update.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expiresAt));
        update.Parameters.AddWithValue("$token", tokenHash);
        _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return (artifactRef, owner);
    }

    public async Task ReleaseLeaseAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM leases WHERE token_hash = $token;";
        command.Parameters.AddWithValue("$token", tokenHash);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GarbageCollectionPlan> CollectGarbageAsync(
        DateTimeOffset now,
        int maxArtifacts,
        int maxContents,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var nowMilliseconds = ToUnixMilliseconds(now);

        int expiredLeases;
        await using (var deleteLeases = connection.CreateCommand())
        {
            deleteLeases.Transaction = transaction;
            deleteLeases.CommandText = "DELETE FROM leases WHERE expires_utc <= $now;";
            deleteLeases.Parameters.AddWithValue("$now", nowMilliseconds);
            expiredLeases = await deleteLeases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var artifacts = new List<(string Id, string RelativePath)>();
        await using (var selectArtifacts = connection.CreateCommand())
        {
            selectArtifacts.Transaction = transaction;
            selectArtifacts.CommandText = """
                SELECT id, relative_path
                FROM artifacts
                WHERE expires_utc <= $now
                  AND NOT EXISTS(SELECT 1 FROM leases WHERE artifact_id = artifacts.id AND expires_utc > $now)
                ORDER BY expires_utc
                LIMIT $limit;
                """;
            selectArtifacts.Parameters.AddWithValue("$now", nowMilliseconds);
            selectArtifacts.Parameters.AddWithValue("$limit", maxArtifacts);
            await using var reader = await selectArtifacts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                artifacts.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var artifact in artifacts)
        {
            await using (var decrement = connection.CreateCommand())
            {
                decrement.Transaction = transaction;
                decrement.CommandText = """
                    UPDATE contents
                    SET ref_count = ref_count - (
                        SELECT COUNT(*) FROM artifact_entries e
                        WHERE e.artifact_id = $artifact AND e.content_id = contents.id)
                    WHERE id IN (SELECT content_id FROM artifact_entries WHERE artifact_id = $artifact);
                    """;
                decrement.Parameters.AddWithValue("$artifact", artifact.Id);
                _ = await decrement.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM artifacts WHERE id = $artifact;";
            delete.Parameters.AddWithValue("$artifact", artifact.Id);
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var contents = new List<ContentDeletion>();
        await using (var selectContents = connection.CreateCommand())
        {
            selectContents.Transaction = transaction;
            selectContents.CommandText = """
                SELECT relative_path, size
                FROM contents
                WHERE ref_count = 0 AND expires_utc <= $now
                ORDER BY expires_utc
                LIMIT $limit;
                """;
            selectContents.Parameters.AddWithValue("$now", nowMilliseconds);
            selectContents.Parameters.AddWithValue("$limit", maxContents);
            await using var reader = await selectContents.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                contents.Add(new ContentDeletion(reader.GetString(0), reader.GetInt64(1)));
            }
        }

        foreach (var content in contents)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM contents WHERE relative_path = $path AND ref_count = 0;";
            delete.Parameters.AddWithValue("$path", content.RelativePath);
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return new GarbageCollectionPlan(expiredLeases, artifacts.Select(item => item.RelativePath).ToArray(), contents);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000;";
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task UpsertContentCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PublishedContent content,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO contents(id, size, relative_path, created_utc, last_access_utc, expires_utc, ref_count)
                VALUES($id, $size, $path, $now, $now, $expires, 0);
                """;
            insert.Parameters.AddWithValue("$id", content.ContentRef.Value);
            insert.Parameters.AddWithValue("$size", content.Size);
            insert.Parameters.AddWithValue("$path", content.RelativePath);
            insert.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
            insert.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expiresAt));
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT size, relative_path FROM contents WHERE id = $id;";
            verify.Parameters.AddWithValue("$id", content.ContentRef.Value);
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetInt64(0) != content.Size
                || !string.Equals(reader.GetString(1), content.RelativePath, StringComparison.Ordinal))
            {
                throw new ArtifactCorruptedException($"Content metadata for '{content.ContentRef}' does not match its digest.");
            }
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE contents
            SET last_access_utc = $now,
                expires_utc = CASE WHEN expires_utc < $expires THEN $expires ELSE expires_utc END
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        update.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expiresAt));
        update.Parameters.AddWithValue("$id", content.ContentRef.Value);
        _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtendArtifactExpiryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string artifactId,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE artifacts
            SET last_access_utc = $now,
                expires_utc = CASE WHEN expires_utc < $expires THEN $expires ELSE expires_utc END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        command.Parameters.AddWithValue("$expires", ToUnixMilliseconds(expiresAt));
        command.Parameters.AddWithValue("$id", artifactId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TouchArtifactAsync(
        SqliteConnection connection,
        string artifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE artifacts SET last_access_utc = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        command.Parameters.AddWithValue("$id", artifactId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TouchContentAsync(
        SqliteConnection connection,
        string contentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE contents SET last_access_utc = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", ToUnixMilliseconds(now));
        command.Parameters.AddWithValue("$id", contentId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
