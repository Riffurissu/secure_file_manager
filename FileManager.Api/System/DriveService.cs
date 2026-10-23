namespace FileManager.Api.System;

/// <summary>Сведения о локальном накопителе. Пути к каталогам и служебные поля не раскрываются.</summary>
public sealed record DriveEntry(
    string Name,
    string Type,
    string? Format,
    string? Label,
    long TotalSize,
    long TotalFreeSpace,
    long AvailableFreeSpace);

public sealed class DriveService
{
    // Виртуальные и служебные ФС Linux, которые не имеют смысла для файлового менеджера.
    private static readonly HashSet<string> VirtualFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "devtmpfs", "tmpfs", "cgroup", "cgroup2", "overlay", "squashfs", "securityfs",
        "debugfs", "tracefs", "configfs", "devpts", "mqueue", "pstore", "bpf", "fusectl", "autofs", "nsfs",
    };

    /// <summary>
    /// Возвращает готовые локальные диски. В Windows это фиксированные и съёмные тома.
    /// В Linux (контейнер) — смонтированные реальные ФС, виртуальные ФС пропускаются.
    /// Сетевые и оптические носители пропускаются в обеих ОС.
    /// </summary>
    public IReadOnlyList<DriveEntry> ListDrives() =>
        DriveInfo.GetDrives()
            .Where(IsLocalDisk)
            .Select(d => new DriveEntry(
                Name: d.Name,
                Type: d.DriveType.ToString(),
                Format: d.DriveFormat,
                Label: SafeLabel(d),
                TotalSize: d.TotalSize,
                TotalFreeSpace: d.TotalFreeSpace,
                AvailableFreeSpace: d.AvailableFreeSpace))
            .ToList();

    private static bool IsLocalDisk(DriveInfo drive)
    {
        if (!drive.IsReady)
        {
            return false;
        }

        // Сетевые и оптические носители пропускаем. Типы Unknown/Ram допустимы: так Linux отдаёт тома контейнера.
        if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory)
        {
            return false;
        }

        return !VirtualFormats.Contains(drive.DriveFormat);
    }

    /// <summary>Метка тома может выбрасывать исключение на Linux, поэтому читаем её безопасно.</summary>
    private static string? SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
