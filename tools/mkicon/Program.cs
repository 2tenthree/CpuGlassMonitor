using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 生成应用图标：深色圆角底 + 发光芯片轮廓，输出多尺寸 ICO（PNG 压缩）
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
string outPath = args.Length > 0 ? args[0] : "app.ico";

var pngs = new List<byte[]>();
foreach (int size in sizes)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / 256f;
        float pad = 10 * s;

        // 背景圆角方块 + 渐变
        var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        float radius = size * 0.22f;
        using (GraphicsPath bgPath = RoundedRect(rect, radius))
        {
            using var bgBrush = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size),
                Color.FromArgb(255, 26, 32, 42), Color.FromArgb(255, 10, 13, 18));
            g.FillPath(bgBrush, bgPath);

            using var edge = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, 2 * s));
            g.DrawPath(edge, bgPath);
        }

        // 芯片本体
        float bodyInset = size * 0.30f;
        var body = new RectangleF(bodyInset, bodyInset, size - bodyInset * 2, size - bodyInset * 2);
        using (GraphicsPath bodyPath = RoundedRect(body, size * 0.055f))
        {
            using var glowBrush = new LinearGradientBrush(body.Location,
                new PointF(body.Right, body.Bottom),
                Color.FromArgb(255, 111, 211, 255), Color.FromArgb(255, 64, 168, 226));
            using var bodyPen = new Pen(glowBrush, Math.Max(1.2f, 7 * s));
            g.DrawPath(bodyPen, bodyPath);
        }

        // 引脚
        using var pinPen = new Pen(Color.FromArgb(235, 130, 205, 245), Math.Max(1f, 5 * s))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        float pinLen = size * 0.075f;
        float gap = size * 0.14f;
        float center = size / 2f;
        for (int i = -1; i <= 1; i++)
        {
            float pos = center + i * gap;

            g.DrawLine(pinPen, bodyInset - pinLen, pos, bodyInset, pos);
            g.DrawLine(pinPen, body.Right, pos, body.Right + pinLen, pos);
            g.DrawLine(pinPen, pos, bodyInset - pinLen, pos, bodyInset);
            g.DrawLine(pinPen, pos, body.Bottom, pos, body.Bottom + pinLen);
        }

        // 中心亮点
        if (size >= 32)
        {
            float dot = size * 0.055f;
            using var halo = new SolidBrush(Color.FromArgb(90, 111, 211, 255));
            g.FillEllipse(halo, center - dot * 2.4f, center - dot * 2.4f, dot * 4.8f, dot * 4.8f);
            using var core = new SolidBrush(Color.FromArgb(255, 225, 246, 255));
            g.FillEllipse(core, center - dot, center - dot, dot * 2, dot * 2);
        }
    }

    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngs.Add(ms.ToArray());
}

using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
using var w = new BinaryWriter(fs);

w.Write((ushort)0);          // reserved
w.Write((ushort)1);          // type = icon
w.Write((ushort)sizes.Length);

int offset = 6 + 16 * sizes.Length;
for (int i = 0; i < sizes.Length; i++)
{
    int size = sizes[i];
    w.Write((byte)(size >= 256 ? 0 : size));
    w.Write((byte)(size >= 256 ? 0 : size));
    w.Write((byte)0);        // color count
    w.Write((byte)0);        // reserved
    w.Write((ushort)1);      // planes
    w.Write((ushort)32);     // bpp
    w.Write(pngs[i].Length);
    w.Write(offset);
    offset += pngs[i].Length;
}

foreach (byte[] png in pngs) w.Write(png);

Console.WriteLine($"icon written: {Path.GetFullPath(outPath)} ({new FileInfo(outPath).Length} bytes)");

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var path = new GraphicsPath();
    float d = radius * 2;
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}
