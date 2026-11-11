using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SteamNewsFeed;

/// <summary>
/// 按字形的实际轮廓居中的文字（徽标里的数字用）。TextBlock 按字体的行高排版，
/// 数字在行高里本来就不在正中；这里取文字轮廓的边界框，把它放在控件正中间。
/// </summary>
public sealed class GlyphText : FrameworkElement
{
	public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(GlyphText),
		new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(GlyphText),
		new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(GlyphText),
		new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(GlyphText),
		new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(GlyphText),
		new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

	public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

	public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

	public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

	public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

	public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

	private Geometry Outline()
	{
		var text = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
			new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
		return text.BuildGeometry(new Point());
	}

	protected override Size MeasureOverride(Size available)
	{
		Rect b = Outline().Bounds;
		return b.IsEmpty ? new Size() : new Size(b.Width, b.Height);
	}

	protected override void OnRender(DrawingContext dc)
	{
		Geometry g = Outline();
		Rect b = g.Bounds;
		if (b.IsEmpty)
		{
			return;
		}
		dc.PushTransform(new TranslateTransform((RenderSize.Width - b.Width) / 2 - b.X, (RenderSize.Height - b.Height) / 2 - b.Y));
		dc.DrawGeometry(Foreground, null, g);
		dc.Pop();
	}
}
