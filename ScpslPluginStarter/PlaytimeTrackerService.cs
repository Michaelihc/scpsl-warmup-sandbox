using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LabApi.Features.Wrappers;

namespace ScpslPluginStarter;

internal sealed class PlaytimeTrackerService
{
    private const string Header = "user_id\tnickname\ttotal_seconds\tsession_count\tfirst_join_utc\tlast_join_utc\tlast_seen_utc";
    private readonly Dictionary<string, PlaytimeRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ActivePlaytimeSession> _activeSessions = new();
    private string? _dataPath;

    public void Enable(PlaytimeTrackingConfig config)
    {
        _records.Clear();
        _activeSessions.Clear();
        _dataPath = ResolveDataPath(config);
        Load();
    }

    public void Disable()
    {
        FlushActiveSessions(DateTimeOffset.UtcNow);
        Save();
        _activeSessions.Clear();
    }

    public void PlayerJoined(Player player, PlaytimeTrackingConfig config)
    {
        if (!config.Enabled || !TryGetUserId(player, out string userId))
        {
            return;
        }

        if (_activeSessions.TryGetValue(player.PlayerId, out ActivePlaytimeSession? existing))
        {
            Accrue(existing, DateTimeOffset.UtcNow);
            _activeSessions.Remove(player.PlayerId);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlaytimeRecord record = GetOrCreateRecord(userId);
        record.Nickname = Sanitize(player.Nickname);
        record.SessionCount++;
        if (record.FirstJoinUtc == DateTimeOffset.MinValue)
        {
            record.FirstJoinUtc = now;
        }

        record.LastJoinUtc = now;
        record.LastSeenUtc = now;
        _activeSessions[player.PlayerId] = new ActivePlaytimeSession(player.PlayerId, userId, now, now);
        Save();
    }

    public void PlayerLeft(Player player, PlaytimeTrackingConfig config)
    {
        if (!config.Enabled)
        {
            return;
        }

        if (_activeSessions.TryGetValue(player.PlayerId, out ActivePlaytimeSession? session))
        {
            Accrue(session, DateTimeOffset.UtcNow, player.Nickname);
            _activeSessions.Remove(player.PlayerId);
            Save();
        }
    }

    public void FlushIfDue(PlaytimeTrackingConfig config)
    {
        if (!config.Enabled || config.FlushIntervalSeconds <= 0 || _activeSessions.Count == 0)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_activeSessions.Values.All(session => (now - session.LastAccountedUtc).TotalSeconds < config.FlushIntervalSeconds))
        {
            return;
        }

        FlushActiveSessions(now);
        Save();
    }

    public string BuildReport(int limit = 10)
    {
        FlushActiveSessions(DateTimeOffset.UtcNow);
        Save();

        string summary = BuildSummary();
        List<PlaytimeRecord> records = _records.Values
            .OrderByDescending(record => record.TotalSeconds)
            .ThenBy(record => record.UserId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToList();

        if (records.Count == 0)
        {
            return summary + "\n" + WarmupLocalization.T("No playtime has been tracked yet.", "尚未记录游玩时长。");
        }

        string leaderboard = string.Join(
            "\n",
            records.Select((record, index) =>
                $"{index + 1}. {FormatHours(record.TotalSeconds)}h {record.Nickname} ({record.UserId}) sessions={record.SessionCount}"));
        return summary + "\n" + leaderboard;
    }

    public string BuildSummaryReport()
    {
        FlushActiveSessions(DateTimeOffset.UtcNow);
        Save();
        return BuildSummary();
    }

    private string BuildSummary()
    {
        int totalPlayers = _records.Count;
        int totalSessions = _records.Values.Sum(record => Math.Max(0, record.SessionCount));
        double totalSeconds = _records.Values.Sum(record => Math.Max(0d, record.TotalSeconds));
        int activePlayers = _activeSessions.Count;
        double averageHours = totalPlayers <= 0 ? 0d : totalSeconds / 3600d / totalPlayers;
        DateTimeOffset lastSeen = _records.Values
            .Select(record => record.LastSeenUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        return WarmupLocalization.T(
            $"Player stats: unique_players={totalPlayers}, total_hours={FormatHours(totalSeconds)}h, sessions={totalSessions}, active_now={activePlayers}, avg_hours_per_player={averageHours:F2}, last_seen_utc={FormatDisplayTime(lastSeen)}",
            $"玩家统计：总玩家={totalPlayers}，总游玩={FormatHours(totalSeconds)}小时，会话={totalSessions}，当前在线={activePlayers}，人均={averageHours:F2}小时，最后在线UTC={FormatDisplayTime(lastSeen)}");
    }

    private void FlushActiveSessions(DateTimeOffset now)
    {
        foreach (ActivePlaytimeSession session in _activeSessions.Values)
        {
            Accrue(session, now);
        }
    }

    private void Accrue(ActivePlaytimeSession session, DateTimeOffset now, string? nickname = null)
    {
        if (now < session.LastAccountedUtc)
        {
            now = session.LastAccountedUtc;
        }

        PlaytimeRecord record = GetOrCreateRecord(session.UserId);
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            record.Nickname = Sanitize(nickname);
        }

        record.TotalSeconds += Math.Max(0, (now - session.LastAccountedUtc).TotalSeconds);
        record.LastSeenUtc = now;
        session.LastAccountedUtc = now;
    }

    private PlaytimeRecord GetOrCreateRecord(string userId)
    {
        if (_records.TryGetValue(userId, out PlaytimeRecord? record))
        {
            return record;
        }

        record = new PlaytimeRecord(userId);
        _records[userId] = record;
        return record;
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_dataPath) || !File.Exists(_dataPath))
        {
            return;
        }

        foreach (string line in File.ReadLines(_dataPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("user_id\t", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 6 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            bool hasFirstJoin = parts.Length >= 7;
            PlaytimeRecord record = new(Unescape(parts[0]))
            {
                Nickname = Unescape(parts[1]),
                TotalSeconds = ParseDouble(parts[2]),
                SessionCount = ParseInt(parts[3]),
                FirstJoinUtc = ParseTime(parts[4]),
                LastJoinUtc = hasFirstJoin ? ParseTime(parts[5]) : ParseTime(parts[4]),
                LastSeenUtc = hasFirstJoin ? ParseTime(parts[6]) : ParseTime(parts[5]),
            };

            if (record.FirstJoinUtc == DateTimeOffset.MinValue)
            {
                record.FirstJoinUtc = record.LastJoinUtc;
            }

            _records[record.UserId] = record;
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_dataPath))
        {
            return;
        }

        string directory = Path.GetDirectoryName(_dataPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _dataPath + ".tmp";
        using (StreamWriter writer = new(tempPath, false))
        {
            writer.WriteLine(Header);
            foreach (PlaytimeRecord record in _records.Values.OrderBy(record => record.UserId, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine(string.Join(
                    "\t",
                    Escape(record.UserId),
                    Escape(record.Nickname),
                    record.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                    record.SessionCount.ToString(CultureInfo.InvariantCulture),
                    FormatTime(record.FirstJoinUtc),
                    FormatTime(record.LastJoinUtc),
                    FormatTime(record.LastSeenUtc)));
            }
        }

        if (File.Exists(_dataPath))
        {
            File.Delete(_dataPath);
        }

        File.Move(tempPath, _dataPath);
    }

    private static bool TryGetUserId(Player player, out string userId)
    {
        userId = player.UserId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(userId)
            && !player.IsHost
            && !player.IsDummy
            && !player.IsNpc
            && !player.IsDestroyed;
    }

    private static string ResolveDataPath(PlaytimeTrackingConfig config)
    {
        string fileName = string.IsNullOrWhiteSpace(config.DataFileName)
            ? "playtime.tsv"
            : config.DataFileName;

        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SCP Secret Laboratory", "LabAPI", "configs", "global", "WarmupSandbox", fileName);
    }

    private static string Sanitize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value!.Trim();
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\t", "\\t")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static string Unescape(string value)
    {
        return value
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\\\", "\\");
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0d;
    }

    private static DateTimeOffset ParseTime(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset result)
            ? result.ToUniversalTime()
            : DateTimeOffset.MinValue;
    }

    private static string FormatTime(DateTimeOffset value)
    {
        return value == DateTimeOffset.MinValue ? string.Empty : value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string FormatHours(double totalSeconds)
    {
        return (totalSeconds / 3600d).ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string FormatDisplayTime(DateTimeOffset value)
    {
        return value == DateTimeOffset.MinValue ? "n/a" : value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private sealed class PlaytimeRecord
    {
        public PlaytimeRecord(string userId)
        {
            UserId = userId;
        }

        public string UserId { get; }

        public string Nickname { get; set; } = string.Empty;

        public double TotalSeconds { get; set; }

        public int SessionCount { get; set; }

        public DateTimeOffset FirstJoinUtc { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset LastJoinUtc { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed class ActivePlaytimeSession
    {
        public ActivePlaytimeSession(int playerId, string userId, DateTimeOffset startUtc, DateTimeOffset lastAccountedUtc)
        {
            PlayerId = playerId;
            UserId = userId;
            StartUtc = startUtc;
            LastAccountedUtc = lastAccountedUtc;
        }

        public int PlayerId { get; }

        public string UserId { get; }

        public DateTimeOffset StartUtc { get; }

        public DateTimeOffset LastAccountedUtc { get; set; }
    }
}
