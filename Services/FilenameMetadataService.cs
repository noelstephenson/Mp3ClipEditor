using System.IO;
using System.Text.RegularExpressions;
using Mp3ClipEditorTagger.Models;
using TagLib;

namespace Mp3ClipEditorTagger.Services;

public sealed class FilenameMetadataService
{
    public static IReadOnlyList<RenamePatternOption> PatternOptions { get; } =
    [
        new("Auto", "Auto Detect"),
        new("ArtistTitleDash", "Artist - Title"),
        new("ArtistTitleComma", "Artist, Title"),
        new("ArtistTitleDot", "Artist.Title"),
        new("TitleArtistDash", "Title - Artist"),
        new("TitleArtistComma", "Title, Artist"),
        new("TitleArtistDot", "Title.Artist"),
        new("ArtistTitleUnderscore", "Artist_Title"),
        new("TitleArtistUnderscore", "Title_Artist"),
        new("TitleOnly", "Title Only")
    ];

    public void ApplyFilenameTags(IEnumerable<TrackItem> tracks, string patternKey)
    {
        foreach (var track in tracks)
        {
            var parsed = ParseFromFileName(track, patternKey);
            if (!string.IsNullOrWhiteSpace(parsed.artist))
            {
                track.Artist = parsed.artist;
            }

            if (!string.IsNullOrWhiteSpace(parsed.title))
            {
                track.Title = parsed.title;
            }

            track.UpdateFilenameTags(parsed.artist, parsed.title);
        }
    }

    public int RenameTracksToArtistTitle(IEnumerable<TrackItem> tracks)
    {
        var renamedCount = 0;
        var groupedByDirectory = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.FilePath))
            .GroupBy(track => Path.GetDirectoryName(track.FilePath) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupedByDirectory)
        {
            var usedNames = new HashSet<string>(
                Directory.Exists(group.Key)
                    ? Directory.EnumerateFiles(group.Key, "*.mp3").Select(Path.GetFileName).Where(name => name is not null)!.Cast<string>()
                    : Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var track in group)
            {
                usedNames.Remove(track.FileName);
            }

            foreach (var track in group)
            {
                var desiredName = track.OutputFileName;
                var uniqueName = GetUniqueFileName(desiredName, usedNames);
                var targetPath = Path.Combine(group.Key, uniqueName);

                if (string.Equals(track.FilePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    usedNames.Add(uniqueName);
                    continue;
                }

                System.IO.File.Move(track.FilePath, targetPath);
                track.UpdateFileReference(targetPath);
                renamedCount++;
            }
        }

        return renamedCount;
    }

    public void WriteTagsToSourceFiles(IEnumerable<TrackItem> tracks)
    {
        foreach (var track in tracks)
        {
            using var file = TagLib.File.Create(track.FilePath);
            file.Tag.Performers = string.IsNullOrWhiteSpace(track.Artist) ? Array.Empty<string>() : [track.Artist.Trim()];
            file.Tag.Title = string.IsNullOrWhiteSpace(track.Title) ? null : track.Title.Trim();
            file.Save();
        }
    }

    public (string artist, string title) ParseFromFileName(TrackItem track, string patternKey)
    {
        var fileBaseName = NormalizeFileName(track.FileBaseName);
        return patternKey switch
        {
            "ArtistTitleDash" => ParseArtistTitle(fileBaseName, " - "),
            "ArtistTitleComma" => ParseArtistTitle(fileBaseName, ", "),
            "ArtistTitleDot" => ParseArtistTitleByLastDelimiter(fileBaseName, "."),
            "TitleArtistDash" => ParseTitleArtist(fileBaseName, " - "),
            "TitleArtistComma" => ParseTitleArtist(fileBaseName, ", "),
            "TitleArtistDot" => ParseTitleArtistByLastDelimiter(fileBaseName, "."),
            "ArtistTitleUnderscore" => ParseArtistTitle(fileBaseName, " _ "),
            "TitleArtistUnderscore" => ParseTitleArtist(fileBaseName, " _ "),
            "TitleOnly" => (track.SourceArtist, CleanupPart(fileBaseName)),
            _ => AutoDetect(track, fileBaseName)
        };
    }

    private static (string artist, string title) AutoDetect(TrackItem track, string fileBaseName)
    {
        var candidates = new List<(string artist, string title)>
        {
            ParseArtistTitle(fileBaseName, " - "),
            ParseArtistTitle(fileBaseName, ", "),
            ParseArtistTitleByLastDelimiter(fileBaseName, "."),
            ParseTitleArtist(fileBaseName, " - "),
            ParseTitleArtist(fileBaseName, ", "),
            ParseTitleArtistByLastDelimiter(fileBaseName, "."),
            ParseArtistTitle(fileBaseName, " _ "),
            ParseTitleArtist(fileBaseName, " _ "),
            (track.SourceArtist, CleanupPart(fileBaseName))
        };

        var best = candidates
            .Select(candidate => (candidate, score: ScoreCandidate(track, candidate.artist, candidate.title)))
            .OrderByDescending(item => item.score)
            .FirstOrDefault();

        return best.candidate == default
            ? (track.SourceArtist, CleanupPart(fileBaseName))
            : best.candidate;
    }

    private static int ScoreCandidate(TrackItem track, string artist, string title)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            score += 2;
        }

        if (Matches(track.SourceArtist, artist))
        {
            score += 4;
        }

        if (Matches(track.SourceTitle, title))
        {
            score += 4;
        }

        if (Matches(track.Artist, artist))
        {
            score += 1;
        }

        if (Matches(track.Title, title))
        {
            score += 1;
        }

        if (string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(track.SourceArtist))
        {
            score -= 2;
        }

        return score;
    }

    private static bool Matches(string? left, string? right)
        => NormalizeComparable(left) == NormalizeComparable(right);

    private static string NormalizeComparable(string? value)
        => Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");

    private static (string artist, string title) ParseArtistTitle(string fileBaseName, string delimiter)
    {
        var parts = fileBaseName.Split([delimiter], 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (CleanupPart(parts[0]), CleanupPart(parts[1]))
            : (string.Empty, CleanupPart(fileBaseName));
    }

    private static (string artist, string title) ParseArtistTitleByLastDelimiter(string fileBaseName, string delimiter)
    {
        var index = fileBaseName.LastIndexOf(delimiter, StringComparison.Ordinal);
        if (index <= 0 || index >= fileBaseName.Length - delimiter.Length)
        {
            return (string.Empty, CleanupPart(fileBaseName));
        }

        var artist = fileBaseName[..index];
        var title = fileBaseName[(index + delimiter.Length)..];
        return (CleanupPart(artist), CleanupPart(title));
    }

    private static (string artist, string title) ParseTitleArtist(string fileBaseName, string delimiter)
    {
        var parts = fileBaseName.Split([delimiter], 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (CleanupPart(parts[1]), CleanupPart(parts[0]))
            : (string.Empty, CleanupPart(fileBaseName));
    }

    private static (string artist, string title) ParseTitleArtistByLastDelimiter(string fileBaseName, string delimiter)
    {
        var index = fileBaseName.LastIndexOf(delimiter, StringComparison.Ordinal);
        if (index <= 0 || index >= fileBaseName.Length - delimiter.Length)
        {
            return (string.Empty, CleanupPart(fileBaseName));
        }

        var title = fileBaseName[..index];
        var artist = fileBaseName[(index + delimiter.Length)..];
        return (CleanupPart(artist), CleanupPart(title));
    }

    private static string NormalizeFileName(string value)
        => value.Replace('_', ' ').Replace(" – ", " - ", StringComparison.Ordinal).Trim();

    private static string CleanupPart(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ");

    private static string GetUniqueFileName(string desiredName, ISet<string> usedNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(desiredName);
        var extension = Path.GetExtension(desiredName);
        var candidate = desiredName;
        var counter = 2;

        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName} ({counter}){extension}";
            counter++;
        }

        return candidate;
    }
}

public sealed record RenamePatternOption(string Key, string Label);
