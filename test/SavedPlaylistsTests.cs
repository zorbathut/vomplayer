using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Vomplayer.UserData;

namespace Vomplayer.Tests;

[TestFixture]
public class SavedPlaylistsTests
{
    private string? tempDir;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "vompl-playlists-" + Guid.NewGuid().ToString("N"));
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

    private static SavedPlaylistStream Stream(int slot, int currentIndex, params string[] items)
    {
        return new SavedPlaylistStream(slot, currentIndex, items);
    }

    [Test]
    public void RoundTripSingleStream()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        var guid = Guid.NewGuid();
        sp.Save(guid, "My Movie", new[] { Stream(0, 1, "/a.mp4", "/b.mp4", "/c.mp4") });

        var loaded = sp.GetById(guid);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Title, Is.EqualTo("My Movie"));
        Assert.That(loaded.StreamCount, Is.EqualTo(1));
        Assert.That(loaded.Streams.Count, Is.EqualTo(1));
        Assert.That(loaded.Streams[0].SlotIndex, Is.EqualTo(0));
        Assert.That(loaded.Streams[0].CurrentIndex, Is.EqualTo(1));
        Assert.That(loaded.Streams[0].Items, Is.EqualTo(new[] { "/a.mp4", "/b.mp4", "/c.mp4" }));
    }

    [Test]
    public void RoundTripMultiStream()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        var guid = Guid.NewGuid();
        var streams = new[]
        {
            Stream(0, 0, "/primary.mp4"),
            Stream(1, 2, "/sec1.mp4", "/sec2.mp4", "/sec3.mp4"),
        };
        sp.Save(guid, "A + B", streams);

        var loaded = sp.GetById(guid);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.StreamCount, Is.EqualTo(2));
        Assert.That(loaded.Streams.Count, Is.EqualTo(2));
        var slot0 = loaded.Streams.First(s => s.SlotIndex == 0);
        var slot1 = loaded.Streams.First(s => s.SlotIndex == 1);
        Assert.That(slot0.CurrentIndex, Is.EqualTo(0));
        Assert.That(slot0.Items, Is.EqualTo(new[] { "/primary.mp4" }));
        Assert.That(slot1.CurrentIndex, Is.EqualTo(2));
        Assert.That(slot1.Items, Is.EqualTo(new[] { "/sec1.mp4", "/sec2.mp4", "/sec3.mp4" }));
    }

    [Test]
    public void SaveFiltersEmptyStreams()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        var guid = Guid.NewGuid();
        // Slot 1 has no items — should be dropped, leaving a single-stream entry.
        sp.Save(guid, "single after filter", new[]
        {
            Stream(0, 0, "/a.mp4"),
            Stream(1, 0),
        });
        var loaded = sp.GetById(guid);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.StreamCount, Is.EqualTo(1));
        Assert.That(loaded.Streams.Count, Is.EqualTo(1));
        Assert.That(loaded.Streams[0].SlotIndex, Is.EqualTo(0));
    }

    [Test]
    public void SaveAllEmptyIsNoOp()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        var guid = Guid.NewGuid();
        sp.Save(guid, "empty", new[] { Stream(0, 0), Stream(1, 0) });
        Assert.That(sp.GetById(guid), Is.Null);
        Assert.That(sp.GetMostRecent(), Is.Null);
    }

    [Test]
    public void SaveIsUpsertRewritingPayload()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        var guid = Guid.NewGuid();
        sp.Save(guid, "v1", new[] { Stream(0, 0, "/a.mp4", "/b.mp4") });
        sp.Save(guid, "v2", new[] { Stream(0, 0, "/x.mp4") });
        var loaded = sp.GetById(guid);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Title, Is.EqualTo("v2"));
        Assert.That(loaded.Streams[0].Items, Is.EqualTo(new[] { "/x.mp4" }));
        // Defensive: only one row exists, no leftover from the v1 save.
        Assert.That(sp.GetMostRecent(100).Count, Is.EqualTo(1));
    }

    [Test]
    public void TouchBumpsLastUsedAtWithoutRewritingPayload()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        var oldGuid = Guid.NewGuid();
        var newGuid = Guid.NewGuid();
        sp.Save(oldGuid, "old", new[] { Stream(0, 0, "/a.mp4") });
        // Tiny sleep to ensure the next save lands on a strictly later tick.
        Thread.Sleep(1);
        sp.Save(newGuid, "new", new[] { Stream(0, 0, "/b.mp4") });
        // newGuid is most recent.
        Assert.That(sp.GetMostRecent()!.Guid, Is.EqualTo(newGuid));
        Thread.Sleep(1);
        sp.Touch(oldGuid);
        // After Touch, oldGuid is most recent and its payload still matches the original.
        var top = sp.GetMostRecent();
        Assert.That(top, Is.Not.Null);
        Assert.That(top!.Guid, Is.EqualTo(oldGuid));
        Assert.That(top.Title, Is.EqualTo("old"));
        Assert.That(top.Streams[0].Items, Is.EqualTo(new[] { "/a.mp4" }));
    }

    [Test]
    public void TouchOnUnknownGuidIsSilentNoOp()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        Assert.DoesNotThrow(() => sp.Touch(Guid.NewGuid()));
    }

    [Test]
    public void GetMostRecentOrdersByLastUsedDescending()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var g3 = Guid.NewGuid();
        sp.Save(g1, "a", new[] { Stream(0, 0, "/a.mp4") });
        Thread.Sleep(1);
        sp.Save(g2, "b", new[] { Stream(0, 0, "/b.mp4") });
        Thread.Sleep(1);
        sp.Save(g3, "c", new[] { Stream(0, 0, "/c.mp4") });
        var list = sp.GetMostRecent(10);
        Assert.That(list.Select(e => e.Guid), Is.EqualTo(new[] { g3, g2, g1 }));
    }

    [Test]
    public void GetMostRecentRespectsLimit()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        for (int i = 0; i < 5; i++)
        {
            sp.Save(Guid.NewGuid(), $"e{i}", new[] { Stream(0, 0, $"/{i}.mp4") });
            Thread.Sleep(1);
        }
        Assert.That(sp.GetMostRecent(3).Count, Is.EqualTo(3));
    }

    [Test]
    public void GetMostRecentReturnsNullOnEmpty()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        Assert.That(sp.GetMostRecent(), Is.Null);
        Assert.That(sp.GetMostRecent(10), Is.Empty);
    }

    [Test]
    public void GetByIdReturnsNullForUnknownGuid()
    {
        using var db = StateDatabase.Open(DbPath());
        var sp = new SavedPlaylists(db.Connection);
        Assert.That(sp.GetById(Guid.NewGuid()), Is.Null);
    }

    [Test]
    public void StateSurvivesAcrossOpens()
    {
        var path = DbPath();
        var guid = Guid.NewGuid();
        using (var db = StateDatabase.Open(path))
        {
            var sp = new SavedPlaylists(db.Connection);
            sp.Save(guid, "persistent", new[] { Stream(0, 0, "/a.mp4", "/b.mp4") });
        }
        using (var db = StateDatabase.Open(path))
        {
            var sp = new SavedPlaylists(db.Connection);
            var loaded = sp.GetById(guid);
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Title, Is.EqualTo("persistent"));
            Assert.That(loaded.Streams[0].Items, Is.EqualTo(new[] { "/a.mp4", "/b.mp4" }));
        }
    }

    [Test]
    public void V3RecentsDataSurvivesV4Migration()
    {
        // Pre-stage a v3 DB with both a recents row and a track_preferences row, then run normal Open() which carries through v3→v4. v4 only adds a new table, so all v3 data must be untouched and the new saved_playlists table must exist + be writeable.
        var path = DbPath();
        long t0 = DateTimeOffset.UtcNow.UtcTicks;
        using (var conn = StateDatabase.OpenConnectionAndMigrateTo(path, 3))
        {
            using var insertRecents = conn.CreateCommand();
            insertRecents.CommandText = "INSERT INTO recent_files (path_or_uri, last_opened, open_count, position_seconds) VALUES ('/v3.mp4', $t, 4, 12.5);";
            insertRecents.Parameters.AddWithValue("$t", t0);
            insertRecents.ExecuteNonQuery();

            using var insertPrefs = conn.CreateCommand();
            insertPrefs.CommandText = "INSERT INTO track_preferences (directory, kind, is_none, title, lang, external, external_filename, index_in_kind) VALUES ('/somedir', 'audio', 0, 'English', 'eng', 0, NULL, 0);";
            insertPrefs.ExecuteNonQuery();
        }

        using (var db = StateDatabase.Open(path))
        {
            var rf = new RecentFiles(db.Connection);
            var entries = rf.GetMostRecent(10);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].PathOrUri, Is.EqualTo("/v3.mp4"));
            Assert.That(entries[0].OpenCount, Is.EqualTo(4));
            Assert.That(rf.GetPosition("/v3.mp4"), Is.EqualTo(12.5));

            var sp = new SavedPlaylists(db.Connection);
            Assert.That(sp.GetMostRecent(), Is.Null);
            // v4 table is writeable.
            var guid = Guid.NewGuid();
            sp.Save(guid, "post-migration", new[] { Stream(0, 0, "/new.mp4") });
            Assert.That(sp.GetById(guid), Is.Not.Null);
        }

        using var probe = new SqliteConnection($"Data Source={path}");
        probe.Open();
        using var cmd = probe.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(StateDatabase.CurrentSchemaVersion));
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='saved_playlists';";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("saved_playlists"));
        cmd.CommandText = "SELECT title FROM track_preferences WHERE directory = '/somedir' AND kind = 'audio';";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo("English"));
    }
}
