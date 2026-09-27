using CodexBarWin.WinUI;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "wincodexbar-tray-qa.png");
var styles = TrayIconRenderer.StyleOptions.Where(option => option.Id != "classic").ToArray();
using var sheet = new Bitmap(750, 56 + styles.Length * 96);
using var graphics = Graphics.FromImage(sheet);
graphics.Clear(Color.FromArgb(235, 241, 248));
using var heading = new Font("Segoe UI", 13, FontStyle.Bold);
using var labelFont = new Font("Segoe UI", 11);
graphics.DrawString("托盘样式 · 剩余 100% / 42% / 0% / 仅 7 天（16px 渲染后放大）", heading, Brushes.Black, 12, 8);
for (var row = 0; row < styles.Length; row++)
{
    var style = styles[row];
    graphics.DrawString(style.Label, labelFont, Brushes.Black, 12, 58 + row * 96);
    for (var column = 0; column < 4; column++)
    {
        double? fiveHourUsed = column == 3 ? null : column == 0 ? 0 : column == 1 ? 58 : 100;
        using var icon = TrayIconRenderer.Render(style.Id, fiveHourUsed, 42, true, 64);
        using var small = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var target = Graphics.FromImage(small))
        {
            target.InterpolationMode = InterpolationMode.HighQualityBicubic;
            target.DrawImage(icon, 0, 0, 16, 16);
        }
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(small, new Rectangle(210 + column * 140, 48 + row * 96, 64, 64));
    }
    using var bytes = new MemoryStream();
    TrayIconRenderer.WriteIcon(bytes, style.Id, 0, 42, true);
    bytes.Position = 0;
    using var reader = new BinaryReader(bytes, System.Text.Encoding.UTF8, leaveOpen: true);
    if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 8)
        throw new InvalidDataException($"{style.Id}: multi-size ICO header is invalid");
    bytes.Position = 0;
    using var loaded = new Icon(bytes, new Size(16, 16));
    if (loaded.Width != 16 || loaded.Height != 16)
        throw new InvalidDataException($"{style.Id}: 16px icon could not be loaded");
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
sheet.Save(output, ImageFormat.Png);
Console.WriteLine($"{styles.Length} styles loaded at 16px, 8 ICO sizes each. Sheet: {output}");
