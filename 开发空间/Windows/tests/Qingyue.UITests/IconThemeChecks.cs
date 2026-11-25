using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Qingyue.Windows;

internal static class IconThemeChecks
{
    public static void Run(Action<bool, string> check, MainWindow window, string root)
    {
        check(AppIconTheme.Options.Count == 4, "应用图标提供四套可切换主题");
        check(AppIconTheme.Parse(null) == AppIconThemeKind.Sky
            && AppIconTheme.Parse("invalid") == AppIconThemeKind.Sky,
            "未知图标主题默认回退天空蓝");
        check(AppIconTheme.NormalizeName("sakura") == "Sakura"
            && AppIconTheme.NormalizeName("mint") == "Mint"
            && AppIconTheme.NormalizeName("peach") == "Peach",
            "图标主题名称可规范化");

        string settingsRoot = Path.Combine(root, "icon-settings");
        var settings = new ReaderSettings { IconTheme = "Peach" };
        settings.Save(settingsRoot);
        var restored = ReaderSettings.Load(settingsRoot);
        check(restored.IconTheme == "Peach", "图标主题可以保存并恢复");

        File.WriteAllText(
            Path.Combine(settingsRoot, "Settings.json"),
            """{"Dark":true,"Theme":"Dark"}""");
        check(ReaderSettings.Load(settingsRoot).IconTheme == "Sky",
            "旧版缺失图标字段时默认天空蓝");

        check(window.Icon is BitmapImage image
            && image.UriSource?.AbsolutePath.EndsWith("/Assets/Qingyue-sky.png", StringComparison.OrdinalIgnoreCase) == true,
            "主窗口启动加载天空蓝图标");

        string assemblyName = typeof(MainWindow).Assembly.GetName().Name ?? "Qingyue";
        foreach (string fileName in AppIconTheme.Options
                     .Select(option => option.FileName)
                     .Append("Qingyue.png"))
        {
            var frame = DecodePngResource(assemblyName, fileName);
            check(frame.PixelWidth >= 1024 && frame.PixelHeight >= 1024,
                $"{fileName} 使用至少 1024×1024 的真实图像资源");
        }

        var icoBytes = ReadResource(assemblyName, "Qingyue.ico");
        var entries = ReadIcoDirectory(icoBytes);
        check(entries.Select(entry => entry.Width).SequenceEqual([16, 24, 32, 48, 64, 128, 256]) &&
            entries.All(entry => entry.Width == entry.Height && entry.BitCount == 32),
            "Windows ICO 真实包含 16 / 24 / 32 / 48 / 64 / 128 / 256 的 32 位帧");
        check(entries.All(entry =>
                entry.Offset >= 0 &&
                entry.Bytes > 0 &&
                entry.Offset + entry.Bytes <= icoBytes.Length),
            "Windows ICO 目录项的负载范围完整且未越界");

        using var icoStream = new MemoryStream(icoBytes, writable: false);
        var icoDecoder = new IconBitmapDecoder(
            icoStream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        check(icoDecoder.Frames.Select(frame => frame.PixelWidth)
                .OrderBy(width => width)
                .SequenceEqual([16, 24, 32, 48, 64, 128, 256]),
            "Windows ICO 七个目录项均可由系统解码器实际解码");

        foreach (var option in AppIconTheme.Options)
        {
            window.ApplyAppIconTheme(option.Kind);
            check(window.Icon is BitmapImage themeImage
                && themeImage.UriSource?.AbsolutePath.EndsWith(
                    "/Assets/" + option.FileName, StringComparison.OrdinalIgnoreCase) == true,
                $"主窗口可以即时切换{option.Label}图标");
        }
        window.ApplyAppIconTheme(AppIconThemeKind.Sky);
    }

    private static BitmapFrame DecodePngResource(string assemblyName, string fileName)
    {
        using var stream = new MemoryStream(ReadResource(assemblyName, fileName), writable: false);
        return BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
    }

    private static byte[] ReadResource(string assemblyName, string fileName)
    {
        var uri = new Uri(
            $"pack://application:,,,/{assemblyName};component/Assets/{fileName}",
            UriKind.Absolute);
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"找不到图像资源：{fileName}");
        using var stream = resource.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IReadOnlyList<IcoEntry> ReadIcoDirectory(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
            throw new InvalidDataException("ICO 头无效。");
        int count = reader.ReadUInt16();
        if (count <= 0 || count > 64)
            throw new InvalidDataException($"ICO 帧数量异常：{count}");

        var entries = new List<IcoEntry>(count);
        for (int index = 0; index < count; index++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            if (width == 0) width = 256;
            if (height == 0) height = 256;
            reader.ReadByte();
            reader.ReadByte();
            int planes = reader.ReadUInt16();
            int bitCount = reader.ReadUInt16();
            long size = reader.ReadUInt32();
            long offset = reader.ReadUInt32();
            if (planes != 1)
                throw new InvalidDataException($"ICO 第 {index + 1} 帧平面数异常：{planes}");
            entries.Add(new IcoEntry(width, height, bitCount, size, offset));
        }
        return entries;
    }

    private readonly record struct IcoEntry(
        int Width,
        int Height,
        int BitCount,
        long Bytes,
        long Offset);
}
