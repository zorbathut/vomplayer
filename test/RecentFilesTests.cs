using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class RecentFilesTests
{
    private string? tempDir;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vompl-state-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void Teardown()
    {
        if (tempDir != null && Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private string DbPath()
    {
        return Path.Combine(tempDir!, "state.db");
    }

    [Test]
    public void OpenCreatesDirectoryAndDbFile()
    {
        var path = DbPath();
        using var __db_rf = StateDatabase.Open(path);
        var rf = new RecentFiles(__db_rf.Connection);
        Assert.That(File.Exists(path), Is.True);
    }

    [Test]
    public void OpenSetsWalJournalMode()
    {
        // Direct probe of the underlying DB: open a fresh raw SqliteConnection and ask it for the journal mode. If RecentFiles.Open ever stops setting WAL (or sets it via ExecuteNonQuery and silently misses a fallback to DELETE), this test catches it.
        var path = DbPath();
        using (var __db_rf = StateDatabase.Open(path)) {
            var rf = new RecentFiles(__db_rf.Connection);
            // Recording forces a write so the WAL file is materialised on disk.
            rf.Record("/probe.mp4");
        }
        using var probe = new SqliteConnection($"Data Source={path}");
        probe.Open();
        using var cmd = probe.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = cmd.ExecuteScalar() as string;
        Assert.That(mode, Is.EqualTo("wal").IgnoreCase);
    }

    [Test]
    public void RecordAndRetrieveSingleEntry()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/path/to/video.mp4");
        var entries = rf.GetMostRecent(10);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].PathOrUri, Is.EqualTo("/path/to/video.mp4"));
        Assert.That(entries[0].OpenCount, Is.EqualTo(1));
    }

    [Test]
    public void RecordingSamePathTwiceDeduplicatesAndIncrementsCount()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/a.mp4");
        rf.Record("/a.mp4");
        rf.Record("/a.mp4");
        var entries = rf.GetMostRecent(10);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].OpenCount, Is.EqualTo(3));
    }

    [Test]
    public void GetMostRecentOrdersByLastOpenedDescending()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        // No Thread.Sleep needed: last_opened is .NET ticks (100ns), strictly increasing across these back-to-back calls in practice.
        rf.Record("/a.mp4");
        rf.Record("/b.mp4");
        rf.Record("/c.mp4");

        var entries = rf.GetMostRecent(10);
        Assert.That(entries, Has.Count.EqualTo(3));
        Assert.That(entries[0].PathOrUri, Is.EqualTo("/c.mp4"));
        Assert.That(entries[1].PathOrUri, Is.EqualTo("/b.mp4"));
        Assert.That(entries[2].PathOrUri, Is.EqualTo("/a.mp4"));
    }

    [Test]
    public void ReRecordingMovesEntryToFront()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/a.mp4");
        rf.Record("/b.mp4");
        rf.Record("/a.mp4");

        var entries = rf.GetMostRecent(10);
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries[0].PathOrUri, Is.EqualTo("/a.mp4"));
        Assert.That(entries[0].OpenCount, Is.EqualTo(2));
        Assert.That(entries[1].PathOrUri, Is.EqualTo("/b.mp4"));
    }

    [Test]
    public void GetMostRecentRespectsLimit()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        for (int i = 0; i < 5; i++)
        {
            rf.Record($"/f{i}.mp4");
        }
        var entries = rf.GetMostRecent(3);
        Assert.That(entries, Has.Count.EqualTo(3));
    }

    [Test]
    public void GetMostRecentZeroLimitReturnsEmpty()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/a.mp4");
        Assert.That(rf.GetMostRecent(0), Is.Empty);
    }

    [Test]
    public void RecordEmptyThrows()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        Assert.Throws<ArgumentException>(() => rf.Record(""));
    }

    [Test]
    public void StateSurvivesAcrossOpens()
    {
        var path = DbPath();
        using (var __db_rf = StateDatabase.Open(path)) {
            var rf = new RecentFiles(__db_rf.Connection);
            rf.Record("/persistent.mp4");
        }
        using (var __db_rf2 = StateDatabase.Open(path)) {
            var rf2 = new RecentFiles(__db_rf2.Connection);
            var entries = rf2.GetMostRecent(10);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].PathOrUri, Is.EqualTo("/persistent.mp4"));
        }
    }

    [Test]
    public void NewerSchemaVersionIsRejected()
    {
        // Forge a future-version DB by hand, then have RecentFiles.Open refuse to touch it. Catches the downgrade-corruption guard.
        var path = DbPath();
        Directory.CreateDirectory(tempDir!);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 999;";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => StateDatabase.Open(path).Dispose());
    }

    // --- Migration scaffolding tests ---
    //
    // These exercise the OpenConnectionAndMigrateTo escape hatch: each test pre-stages a database at a specific schema version, optionally populates it with version-shaped data, then verifies that a normal RecentFiles.Open() walks the migration chain and arrives at a working current-schema database with the data correctly preserved/transformed.
    //
    // When v2 lands, the v1-data test below will start exercising real v1→v2 migration logic, and a new v2-data test should be added alongside.

    [Test]
    public void MigrationsArrayIsAppendOnlyAndStartsAtV1()
    {
        // Sanity: the array must start at v1 and increment by 1 with no gaps. A typo or accidental reorder during a future migration add would otherwise corrupt user databases on the next launch (mid-chain steps would be skipped and the user_version stamp would advance through holes).
        Assert.That(StateDatabase.Migrations, Is.Not.Empty);
        for (int i = 0; i < StateDatabase.Migrations.Count; i++)
        {
            Assert.That(StateDatabase.Migrations[i].Version, Is.EqualTo(i + 1),
                $"Migrations[{i}].Version must be {i + 1} (append-only, no gaps)");
        }
        Assert.That(StateDatabase.CurrentSchemaVersion, Is.EqualTo(StateDatabase.Migrations.Count));
    }

    [Test]
    public void V0DbHasNoSchemaAndMigratesToCurrentOnNormalOpen()
    {
        var path = DbPath();
        // OpenConnectionAndMigrateTo(path, 0) opens the DB without applying any migrations — user_version stays 0, no tables exist. Verify that, then verify the production Open() walks v0→current and produces a working DB.
        using (var conn = StateDatabase.OpenConnectionAndMigrateTo(path, 0))
        {
            using var probe = conn.CreateCommand();
            probe.CommandText = "PRAGMA user_version;";
            Assert.That(Convert.ToInt32(probe.ExecuteScalar()), Is.EqualTo(0));
            probe.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='recent_files';";
            Assert.That(probe.ExecuteScalar(), Is.Null, "v0 must have no recent_files table");
        }
        using var __db_rf = StateDatabase.Open(path);
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/post-migration.mp4");
        var entries = rf.GetMostRecent(10);
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].PathOrUri, Is.EqualTo("/post-migration.mp4"));
    }

    [Test]
    public void V1DbWithDataSurvivesNormalOpen()
    {
        var path = DbPath();
        // Stage a v1 DB and populate it with v1-shaped rows by raw INSERT (going around RecentFiles.Record so the test exercises the schema, not the API). When v2 lands and adds a column / transforms data, this test will catch a v1→v2 migration that mishandles existing rows.
        long t0 = DateTimeOffset.UtcNow.UtcTicks;
        using (var conn = StateDatabase.OpenConnectionAndMigrateTo(path, 1))
        {
            using var probe = conn.CreateCommand();
            probe.CommandText = "PRAGMA user_version;";
            Assert.That(Convert.ToInt32(probe.ExecuteScalar()), Is.EqualTo(1));

            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO recent_files (path_or_uri, last_opened, open_count) VALUES ('/a.mp4', $t1, 7);
                INSERT INTO recent_files (path_or_uri, last_opened, open_count) VALUES ('/b.mp4', $t2, 1);
                """;
            insert.Parameters.AddWithValue("$t1", t0);
            insert.Parameters.AddWithValue("$t2", t0 + 1);
            insert.ExecuteNonQuery();
        }

        using var __db_rf = StateDatabase.Open(path);
        var rf = new RecentFiles(__db_rf.Connection);
        var entries = rf.GetMostRecent(10);
        Assert.That(entries, Has.Count.EqualTo(2));
        // Newest first by last_opened.
        Assert.That(entries[0].PathOrUri, Is.EqualTo("/b.mp4"));
        Assert.That(entries[1].PathOrUri, Is.EqualTo("/a.mp4"));
        Assert.That(entries[1].OpenCount, Is.EqualTo(7));
    }

    [Test]
    public void MigrateToBelowCurrentVersionIsRejected()
    {
        // Build a current DB, then ask MigrateTo to take it backwards. The downgrade check is a precondition on MigrateTo — the production Open() never asks for less than CurrentSchemaVersion, but a future test that misuses the scaffolding (e.g. opens at v3 then asks for v2) needs the loud failure.
        var path = DbPath();
        using (var __db_rf = StateDatabase.Open(path)) {
            var rf = new RecentFiles(__db_rf.Connection);
            rf.Record("/anything.mp4");
        }
        using var conn = StateDatabase.OpenConnectionAndMigrateTo(path, StateDatabase.CurrentSchemaVersion);
        Assert.Throws<InvalidOperationException>(() => StateDatabase.MigrateTo(conn, StateDatabase.CurrentSchemaVersion - 1));
    }

    [Test]
    public void V1RecentsDataSurvivesV2Migration()
    {
        var path = DbPath();
        // Pre-stage a v1 DB with a recents row, then run normal Open() which carries through v1→v2. The recents row must survive and the track_preferences table must exist post-migration.
        long t0 = DateTimeOffset.UtcNow.UtcTicks;
        using (var conn = StateDatabase.OpenConnectionAndMigrateTo(path, 1))
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO recent_files (path_or_uri, last_opened, open_count) VALUES ('/v1.mp4', $t, 3);";
            insert.Parameters.AddWithValue("$t", t0);
            insert.ExecuteNonQuery();
        }

        using (var __db_rf = StateDatabase.Open(path)) {
            var rf = new RecentFiles(__db_rf.Connection);
            var entries = rf.GetMostRecent(10);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].PathOrUri, Is.EqualTo("/v1.mp4"));
            Assert.That(entries[0].OpenCount, Is.EqualTo(3));
        }

        using var probe = new SqliteConnection($"Data Source={path}");
        probe.Open();
        using var cmd = probe.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(StateDatabase.CurrentSchemaVersion));
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='track_preferences';";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("track_preferences"));
    }

    // --- Per-file play position (v3) ---

    [Test]
    public void RecordAndGetPositionRoundtrip()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/a.mp4");
        rf.RecordPosition("/a.mp4", 123.45);
        Assert.That(rf.GetPosition("/a.mp4"), Is.EqualTo(123.45));
    }

    [Test]
    public void GetPositionForUnknownFileReturnsNull()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        Assert.That(rf.GetPosition("/never-recorded.mp4"), Is.Null);
    }

    [Test]
    public void GetPositionWhenNeverRecordedPositionReturnsNull()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/a.mp4");
        Assert.That(rf.GetPosition("/a.mp4"), Is.Null);
    }

    [Test]
    public void RecordPositionDoesNotAffectRecentsOrder()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/a.mp4");
        rf.Record("/b.mp4");
        rf.RecordPosition("/a.mp4", 50);
        var entries = rf.GetMostRecent(10);
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries[0].PathOrUri, Is.EqualTo("/b.mp4"));
        Assert.That(entries[1].PathOrUri, Is.EqualTo("/a.mp4"));
    }

    [Test]
    public void ReRecordingFileDoesNotClobberPosition()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        rf.Record("/a.mp4");
        rf.RecordPosition("/a.mp4", 50);
        rf.Record("/a.mp4");
        Assert.That(rf.GetPosition("/a.mp4"), Is.EqualTo(50));
    }

    [Test]
    public void RecordPositionForUnrecordedFileIsSilentNoOp()
    {
        // RecordPosition is reachable only via VM paths that always Record() first, so the file should already be in recents. If somehow it isn't, the UPDATE matches zero rows — don't synthesize a phantom recents row from the position side channel.
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        Assert.DoesNotThrow(() => rf.RecordPosition("/never-recorded.mp4", 50));
        Assert.That(rf.GetPosition("/never-recorded.mp4"), Is.Null);
        Assert.That(rf.GetMostRecent(10), Is.Empty);
    }

    [Test]
    public void PositionSurvivesAcrossOpens()
    {
        var path = DbPath();
        using (var __db_rf = StateDatabase.Open(path))
        {
            var rf = new RecentFiles(__db_rf.Connection);
            rf.Record("/persistent.mp4");
            rf.RecordPosition("/persistent.mp4", 77.5);
        }
        using (var __db_rf2 = StateDatabase.Open(path))
        {
            var rf2 = new RecentFiles(__db_rf2.Connection);
            Assert.That(rf2.GetPosition("/persistent.mp4"), Is.EqualTo(77.5));
        }
    }

    [Test]
    public void RecordPositionEmptyPathThrows()
    {
        using var __db_rf = StateDatabase.Open(DbPath());
        var rf = new RecentFiles(__db_rf.Connection);
        Assert.Throws<ArgumentException>(() => rf.RecordPosition("", 50));
    }

    [Test]
    public void V2RecentsAndPrefsDataSurviveV3Migration()
    {
        // Pre-stage a v2 DB with both a recents row and a track_preferences row, then run normal Open() which carries through v2→v3 (an ALTER TABLE rather than a CREATE TABLE — first migration of that shape, so worth covering explicitly). All v2 data must survive, the new position_seconds column must default to NULL on the existing recents row, and RecordPosition must work against that pre-existing row.
        var path = DbPath();
        long t0 = DateTimeOffset.UtcNow.UtcTicks;
        using (var conn = StateDatabase.OpenConnectionAndMigrateTo(path, 2))
        {
            using var insertRecents = conn.CreateCommand();
            insertRecents.CommandText = "INSERT INTO recent_files (path_or_uri, last_opened, open_count) VALUES ('/v2.mp4', $t, 5);";
            insertRecents.Parameters.AddWithValue("$t", t0);
            insertRecents.ExecuteNonQuery();

            using var insertPrefs = conn.CreateCommand();
            insertPrefs.CommandText = "INSERT INTO track_preferences (directory, kind, is_none, title, lang, external, external_filename, index_in_kind) VALUES ('/somedir', 'audio', 0, 'English', 'eng', 0, NULL, 0);";
            insertPrefs.ExecuteNonQuery();
        }

        using (var __db_rf = StateDatabase.Open(path))
        {
            var rf = new RecentFiles(__db_rf.Connection);
            var entries = rf.GetMostRecent(10);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].PathOrUri, Is.EqualTo("/v2.mp4"));
            Assert.That(entries[0].OpenCount, Is.EqualTo(5));
            Assert.That(rf.GetPosition("/v2.mp4"), Is.Null);
            rf.RecordPosition("/v2.mp4", 42);
            Assert.That(rf.GetPosition("/v2.mp4"), Is.EqualTo(42));
        }

        using var probe = new SqliteConnection($"Data Source={path}");
        probe.Open();
        using var cmd = probe.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(StateDatabase.CurrentSchemaVersion));
        cmd.CommandText = "SELECT title FROM track_preferences WHERE directory = '/somedir' AND kind = 'audio';";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("English"));
    }
}
