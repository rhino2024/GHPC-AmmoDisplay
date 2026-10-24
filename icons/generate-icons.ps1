# 生成弹药显示 mod 自带的弹种图标（全部为程序绘制的原创图形，不含任何第三方素材）
# 用法：powershell -ExecutionPolicy Bypass -File generate-icons.ps1
# 输出：本目录下的 apds.png / apds_belt.png / apfsds.png / atgm.png / he.png / heatfs.png（128x128）
#       以及 _preview.png（仅供人眼检查，不发版）

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class AmmoIconGen
{
    const int S = 512;    // 绘制画布
    const int OUT = 128;  // 输出边长

    static readonly Color BgTop = Color.FromArgb(0x26, 0x3a, 0x55);
    static readonly Color BgBottom = Color.FromArgb(0x0c, 0x13, 0x1e);
    static readonly Color Edge = Color.FromArgb(0x54, 0x74, 0x99);
    static readonly Color EdgeDark = Color.FromArgb(0x08, 0x0d, 0x14);

    static readonly Color BrassL = Color.FromArgb(0x8a, 0x62, 0x1f);
    static readonly Color BrassM = Color.FromArgb(0xeb, 0xcd, 0x86);
    static readonly Color BrassR = Color.FromArgb(0x6d, 0x4b, 0x14);

    static readonly Color SteelL = Color.FromArgb(0x5e, 0x6b, 0x79);
    static readonly Color SteelM = Color.FromArgb(0xdf, 0xe6, 0xed);
    static readonly Color SteelR = Color.FromArgb(0x4a, 0x55, 0x61);

    static readonly Color DarkL = Color.FromArgb(0x2a, 0x32, 0x3c);
    static readonly Color DarkM = Color.FromArgb(0x62, 0x70, 0x7d);
    static readonly Color DarkR = Color.FromArgb(0x1d, 0x23, 0x2a);

    public static void Generate(string dir)
    {
        Draw("apfsds", dir, new Action<Graphics>(DrawApfsds));
        Draw("apds", dir, new Action<Graphics>(DrawApds));
        Draw("heatfs", dir, new Action<Graphics>(DrawHeatfs));
        Draw("he", dir, new Action<Graphics>(DrawHe));
        Draw("atgm", dir, new Action<Graphics>(DrawAtgm));
        Draw("apds_belt", dir, new Action<Graphics>(DrawBelt));
        Preview(dir);
        HudPreview(dir);
    }

    static void Draw(string name, string dir, Action<Graphics> body)
    {
        using (var big = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(big))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawTile(g);
                body(g);
            }
            using (var small = new Bitmap(OUT, OUT, PixelFormat.Format32bppArgb))
            using (var sg = Graphics.FromImage(small))
            {
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.DrawImage(big, new Rectangle(0, 0, OUT, OUT));
                small.Save(System.IO.Path.Combine(dir, name + ".png"), ImageFormat.Png);
            }
        }
    }

    static void DrawTile(Graphics g)
    {
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, S, S), BgTop, BgBottom, LinearGradientMode.Vertical))
            g.FillRectangle(bg, 0, 0, S, S);

        // 左上角一层很淡的斜向反光，避免整块看起来是死色
        using (var sheen = new LinearGradientBrush(new Rectangle(0, 0, S, S),
            Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.ForwardDiagonal))
            g.FillRectangle(sheen, 0, 0, S, S);

        using (var pen = new Pen(Edge, 13)) { pen.Alignment = PenAlignment.Inset; g.DrawRectangle(pen, 0, 0, S, S); }
        using (var pen = new Pen(EdgeDark, 5)) { pen.Alignment = PenAlignment.Inset; g.DrawRectangle(pen, 15, 15, S - 30, S - 30); }
    }

    static void FillCylinder(Graphics g, GraphicsPath path, Color left, Color mid, Color right)
    {
        RectangleF b = path.GetBounds();
        using (var br = new LinearGradientBrush(new RectangleF(b.X, b.Y, Math.Max(1f, b.Width), 2f), left, right, LinearGradientMode.Horizontal))
        {
            br.InterpolationColors = new ColorBlend(3)
            {
                Colors = new Color[] { left, mid, right },
                Positions = new float[] { 0f, 0.34f, 1f }
            };
            g.FillPath(br, path);
        }
        using (var pen = new Pen(Color.FromArgb(170, 0, 0, 0), 3.5f))
            g.DrawPath(pen, path);
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2f;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // 尖拱形弹头：底边在 baseY，尖端在 tipY
    static GraphicsPath Ogive(float cx, float baseY, float tipY, float halfW)
    {
        float h = baseY - tipY;
        var p = new GraphicsPath();
        p.AddBezier(new PointF(cx - halfW, baseY),
            new PointF(cx - halfW * 0.92f, baseY - h * 0.52f),
            new PointF(cx - halfW * 0.44f, tipY + h * 0.10f),
            new PointF(cx, tipY));
        p.AddBezier(new PointF(cx, tipY),
            new PointF(cx + halfW * 0.44f, tipY + h * 0.10f),
            new PointF(cx + halfW * 0.92f, baseY - h * 0.52f),
            new PointF(cx + halfW, baseY));
        p.CloseFigure();
        return p;
    }

    // 圆锥弹头
    static GraphicsPath Cone(float cx, float baseY, float tipY, float halfW)
    {
        var p = new GraphicsPath();
        p.AddPolygon(new PointF[] {
            new PointF(cx - halfW, baseY),
            new PointF(cx, tipY),
            new PointF(cx + halfW, baseY)
        });
        return p;
    }

    static void Case(Graphics g, float cx, float top, float bottom, float halfW)
    {
        var body = Rounded(new RectangleF(cx - halfW, top, halfW * 2f, bottom - top), 10f);
        FillCylinder(g, body, BrassL, BrassM, BrassR);

        // 底缘（抽壳钩槽）与壳口暗边
        using (var br = new SolidBrush(Color.FromArgb(150, 30, 20, 5)))
        {
            g.FillRectangle(br, cx - halfW - 7f, bottom - 30f, halfW * 2f + 14f, 16f);
        }
        using (var pen = new Pen(Color.FromArgb(160, 40, 26, 8), 6f))
            g.DrawLine(pen, cx - halfW, top + 7f, cx + halfW, top + 7f);
    }

    static void Band(Graphics g, float cx, float y, float halfW, float h, Color c)
    {
        var r = Rounded(new RectangleF(cx - halfW, y, halfW * 2f, h), 4f);
        FillCylinder(g, r, Shade(c, 0.62f), Shade(c, 1.15f), Shade(c, 0.55f));
    }

    static Color Shade(Color c, float f)
    {
        return Color.FromArgb(c.A,
            Clamp((int)(c.R * f)), Clamp((int)(c.G * f)), Clamp((int)(c.B * f)));
    }

    static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

    // ---- 各弹种 ----

    static void DrawApfsds(Graphics g)
    {
        float cx = 256f;
        // 尾翼：细长三角形，贴住长杆末端，别做成裙摆
        using (var p = new GraphicsPath())
        {
            p.AddPolygon(new PointF[] {
                new PointF(cx - 16f, 320f), new PointF(cx - 62f, 378f),
                new PointF(cx - 62f, 344f), new PointF(cx - 16f, 300f) });
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        }
        using (var p = new GraphicsPath())
        {
            p.AddPolygon(new PointF[] {
                new PointF(cx + 16f, 320f), new PointF(cx + 62f, 378f),
                new PointF(cx + 62f, 344f), new PointF(cx + 16f, 300f) });
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        }

        Case(g, cx, 336f, 466f, 54f);

        // 长杆穿甲体：细、长，几乎顶到画面上沿
        using (var p = Rounded(new RectangleF(cx - 17f, 132f, 34f, 218f), 6f))
            FillCylinder(g, p, SteelL, SteelM, SteelR);
        using (var p = Cone(cx, 140f, 58f, 17f))
            FillCylinder(g, p, SteelL, SteelM, SteelR);
        // 弹托环
        Band(g, cx, 320f, 26f, 20f, Color.FromArgb(0xd8, 0x9a, 0x3c));

        // 风帽尖端压暗
        using (var p = Cone(cx, 104f, 58f, 9f))
        using (var b = new SolidBrush(Color.FromArgb(200, 0x22, 0x28, 0x30)))
            g.FillPath(b, p);
    }

    static void DrawApds(Graphics g)
    {
        float cx = 256f;
        Case(g, cx, 300f, 466f, 58f);
        using (var p = Ogive(cx, 306f, 108f, 46f))
            FillCylinder(g, p, SteelL, SteelM, SteelR);
        // 被帽
        using (var p = Ogive(cx, 168f, 108f, 26f))
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        // 铜被甲带
        Band(g, cx, 276f, 47f, 22f, Color.FromArgb(0xc9, 0x7e, 0x33));
    }

    static void DrawHeatfs(Graphics g)
    {
        float cx = 256f;
        Case(g, cx, 306f, 466f, 55f);
        using (var p = Ogive(cx, 312f, 148f, 48f))
            FillCylinder(g, p, Color.FromArgb(0x5a, 0x5f, 0x4a), Color.FromArgb(0xc2, 0xc9, 0xa4), Color.FromArgb(0x43, 0x48, 0x37));
        // 风帽
        using (var p = Ogive(cx, 152f, 100f, 26f))
            FillCylinder(g, p, Color.FromArgb(0x8a, 0x3a, 0x24), Color.FromArgb(0xe8, 0x7a, 0x45), Color.FromArgb(0x6b, 0x28, 0x18));
        Band(g, cx, 276f, 49f, 24f, Color.FromArgb(0xe0, 0x9a, 0x2e));
    }

    static void DrawHe(Graphics g)
    {
        float cx = 256f;
        Case(g, cx, 306f, 466f, 57f);
        using (var p = Ogive(cx, 312f, 96f, 52f))
            FillCylinder(g, p, Color.FromArgb(0x4a, 0x57, 0x2c), Color.FromArgb(0x9d, 0xb0, 0x60), Color.FromArgb(0x33, 0x3d, 0x1d));
        // 引信
        using (var p = Cone(cx, 150f, 92f, 22f))
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        Band(g, cx, 280f, 52f, 22f, Color.FromArgb(0xb8, 0x40, 0x30));
    }

    static void DrawAtgm(Graphics g)
    {
        float cx = 256f;

        // 中部弹翼
        using (var p = new GraphicsPath())
        {
            p.AddPolygon(new PointF[] {
                new PointF(cx - 24f, 322f), new PointF(cx - 78f, 366f),
                new PointF(cx - 78f, 322f), new PointF(cx - 24f, 292f) });
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        }
        using (var p = new GraphicsPath())
        {
            p.AddPolygon(new PointF[] {
                new PointF(cx + 24f, 322f), new PointF(cx + 78f, 366f),
                new PointF(cx + 78f, 322f), new PointF(cx + 24f, 292f) });
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        }
        // 尾翼
        using (var p = new GraphicsPath())
        {
            p.AddPolygon(new PointF[] {
                new PointF(cx - 24f, 404f), new PointF(cx - 66f, 438f),
                new PointF(cx - 66f, 396f), new PointF(cx - 24f, 380f) });
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        }
        using (var p = new GraphicsPath())
        {
            p.AddPolygon(new PointF[] {
                new PointF(cx + 24f, 404f), new PointF(cx + 66f, 438f),
                new PointF(cx + 66f, 396f), new PointF(cx + 24f, 380f) });
            FillCylinder(g, p, DarkL, DarkM, DarkR);
        }

        // 弹体
        using (var p = Rounded(new RectangleF(cx - 25f, 150f, 50f, 292f), 8f))
            FillCylinder(g, p, Color.FromArgb(0x3f, 0x4a, 0x38), Color.FromArgb(0xa8, 0xb4, 0x92), Color.FromArgb(0x2e, 0x36, 0x28));
        using (var p = Cone(cx, 160f, 74f, 25f))
            FillCylinder(g, p, Color.FromArgb(0x8d, 0x99, 0x7f), Color.FromArgb(0xdf, 0xe5, 0xd2), Color.FromArgb(0x6a, 0x75, 0x5e));
        // 导引头窗口
        using (var p = Cone(cx, 128f, 74f, 17f))
            FillCylinder(g, p, Color.FromArgb(0x6e, 0x1d, 0x18), Color.FromArgb(0xd4, 0x4c, 0x3c), Color.FromArgb(0x54, 0x13, 0x10));
        Band(g, cx, 262f, 26f, 20f, Color.FromArgb(0xd8, 0x9a, 0x3c));
        Band(g, cx, 330f, 26f, 14f, Color.FromArgb(0xc9, 0x7e, 0x33));
    }

    static void DrawBelt(Graphics g)
    {
        // 三发并排的机炮 APDS-T
        float[] xs = new float[] { 118f, 256f, 394f };
        for (int i = 0; i < xs.Length; i++)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(xs[i], 256f);
            g.ScaleTransform(0.70f, 0.70f);
            g.TranslateTransform(-256f, -256f);
            Case(g, 256f, 300f, 466f, 58f);
            using (var p = Ogive(256f, 306f, 118f, 46f))
                FillCylinder(g, p, SteelL, SteelM, SteelR);
            using (var p = Ogive(256f, 172f, 118f, 26f))
                FillCylinder(g, p, DarkL, DarkM, DarkR);
            Band(g, 256f, 280f, 47f, 20f, Color.FromArgb(0xc9, 0x7e, 0x33));
            g.Restore(st);
        }
    }

    static void Preview(string dir)
    {
        string[] names = new string[] { "apfsds", "apds", "apds_belt", "heatfs", "he", "atgm" };
        int cell = 160, pad = 12, label = 22;
        using (var bmp = new Bitmap(cell * 3 + pad * 4, (cell + label) * 2 + pad * 3, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0x1a, 0x22, 0x2e));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var font = new Font("Consolas", 11f, FontStyle.Bold))
            using (var white = new SolidBrush(Color.FromArgb(0xe8, 0xee, 0xf4)))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    int col = i % 3, row = i / 3;
                    int x = pad + col * (cell + pad), y = pad + row * (cell + label + pad);
                    using (var img = Image.FromFile(System.IO.Path.Combine(dir, names[i] + ".png")))
                        g.DrawImage(img, new Rectangle(x, y, cell, cell));
                    g.DrawString(names[i], font, white, x, y + cell + 2);
                }
            }
            bmp.Save(System.IO.Path.Combine(dir, "_preview.png"), ImageFormat.Png);
        }
    }

    // 96 px 格子 + 右下角数字，模拟游戏里 HUD 的实际观感
    static void HudPreview(string dir)
    {
        string[] names = new string[] { "apfsds", "apds", "apds_belt", "heatfs", "he", "atgm" };
        string[] counts = new string[] { "12", "7", "195", "8", "6", "2" };
        int cell = 96, gap = 10, pad = 14;
        int w = pad * 2 + (cell + gap) * names.Length - gap;
        int h = pad * 2 + cell;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0x3a, 0x42, 0x36));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var font = new Font("Arial", 25f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var black = new SolidBrush(Color.FromArgb(220, 0, 0, 0)))
            using (var white = new SolidBrush(Color.White))
            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Far;
                fmt.LineAlignment = StringAlignment.Near;
                for (int i = 0; i < names.Length; i++)
                {
                    int x = pad + i * (cell + gap);
                    using (var img = Image.FromFile(System.IO.Path.Combine(dir, names[i] + ".png")))
                        g.DrawImage(img, new Rectangle(x, pad, cell, cell));
                    float ty = pad + cell - 32f;
                    string s = counts[i];
                    g.DrawString(s, font, black, new PointF(x + 1f, ty + 1f), fmt);
                    g.DrawString(s, font, black, new PointF(x - 1f, ty + 1f), fmt);
                    g.DrawString(s, font, black, new PointF(x + 1f, ty - 1f), fmt);
                    g.DrawString(s, font, black, new PointF(x - 1f, ty - 1f), fmt);
                    RectangleF r = new RectangleF(x + (cell - 2f) - 1e-3f, ty, 2f, 40f);
                    g.DrawString(s, font, white, r, fmt);
                }
            }
            bmp.Save(System.IO.Path.Combine(dir, "_preview96.png"), ImageFormat.Png);
        }
    }
}
'@

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
[AmmoIconGen]::Generate($dir)
Get-ChildItem $dir -Filter *.png | ForEach-Object { '{0} {1} bytes' -f $_.Name, $_.Length }
