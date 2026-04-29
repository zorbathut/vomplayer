using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class TrackPreferencesTests
{
    private string? tempDir;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vompl-prefs-" + Guid.NewGuid().ToString("N"));
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

    // Helper: open and immediately close the StateDatabase to materialize the schema, then return the path so the per-test code can construct a fresh TrackPreferences against the migrated DB.
    private static StateDatabase OpenStateDb(string path)
    {
        return StateDatabase.Open(path);
    }

    [Test]
    public void RecordOnUnmigratedDbThrows()
    {
        // Without a StateDatabase.Open to migrate, the track_preferences table doesn't exist yet — Record's INSERT must surface that as a real error rather than silently swallow it.
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath())!);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath()}");
        conn.Open();
        var prefs = new TrackPreferences(conn);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => prefs.Record("/dir", MediaKind.Audio, new TrackPreference(false, null, null, false, null, null)));
    }

    [Test]
    public void RoundtripBasicPreference()
    {
        var path = DbPath();
        using var db = OpenStateDb(path);
        var prefs = new TrackPreferences(db.Connection);

        var input = new TrackPreference(IsNone: false, Title: "English", Lang: "eng", External: false, ExternalFilename: null, IndexInKind: 1);
        prefs.Record("/movies/horror", MediaKind.Audio, input);
        var loaded = prefs.Get("/movies/horror", MediaKind.Audio);

        Assert.That(loaded, Is.EqualTo(input));
    }

    [Test]
    public void RoundtripIsNonePreference()
    {
        var path = DbPath();
        using var db = OpenStateDb(path);
        var prefs = new TrackPreferences(db.Connection);

        var input = new TrackPreference(IsNone: true, Title: null, Lang: null, External: false, ExternalFilename: null, IndexInKind: null);
        prefs.Record("/movies/horror", MediaKind.Subtitle, input);
        var loaded = prefs.Get("/movies/horror", MediaKind.Subtitle);

        Assert.That(loaded, Is.EqualTo(input));
    }

    [Test]
    public void GetMissingReturnsNull()
    {
        var path = DbPath();
        using var db = OpenStateDb(path);
        var prefs = new TrackPreferences(db.Connection);

        Assert.That(prefs.Get("/never/seen", MediaKind.Audio), Is.Null);
    }

    [Test]
    public void RecordTwiceReplaces()
    {
        var path = DbPath();
        using var db = OpenStateDb(path);
        var prefs = new TrackPreferences(db.Connection);

        prefs.Record("/dir", MediaKind.Audio, new TrackPreference(false, "Old", "eng", false, null, 0));
        prefs.Record("/dir", MediaKind.Audio, new TrackPreference(false, "New", "fre", true, "side.ac3", 2));

        var loaded = prefs.Get("/dir", MediaKind.Audio);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Title, Is.EqualTo("New"));
        Assert.That(loaded.Lang, Is.EqualTo("fre"));
        Assert.That(loaded.External, Is.True);
        Assert.That(loaded.ExternalFilename, Is.EqualTo("side.ac3"));
        Assert.That(loaded.IndexInKind, Is.EqualTo(2));
    }

    [Test]
    public void DifferentKindsForSameDirectoryAreIndependent()
    {
        var path = DbPath();
        using var db = OpenStateDb(path);
        var prefs = new TrackPreferences(db.Connection);

        prefs.Record("/dir", MediaKind.Video, new TrackPreference(false, "VidA", null, false, null, 0));
        prefs.Record("/dir", MediaKind.Audio, new TrackPreference(false, "AudA", "eng", false, null, 0));
        prefs.Record("/dir", MediaKind.Subtitle, new TrackPreference(true, null, null, false, null, null));

        Assert.That(prefs.Get("/dir", MediaKind.Video)!.Title, Is.EqualTo("VidA"));
        Assert.That(prefs.Get("/dir", MediaKind.Audio)!.Title, Is.EqualTo("AudA"));
        Assert.That(prefs.Get("/dir", MediaKind.Subtitle)!.IsNone, Is.True);
    }

    [Test]
    public void StateActuallyPersistsToDiskNotJustPool()
    {
        // Microsoft.Data.Sqlite's default Pooling=true means a Dispose returns the connection to a per-connection-string pool — it doesn't actually close the underlying handle. Two opens with the same connection string can both pull from the pool and "see" data that's still only in the in-memory connection state. Use a Pooling=false read-back to read from a TRULY independent connection that touches the disk file.
        var path = DbPath();
        using (var db = OpenStateDb(path))
        {
            var prefs = new TrackPreferences(db.Connection);
            prefs.Record("/disk", MediaKind.Audio, new TrackPreference(false, "DiskTest", "eng", false, null, 0));
        }

        using var rawConn = new SqliteConnection($"Data Source={path};Pooling=false");
        rawConn.Open();
        using var cmd = rawConn.CreateCommand();
        cmd.CommandText = "SELECT title FROM track_preferences WHERE directory='/disk' AND kind='audio';";
        var result = cmd.ExecuteScalar() as string;
        Assert.That(result, Is.EqualTo("DiskTest"));
    }

    [Test]
    public void StateSurvivesAcrossOpens()
    {
        // Mirrors the production lifecycle: open StateDatabase, write, dispose. Then reopen and read. If this test breaks, the bug is in our SQLite layer; if this passes, the production "preferences reset on close" report points elsewhere (wiring, paths, save callbacks not firing).
        var path = DbPath();
        using (var db = OpenStateDb(path))
        {
            var prefs = new TrackPreferences(db.Connection);
            prefs.Record("/persistent", MediaKind.Audio, new TrackPreference(false, "English", "eng", false, null, 0));
            prefs.Record("/persistent", MediaKind.Subtitle, new TrackPreference(true, null, null, false, null, null));
        }
        using (var db = OpenStateDb(path))
        {
            var prefs = new TrackPreferences(db.Connection);
            var audio = prefs.Get("/persistent", MediaKind.Audio);
            var sub = prefs.Get("/persistent", MediaKind.Subtitle);
            Assert.That(audio, Is.Not.Null);
            Assert.That(audio!.Title, Is.EqualTo("English"));
            Assert.That(sub, Is.Not.Null);
            Assert.That(sub!.IsNone, Is.True);
        }
    }

    [Test]
    public void DifferentDirectoriesAreIndependent()
    {
        var path = DbPath();
        using var db = OpenStateDb(path);
        var prefs = new TrackPreferences(db.Connection);

        prefs.Record("/a", MediaKind.Audio, new TrackPreference(false, "A-track", "eng", false, null, 0));
        prefs.Record("/b", MediaKind.Audio, new TrackPreference(false, "B-track", "fre", false, null, 1));

        Assert.That(prefs.Get("/a", MediaKind.Audio)!.Title, Is.EqualTo("A-track"));
        Assert.That(prefs.Get("/b", MediaKind.Audio)!.Title, Is.EqualTo("B-track"));
    }

    [Test]
    public void NullArgumentsThrow()
    {
        var path = DbPath();
        using var db = OpenStateDb(path);
        var prefs = new TrackPreferences(db.Connection);

        Assert.Throws<ArgumentException>(() => prefs.Record("", MediaKind.Audio, new TrackPreference(false, null, null, false, null, null)));
        Assert.Throws<ArgumentNullException>(() => prefs.Record("/dir", MediaKind.Audio, null!));
        Assert.Throws<ArgumentException>(() => prefs.Get("", MediaKind.Audio));
    }

    [Test]
    public void SchemaCheckConstraintRejectsBogusKindValue()
    {
        // Defends the CHECK (kind IN (...)) constraint — a future refactor of MediaKindNames that emits "subtitles" instead of "subtitle" should fail at the SQLite layer instead of silently mismatching reads.
        var path = DbPath();
        using (var db = OpenStateDb(path))
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO track_preferences (directory, kind, is_none, external) VALUES ('/x', 'bogus', 0, 0);";
            Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        }
    }

    [TestCase("http://example.com/stream.m3u8", null)]
    [TestCase("https://example.com/v.mp4", null)]
    [TestCase("smb://server/share/film.mkv", null)]
    [TestCase("ftp://host/path", null)]
    [TestCase("", null)]
    public void TryGetDirectoryKeyReturnsNullForNonLocal(string input, string? expected)
    {
        Assert.That(TrackPreferences.TryGetDirectoryKey(input), Is.EqualTo(expected));
    }

    [Test]
    public void TryGetDirectoryKeyReturnsParentForLocalPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "vompl-prefs-key-" + Guid.NewGuid().ToString("N"), "movie.mkv");
        var key = TrackPreferences.TryGetDirectoryKey(path);
        Assert.That(key, Is.EqualTo(Path.GetDirectoryName(path)));
    }

    [Test]
    public void TryGetDirectoryKeyResolvesRelativePath()
    {
        var key = TrackPreferences.TryGetDirectoryKey("./relative.mkv");
        Assert.That(key, Is.EqualTo(Path.GetFullPath(".").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }
}
