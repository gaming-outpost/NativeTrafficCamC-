using System.Globalization;
using System.Text;
using CoastalCommandCenter.Application.Abstractions;
using CoastalCommandCenter.Domain.Cameras;
using Microsoft.Data.Sqlite;

namespace CoastalCommandCenter.Infrastructure.Persistence;

public sealed class SqliteCameraRepository : ICameraRepository
{
    // Bump when adding migrations. InitializeSchema refuses to open a DB whose
    // user_version exceeds this — protects an older binary from misreading a
    // newer schema.
    private const int CurrentSchemaVersion = 1;

    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteCameraRepository(string? databasePath = null)
    {
        _databasePath = databasePath ?? LocalStoragePaths.GetCameraDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        _connectionString = connectionStringBuilder.ToString();
    }

    public string DatabasePath => _databasePath;

    public void InitializeSchema()
    {
        using var connection = OpenConnection();

        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version;";
            var existing = Convert.ToInt32(versionCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (existing > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Database at {_databasePath} has schema version {existing}, " +
                    $"but this binary only understands up to {CurrentSchemaVersion}. " +
                    "Update the app or delete the database to re-seed.");
            }
        }

        using (var command = connection.CreateCommand())
        {
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS providers (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                source_system TEXT NULL,
                base_url TEXT NULL,
                notes TEXT NULL,
                metadata_json TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS cameras (
                id TEXT PRIMARY KEY,
                external_id TEXT NULL,
                provider_id TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                feed_type TEXT NOT NULL,
                name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                region TEXT NULL,
                category TEXT NULL,
                subgroup TEXT NULL,
                location TEXT NULL,
                latitude REAL NULL,
                longitude REAL NULL,
                stream_url TEXT NULL,
                snapshot_url TEXT NULL,
                refresh_seconds INTEGER NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL DEFAULT 0,
                description TEXT NULL,
                notes TEXT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                custom_display_name TEXT NULL,
                health_state TEXT NULL,
                metadata_json TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (provider_id) REFERENCES providers(id)
            );

            CREATE TABLE IF NOT EXISTS camera_groups (
                id TEXT PRIMARY KEY,
                provider_id TEXT NULL,
                group_type TEXT NOT NULL,
                name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                region TEXT NULL,
                category TEXT NULL,
                refresh_seconds INTEGER NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL DEFAULT 0,
                notes TEXT NULL,
                metadata_json TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (provider_id) REFERENCES providers(id)
            );

            CREATE TABLE IF NOT EXISTS group_memberships (
                group_id TEXT NOT NULL,
                camera_id TEXT NOT NULL,
                membership_role TEXT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                PRIMARY KEY (group_id, camera_id),
                FOREIGN KEY (group_id) REFERENCES camera_groups(id) ON DELETE CASCADE,
                FOREIGN KEY (camera_id) REFERENCES cameras(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_cameras_provider_id ON cameras(provider_id);
            CREATE INDEX IF NOT EXISTS idx_cameras_source_kind ON cameras(source_kind);
            CREATE INDEX IF NOT EXISTS idx_cameras_feed_type ON cameras(feed_type);
            CREATE INDEX IF NOT EXISTS idx_cameras_region ON cameras(region);
            CREATE INDEX IF NOT EXISTS idx_group_memberships_group_id ON group_memberships(group_id);
            CREATE INDEX IF NOT EXISTS idx_group_memberships_camera_id ON group_memberships(camera_id);
            """;
        command.ExecuteNonQuery();
        }

        MigrateAddColumnIfMissing(connection, "cameras", "custom_display_name", "TEXT NULL");

        using (var setVersion = connection.CreateCommand())
        {
            // Inline interpolation is safe — value is a private constant integer.
            setVersion.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            setVersion.ExecuteNonQuery();
        }
    }

    public bool HasAnyCameras()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM cameras LIMIT 1);";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public void ReplaceCatalog(CameraCatalogSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        ExecuteNonQuery(connection, transaction, "DELETE FROM group_memberships;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM camera_groups;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM cameras;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM providers;");

        foreach (var provider in seed.Providers)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO providers (
                    id, name, kind, source_system, base_url, notes, metadata_json, created_utc, updated_utc
                ) VALUES (
                    $id, $name, $kind, $source_system, $base_url, $notes, $metadata_json, $created_utc, $updated_utc
                );
                """;
            command.Parameters.AddWithValue("$id", provider.Id);
            command.Parameters.AddWithValue("$name", provider.Name);
            command.Parameters.AddWithValue("$kind", provider.Kind);
            command.Parameters.AddWithValue("$source_system", (object?)provider.SourceSystem ?? DBNull.Value);
            command.Parameters.AddWithValue("$base_url", (object?)provider.BaseUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)provider.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$metadata_json", (object?)provider.MetadataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_utc", FormatUtc(provider.CreatedUtc));
            command.Parameters.AddWithValue("$updated_utc", FormatUtc(provider.UpdatedUtc));
            command.ExecuteNonQuery();
        }

        foreach (var camera in seed.Cameras)
        {
            InsertCamera(connection, transaction, camera);
        }

        foreach (var group in seed.Groups)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO camera_groups (
                    id, provider_id, group_type, name, display_name, region, category, refresh_seconds,
                    is_enabled, sort_order, notes, metadata_json, created_utc, updated_utc
                ) VALUES (
                    $id, $provider_id, $group_type, $name, $display_name, $region, $category, $refresh_seconds,
                    $is_enabled, $sort_order, $notes, $metadata_json, $created_utc, $updated_utc
                );
                """;
            command.Parameters.AddWithValue("$id", group.Id);
            command.Parameters.AddWithValue("$provider_id", (object?)group.ProviderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$group_type", group.GroupType.ToString());
            command.Parameters.AddWithValue("$name", group.Name);
            command.Parameters.AddWithValue("$display_name", group.DisplayName);
            command.Parameters.AddWithValue("$region", (object?)group.Region ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", (object?)group.Category ?? DBNull.Value);
            command.Parameters.AddWithValue("$refresh_seconds", (object?)group.RefreshSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$is_enabled", group.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$sort_order", group.SortOrder);
            command.Parameters.AddWithValue("$notes", (object?)group.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$metadata_json", (object?)group.MetadataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$created_utc", FormatUtc(group.CreatedUtc));
            command.Parameters.AddWithValue("$updated_utc", FormatUtc(group.UpdatedUtc));
            command.ExecuteNonQuery();
        }

        foreach (var membership in seed.GroupMemberships)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO group_memberships (
                    group_id, camera_id, membership_role, sort_order, created_utc
                ) VALUES (
                    $group_id, $camera_id, $membership_role, $sort_order, $created_utc
                );
                """;
            command.Parameters.AddWithValue("$group_id", membership.GroupId);
            command.Parameters.AddWithValue("$camera_id", membership.CameraId);
            command.Parameters.AddWithValue("$membership_role", (object?)membership.MembershipRole ?? DBNull.Value);
            command.Parameters.AddWithValue("$sort_order", membership.SortOrder);
            command.Parameters.AddWithValue("$created_utc", FormatUtc(membership.CreatedUtc));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<CameraRecord> GetAllCameras() => GetCameras(new CameraQuery());

    public IReadOnlyList<CameraRecord> GetCameras(CameraQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        var sql = new StringBuilder();
        sql.AppendLine("SELECT DISTINCT");
        sql.AppendLine("  c.id, c.external_id, c.provider_id, c.source_kind, c.feed_type, c.name, c.display_name,");
        sql.AppendLine("  c.region, c.category, c.subgroup, c.location, c.latitude, c.longitude,");
        sql.AppendLine("  c.stream_url, c.snapshot_url, c.refresh_seconds, c.is_enabled, c.sort_order,");
        sql.AppendLine("  c.description, c.notes, c.is_favorite, c.health_state, c.metadata_json,");
        sql.AppendLine("  c.created_utc, c.updated_utc, c.custom_display_name");
        sql.AppendLine("FROM cameras c");

        if (query.GroupIds.Count > 0)
        {
            sql.AppendLine("INNER JOIN group_memberships gm ON gm.camera_id = c.id");
        }

        var whereClauses = BuildCameraWhereClauses(command, query);
        if (whereClauses.Count > 0)
        {
            sql.AppendLine("WHERE " + string.Join(" AND ", whereClauses));
        }

        sql.AppendLine("ORDER BY c.sort_order, c.display_name, c.name;");
        command.CommandText = sql.ToString();

        using var reader = command.ExecuteReader();
        var cameras = new List<CameraRecord>();
        while (reader.Read())
        {
            cameras.Add(MapCamera(reader));
        }

        return cameras;
    }

    public IReadOnlyList<CameraGroupRecord> GetGroups(CameraGroupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        var whereClauses = new List<string>();
        if (!query.IncludeDisabled)
        {
            whereClauses.Add("g.is_enabled = 1");
        }

        if (query.GroupType.HasValue)
        {
            whereClauses.Add("g.group_type = $group_type");
            command.Parameters.AddWithValue("$group_type", query.GroupType.Value.ToString());
        }

        AddStringInClause(command, whereClauses, "g.id", "$group_id", query.GroupIds);
        AddStringInClause(command, whereClauses, "g.provider_id", "$provider_id", query.ProviderIds);

        var sql = new StringBuilder();
        sql.AppendLine(
            """
            SELECT
              g.id, g.provider_id, g.group_type, g.name, g.display_name, g.region, g.category,
              g.refresh_seconds, g.is_enabled, g.sort_order, g.notes, g.metadata_json,
              g.created_utc, g.updated_utc
            FROM camera_groups g
            """);

        if (whereClauses.Count > 0)
        {
            sql.AppendLine("WHERE " + string.Join(" AND ", whereClauses));
        }

        sql.AppendLine("ORDER BY g.sort_order, g.display_name, g.name;");
        command.CommandText = sql.ToString();

        using var reader = command.ExecuteReader();
        var groups = new List<CameraGroupRecord>();
        while (reader.Read())
        {
            groups.Add(MapGroup(reader));
        }

        return groups;
    }

    public IReadOnlyList<GroupedCameraRecordSet> GetGroupsWithCameras(CameraGroupQuery query)
    {
        var groups = GetGroups(query);
        if (groups.Count == 0)
        {
            return [];
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var groupIds = groups.Select(group => group.Id).ToArray();

        var sql = new StringBuilder();
        sql.AppendLine(
            """
            SELECT
              gm.group_id,
              c.id, c.external_id, c.provider_id, c.source_kind, c.feed_type, c.name, c.display_name,
              c.region, c.category, c.subgroup, c.location, c.latitude, c.longitude,
              c.stream_url, c.snapshot_url, c.refresh_seconds, c.is_enabled, c.sort_order,
              c.description, c.notes, c.is_favorite, c.health_state, c.metadata_json,
              c.created_utc, c.updated_utc, c.custom_display_name
            FROM group_memberships gm
            INNER JOIN cameras c ON c.id = gm.camera_id
            """);

        var whereClauses = new List<string>();
        AddStringInClause(command, whereClauses, "gm.group_id", "$group_id", groupIds);
        if (!query.IncludeDisabled)
        {
            whereClauses.Add("c.is_enabled = 1");
        }

        sql.AppendLine("WHERE " + string.Join(" AND ", whereClauses));
        sql.AppendLine("ORDER BY gm.group_id, gm.sort_order, c.sort_order, c.display_name, c.name;");
        command.CommandText = sql.ToString();

        var lookup = groups.ToDictionary(group => group.Id, _ => new List<CameraRecord>(), StringComparer.OrdinalIgnoreCase);

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var groupId = reader.GetString(0);
                var camera = MapCamera(reader, 1);
                if (lookup.TryGetValue(groupId, out var cameras))
                {
                    cameras.Add(camera);
                }
            }
        }

        return groups
            .Select(group => new GroupedCameraRecordSet(group, lookup[group.Id]))
            .ToList();
    }

    public IReadOnlyList<ProviderRecord> GetProviders()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, kind, source_system, base_url, notes, metadata_json, created_utc, updated_utc
            FROM providers
            ORDER BY name;
            """;

        using var reader = command.ExecuteReader();
        var providers = new List<ProviderRecord>();
        while (reader.Read())
        {
            providers.Add(new ProviderRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = reader.GetString(2),
                SourceSystem = reader.IsDBNull(3) ? null : reader.GetString(3),
                BaseUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                Notes = reader.IsDBNull(5) ? null : reader.GetString(5),
                MetadataJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedUtc = ParseUtc(reader.GetString(7)),
                UpdatedUtc = ParseUtc(reader.GetString(8))
            });
        }

        return providers;
    }

    public void UpsertCamera(CameraRecord camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        ExecuteNonQuery(connection, transaction,
            """
            INSERT INTO cameras (
                id, external_id, provider_id, source_kind, feed_type, name, display_name, region, category,
                subgroup, location, latitude, longitude, stream_url, snapshot_url, refresh_seconds,
                is_enabled, sort_order, description, notes, is_favorite, custom_display_name, health_state,
                metadata_json, created_utc, updated_utc
            ) VALUES (
                $id, $external_id, $provider_id, $source_kind, $feed_type, $name, $display_name, $region, $category,
                $subgroup, $location, $latitude, $longitude, $stream_url, $snapshot_url, $refresh_seconds,
                $is_enabled, $sort_order, $description, $notes, $is_favorite, $custom_display_name, $health_state,
                $metadata_json, $created_utc, $updated_utc
            )
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                provider_id = excluded.provider_id,
                source_kind = excluded.source_kind,
                feed_type = excluded.feed_type,
                name = excluded.name,
                display_name = excluded.display_name,
                region = excluded.region,
                category = excluded.category,
                subgroup = excluded.subgroup,
                location = excluded.location,
                latitude = excluded.latitude,
                longitude = excluded.longitude,
                stream_url = excluded.stream_url,
                snapshot_url = excluded.snapshot_url,
                refresh_seconds = excluded.refresh_seconds,
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                description = excluded.description,
                notes = excluded.notes,
                is_favorite = excluded.is_favorite,
                custom_display_name = excluded.custom_display_name,
                health_state = excluded.health_state,
                metadata_json = excluded.metadata_json,
                updated_utc = excluded.updated_utc;
            """,
            parameters =>
            {
                parameters.AddWithValue("$id", camera.Id);
                parameters.AddWithValue("$external_id", (object?)camera.ExternalId ?? DBNull.Value);
                parameters.AddWithValue("$provider_id", camera.ProviderId);
                parameters.AddWithValue("$source_kind", camera.SourceKind.ToString());
                parameters.AddWithValue("$feed_type", camera.FeedType.ToString());
                parameters.AddWithValue("$name", camera.Name);
                parameters.AddWithValue("$display_name", camera.DisplayName);
                parameters.AddWithValue("$region", (object?)camera.Region ?? DBNull.Value);
                parameters.AddWithValue("$category", (object?)camera.Category ?? DBNull.Value);
                parameters.AddWithValue("$subgroup", (object?)camera.Subgroup ?? DBNull.Value);
                parameters.AddWithValue("$location", (object?)camera.Location ?? DBNull.Value);
                parameters.AddWithValue("$latitude", (object?)camera.Latitude ?? DBNull.Value);
                parameters.AddWithValue("$longitude", (object?)camera.Longitude ?? DBNull.Value);
                parameters.AddWithValue("$stream_url", (object?)camera.StreamUrl ?? DBNull.Value);
                parameters.AddWithValue("$snapshot_url", (object?)camera.SnapshotUrl ?? DBNull.Value);
                parameters.AddWithValue("$refresh_seconds", (object?)camera.RefreshSeconds ?? DBNull.Value);
                parameters.AddWithValue("$is_enabled", camera.IsEnabled ? 1 : 0);
                parameters.AddWithValue("$sort_order", camera.SortOrder);
                parameters.AddWithValue("$description", (object?)camera.Description ?? DBNull.Value);
                parameters.AddWithValue("$notes", (object?)camera.Notes ?? DBNull.Value);
                parameters.AddWithValue("$is_favorite", camera.IsFavorite ? 1 : 0);
                parameters.AddWithValue("$custom_display_name", (object?)camera.CustomDisplayName ?? DBNull.Value);
                parameters.AddWithValue("$health_state", camera.HealthState.ToString());
                parameters.AddWithValue("$metadata_json", (object?)camera.MetadataJson ?? DBNull.Value);
                parameters.AddWithValue("$created_utc", FormatUtc(camera.CreatedUtc));
                parameters.AddWithValue("$updated_utc", FormatUtc(camera.UpdatedUtc));
            });

        transaction.Commit();
    }

    public void UpdateCameraDisplayName(string cameraId, string? customDisplayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE cameras
            SET custom_display_name = $custom_display_name, updated_utc = $updated_utc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$custom_display_name", (object?)customDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_utc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", cameraId);
        command.ExecuteNonQuery();
    }

    public void UpdateCameraFavorite(string cameraId, bool isFavorite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE cameras
            SET is_favorite = $is_favorite, updated_utc = $updated_utc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$is_favorite", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$updated_utc", FormatUtc(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", cameraId);
        command.ExecuteNonQuery();
    }

    private static void MigrateAddColumnIfMissing(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        // SQLite doesn't permit binding identifiers, so validate against a
        // SQL-identifier whitelist before composing DDL. Callers pass literals
        // today, but this prevents accidental future injection.
        EnsureIsSqlIdentifier(table, nameof(table));
        EnsureIsSqlIdentifier(column, nameof(column));

        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        checkCmd.Parameters.AddWithValue("$table", table);
        checkCmd.Parameters.AddWithValue("$column", column);
        var exists = Convert.ToInt32(checkCmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        if (!exists)
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            alterCmd.ExecuteNonQuery();
        }
    }

    private static void EnsureIsSqlIdentifier(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Identifier must not be empty.", paramName);
        foreach (var c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                throw new ArgumentException(
                    $"'{value}' is not a valid SQL identifier (letters, digits, underscore only).",
                    paramName);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static List<string> BuildCameraWhereClauses(SqliteCommand command, CameraQuery query)
    {
        var whereClauses = new List<string>();

        if (!query.IncludeDisabled)
        {
            whereClauses.Add("c.is_enabled = 1");
        }

        if (query.OnlyWithCoordinates)
        {
            whereClauses.Add("c.latitude IS NOT NULL");
            whereClauses.Add("c.longitude IS NOT NULL");
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            whereClauses.Add("c.category = $category");
            command.Parameters.AddWithValue("$category", query.Category.Trim());
        }

        AddStringInClause(command, whereClauses, "c.id", "$camera_id", query.CameraIds);
        AddStringInClause(command, whereClauses, "c.provider_id", "$provider_id", query.ProviderIds);
        AddStringInClause(command, whereClauses, "c.region", "$region", query.Regions);
        AddStringInClause(command, whereClauses, "gm.group_id", "$group_id", query.GroupIds);
        AddEnumInClause(command, whereClauses, "c.source_kind", "$source_kind", query.SourceKinds);
        AddEnumInClause(command, whereClauses, "c.feed_type", "$feed_type", query.FeedTypes);

        return whereClauses;
    }

    private static void AddStringInClause(
        SqliteCommand command,
        ICollection<string> whereClauses,
        string columnName,
        string parameterPrefix,
        IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var parameterNames = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var parameterName = $"{parameterPrefix}_{index++}";
            command.Parameters.AddWithValue(parameterName, value);
            parameterNames.Add(parameterName);
        }

        if (parameterNames.Count > 0)
        {
            whereClauses.Add($"{columnName} IN ({string.Join(", ", parameterNames)})");
        }
    }

    private static void AddEnumInClause<TEnum>(
        SqliteCommand command,
        ICollection<string> whereClauses,
        string columnName,
        string parameterPrefix,
        IReadOnlyCollection<TEnum> values)
        where TEnum : struct, Enum
    {
        if (values.Count == 0)
        {
            return;
        }

        var parameterNames = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values)
        {
            var parameterName = $"{parameterPrefix}_{index++}";
            command.Parameters.AddWithValue(parameterName, value.ToString());
            parameterNames.Add(parameterName);
        }

        whereClauses.Add($"{columnName} IN ({string.Join(", ", parameterNames)})");
    }

    private static CameraRecord MapCamera(SqliteDataReader reader, int offset = 0)
    {
        return new CameraRecord
        {
            Id = reader.GetString(offset + 0),
            ExternalId = reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
            ProviderId = reader.GetString(offset + 2),
            SourceKind = ParseEnum<CameraSourceKind>(reader.GetString(offset + 3), CameraSourceKind.PublicLiveFeed),
            FeedType = ParseEnum<CameraFeedType>(reader.GetString(offset + 4), CameraFeedType.Unknown),
            Name = reader.GetString(offset + 5),
            DisplayName = reader.GetString(offset + 6),
            Region = reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
            Category = reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
            Subgroup = reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9),
            Location = reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10),
            Latitude = reader.IsDBNull(offset + 11) ? null : reader.GetDouble(offset + 11),
            Longitude = reader.IsDBNull(offset + 12) ? null : reader.GetDouble(offset + 12),
            StreamUrl = reader.IsDBNull(offset + 13) ? null : reader.GetString(offset + 13),
            SnapshotUrl = reader.IsDBNull(offset + 14) ? null : reader.GetString(offset + 14),
            RefreshSeconds = reader.IsDBNull(offset + 15) ? null : reader.GetInt32(offset + 15),
            IsEnabled = reader.GetInt32(offset + 16) == 1,
            SortOrder = reader.GetInt32(offset + 17),
            Description = reader.IsDBNull(offset + 18) ? null : reader.GetString(offset + 18),
            Notes = reader.IsDBNull(offset + 19) ? null : reader.GetString(offset + 19),
            IsFavorite = reader.GetInt32(offset + 20) == 1,
            HealthState = ParseEnum<CameraHealthState>(
                reader.IsDBNull(offset + 21) ? nameof(CameraHealthState.Unknown) : reader.GetString(offset + 21),
                CameraHealthState.Unknown),
            MetadataJson = reader.IsDBNull(offset + 22) ? null : reader.GetString(offset + 22),
            CreatedUtc = ParseUtc(reader.GetString(offset + 23)),
            UpdatedUtc = ParseUtc(reader.GetString(offset + 24)),
            CustomDisplayName = reader.FieldCount > offset + 25 && !reader.IsDBNull(offset + 25)
                ? reader.GetString(offset + 25)
                : null
        };
    }

    private static CameraGroupRecord MapGroup(SqliteDataReader reader)
    {
        return new CameraGroupRecord
        {
            Id = reader.GetString(0),
            ProviderId = reader.IsDBNull(1) ? null : reader.GetString(1),
            GroupType = ParseEnum<CameraGroupType>(reader.GetString(2), CameraGroupType.Playlist),
            Name = reader.GetString(3),
            DisplayName = reader.GetString(4),
            Region = reader.IsDBNull(5) ? null : reader.GetString(5),
            Category = reader.IsDBNull(6) ? null : reader.GetString(6),
            RefreshSeconds = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            IsEnabled = reader.GetInt32(8) == 1,
            SortOrder = reader.GetInt32(9),
            Notes = reader.IsDBNull(10) ? null : reader.GetString(10),
            MetadataJson = reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedUtc = ParseUtc(reader.GetString(12)),
            UpdatedUtc = ParseUtc(reader.GetString(13))
        };
    }

    private static TEnum ParseEnum<TEnum>(string rawValue, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(rawValue, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static DateTime ParseUtc(string rawValue)
    {
        return DateTime.TryParse(
            rawValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;
    }

    private static string FormatUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value.ToString("O", CultureInfo.InvariantCulture)
            : value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Action<SqliteParameterCollection>? configureParameters = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        configureParameters?.Invoke(command.Parameters);
        command.ExecuteNonQuery();
    }

    private static void InsertCamera(SqliteConnection connection, SqliteTransaction transaction, CameraRecord camera)
    {
        ExecuteNonQuery(connection, transaction,
            """
            INSERT INTO cameras (
                id, external_id, provider_id, source_kind, feed_type, name, display_name, region, category,
                subgroup, location, latitude, longitude, stream_url, snapshot_url, refresh_seconds,
                is_enabled, sort_order, description, notes, is_favorite, custom_display_name, health_state,
                metadata_json, created_utc, updated_utc
            ) VALUES (
                $id, $external_id, $provider_id, $source_kind, $feed_type, $name, $display_name, $region, $category,
                $subgroup, $location, $latitude, $longitude, $stream_url, $snapshot_url, $refresh_seconds,
                $is_enabled, $sort_order, $description, $notes, $is_favorite, $custom_display_name, $health_state,
                $metadata_json, $created_utc, $updated_utc
            );
            """,
            parameters =>
            {
                parameters.AddWithValue("$id", camera.Id);
                parameters.AddWithValue("$external_id", (object?)camera.ExternalId ?? DBNull.Value);
                parameters.AddWithValue("$provider_id", camera.ProviderId);
                parameters.AddWithValue("$source_kind", camera.SourceKind.ToString());
                parameters.AddWithValue("$feed_type", camera.FeedType.ToString());
                parameters.AddWithValue("$name", camera.Name);
                parameters.AddWithValue("$display_name", camera.DisplayName);
                parameters.AddWithValue("$region", (object?)camera.Region ?? DBNull.Value);
                parameters.AddWithValue("$category", (object?)camera.Category ?? DBNull.Value);
                parameters.AddWithValue("$subgroup", (object?)camera.Subgroup ?? DBNull.Value);
                parameters.AddWithValue("$location", (object?)camera.Location ?? DBNull.Value);
                parameters.AddWithValue("$latitude", (object?)camera.Latitude ?? DBNull.Value);
                parameters.AddWithValue("$longitude", (object?)camera.Longitude ?? DBNull.Value);
                parameters.AddWithValue("$stream_url", (object?)camera.StreamUrl ?? DBNull.Value);
                parameters.AddWithValue("$snapshot_url", (object?)camera.SnapshotUrl ?? DBNull.Value);
                parameters.AddWithValue("$refresh_seconds", (object?)camera.RefreshSeconds ?? DBNull.Value);
                parameters.AddWithValue("$is_enabled", camera.IsEnabled ? 1 : 0);
                parameters.AddWithValue("$sort_order", camera.SortOrder);
                parameters.AddWithValue("$description", (object?)camera.Description ?? DBNull.Value);
                parameters.AddWithValue("$notes", (object?)camera.Notes ?? DBNull.Value);
                parameters.AddWithValue("$is_favorite", camera.IsFavorite ? 1 : 0);
                parameters.AddWithValue("$custom_display_name", (object?)camera.CustomDisplayName ?? DBNull.Value);
                parameters.AddWithValue("$health_state", camera.HealthState.ToString());
                parameters.AddWithValue("$metadata_json", (object?)camera.MetadataJson ?? DBNull.Value);
                parameters.AddWithValue("$created_utc", FormatUtc(camera.CreatedUtc));
                parameters.AddWithValue("$updated_utc", FormatUtc(camera.UpdatedUtc));
            });
    }
}
