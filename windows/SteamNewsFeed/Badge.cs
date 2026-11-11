using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SteamNewsFeed;

/// <summary>
/// 未读数角标：圆角底和数字在同一次绘制里画出来，数字取字形轮廓、按轮廓的几何中心放在底的正中。
/// 不用 Border 套 TextBlock：那样底和文字各自对齐到像素、文字还按字体行高排版，在缩放屏上会差半个像素。
/// 网页里的角标（feed.html 的 badgeSvg）是同样的画法。
/// </summary>
public sealed class Badge : FrameworkElement
{
	public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Badge),
		new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(Badge),
		new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(Badge),
		new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(Badge),
		new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Badge),
		new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(Badge),
		new FrameworkPropertyMetadata(Brushes.DarkOrange, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>数字左右的留白（单个数字时角标是圆形）。</summary>
	public static readonly DependencyProperty PadProperty = DependencyProperty.Register(nameof(Pad), typeof(double), typeof(Badge),
		new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

	public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

	public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

	public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

	public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

	public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

	public double Pad { get => (double)GetValue(PadProperty); set => SetValue(PadProperty, value); }

	private Geometry Outline()
	{
		var text = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
			new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
		return text.BuildGeometry(new Point());
	}

	/// <summary>高度取 Height（没设时 18），宽度 = 数字宽 + 两边留白，至少和高度一样（圆形）。</summary>
	protected override Size MeasureOverride(Size available)
	{
		double h = double.IsNaN(Height) ? 18 : Height;
		Rect b = Outline().Bounds;
		return new Size(Math.Max(h, (b.IsEmpty ? 0 : b.Width) + 2 * Pad), h);
	}

	protected override void OnRender(DrawingContext dc)
	{
		double w = RenderSize.Width, h = RenderSize.Height;
		dc.DrawRoundedRectangle(Background, null, new Rect(0, 0, w, h), h / 2, h / 2);
		Geometry g = Outline();
		Rect b = g.Bounds;
		if (b.IsEmpty)
		{
			return;
		}
		dc.PushTransform(new TranslateTransform((w - b.Width) / 2 - b.X, (h - b.Height) / 2 - b.Y));
		dc.DrawGeometry(Foreground, null, g);
		dc.Pop();
	}
}
