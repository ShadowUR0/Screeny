using Microsoft.Data.Sqlite;
using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using SQLitePCL;
using System.Diagnostics;
using System.Linq;

namespace ScreenTimeTracker.Services
{
    public class DatabaseService : IDisposable
    {
        private const int LatestSchemaVersion = 3;
        private const int BusyTimeoutMilliseconds = 5000;
        private const string JournalMode = "WAL";
        private const string FinalizedSliceIndexName = "idx_app_usage_finalized_slice_unique";
        private readonly string _dbPath;
        private static string DefaultDatabasePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTimeTracker", "screentime.db");
        private readonly SqliteConnection _connection;
        private bool _initializedSuccessfully = false;
        private bool _disposed = false;

        public DatabaseService()
            : this(DefaultDatabasePath)
        {
        }

        internal DatabaseService(string dbPath)
        {
            _dbPath = dbPath;

            try
            {
                var dataDirectory = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dataDirectory) && !Directory.Exists(dataDirectory))
                {
                    Directory.CreateDirectory(dataDirectory);
                    Debug.WriteLine($"Created database directory: {dataDirectory}");
                }

                Debug.WriteLine($"Database path: {_dbPath}");

                // Keep the startup safety check lightweight. A full integrity_check scans every
                // index and was paid on every launch even though SQLite/WAL already provides strong
                // crash consistency. quick_check catches structural corruption with much less work.
                if (File.Exists(_dbPath) && !CheckDatabaseIntegrity(_dbPath))
                {
                    HandleCorruptedDatabase(_dbPath);
                }

                var connectionString = CreateConnectionString(_dbPath);
                Debug.WriteLine($"Using connection string: {connectionString}");

                _connection = new SqliteConnection(connectionString);
                InitializeDatabase();

                Debug.WriteLine($"Database file exists after initialization: {File.Exists(_dbPath)}");
                _initializedSuccessfully = true;
            }
            catch (Exception ex)
            {
                _connection = new SqliteConnection();
                _initializedSuccessfully = false;
                Debug.WriteLine($"Database initialization failed; entering degraded state: {ex.Message}");
            }
        }

        private string CreateConnectionString(string dataSource)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = dataSource,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString();
        }

        private SqliteConnection CreateConnection()
        {
            return new SqliteConnection(_connection.ConnectionString);
        }

        private static void OpenConnection(SqliteConnection connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // WAL + NORMAL is a good fit for disposable screen-time telemetry: transactions
            // remain atomic and consistent while avoiding an unnecessary fsync for every tiny
            // finalized slice. temp_store=MEMORY also keeps report scratch work off disk.
            command.CommandText = $@"
                PRAGMA busy_timeout = {BusyTimeoutMilliseconds};
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;";
            command.ExecuteNonQuery();
        }

        private bool CheckDatabaseIntegrity(string dbPath)
        {
            Debug.WriteLine($"Quick-checking database integrity for: {dbPath}");
            if (!File.Exists(dbPath)) return true;

            var checkConnectionString = new SqliteConnectionStringBuilder(CreateConnectionString(dbPath))
            {
                Pooling = false
            }.ToString();

            using var checkConnection = new SqliteConnection(checkConnectionString);
            try
            {
                OpenConnection(checkConnection);
                using var command = checkConnection.CreateCommand();
                command.CommandText = "PRAGMA quick_check(1);";
                var result = command.ExecuteScalar()?.ToString();
                Debug.WriteLine($"PRAGMA quick_check result: {result}");
                return result != null && result.Equals("ok", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // A transient lock, antivirus scan or filesystem hiccup is not proof of
                // corruption. Do not rename/delete the user's history on an inconclusive check;
                // normal initialization below will still fail safely if SQLite cannot open it.
                Debug.WriteLine($"Database quick_check was inconclusive: {ex.Message}");
                SqliteConnection.ClearPool(checkConnection);
                return true;
            }
        }

        private void HandleCorruptedDatabase(string dbPath)
        {
            string backupPath = dbPath + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss");
            Debug.WriteLine($"Database integrity check failed! Moving corrupted file to: {backupPath}");
            try
            {
                SqliteConnection.ClearAllPools();
                File.Move(dbPath, backupPath);
                Debug.WriteLine("Successfully moved corrupted database file.");
            }
            catch (IOException ioEx)
            {
                Debug.WriteLine($"Error moving corrupted database file (IOException): {ioEx.Message}");
                try
                {
                    File.Delete(dbPath);
                    Debug.WriteLine("Successfully deleted corrupted database file after move failed.");
                }
                catch (Exception delEx)
                {
                    Debug.WriteLine($"CRITICAL ERROR: Failed to move AND delete corrupted database file: {delEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL ERROR: Failed to move corrupted database file: {ex.Message}");
            }
        }

        public bool IsDatabaseInitialized() => _initializedSuccessfully;

        internal int SchemaVersion
        {
            get
            {
                if (!_initializedSuccessfully)
                {
                    return 0;
                }

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                var result = command.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        internal string CurrentJournalMode
        {
            get
            {
                if (!_initializedSuccessfully)
                {
                    return string.Empty;
                }

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode;";
                return command.ExecuteScalar()?.ToString() ?? string.Empty;
            }
        }

        public bool IsFirstRun()
        {
            if (!_initializedSuccessfully)
                return true;

            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);
                Debug.WriteLine("IsFirstRun: Opened database connection");

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM app_usage LIMIT 1);";
                var result = Convert.ToInt32(command.ExecuteScalar());

                Debug.WriteLine($"IsFirstRun: Has records = {result != 0}");
                return result == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IsFirstRun failed: {ex.Message}");
                return true;
            }
        }

        private void InitializeDatabase()
        {
            try
            {
                OpenConnection(_connection);
                ConfigureDatabasePragmas();

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS app_usage (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        date TEXT NOT NULL,
                        process_name TEXT NOT NULL,
                        app_name TEXT,
                        executable_path TEXT,
                        start_time TEXT NOT NULL,
                        end_time TEXT,
                        duration INTEGER,
                        is_focused INTEGER DEFAULT 0,
                        last_updated TEXT
                    );";
                    command.ExecuteNonQuery();
                }

                CheckAndPerformMigrations();

                if (HasLatestSchema())
                {
                    SetDatabaseVersion(LatestSchemaVersion);
                }

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "CREATE INDEX IF NOT EXISTS idx_app_usage_date ON app_usage(date);";
                    command.ExecuteNonQuery();
                }

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "CREATE INDEX IF NOT EXISTS idx_app_usage_process_name ON app_usage(process_name);";
                    command.ExecuteNonQuery();
                }

                CreateFinalizedSliceUniqueIndex();

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"
                        CREATE VIEW IF NOT EXISTS vw_daily_app_usage AS
                        SELECT
                            date,
                            process_name,
                            SUM(duration) AS total_duration_ms
                        FROM app_usage
                        GROUP BY date, process_name;";
                    command.ExecuteNonQuery();
                }

                Debug.WriteLine("Database initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing database: {ex.Message}");
                throw;
            }
            finally
            {
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
        }

        private void CheckAndPerformMigrations()
        {
            try
            {
                int currentVersion = GetDatabaseVersion();
                Debug.WriteLine($"Current database version: {currentVersion}");

                if (currentVersion < 1)
                {
                    MigrateToV1();
                    currentVersion = GetDatabaseVersion();
                }

                if (currentVersion < 2)
                {
                    MigrateToV2();
                    currentVersion = GetDatabaseVersion();
                }

                if (currentVersion < 3)
                {
                    MigrateToV3();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during database migration: {ex.Message}");
            }
        }

        private int GetDatabaseVersion()
        {
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                var result = command.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void SetDatabaseVersion(int version)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {version};";
            command.ExecuteNonQuery();
        }

        private void ConfigureDatabasePragmas()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA journal_mode = {JournalMode};";
            command.ExecuteScalar();
        }

        private bool FinalizedSliceIndexExists()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                SELECT 1
                FROM sqlite_master
                WHERE type = 'index' AND name = @IndexName
                LIMIT 1;";
            command.Parameters.AddWithValue("@IndexName", FinalizedSliceIndexName);
            return command.ExecuteScalar() != null;
        }

        private void CreateFinalizedSliceUniqueIndex()
        {
            // Once the unique index exists, duplicates cannot accumulate. The previous code
            // still ran a whole-table deduplication DELETE on every launch, making startup
            // increasingly expensive as history grew.
            if (FinalizedSliceIndexExists())
                return;

            RemoveExactDuplicateFinalizedSlices();

            using var command = _connection.CreateCommand();
            command.CommandText = $@"
                CREATE UNIQUE INDEX IF NOT EXISTS {FinalizedSliceIndexName}
                ON app_usage(date, process_name, start_time, end_time)
                WHERE end_time IS NOT NULL;";
            command.ExecuteNonQuery();
        }

        private void RemoveExactDuplicateFinalizedSlices()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM app_usage
                WHERE end_time IS NOT NULL
                  AND id NOT IN (
                      SELECT MIN(id)
                      FROM app_usage
                      WHERE end_time IS NOT NULL
                      GROUP BY date, process_name, start_time, end_time
                  );";
            command.ExecuteNonQuery();
        }

        private bool ColumnExists(string tableName, string columnName)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var currentColumnName = reader.GetString(1);
                if (currentColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasLatestSchema()
        {
            return ColumnExists("app_usage", "date")
                && ColumnExists("app_usage", "is_focused")
                && ColumnExists("app_usage", "last_updated")
                && ColumnExists("app_usage", "executable_path");
        }

        private void MigrateToV1()
        {
            Debug.WriteLine("Performing migration to version 1");

            try
            {
                bool hasDateColumn = ColumnExists("app_usage", "date");

                if (!hasDateColumn)
                {
                    Debug.WriteLine("Adding date column to app_usage table");

                    using (var transaction = _connection.BeginTransaction())
                    {
                        try
                        {
                            using (var command = _connection.CreateCommand())
                            {
                                command.Transaction = transaction;
                                command.CommandText = "ALTER TABLE app_usage ADD COLUMN date TEXT;";
                                command.ExecuteNonQuery();
                            }

                            using (var command = _connection.CreateCommand())
                            {
                                command.Transaction = transaction;
                                command.CommandText = @"
                                    UPDATE app_usage
                                    SET date = substr(start_time, 1, 10)
                                    WHERE date IS NULL;";
                                command.ExecuteNonQuery();
                            }

                            transaction.Commit();
                            Debug.WriteLine("Migration completed successfully");
                        }
                        catch (Exception ex)
                        {
                            transaction.Rollback();
                            Debug.WriteLine($"Migration failed: {ex.Message}");
                            throw;
                        }
                    }

                    using (var command = _connection.CreateCommand())
                    {
                        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_app_usage_date ON app_usage(date);";
                        command.ExecuteNonQuery();
                    }
                }

                SetDatabaseVersion(1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during migration to V1: {ex.Message}");
            }
        }

        private void MigrateToV2()
        {
            Debug.WriteLine("Performing migration to version 2");
            try
            {
                bool hasIsFocusedColumn = ColumnExists("app_usage", "is_focused");
                bool hasLastUpdatedColumn = ColumnExists("app_usage", "last_updated");

                using (var transaction = _connection.BeginTransaction())
                {
                    try
                    {
                        if (!hasIsFocusedColumn)
                        {
                            using var command = _connection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = "ALTER TABLE app_usage ADD COLUMN is_focused INTEGER DEFAULT 0;";
                            command.ExecuteNonQuery();
                            Debug.WriteLine("Added is_focused column to app_usage table.");
                        }

                        if (!hasLastUpdatedColumn)
                        {
                            using var command = _connection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = "ALTER TABLE app_usage ADD COLUMN last_updated TEXT;";
                            command.ExecuteNonQuery();
                            Debug.WriteLine("Added last_updated column to app_usage table.");
                        }

                        transaction.Commit();
                        Debug.WriteLine("Migration to V2 (columns) completed successfully.");
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        Debug.WriteLine($"Migration to V2 (columns) failed: {ex.Message}");
                        return;
                    }
                }

                SetDatabaseVersion(2);
                Debug.WriteLine("Database user_version set to 2.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during migration to V2: {ex.Message}");
            }
        }

        private void MigrateToV3()
        {
            Debug.WriteLine("Performing migration to version 3");
            try
            {
                if (!ColumnExists("app_usage", "executable_path"))
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "ALTER TABLE app_usage ADD COLUMN executable_path TEXT;";
                    command.ExecuteNonQuery();
                }

                SetDatabaseVersion(3);
                Debug.WriteLine("Database user_version set to 3.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during migration to V3: {ex.Message}");
            }
        }

        private DateTime ValidateDate(DateTime date)
        {
            if (date > DateTime.Now)
            {
                Debug.WriteLine($"WARNING: Future date detected ({date:yyyy-MM-dd}), using current date instead.");
                return DateTime.Now;
            }
            return date;
        }

        private (DateTime validatedStartTime, DateTime? validatedEndTime, string dateString, long durationMs)
            ValidateRecordData(AppUsageRecord record)
        {
            DateTime validatedStartTime = ValidateDate(record.StartTime);
            DateTime? validatedEndTime = record.EndTime.HasValue ? ValidateDate(record.EndTime.Value) : null;
            string dateString = validatedStartTime.ToString("yyyy-MM-dd");

            long durationMs = Math.Max(0L, (long)record.Duration.TotalMilliseconds);

            if (validatedEndTime.HasValue)
            {
                var calculatedDuration = (validatedEndTime.Value - validatedStartTime).TotalMilliseconds;
                if (calculatedDuration >= 0 && calculatedDuration < durationMs)
                {
                    durationMs = (long)calculatedDuration;
                }
            }

            return (validatedStartTime, validatedEndTime, dateString, durationMs);
        }

        public bool SaveRecord(AppUsageRecord record)
        {
            if (!_initializedSuccessfully)
            {
                return false;
            }

            try
            {
                var (validatedStartTime, validatedEndTime, dateString, durationMs) = ValidateRecordData(record);

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var transaction = connection.BeginTransaction();

                string insertSql = @"INSERT INTO app_usage (date, process_name, app_name, executable_path, start_time, end_time, duration, is_focused, last_updated)
                                   VALUES (@Date, @ProcessName, @ApplicationName, @ExecutablePath, @StartTime, @EndTime, @Duration, @IsFocused, @LastUpdated);
                                   SELECT last_insert_rowid();";

                using var command = new SqliteCommand(insertSql, connection, transaction);
                command.Parameters.AddWithValue("@Date", dateString);
                command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                command.Parameters.AddWithValue("@ExecutablePath", string.IsNullOrWhiteSpace(record.ExecutablePath) ? (object)DBNull.Value : record.ExecutablePath);
                command.Parameters.AddWithValue("@StartTime", validatedStartTime.ToString("o"));
                command.Parameters.AddWithValue("@EndTime", validatedEndTime?.ToString("o") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Duration", durationMs);
                command.Parameters.AddWithValue("@IsFocused", record.IsFocused ? 1 : 0);
                command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));

                var result = command.ExecuteScalar();

                if (result != null)
                {
                    record.Id = (int)Convert.ToInt64(result);
                    transaction.Commit();
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving record: {ex.Message}");
                return false;
            }
        }

        public bool SaveSlice(UsageSlice slice)
        {
            var result = SaveSliceWithResult(slice);
            return result == PersistenceResult.Saved || result == PersistenceResult.DuplicateIgnored;
        }

        public PersistenceResult SaveSliceWithResult(UsageSlice slice)
        {
            if (!_initializedSuccessfully)
            {
                return PersistenceResult.FatalFailure;
            }

            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);
                using var transaction = connection.BeginTransaction();

                // The unique finalized-slice index is the source of truth for deduplication.
                // INSERT OR IGNORE collapses the old SELECT-then-INSERT pair into one indexed
                // write and removes a race between the duplicate probe and insertion.
                const string insertSql = @"INSERT OR IGNORE INTO app_usage
                                   (date, process_name, app_name, executable_path, start_time, end_time, duration, is_focused, last_updated)
                                   VALUES (@Date, @ProcessName, @ApplicationName, @ExecutablePath, @StartTime, @EndTime, @Duration, @IsFocused, @LastUpdated);";

                using var command = new SqliteCommand(insertSql, connection, transaction);
                command.Parameters.AddWithValue("@Date", slice.Date.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("@ProcessName", slice.ProcessName);
                command.Parameters.AddWithValue("@ApplicationName", slice.ApplicationName);
                command.Parameters.AddWithValue("@ExecutablePath", string.IsNullOrWhiteSpace(slice.ExecutablePath) ? (object)DBNull.Value : slice.ExecutablePath);
                command.Parameters.AddWithValue("@StartTime", slice.StartTime.ToString("o"));
                command.Parameters.AddWithValue("@EndTime", slice.EndTime.ToString("o"));
                command.Parameters.AddWithValue("@Duration", Math.Max(0L, (long)slice.Duration.TotalMilliseconds));
                command.Parameters.AddWithValue("@IsFocused", 0);
                command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));

                int rowsInserted = command.ExecuteNonQuery();
                transaction.Commit();
                return rowsInserted > 0 ? PersistenceResult.Saved : PersistenceResult.DuplicateIgnored;
            }
            catch (SqliteException ex) when (IsRetryableSqliteException(ex))
            {
                Debug.WriteLine($"Retryable error saving usage slice: {ex.Message}");
                return PersistenceResult.RetryableFailure;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving usage slice: {ex.Message}");
                return PersistenceResult.FatalFailure;
            }
        }

        private static bool IsRetryableSqliteException(SqliteException ex)
        {
            return ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;
        }

        public void UpdateRecord(AppUsageRecord record)
        {
            if (!_initializedSuccessfully)
            {
                return;
            }

            try
            {
                var (validatedStartTime, validatedEndTime, dateString, durationMs) = ValidateRecordData(record);

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var transaction = connection.BeginTransaction();

                string updateSql = @"UPDATE app_usage SET date = @Date, process_name = @ProcessName, app_name = @ApplicationName,
                                   executable_path = @ExecutablePath, end_time = @EndTime, duration = @Duration, is_focused = @IsFocused, last_updated = @LastUpdated
                                   WHERE id = @Id;";

                using var command = new SqliteCommand(updateSql, connection, transaction);
                command.Parameters.AddWithValue("@Id", record.Id);
                command.Parameters.AddWithValue("@Date", dateString);
                command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                command.Parameters.AddWithValue("@ExecutablePath", string.IsNullOrWhiteSpace(record.ExecutablePath) ? (object)DBNull.Value : record.ExecutablePath);
                command.Parameters.AddWithValue("@EndTime", validatedEndTime?.ToString("o") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Duration", durationMs);
                command.Parameters.AddWithValue("@IsFocused", record.IsFocused ? 1 : 0);
                command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));

                int rowsAffected = command.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    transaction.Commit();
                }
                else
                {
                    Debug.WriteLine($"Update failed, record with ID {record.Id} not found.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating record: {ex.Message}");
            }
        }

        public List<AppUsageRecord> GetRecordsForDate(DateTime date)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping get records operation");
                return new List<AppUsageRecord>();
            }

            try
            {
                var dateString = date.ToString("yyyy-MM-dd");
                Debug.WriteLine($"Getting records for date: {dateString}");

                using var connection = CreateConnection();
                OpenConnection(connection);

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT id, process_name, app_name, executable_path, start_time, end_time, duration, is_focused, last_updated
                    FROM app_usage
                    WHERE date = $date
                    ORDER BY process_name, start_time;";
                command.Parameters.AddWithValue("$date", dateString);

                using var reader = command.ExecuteReader();
                var processGroups = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

                while (reader.Read())
                {
                    try
                    {
                        string processName = reader.GetString(reader.GetOrdinal("process_name"));
                        var dbStartTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("start_time")));
                        long durationMs = !reader.IsDBNull(reader.GetOrdinal("duration")) ? reader.GetInt64(reader.GetOrdinal("duration")) : 0;
                        bool isFocused = !reader.IsDBNull(reader.GetOrdinal("is_focused")) && reader.GetInt32(reader.GetOrdinal("is_focused")) == 1;
                        DateTime? lastUpdated = !reader.IsDBNull(reader.GetOrdinal("last_updated")) ? DateTime.Parse(reader.GetString(reader.GetOrdinal("last_updated"))) : null;
                        string executablePath = !reader.IsDBNull(reader.GetOrdinal("executable_path")) ? reader.GetString(reader.GetOrdinal("executable_path")) : string.Empty;

                        if (processGroups.TryGetValue(processName, out var existingRecord))
                        {
                            existingRecord._accumulatedDuration += TimeSpan.FromMilliseconds(durationMs);
                            if (string.IsNullOrWhiteSpace(existingRecord.ExecutablePath) && !string.IsNullOrWhiteSpace(executablePath))
                                existingRecord.ExecutablePath = executablePath;
                            if (lastUpdated.HasValue && (!existingRecord.LastUpdated.HasValue || lastUpdated.Value > existingRecord.LastUpdated.Value))
                            {
                                existingRecord.LastUpdated = lastUpdated;
                                existingRecord.IsFocused = isFocused;
                            }
                            if (dbStartTime < existingRecord.StartTime) existingRecord.StartTime = dbStartTime;
                            if (reader.IsDBNull(reader.GetOrdinal("end_time"))) existingRecord.EndTime = null;
                            else if (existingRecord.EndTime.HasValue && DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time"))) > existingRecord.EndTime.Value)
                            {
                                existingRecord.EndTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time")));
                            }
                        }
                        else
                        {
                            var record = new AppUsageRecord
                            {
                                Id = reader.GetInt32(reader.GetOrdinal("id")),
                                ProcessName = processName,
                                ApplicationName = !reader.IsDBNull(reader.GetOrdinal("app_name")) ? reader.GetString(reader.GetOrdinal("app_name")) : processName,
                                ExecutablePath = executablePath,
                                Date = date,
                                StartTime = dbStartTime,
                                IsFocused = isFocused,
                                LastUpdated = lastUpdated
                            };

                            if (!reader.IsDBNull(reader.GetOrdinal("end_time")))
                            {
                                record.EndTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time")));
                            }

                            record._accumulatedDuration = TimeSpan.FromMilliseconds(durationMs);
                            processGroups[processName] = record;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Debug.WriteLine($"Error parsing record in GetRecordsForDate: {parseEx.Message}");
                    }
                }

                return processGroups.Values.ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting records for date: {ex.Message}");
                return new List<AppUsageRecord>();
            }
        }

        public List<(string ProcessName, TimeSpan TotalDuration)> GetUsageReportForDateRange(DateTime startDate, DateTime endDate)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping get usage report operation");
                return new List<(string, TimeSpan)>();
            }

            var results = new List<(string, TimeSpan)>();
            string startDateString = startDate.ToString("yyyy-MM-dd");
            string endDateString = endDate.ToString("yyyy-MM-dd");

            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);
                const string sql = @"
                    SELECT process_name, COALESCE(SUM(duration), 0) AS total_ms
                    FROM app_usage
                    WHERE date >= @StartDate AND date <= @EndDate
                    GROUP BY process_name;";

                using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@StartDate", startDateString);
                cmd.Parameters.AddWithValue("@EndDate", endDateString);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var processName = reader.GetString(0);
                    long totalMs = reader.GetInt64(1);
                    results.Add((processName, TimeSpan.FromMilliseconds(totalMs)));
                }

                return results.OrderByDescending(r => r.Item2).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error retrieving usage report: {ex.Message}");
                throw;
            }
        }

        public void CleanupExpiredRecords(int daysToKeep)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping cleanup operation");
                return;
            }

            SqliteTransaction? transaction = null;
            try
            {
                DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep).Date;
                string cutoffDateString = cutoffDate.ToString("yyyy-MM-dd");

                using var connection = CreateConnection();
                OpenConnection(connection);
                transaction = connection.BeginTransaction();

                const string deleteSql = "DELETE FROM app_usage WHERE date < @CutoffDate;";
                int rowsDeleted;

                using (var command = new SqliteCommand(deleteSql, connection, transaction))
                {
                    command.Parameters.AddWithValue("@CutoffDate", cutoffDateString);
                    rowsDeleted = command.ExecuteNonQuery();
                }

                transaction.Commit();

                if (rowsDeleted > 0)
                {
                    // Avoid a blocking whole-file VACUUM after routine retention cleanup.
                    // SQLite can reuse freed pages; optimize refreshes planner statistics cheaply.
                    using var optimizeCommand = connection.CreateCommand();
                    optimizeCommand.CommandText = "PRAGMA optimize;";
                    optimizeCommand.ExecuteNonQuery();
                }
            }
            catch (Exception)
            {
                try
                {
                    transaction?.Rollback();
                }
                catch (Exception)
                {
                }
            }
        }

        public bool PerformDatabaseMaintenance()
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping maintenance");
                return false;
            }

            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);

                bool integrityPassed;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA quick_check(1);";
                    var resultObj = command.ExecuteScalar();
                    string? result = resultObj as string;
                    integrityPassed = result != null && result.Equals("ok", StringComparison.OrdinalIgnoreCase);
                    Debug.WriteLine($"Database quick_check: {result ?? "null"}");
                }

                using (var command = connection.CreateCommand())
                {
                    // optimize already runs targeted ANALYZE work when SQLite determines it is
                    // useful, so a second unconditional ANALYZE pass only duplicates I/O.
                    command.CommandText = "PRAGMA optimize;";
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    // PASSIVE does not block readers/writers; it merely trims accumulated WAL
                    // pages when possible.
                    command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                    command.ExecuteNonQuery();
                }

                return integrityPassed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during database maintenance: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _connection?.Dispose();
                }
                _disposed = true;
            }
        }

        public List<AppUsageRecord> GetRawRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            try
            {
                if (!IsDatabaseInitialized())
                {
                    Debug.WriteLine("ERROR: Database not initialized in GetRawRecordsForDateRange");
                    return new List<AppUsageRecord>();
                }

                string startDateStr = startDate.ToString("yyyy-MM-dd");
                string endDateStr = endDate.ToString("yyyy-MM-dd");

                const string sql = @"
                    SELECT id, process_name, app_name, executable_path, start_time, end_time, duration, is_focused, last_updated, date
                    FROM app_usage
                    WHERE date >= @StartDate AND date <= @EndDate
                    ORDER BY date ASC, start_time ASC, process_name ASC;";

                var records = new List<AppUsageRecord>();

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@StartDate", startDateStr);
                command.Parameters.AddWithValue("@EndDate", endDateStr);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    try
                    {
                        var record = new AppUsageRecord
                        {
                            Id = reader.GetInt32(reader.GetOrdinal("id")),
                            ProcessName = reader.GetString(reader.GetOrdinal("process_name")),
                            ApplicationName = !reader.IsDBNull(reader.GetOrdinal("app_name")) ? reader.GetString(reader.GetOrdinal("app_name")) : reader.GetString(reader.GetOrdinal("process_name")),
                            ExecutablePath = !reader.IsDBNull(reader.GetOrdinal("executable_path")) ? reader.GetString(reader.GetOrdinal("executable_path")) : string.Empty,
                            StartTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("start_time"))),
                            EndTime = !reader.IsDBNull(reader.GetOrdinal("end_time")) ? DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time"))) : (DateTime?)null,
                            _accumulatedDuration = TimeSpan.FromMilliseconds(!reader.IsDBNull(reader.GetOrdinal("duration")) ? reader.GetInt64(reader.GetOrdinal("duration")) : 0),
                            IsFocused = !reader.IsDBNull(reader.GetOrdinal("is_focused")) && reader.GetInt32(reader.GetOrdinal("is_focused")) == 1,
                            LastUpdated = !reader.IsDBNull(reader.GetOrdinal("last_updated")) ? DateTime.Parse(reader.GetString(reader.GetOrdinal("last_updated"))) : (DateTime?)null,
                            Date = DateTime.Parse(reader.GetString(reader.GetOrdinal("date")))
                        };
                        records.Add(record);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error parsing record in GetRawRecordsForDateRange: {ex.Message} on row ID {reader.GetInt32(reader.GetOrdinal("id"))}");
                    }
                }

                return records;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in GetRawRecordsForDateRange: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
                return new List<AppUsageRecord>();
            }
        }

        public List<AppUsageRecord> GetRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            return GetRawRecordsForDateRange(startDate, endDate);
        }

        public bool WipeDatabase()
        {
            try
            {
                if (!_initializedSuccessfully)
                {
                    return false;
                }

                using var connection = CreateConnection();
                OpenConnection(connection);

                using (var tx = connection.BeginTransaction())
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM app_usage;";
                    cmd.ExecuteNonQuery();
                    tx.Commit();
                }

                using (var vacuumCmd = connection.CreateCommand())
                {
                    vacuumCmd.CommandText = "VACUUM;";
                    vacuumCmd.ExecuteNonQuery();
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

#if !UNIT_TEST
        public List<AppUsageRecord> GetAggregatedRecordsWithLive(DateTime startDate, DateTime endDate, WindowTrackingService? trackingService, bool includeLiveRecords = true)
        {
            if (startDate > endDate) (startDate, endDate) = (endDate, startDate);

            var aggregated = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            var dbReport = GetUsageReportForDateRange(startDate, endDate);
            var dbRecords = dbReport.Select(pair => new AppUsageRecord
            {
                ProcessName = pair.ProcessName,
                _accumulatedDuration = pair.TotalDuration,
                Date = startDate,
                StartTime = startDate
            });

            var dbAggregated = AggregateRecords(dbRecords, startDate);
            foreach (var (processName, record) in dbAggregated)
            {
                aggregated[processName] = record;
            }

            if (includeLiveRecords && trackingService != null)
            {
                MergeLiveRecords(aggregated, trackingService, startDate, endDate);
            }

            return aggregated.Values
                .Where(r => !Models.ProcessFilter.ShouldIgnoreProcess(r.ProcessName) &&
                       !r.ProcessName.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Duration.TotalSeconds)
                .ToList();
        }

        public List<AppUsageRecord> GetDetailRecordsWithLive(DateTime date, WindowTrackingService? trackingService)
        {
            var dbRecords = GetRecordsForDate(date) ?? new List<AppUsageRecord>();
            var aggregated = AggregateRecords(dbRecords, date);

            if (date.Date == DateTime.Today && trackingService != null)
            {
                var liveRecords = trackingService.GetRecords().Where(r => r.IsFromDate(date));
                var liveAggregated = AggregateRecords(liveRecords, date);

                foreach (var (processName, liveRecord) in liveAggregated)
                {
                    if (aggregated.TryGetValue(processName, out var existing))
                    {
                        existing._accumulatedDuration += liveRecord.Duration;
                        existing.ProcessId = liveRecord.ProcessId;
                        existing.WindowHandle = liveRecord.WindowHandle;
                        if (!string.IsNullOrWhiteSpace(liveRecord.ExecutablePath)) existing.ExecutablePath = liveRecord.ExecutablePath;
                        if (!string.IsNullOrEmpty(liveRecord.WindowTitle)) existing.WindowTitle = liveRecord.WindowTitle;
                    }
                    else
                    {
                        aggregated[processName] = liveRecord;
                    }
                }
            }

            return aggregated.Values.OrderByDescending(r => r.Duration.TotalSeconds).ToList();
        }

        private static void MergeLiveRecords(Dictionary<string, AppUsageRecord> aggregated, WindowTrackingService trackingService, DateTime startDate, DateTime endDate)
        {
            if (endDate.Date < DateTime.Today) return;

            var liveRecords = trackingService.GetRecords()
                .Where(r => r.Date >= startDate && r.Date <= endDate && r.Duration.TotalSeconds > 0)
                .ToList();

            foreach (var liveRec in liveRecords)
            {
                var temp = new AppUsageRecord
                {
                    ProcessName = liveRec.ProcessName,
                    ProcessId = liveRec.ProcessId,
                    WindowTitle = liveRec.WindowTitle,
                    ExecutablePath = liveRec.ExecutablePath
                };
                ApplicationProcessingHelper.ProcessApplicationRecord(temp);
                var processName = temp.ProcessName;

                if (aggregated.TryGetValue(processName, out var existing))
                {
                    existing._accumulatedDuration += liveRec.Duration;
                    existing.ProcessId = liveRec.ProcessId;
                    existing.WindowHandle = liveRec.WindowHandle;
                    if (!string.IsNullOrWhiteSpace(liveRec.ExecutablePath)) existing.ExecutablePath = liveRec.ExecutablePath;
                    if (!string.IsNullOrEmpty(liveRec.WindowTitle)) existing.WindowTitle = liveRec.WindowTitle;
                    if (liveRec.StartTime < existing.StartTime) existing.StartTime = liveRec.StartTime;
                }
                else
                {
                    var newRecord = new AppUsageRecord
                    {
                        ProcessName = processName,
                        ApplicationName = processName,
                        _accumulatedDuration = liveRec.Duration,
                        Date = liveRec.Date,
                        StartTime = liveRec.StartTime,
                        EndTime = liveRec.EndTime,
                        ProcessId = liveRec.ProcessId,
                        WindowHandle = liveRec.WindowHandle,
                        WindowTitle = liveRec.WindowTitle,
                        ExecutablePath = liveRec.ExecutablePath
                    };
                    aggregated[processName] = newRecord;
                }
            }
        }

        private static Dictionary<string, AppUsageRecord> AggregateRecords(IEnumerable<AppUsageRecord> records, DateTime? fallbackDate = null)
        {
            var aggregated = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in records)
            {
                if (rec.Duration.TotalSeconds < 5) continue;

                var temp = new AppUsageRecord
                {
                    ProcessName = rec.ProcessName,
                    ProcessId = rec.ProcessId,
                    WindowTitle = rec.WindowTitle,
                    ExecutablePath = rec.ExecutablePath
                };
                ApplicationProcessingHelper.ProcessApplicationRecord(temp);
                var processName = temp.ProcessName;

                if (Models.ProcessFilter.ShouldIgnoreProcess(processName) ||
                    processName.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (aggregated.TryGetValue(processName, out var existing))
                {
                    existing._accumulatedDuration += rec.Duration;
                    if (existing.ProcessId <= 0 && rec.ProcessId > 0) existing.ProcessId = rec.ProcessId;
                    if (string.IsNullOrWhiteSpace(existing.ExecutablePath) && !string.IsNullOrWhiteSpace(rec.ExecutablePath)) existing.ExecutablePath = rec.ExecutablePath;
                    if (rec.StartTime < existing.StartTime) existing.StartTime = rec.StartTime;
                    if (rec.EndTime.HasValue)
                    {
                        if (!existing.EndTime.HasValue || rec.EndTime > existing.EndTime) existing.EndTime = rec.EndTime;
                    }
                }
                else
                {
                    var newRecord = new AppUsageRecord
                    {
                        ProcessName = processName,
                        ApplicationName = processName,
                        _accumulatedDuration = rec.Duration,
                        Date = rec.Date != default ? rec.Date : (fallbackDate ?? DateTime.Today),
                        StartTime = rec.StartTime,
                        EndTime = rec.EndTime,
                        ProcessId = rec.ProcessId,
                        WindowTitle = rec.WindowTitle,
                        ExecutablePath = rec.ExecutablePath
                    };
                    aggregated[processName] = newRecord;
                }
            }

            return aggregated;
        }
#endif
    }
}
