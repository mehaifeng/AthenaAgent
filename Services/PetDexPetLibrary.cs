using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SkiaSharp;

namespace Athena.UI.Services;

public enum PetDexAnimationState
{
    Idle,
    RunningRight,
    RunningLeft,
    Waving,
    Jumping,
    Failed,
    Waiting,
    Running,
    Review
}

public sealed record PetDexBuiltInPet(
    string Slug,
    string DisplayName,
    string Kind,
    string SubmittedBy);

public sealed class PetDexPetDefinition
{
    private readonly IReadOnlyDictionary<string, int> _rowIndices;
    private readonly IReadOnlyDictionary<int, int> _frameCountsByRow;

    internal PetDexPetDefinition(
        string slug,
        string displayName,
        string description,
        Bitmap spriteSheet,
        int frameWidth,
        int frameHeight,
        int columns,
        IReadOnlyList<string> rows,
        IReadOnlyDictionary<int, int> frameCountsByRow,
        int bottomTransparentPixels,
        int loopMs)
    {
        Slug = slug;
        DisplayName = displayName;
        Description = description;
        SpriteSheet = spriteSheet;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Columns = columns;
        Rows = rows;
        LoopMs = loopMs;
        FramesPerState = Math.Min(PetDexPetLibrary.StandardFramesPerState, columns);
        _frameCountsByRow = frameCountsByRow;
        BottomTransparentPixels = bottomTransparentPixels;
        _rowIndices = rows
            .Select((name, index) => (Name: name, Index: index))
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);
    }

    public string Slug { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public Bitmap SpriteSheet { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public int Columns { get; }
    public int FramesPerState { get; }
    public IReadOnlyList<string> Rows { get; }
    public int BottomTransparentPixels { get; }
    public int LoopMs { get; }

    public int RowIndex(PetDexAnimationState state)
    {
        var aliases = state switch
        {
            PetDexAnimationState.RunningRight => new[] { "running-right", "running" },
            PetDexAnimationState.RunningLeft => new[] { "running-left", "running" },
            PetDexAnimationState.Waving => new[] { "waving", "wave" },
            PetDexAnimationState.Jumping => new[] { "jumping", "jump" },
            PetDexAnimationState.Failed => new[] { "failed" },
            PetDexAnimationState.Waiting => new[] { "waiting", "idle" },
            PetDexAnimationState.Running => new[] { "running", "run" },
            PetDexAnimationState.Review => new[] { "review", "idle" },
            _ => new[] { "idle" }
        };
        foreach (var alias in aliases)
        {
            if (_rowIndices.TryGetValue(alias, out var row)) return row;
        }
        return 0;
    }

    /// <summary>
    /// Returns the real, padding-trimmed frame count for an action row. PetDex
    /// sheets may leave trailing cells transparent (for example a four-frame
    /// wave in a six-frame grid), and stepping into those cells flashes blank.
    /// </summary>
    public int FrameCount(PetDexAnimationState state)
    {
        var row = RowIndex(state);
        if (_frameCountsByRow.TryGetValue(row, out var count) && count > 0)
            return Math.Min(count, FramesPerState);
        if (_frameCountsByRow.TryGetValue(0, out var idleCount) && idleCount > 0)
            return Math.Min(idleCount, FramesPerState);
        return Math.Max(1, FramesPerState);
    }
}

/// <summary>
/// Resolves bundled and downloaded PetDex packages. Decoded sheets are shared by every
/// conversation and invalidated only when a newly downloaded package is installed.
/// </summary>
public static class PetDexPetLibrary
{
    public const string DefaultSlug = "boba";
    public const int StandardFrameWidth = 192;
    public const int StandardFrameHeight = 208;
    public const int StandardFramesPerState = 6;
    public const double MinimumScale = 0.25;
    public const double MaximumScale = 1.0;

    public static IReadOnlyList<PetDexBuiltInPet> BuiltIns { get; } =
    [
        new("boba", "Boba", "creature", "railly"),
        new("cache-capy", "Cache Capy", "creature", "railly"),
        new("pixel-panda", "Pixel Panda", "creature", "railly"),
        new("byte-bunny", "Byte Bunny", "creature", "railly")
    ];

    private static readonly HashSet<string> BuiltInSlugs =
        BuiltIns.Select(pet => pet.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<PetDexPetDefinition>> Definitions =
        new(StringComparer.OrdinalIgnoreCase);

    public static string InstalledRoot { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "AthenaData", "Pets");

    public static void ConfigureInstalledRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        var normalized = Path.GetFullPath(root);
        if (string.Equals(InstalledRoot, normalized, StringComparison.Ordinal)) return;
        InstalledRoot = normalized;
        ClearDownloadedDefinitions();
    }

    public static bool IsBuiltIn(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && BuiltInSlugs.Contains(slug);

    public static bool IsInstalled(string? slug) =>
        IsBuiltIn(slug) || TryGetInstalledDirectory(slug, out _);

    public static PetDexPetDefinition Resolve(string? slug) =>
        TryResolveExact(slug, out var definition)
            ? definition
            : ResolveExact(DefaultSlug);

    public static bool TryResolveExact(string? slug, out PetDexPetDefinition definition)
    {
        definition = null!;
        var safeSlug = SafeSlug(slug);
        if (safeSlug is null || (!IsBuiltIn(safeSlug) && !TryGetInstalledDirectory(safeSlug, out _)))
            return false;
        try
        {
            definition = ResolveExact(safeSlug);
            return true;
        }
        catch
        {
            Definitions.TryRemove(safeSlug, out _);
            return false;
        }
    }

    public static void Invalidate(string slug)
    {
        var safeSlug = SafeSlug(slug);
        if (safeSlug is not null && Definitions.TryRemove(safeSlug, out var lazy) && lazy.IsValueCreated)
            lazy.Value.SpriteSheet.Dispose();
    }

    public static double ClampScale(double scale) =>
        Math.Clamp(double.IsFinite(scale) ? scale : 0.5, MinimumScale, MaximumScale);

    private static PetDexPetDefinition ResolveExact(string slug) =>
        Definitions.GetOrAdd(
            slug,
            key => new Lazy<PetDexPetDefinition>(
                () => IsBuiltIn(key) ? LoadBuiltIn(key) : LoadDownloaded(key),
                isThreadSafe: true)).Value;

    private static PetDexPetDefinition LoadBuiltIn(string slug)
    {
        var root = $"avares://Athena.UI/Assets/Pets/{slug}/";
        using var metadataStream = AssetLoader.Open(new Uri(root + "pet.json"));
        var metadata = ReadMetadata(metadataStream, slug);
        var spritePath = SafeSpriteFileName(metadata.SpritesheetPath);
        using var spriteStream = AssetLoader.Open(new Uri(root + spritePath));
        using var spriteBuffer = new MemoryStream();
        spriteStream.CopyTo(spriteBuffer);
        var spriteBytes = spriteBuffer.ToArray();
        Bitmap? bitmap = null;
        try
        {
            using var avaloniaStream = new MemoryStream(spriteBytes, writable: false);
            bitmap = new Bitmap(avaloniaStream);
            using var pixelSheet = SKBitmap.Decode(spriteBytes);
            var definition = CreateDefinition(slug, metadata, bitmap, pixelSheet);
            bitmap = null;
            return definition;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static PetDexPetDefinition LoadDownloaded(string slug)
    {
        if (!TryGetInstalledDirectory(slug, out var directory))
            throw new FileNotFoundException($"PetDex package '{slug}' is not installed.");
        using var metadataStream = File.OpenRead(Path.Combine(directory, "pet.json"));
        var metadata = ReadMetadata(metadataStream, slug);
        var spritePath = Path.Combine(directory, SafeSpriteFileName(metadata.SpritesheetPath));
        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(spritePath);
            using var pixelSheet = SKBitmap.Decode(spritePath);
            var definition = CreateDefinition(slug, metadata, bitmap, pixelSheet);
            bitmap = null;
            return definition;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static PetDexPetDefinition CreateDefinition(
        string slug,
        PetDexMetadata metadata,
        Bitmap bitmap,
        SKBitmap? pixelSheet)
    {
        var frameWidth = metadata.FrameWidth > 0 ? metadata.FrameWidth : StandardFrameWidth;
        var frameHeight = metadata.FrameHeight > 0 ? metadata.FrameHeight : StandardFrameHeight;
        var columns = metadata.Columns > 0 ? metadata.Columns : bitmap.PixelSize.Width / frameWidth;
        var physicalRows = bitmap.PixelSize.Height / frameHeight;
        var rows = metadata.Rows is { Length: > 0 }
            ? metadata.Rows
            : physicalRows >= CurrentRows.Length ? CurrentRows : LegacyRows;

        var pixelSize = bitmap.PixelSize;
        if (columns < StandardFramesPerState
            || physicalRows < rows.Length
            || pixelSize.Width < columns * frameWidth)
        {
            throw new InvalidDataException(
                $"PetDex package '{slug}' spritesheet has unsupported geometry {pixelSize.Width}x{pixelSize.Height}.");
        }

        var frameCounts = DetectFrameCounts(pixelSheet, frameWidth, frameHeight, columns, rows.Length);
        return new PetDexPetDefinition(
            metadata.Id ?? slug,
            metadata.DisplayName ?? slug,
            metadata.Description ?? string.Empty,
            bitmap,
            frameWidth,
            frameHeight,
            columns,
            rows,
            frameCounts,
            DetectBottomTransparentPixels(
                pixelSheet,
                frameWidth,
                frameHeight,
                frameCounts.TryGetValue(0, out var idleFrames) ? idleFrames : StandardFramesPerState),
            metadata.LoopMs > 0 ? metadata.LoopMs : 1100);
    }

    private static IReadOnlyDictionary<int, int> DetectFrameCounts(
        SKBitmap? sheet,
        int frameWidth,
        int frameHeight,
        int columns,
        int rowCount)
    {
        var result = new Dictionary<int, int>();
        if (sheet is null) return result;
        var cells = Math.Min(StandardFramesPerState, columns);
        for (var row = 0; row < rowCount; row++)
        {
            var count = 0;
            for (var column = 0; column < cells; column++)
            {
                if (IsTransparentCell(sheet, column * frameWidth, row * frameHeight, frameWidth, frameHeight))
                    break;
                count++;
            }
            result[row] = count;
        }
        return result;
    }

    private static bool IsTransparentCell(SKBitmap sheet, int left, int top, int width, int height)
    {
        var right = Math.Min(sheet.Width, left + width);
        var bottom = Math.Min(sheet.Height, top + height);
        for (var y = Math.Max(0, top); y < bottom; y++)
        for (var x = Math.Max(0, left); x < right; x++)
        {
            // Match PetDex's padding detection tolerance: compression can leave
            // tiny alpha noise in what is visually an empty WebP cell.
            if (sheet.GetPixel(x, y).Alpha > 8) return false;
        }
        return true;
    }

    private static int DetectBottomTransparentPixels(
        SKBitmap? sheet,
        int frameWidth,
        int frameHeight,
        int idleFrameCount)
    {
        if (sheet is null) return 0;
        var lastOpaqueY = -1;
        for (var column = 0; column < Math.Max(1, idleFrameCount); column++)
        {
            var left = column * frameWidth;
            for (var localY = frameHeight - 1; localY >= 0; localY--)
            {
                var y = localY;
                var hasOpaquePixel = false;
                for (var x = left; x < Math.Min(sheet.Width, left + frameWidth); x++)
                {
                    if (sheet.GetPixel(x, y).Alpha <= 8) continue;
                    hasOpaquePixel = true;
                    break;
                }
                if (!hasOpaquePixel) continue;
                lastOpaqueY = Math.Max(lastOpaqueY, localY);
                break;
            }
        }
        return lastOpaqueY < 0 ? 0 : Math.Max(0, frameHeight - lastOpaqueY - 1);
    }

    private static PetDexMetadata ReadMetadata(Stream stream, string slug) =>
        JsonSerializer.Deserialize<PetDexMetadata>(stream, JsonOptions)
        ?? throw new InvalidDataException($"PetDex package '{slug}' has invalid pet.json.");

    private static string SafeSpriteFileName(string? path)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(path) ? "spritesheet.webp" : path);
        if (!fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PetDex spritesheets must be WebP or PNG files.");
        return fileName;
    }

    private static bool TryGetInstalledDirectory(string? slug, out string directory)
    {
        directory = string.Empty;
        var safeSlug = SafeSlug(slug);
        if (safeSlug is null) return false;
        directory = Path.Combine(InstalledRoot, safeSlug);
        return Directory.Exists(directory)
               && File.Exists(Path.Combine(directory, "pet.json"));
    }

    private static string? SafeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var trimmed = slug.Trim();
        return trimmed.Length <= 100
               && trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
            ? trimmed
            : null;
    }

    private static void ClearDownloadedDefinitions()
    {
        foreach (var pair in Definitions.Where(pair => !IsBuiltIn(pair.Key)).ToArray())
        {
            if (Definitions.TryRemove(pair.Key, out var lazy) && lazy.IsValueCreated)
                lazy.Value.SpriteSheet.Dispose();
        }
    }

    private static readonly string[] CurrentRows =
    [
        "idle", "running-right", "running-left", "waving", "jumping",
        "failed", "waiting", "running", "review"
    ];

    private static readonly string[] LegacyRows =
    [
        "idle", "wave", "run", "failed", "review", "jump", "extra1", "extra2"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class PetDexMetadata
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? SpritesheetPath { get; set; }
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int Columns { get; set; }
        public string[]? Rows { get; set; }
        public int LoopMs { get; set; }
    }
}
